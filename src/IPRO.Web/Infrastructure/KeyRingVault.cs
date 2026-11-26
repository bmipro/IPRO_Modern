using System.Xml;
using System.Xml.Linq;
using Azure.Core.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.KeyManagement.Internal;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace IPRO.Web.Infrastructure;

// 681 (audit 2026-11-24 F-5; the owner, 2026-11-25: "yes go ahead with Key Vault"). The keys that seal the portal's
// sign-in cookies and lock two-step secrets and Google tokens (Data Protection) were plain XML in two places: Azure's
// folder on the app's own storage and, since 635, a copy in iPro's storage account beside the database dumps. Whoever
// held that one storage key could read every two-step secret and Google token in a dump, and put a key of their own in
// the copy that the app took in and then accepted cookies made with it.
//
// Now, when the app setting Azure__DataProtectionKeyId names a key in Azure Key Vault, every key is locked with it: a
// new key as it is made (ProtectKeysWithAzureKeyVault), the keys made before by KeyRingLockService soon after start-up
// (each one read back by Data Protection itself, through the vault, before the plain file is replaced, in the folder
// and in the copy, whose earlier plain versions are deleted). The app reaches the vault with its own Azure identity: there is no password to leak.
// MirroredKeyRepository takes keys from the copy only into an empty folder (the disaster 635 is for), never beside keys.
//
// Without the setting nothing is locked and nothing changes, but keys already locked still open: the vault's reader is
// registered either way, so removing the setting later stops locking new keys without locking anyone out.
public static class KeyRingVault
{
    public const string Setting = "Azure:DataProtectionKeyId";

    // https://<vault>.vault.azure.net/keys/<name>, or with /<version>; anything else is no key address.
    public static Uri? KeyUri(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps || !uri.Host.EndsWith(".vault.azure.net", StringComparison.OrdinalIgnoreCase)) return null;
        var parts = uri.AbsolutePath.Trim('/').Split('/');
        if (parts.Length is < 2 or > 3 || !string.Equals(parts[0], "keys", StringComparison.OrdinalIgnoreCase) || parts.Any(string.IsNullOrWhiteSpace)) return null;
        return uri;
    }

    public static void Use(IDataProtectionBuilder dataProtection, Uri? keyUri, IKeyEncryptionKeyResolver vault)
    {
        if (keyUri == null)
        {
            dataProtection.Services.TryAddSingleton<IKeyEncryptionKeyResolver>(vault);
            return;
        }
        dataProtection.ProtectKeysWithAzureKeyVault(keyUri, vault);
        dataProtection.Services.AddHostedService<KeyRingLockService>();
    }
}

// The steps Data Protection takes when it locks a new key (the element marked requiresEncryption is replaced by an
// encryptedSecret holding what the encryptor made of it), applied to a key made before the vault was set. Whether the
// result is right is never judged here: Data Protection's own reader opens it (ReadsBackTheSame) before it is kept.
public static class KeyRingLocking
{
    private const string DataProtectionXmlns = "http://schemas.asp.net/2015/03/dataProtection";
    private static readonly XName RequiresEncryption = XName.Get("requiresEncryption", DataProtectionXmlns);
    private static readonly XName EncryptedSecret = XName.Get("encryptedSecret", DataProtectionXmlns);
    private static readonly XName DecryptorType = "decryptorType";

    public static bool HoldsPlainSecret(XElement element) => element.DescendantsAndSelf().Any(MarkedSecret);

    public static bool HoldsLockedSecret(XElement element) => element.DescendantsAndSelf(EncryptedSecret).Any();

    public static XElement Lock(XElement element, IXmlEncryptor encryptor)
    {
        var locked = new XElement(element);
        while (locked.DescendantsAndSelf().FirstOrDefault(MarkedSecret) is { } secret)
        {
            if (secret == locked) throw new InvalidOperationException("681: a whole key element marked secret is not a shape Data Protection makes");
            var info = encryptor.Encrypt(new XElement(secret));
            secret.ReplaceWith(new XElement(EncryptedSecret,
                new XAttribute(DecryptorType, info.DecryptorType.AssemblyQualifiedName!),
                info.EncryptedElement));
        }
        return locked;
    }

    public static bool SameXml(XElement a, XElement b) =>
        XNode.DeepEquals(XElement.Parse(a.ToString(SaveOptions.DisableFormatting)), XElement.Parse(b.ToString(SaveOptions.DisableFormatting)));

    // What Data Protection itself reads a key element as (opening what is locked, through the vault), written out
    // again in plain XML: the same for a plain key and that key locked, or the locked one is not kept.
    public static XElement ReadByDataProtection(IKeyManager keys, XElement keyElement) =>
        ((IInternalXmlKeyManager)keys).DeserializeDescriptorFromKeyElement(keyElement).ExportToXml().SerializedDescriptorElement;

    private static bool MarkedSecret(XElement e) =>
        e.Attribute(RequiresEncryption) is { } a && XmlConvert.ToBoolean(a.Value);
}

public sealed record KeyLockReport(int LockedNow, int AlreadyLocked, int CopiesReplaced, int OldVersionsDeleted, int SharedFolderLocked = 0)
{
    public string Line() =>
        $"[KeyRing] locked with Key Vault: {LockedNow} key(s) locked now, {AlreadyLocked} already locked; " +
        $"{CopiesReplaced} plain cop(ies) in storage replaced, {OldVersionsDeleted} earlier version(s) deleted; " +
        $"{SharedFolderLocked} left in the shared folder locked.";
}

// Locks the keys made before the vault was set, soon after start-up, and says so on one plain line in the container
// log (production keeps warnings only). The vault may not answer yet (a new role takes minutes to apply): it tries
// again every few minutes for an hour, then on the next start. Until then the keys stay as they were and work.
public sealed class KeyRingLockService : BackgroundService
{
    public static readonly TimeSpan RetryEvery = TimeSpan.FromMinutes(5);
    public const int Tries = 12;

    private readonly IOptions<KeyManagementOptions> _options;
    private readonly IServiceProvider _services;
    private readonly ILogger<KeyRingLockService> _logger;

    public KeyRingLockService(IOptions<KeyManagementOptions> options, IServiceProvider services, ILogger<KeyRingLockService> logger)
    {
        _options = options;
        _services = services;
        _logger = logger;
    }

    public KeyLockReport? LockNow()
    {
        var options = _options.Value;
        if (options.XmlRepository is not MirroredKeyRepository ring || options.XmlEncryptor is not { } encryptor) return null;
        var keys = _services.GetRequiredService<IKeyManager>();
        return ring.LockPlainKeys(encryptor, key => KeyRingLocking.ReadByDataProtection(keys, key));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        for (var attempt = 1; attempt <= Tries; attempt++)
        {
            try
            {
                await Task.Delay(attempt == 1 ? TimeSpan.FromSeconds(20) : RetryEvery, stoppingToken);
                var report = LockNow();
                if (report != null) Console.WriteLine(report.Line());
                return;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "681: the key ring could not be locked with Key Vault (try {Attempt} of {Tries}); the keys stay as they were and keep working", attempt, Tries);
            }
        }
    }
}
