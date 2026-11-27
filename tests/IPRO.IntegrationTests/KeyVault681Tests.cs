using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Azure.Core.Cryptography;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace IPRO.IntegrationTests;

// 681 (audit 2026-11-24 F-5). The key ring was plain XML in Azure's folder and in iPro's storage beside the dumps:
// one storage key read every two-step secret and Google token in a dump, and a key put into the copy was taken in
// and trusted. Now the keys are locked with a Key Vault key (here a stand-in vault holding an RSA key in memory: the
// same calls Key Vault answers, wrap and unwrap), the keys made before are locked after start-up, and the copy fills
// an empty folder only.
public class KeyVault681Tests : IDisposable
{
    private const string Purpose = "IPRO.Web.TwoStep.Secret.v1";
    private static readonly Uri VaultKey = new("https://ipro-test-keys.vault.azure.net/keys/dataprotection");
    private readonly DirectoryInfo _folder = new(Path.Combine(Path.GetTempPath(), "ipro681-" + Guid.NewGuid().ToString("N")[..12]));
    // Data Protection's default folder on App Service, which Kudu uses too (the portal's ring was there before).
    private readonly DirectoryInfo _shared = new(Path.Combine(Path.GetTempPath(), "ipro681s-" + Guid.NewGuid().ToString("N")[..12]));
    // Where a stand-in for Kudu makes its own key.
    private readonly DirectoryInfo _kudu = new(Path.Combine(Path.GetTempPath(), "ipro681k-" + Guid.NewGuid().ToString("N")[..12]));

    public void Dispose()
    {
        foreach (var folder in new[] { _folder, _shared, _kudu })
        {
            folder.Refresh();
            if (folder.Exists) folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void Only_a_Key_Vault_key_address_turns_it_on()
    {
        Assert.Equal(VaultKey, KeyRingVault.KeyUri("https://ipro-test-keys.vault.azure.net/keys/dataprotection"));
        Assert.NotNull(KeyRingVault.KeyUri(" https://ipro-test-keys.vault.azure.net/keys/dataprotection/0123456789abcdef "));
        Assert.Null(KeyRingVault.KeyUri(null));
        Assert.Null(KeyRingVault.KeyUri(""));
        Assert.Null(KeyRingVault.KeyUri("http://ipro-test-keys.vault.azure.net/keys/dataprotection"));     // not https
        Assert.Null(KeyRingVault.KeyUri("https://example.com/keys/dataprotection"));                       // not a vault
        Assert.Null(KeyRingVault.KeyUri("https://ipro-test-keys.vault.azure.net/secrets/dataprotection")); // a secret, not a key
        Assert.Null(KeyRingVault.KeyUri("https://ipro-test-keys.vault.azure.net/keys/"));
        Assert.Equal("Azure:DataProtectionKeyId", KeyRingVault.Setting);
    }

    [Fact]
    public void Beside_keys_a_key_only_in_the_copy_is_never_used()
    {
        var copy = new MemoryCopy();
        new Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository(_folder, NullLoggerFactory.Instance)
            .StoreElement(Key("11111111-1111-1111-1111-111111111111"), "key-11111111-1111-1111-1111-111111111111");
        copy.Put("key-11111111-1111-1111-1111-111111111111", Key("11111111-1111-1111-1111-111111111111"));
        copy.Put("key-99999999-9999-9999-9999-999999999999", Key("99999999-9999-9999-9999-999999999999"));   // nobody here made it

        var ring = Repository(copy).GetAllElements();

        Assert.Equal(new[] { "11111111-1111-1111-1111-111111111111" }, ring.Select(k => k.Attribute("id")!.Value).ToArray());
        Assert.False(File.Exists(Path.Combine(_folder.FullName, "key-99999999-9999-9999-9999-999999999999.xml")));
    }

    [Fact]
    public void The_keys_made_before_the_vault_are_locked_and_what_they_sealed_still_opens()
    {
        var copy = new MemoryCopy();
        var vault = new StandInVault();
        string sealedText;
        using (var before = Provider(copy, keyUri: null, vault))
            sealedText = before.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect("JBSWY3DPEHPK3PXP");
        var keyFile = Directory.GetFiles(_folder.FullName, "key-*.xml").Single();
        var masterKey = PlainMasterKey(File.ReadAllText(keyFile));
        Assert.Contains(masterKey, copy.Text().Single());   // plain in the copy too, as before 681

        using (var on = Provider(copy, VaultKey, vault))
        {
            var report = LockService(on).LockNow()!;
            Assert.Equal(new KeyLockReport(LockedNow: 1, AlreadyLocked: 0, CopiesReplaced: 1, OldVersionsDeleted: 0), report);
            Assert.StartsWith("[KeyRing] locked with Key Vault: 1 key(s) locked now", report.Line());
        }

        var locked = File.ReadAllText(keyFile);
        Assert.DoesNotContain(masterKey, locked);
        Assert.Contains("encryptedSecret", locked);
        Assert.DoesNotContain("requiresEncryption", locked);
        Assert.DoesNotContain(masterKey, copy.Text().Single());
        Assert.Empty(Directory.GetFiles(_folder.FullName, "*.tmp"));

        var unwrapsBefore = vault.Unwraps;
        using (var after = Provider(copy, VaultKey, vault))
            Assert.Equal("JBSWY3DPEHPK3PXP", after.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Unprotect(sealedText));
        Assert.True(vault.Unwraps > unwrapsBefore);   // read through the vault, not from plain XML

        // The setting taken away later: nothing new is locked, but the locked keys still open.
        using (var settingRemoved = Provider(copy, keyUri: null, vault))
            Assert.Equal("JBSWY3DPEHPK3PXP", settingRemoved.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Unprotect(sealedText));

        // Run again: nothing left to do.
        using var again = Provider(copy, VaultKey, vault);
        Assert.Equal(new KeyLockReport(0, 1, 0, 0), LockService(again).LockNow());
    }

    [Fact]
    public void A_key_made_after_the_vault_is_set_is_locked_as_it_is_made()
    {
        var copy = new MemoryCopy();
        var vault = new StandInVault();
        using (var on = Provider(copy, VaultKey, vault))
            on.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect("a Google refresh token");

        var made = File.ReadAllText(Directory.GetFiles(_folder.FullName, "key-*.xml").Single());
        Assert.Contains("encryptedSecret", made);
        Assert.DoesNotContain("requiresEncryption", made);
        Assert.True(vault.Wraps > 0);
        Assert.All(copy.Text(), text => Assert.Contains("encryptedSecret", text));
    }

    [Fact]
    public void A_key_that_does_not_read_back_the_same_is_left_as_it_was()
    {
        var copy = new MemoryCopy();
        var vault = new StandInVault();
        using (var before = Provider(copy, keyUri: null, vault))
            before.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect("JBSWY3DPEHPK3PXP");
        var keyFile = Directory.GetFiles(_folder.FullName, "key-*.xml").Single();
        var plain = File.ReadAllText(keyFile);

        using var on = Provider(copy, VaultKey, vault);
        var encryptor = on.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlEncryptor!;
        // A reader that sees something else in the locked key than in the plain one (a vault answering wrongly).
        Assert.Throws<InvalidOperationException>(() =>
            Repository(copy).LockPlainKeys(encryptor, key => key.ToString().Contains("encryptedSecret") ? new XElement("descriptor", "not the key") : new XElement(key)));

        Assert.Equal(plain, File.ReadAllText(keyFile));
        Assert.Equal(0, copy.Replaces);
    }

    [Fact]
    public void A_plain_copy_out_of_reach_at_first_is_replaced_on_the_next_try()
    {
        var copy = new MemoryCopy();
        var vault = new StandInVault();
        using (var before = Provider(copy, keyUri: null, vault))
            before.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect("JBSWY3DPEHPK3PXP");
        var masterKey = PlainMasterKey(File.ReadAllText(Directory.GetFiles(_folder.FullName, "key-*.xml").Single()));

        using var on = Provider(copy, VaultKey, vault);
        copy.Broken = true;
        Assert.Throws<IOException>(() => LockService(on).LockNow());
        Assert.DoesNotContain(masterKey, File.ReadAllText(Directory.GetFiles(_folder.FullName, "key-*.xml").Single()));   // the folder is done

        copy.Broken = false;
        Assert.Equal(new KeyLockReport(0, 1, 1, 0), LockService(on).LockNow());
        Assert.DoesNotContain(masterKey, copy.Text().Single());
    }

    // Staging, 2026-11-26: Kudu shares Data Protection's default folder; it could not open the locked keys, made a plain one
    // of its own there, and the portal took it in as its newest key. The portal's ring now lives in its own folder.
    [Fact]
    public void The_keys_are_taken_from_the_shared_folder_once_and_a_key_Kudu_makes_there_later_is_never_used()
    {
        var copy = new MemoryCopy();
        var shared = new Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository(_shared, NullLoggerFactory.Instance);
        shared.StoreElement(Key("11111111-1111-1111-1111-111111111111"), "key-11111111-1111-1111-1111-111111111111");
        shared.StoreElement(Key("22222222-2222-2222-2222-222222222222"), "key-22222222-2222-2222-2222-222222222222");

        var ring = Repository(copy, _shared).GetAllElements();

        Assert.Equal(new[] { "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222" }, Ids(ring));
        Assert.True(File.Exists(Path.Combine(_folder.FullName, "key-11111111-1111-1111-1111-111111111111.xml")));
        Assert.True(File.Exists(Path.Combine(_folder.FullName, "key-22222222-2222-2222-2222-222222222222.xml")));
        Assert.True(File.Exists(Path.Combine(_shared.FullName, MirroredKeyRepository.MovedNote)));
        Assert.Equal(2, Directory.GetFiles(_shared.FullName, "key-*.xml").Length);   // nothing taken away from Kudu's folder

        // Kudu makes a key of its own there later: never read.
        shared.StoreElement(Key("99999999-9999-9999-9999-999999999999"), "key-99999999-9999-9999-9999-999999999999");
        Assert.Equal(new[] { "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222" }, Ids(Repository(copy, _shared).GetAllElements()));
        Assert.False(File.Exists(Path.Combine(_folder.FullName, "key-99999999-9999-9999-9999-999999999999.xml")));

        // The own folder lost later: filled from the copy (635), never from the shared folder again.
        _folder.Delete(recursive: true);
        Assert.Equal(new[] { "11111111-1111-1111-1111-111111111111", "22222222-2222-2222-2222-222222222222" }, Ids(Repository(copy, _shared).GetAllElements()));
        Assert.False(File.Exists(Path.Combine(_folder.FullName, "key-99999999-9999-9999-9999-999999999999.xml")));
    }

    [Fact]
    public void The_portals_keys_left_in_the_shared_folder_are_locked_there_and_Kudus_own_key_is_left_alone()
    {
        var copy = new MemoryCopy();
        var vault = new StandInVault();
        string sealedText;
        using (var before = PlainProvider(_shared, "ipro681-test"))   // the portal before its own folder
            sealedText = before.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect("JBSWY3DPEHPK3PXP");
        var ours = Directory.GetFiles(_shared.FullName, "key-*.xml").Single();
        var masterKey = PlainMasterKey(File.ReadAllText(ours));

        Assert.Single(Repository(copy, _shared).GetAllElements());   // taken into the own folder

        using (var kudu = PlainProvider(_kudu, "kudu"))
            kudu.GetRequiredService<IDataProtectionProvider>().CreateProtector("Kudu").Protect("Kudu's own");
        var kudus = Path.Combine(_shared.FullName, Path.GetFileName(Directory.GetFiles(_kudu.FullName, "key-*.xml").Single()));
        File.Copy(Directory.GetFiles(_kudu.FullName, "key-*.xml").Single(), kudus);   // Kudu's key, made after the move
        var kudusText = File.ReadAllText(kudus);

        using (var on = Provider(copy, VaultKey, vault, _shared))
        {
            var report = LockService(on).LockNow()!;
            Assert.Equal(new KeyLockReport(LockedNow: 1, AlreadyLocked: 0, CopiesReplaced: 1, OldVersionsDeleted: 0, SharedFolderLocked: 1), report);
            Assert.EndsWith("; 1 left in the shared folder locked.", report.Line());
        }

        Assert.DoesNotContain(masterKey, File.ReadAllText(ours));                                              // locked where it was left
        Assert.Contains("encryptedSecret", File.ReadAllText(ours));
        Assert.DoesNotContain(masterKey, File.ReadAllText(Path.Combine(_folder.FullName, Path.GetFileName(ours))));
        Assert.DoesNotContain(masterKey, copy.Text().Single());
        Assert.Equal(kudusText, File.ReadAllText(kudus));                                                     // Kudu's, untouched
        Assert.Empty(Directory.GetFiles(_shared.FullName, "*.tmp"));

        using var after = Provider(copy, VaultKey, vault, _shared);
        Assert.Equal("JBSWY3DPEHPK3PXP", after.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Unprotect(sealedText));
        Assert.Equal(new KeyLockReport(0, 1, 0, 0, 0), LockService(after).LockNow());   // nothing left to do
    }

    // ---- 683, the whole-code audit of 2026-11-26 -------------------------------------------------

    // KEY-1. The app is deleted and made again: its disk is new, and Kudu, which starts first, has already made a key in
    // the shared folder. Before, that key was taken in, the folder then "held keys", and the real ones in the copy were
    // refused: everything sealed before was lost in the one disaster the copy is for.
    [Fact]
    public void A_rebuilt_app_gets_its_keys_back_from_the_copy_and_never_takes_Kudus_key()
    {
        var copy = new MemoryCopy();
        var vault = new StandInVault();
        string sealedText;
        using (var before = Provider(copy, keyUri: null, vault, _shared))
            sealedText = before.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect("JBSWY3DPEHPK3PXP");
        var ours = Path.GetFileName(Directory.GetFiles(_folder.FullName, "key-*.xml").Single());

        _folder.Delete(recursive: true);
        using (var kudu = PlainProvider(_shared, "kudu"))
            kudu.GetRequiredService<IDataProtectionProvider>().CreateProtector("Kudu").Protect("Kudu's own");
        var kudus = Path.GetFileName(Directory.GetFiles(_shared.FullName, "key-*.xml").Single());

        using (var after = Provider(copy, keyUri: null, vault, _shared))
            Assert.Equal("JBSWY3DPEHPK3PXP", after.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Unprotect(sealedText));

        Assert.Equal(new[] { ours }, Directory.GetFiles(_folder.FullName, "key-*.xml").Select(Path.GetFileName).ToArray());
        Assert.False(File.Exists(Path.Combine(_folder.FullName, kudus)));
        Assert.Equal(new[] { Path.GetFileNameWithoutExtension(ours) }, copy.Names());
        Assert.False(File.Exists(Path.Combine(_shared.FullName, MirroredKeyRepository.MovedNote)));   // nothing was taken, so no note
    }

    // KEY-1, the other half. The folder is lost while storage cannot be reached. Before, a new key was made, and once
    // storage was back the real keys were refused beside it. Now the read fails, nothing is made, and the real keys
    // come back in the same process as soon as storage answers.
    [Fact]
    public void A_lost_folder_with_storage_out_of_reach_makes_no_new_key_and_the_real_ones_come_back_later()
    {
        var copy = new MemoryCopy();
        var vault = new StandInVault();
        string sealedText;
        using (var before = Provider(copy, keyUri: null, vault, _shared))
            sealedText = before.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect("JBSWY3DPEHPK3PXP");
        var ours = Path.GetFileName(Directory.GetFiles(_folder.FullName, "key-*.xml").Single());

        _folder.Delete(recursive: true);
        using (var kudu = PlainProvider(_shared, "kudu"))
            kudu.GetRequiredService<IDataProtectionProvider>().CreateProtector("Kudu").Protect("Kudu's own");
        copy.Broken = true;

        Assert.Throws<IOException>(() => Repository(copy, _shared).GetAllElements());

        using var during = Provider(copy, keyUri: null, vault, _shared);
        var protector = during.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose);
        Assert.ThrowsAny<Exception>(() => protector.Protect("a secret sealed with a key nobody else has"));
        _folder.Refresh();
        Assert.True(!_folder.Exists || Directory.GetFiles(_folder.FullName, "*.xml").Length == 0, "no key was made, and Kudu's was not taken");

        copy.Broken = false;
        Assert.Equal("JBSWY3DPEHPK3PXP", protector.Unprotect(sealedText));
        Assert.Equal(new[] { ours }, Directory.GetFiles(_folder.FullName, "key-*.xml").Select(Path.GetFileName).ToArray());
        Assert.Equal(new[] { Path.GetFileNameWithoutExtension(ours) }, copy.Names());
    }

    // KEY-1: revocations beside the keys come back with them.
    [Fact]
    public void A_lost_folder_gets_everything_the_copy_holds_and_an_app_with_no_copy_still_starts()
    {
        var copy = new MemoryCopy();
        copy.Put("key-11111111-1111-1111-1111-111111111111", Key("11111111-1111-1111-1111-111111111111"));
        copy.Put("revocation-20261127T0000000Z", new XElement("revocation", new XAttribute("version", "1"), new XElement("revocationDate", "2026-11-27T00:00:00Z")));

        var ring = Repository(copy, _shared).GetAllElements();
        Assert.Equal(new[] { "key", "revocation" }, ring.Select(e => e.Name.LocalName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.True(File.Exists(Path.Combine(_folder.FullName, "revocation-20261127T0000000Z.xml")));

        // A new app: nothing in its folder, in its copy or in the shared folder. The ring is empty and a key is made as ever.
        _folder.Delete(recursive: true);
        var fresh = new MemoryCopy();
        Assert.Empty(Repository(fresh, _shared).GetAllElements());
        using var first = Provider(fresh, keyUri: null, new StandInVault(), _shared);
        var sealedText = first.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect("JBSWY3DPEHPK3PXP");
        Assert.Equal("JBSWY3DPEHPK3PXP", first.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Unprotect(sealedText));
        Assert.Single(fresh.Names());
    }

    // KEY-5. The note in the shared folder was the only record that the portal had moved out; someone tidying up deletes
    // it, and every key Kudu made since came in, unlocked. Nothing depends on the note now.
    [Fact]
    public void With_the_note_deleted_a_key_Kudu_made_is_still_never_taken()
    {
        var copy = new MemoryCopy();
        var shared = new Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository(_shared, NullLoggerFactory.Instance);
        shared.StoreElement(Key("11111111-1111-1111-1111-111111111111"), "key-11111111-1111-1111-1111-111111111111");
        Assert.Single(Repository(copy, _shared).GetAllElements());   // the move, as on 2026-11-26
        var note = Path.Combine(_shared.FullName, MirroredKeyRepository.MovedNote);
        Assert.True(File.Exists(note));

        File.Delete(note);
        shared.StoreElement(Key("99999999-9999-9999-9999-999999999999"), "key-99999999-9999-9999-9999-999999999999");

        Assert.Equal(new[] { "11111111-1111-1111-1111-111111111111" }, Ids(Repository(copy, _shared).GetAllElements()));
        Assert.False(File.Exists(Path.Combine(_folder.FullName, "key-99999999-9999-9999-9999-999999999999.xml")));

        // And with the own folder lost as well: the copy, never the shared folder.
        _folder.Delete(recursive: true);
        Assert.Equal(new[] { "11111111-1111-1111-1111-111111111111" }, Ids(Repository(copy, _shared).GetAllElements()));
        Assert.Equal(new[] { "key-11111111-1111-1111-1111-111111111111" }, copy.Names());
    }

    // KEY-4. Locking a copy uploads the locked key and then deletes the versions before it. Cut short in between, the
    // next pass saw a locked copy and never came back for them: a plain earlier version stayed in storage.
    [Fact]
    public void Every_lock_pass_deletes_the_earlier_versions_of_a_copy_already_locked()
    {
        var copy = new MemoryCopy();
        var vault = new StandInVault();
        using (var before = Provider(copy, keyUri: null, vault))
            before.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect("JBSWY3DPEHPK3PXP");
        var name = copy.Names().Single();

        using var on = Provider(copy, VaultKey, vault);
        Assert.Equal(new KeyLockReport(1, 0, 1, 0), LockService(on).LockNow());

        copy.EarlierVersions[name] = 1;   // as if that pass had stopped after the upload
        var sweeps = copy.Sweeps;
        var second = LockService(on).LockNow()!;
        Assert.Equal(new KeyLockReport(LockedNow: 0, AlreadyLocked: 1, CopiesReplaced: 0, OldVersionsDeleted: 1), second);
        Assert.Equal(sweeps + 1, copy.Sweeps);
        Assert.Equal(new KeyLockReport(0, 1, 0, 0), LockService(on).LockNow());

        // The sweep out of reach is said, not swallowed: the service tries again.
        copy.EarlierVersions[name] = 1;
        copy.SweepBroken = true;
        Assert.Throws<IOException>(() => LockService(on).LockNow());
        copy.SweepBroken = false;
        Assert.Equal(new KeyLockReport(0, 1, 0, 1), LockService(on).LockNow());
    }

    // KEY-7. The shared folder is Kudu's too: one damaged file there stopped every try after the portal's own folder
    // was done, and the portal's keys left there stayed plain.
    [Fact]
    public void A_damaged_file_in_the_shared_folder_stops_neither_the_move_nor_the_lock()
    {
        var copy = new MemoryCopy();
        var vault = new StandInVault();
        using (var before = PlainProvider(_shared, "ipro681-test"))
            before.GetRequiredService<IDataProtectionProvider>().CreateProtector(Purpose).Protect("JBSWY3DPEHPK3PXP");
        var ours = Directory.GetFiles(_shared.FullName, "key-*.xml").Single();
        var masterKey = PlainMasterKey(File.ReadAllText(ours));
        var damaged = Path.Combine(_shared.FullName, "key-damaged.xml");
        File.WriteAllText(damaged, "<key id=\"cut short");

        Assert.Single(Repository(copy, _shared).GetAllElements());   // the move goes past it

        using var on = Provider(copy, VaultKey, vault, _shared);
        Assert.Equal(new KeyLockReport(LockedNow: 1, AlreadyLocked: 0, CopiesReplaced: 1, OldVersionsDeleted: 0, SharedFolderLocked: 1), LockService(on).LockNow());
        Assert.DoesNotContain(masterKey, File.ReadAllText(ours));
        Assert.Equal("<key id=\"cut short", File.ReadAllText(damaged));   // not ours: left as it was
        Assert.False(File.Exists(Path.Combine(_folder.FullName, "key-damaged.xml")));
    }

    [Fact]
    public void The_lock_runs_only_where_a_vault_key_is_set_and_the_reader_is_there_either_way()
    {
        var off = new ServiceCollection();
        KeyRingVault.Use(off.AddDataProtection(), keyUri: null, new StandInVault());
        Assert.DoesNotContain(off, d => d.ImplementationType == typeof(KeyRingLockService));
        Assert.Contains(off, d => d.ServiceType == typeof(IKeyEncryptionKeyResolver));

        var on = new ServiceCollection();
        KeyRingVault.Use(on.AddDataProtection(), VaultKey, new StandInVault());
        Assert.Contains(on, d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(KeyRingLockService));
    }

    [Fact]
    public void The_portal_reaches_the_vault_with_its_own_Azure_identity_on_App_Service_only()
    {
        var program = Read(@"src\IPRO.Web\Program.cs");
        var start = program.IndexOf("if (keyFolder != null && !string.IsNullOrWhiteSpace(keyCopyName) && !string.IsNullOrWhiteSpace(keyCopyStorage))", StringComparison.Ordinal);
        var use = program.IndexOf("IPRO.Web.Infrastructure.KeyRingVault.Use(dataProtection, keyVaultKey,", StringComparison.Ordinal);
        Assert.True(start > 0 && use > start, "the vault is set inside the App Service branch");
        Assert.Contains("new Azure.Security.KeyVault.Keys.Cryptography.KeyResolver(new Azure.Identity.ManagedIdentityCredential(Azure.Identity.ManagedIdentityId.SystemAssigned))", program);
        Assert.Contains("var keyVaultSetting = builder.Configuration[IPRO.Web.Infrastructure.KeyRingVault.Setting];", program);
        // No password or connection string for the vault anywhere: the identity is the only way in.
        Assert.DoesNotContain("ClientSecretCredential", program);
        Assert.DoesNotContain("DefaultAzureCredential", program);
        // The ring in the portal's own folder; the shared one only handed over.
        Assert.Contains("var sharedKeyFolder = IPRO.Web.Infrastructure.KeyRingCopy.SharedKeyFolder(Environment.GetEnvironmentVariable);", program);
        Assert.Contains("            loggers,\n            sharedKeyFolder));", program);
    }

    // ---- harness ------------------------------------------------------------------------------

    private MirroredKeyRepository Repository(IKeyCopyStore copy, DirectoryInfo? shared = null) => new(_folder, copy, NullLoggerFactory.Instance, shared);

    private ServiceProvider Provider(IKeyCopyStore copy, Uri? keyUri, IKeyEncryptionKeyResolver vault, DirectoryInfo? shared = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var dataProtection = services.AddDataProtection().SetApplicationName("ipro681-test");
        KeyRingVault.Use(dataProtection, keyUri, vault);
        services.AddOptions<KeyManagementOptions>().Configure(o => o.XmlRepository = Repository(copy, shared));
        return services.BuildServiceProvider();
    }

    // Data Protection alone on one folder, nothing locked: the portal before its own folder, or Kudu.
    private static ServiceProvider PlainProvider(DirectoryInfo folder, string applicationName)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().SetApplicationName(applicationName).PersistKeysToFileSystem(folder);
        return services.BuildServiceProvider();
    }

    private static string[] Ids(IEnumerable<XElement> ring) => ring.Select(k => k.Attribute("id")!.Value).OrderBy(id => id, StringComparer.Ordinal).ToArray();

    private static KeyRingLockService LockService(IServiceProvider services) => ActivatorUtilities.CreateInstance<KeyRingLockService>(services);

    private static string PlainMasterKey(string keyXml)
    {
        var value = XElement.Parse(keyXml).Descendants("masterKey").Single().Element("value")!.Value;
        Assert.True(value.Length > 40);
        return value;
    }

    private static XElement Key(string id) =>
        new("key", new XAttribute("id", id), new XAttribute("version", "1"), new XElement("creationDate", "2026-11-25T17:00:00Z"));

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative)).Replace("\r\n", "\n");
    }

    // Wrap and unwrap with an RSA key held in memory: what Key Vault does with the key it never lets out.
    private sealed class StandInVault : IKeyEncryptionKeyResolver, IKeyEncryptionKey
    {
        private readonly RSA _rsa = RSA.Create(2048);
        public int Wraps { get; private set; }
        public int Unwraps { get; private set; }
        public string KeyId => VaultKey + "/0123456789abcdef";

        public IKeyEncryptionKey Resolve(string keyId, CancellationToken cancellationToken = default) => this;
        public Task<IKeyEncryptionKey> ResolveAsync(string keyId, CancellationToken cancellationToken = default) => Task.FromResult<IKeyEncryptionKey>(this);

        public byte[] WrapKey(string algorithm, ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default)
        {
            Wraps++;
            return _rsa.Encrypt(key.ToArray(), RSAEncryptionPadding.OaepSHA1);
        }

        public Task<byte[]> WrapKeyAsync(string algorithm, ReadOnlyMemory<byte> key, CancellationToken cancellationToken = default) =>
            Task.FromResult(WrapKey(algorithm, key, cancellationToken));

        public byte[] UnwrapKey(string algorithm, ReadOnlyMemory<byte> encryptedKey, CancellationToken cancellationToken = default)
        {
            Unwraps++;
            return _rsa.Decrypt(encryptedKey.ToArray(), RSAEncryptionPadding.OaepSHA1);
        }

        public Task<byte[]> UnwrapKeyAsync(string algorithm, ReadOnlyMemory<byte> encryptedKey, CancellationToken cancellationToken = default) =>
            Task.FromResult(UnwrapKey(algorithm, encryptedKey, cancellationToken));
    }

    private sealed class MemoryCopy : IKeyCopyStore
    {
        private readonly Dictionary<string, string> _items = new(StringComparer.OrdinalIgnoreCase);
        public bool Broken { get; set; }
        public int Replaces { get; private set; }
        public void Put(string name, XElement element) => _items[name] = element.ToString();
        public string[] Text() => _items.Values.ToArray();

        public IReadOnlyList<(string Name, XElement Element)> ReadAll()
        {
            if (Broken) throw new IOException("storage out of reach");
            return _items.Select(kv => (kv.Key, XElement.Parse(kv.Value))).ToList();
        }

        public void Write(string name, XElement element)
        {
            if (Broken) throw new IOException("storage out of reach");
            if (!_items.ContainsKey(name)) _items[name] = element.ToString();
        }

        public int Replace(string name, XElement element)
        {
            if (Broken) throw new IOException("storage out of reach");
            _items[name] = element.ToString();
            Replaces++;
            return DeleteEarlierVersions(name);
        }

        // 683 (KEY-4): the earlier versions the storage account keeps of one copy, here a number a test sets.
        public Dictionary<string, int> EarlierVersions { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool SweepBroken { get; set; }
        public int Sweeps { get; private set; }
        public string[] Names() => _items.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

        public int DeleteEarlierVersions(string name)
        {
            if (Broken || SweepBroken) throw new IOException("storage out of reach");
            Sweeps++;
            return EarlierVersions.Remove(name, out var left) ? left : 0;
        }
    }
}
