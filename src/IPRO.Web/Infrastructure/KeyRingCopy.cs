using System.Text.RegularExpressions;
using System.Xml.Linq;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;

namespace IPRO.Web.Infrastructure;

// 635 (593's deferred item; built 2026-11-08 on the owner's word). The keys that protect two-step sign-in
// secrets and the Google Calendar tokens (ASP.NET Core Data Protection) have only ever been in one place:
// the folder Azure App Service gives every app for them, %HOME%/ASP.NET/DataProtection-Keys on the app's
// own storage (two files on ipro-prod-web on 2026-11-08, the newest made 2026-09-25). Lose that folder --
// the app deleted and made again, moved, its storage replaced -- and every adviser has to set up two-step
// and connect Google again, because nothing stored can be read.
//
// The folder stays where it is and stays the ring the app reads first: what it reads and writes there is
// unchanged. A second copy of every key goes to a private container in iPro's own storage account
// (data-protection-keys/<app name>/, the account keeps deleted files 30 days). Each time the ring is read
// (at start-up, then about once a day) a key in the folder the copy lacks is copied (the keys made before
// 635 included), and a key in the copy the folder lacks is put back into the folder and used. If storage
// cannot be reached the app goes on with the folder alone, as it always has, and logs a warning.
// Only on Azure App Service (KeyRingCopy.AzureKeyFolder), and only where a storage account is configured:
// nowhere else does anything change.
//
// 681 (audit 2026-11-24 F-5): a key in the copy the folder lacks is put back only when the folder holds no key at all
// (the lost folder this is for); beside keys it is a key nobody made here, so it is logged and never used. With Key
// Vault set (KeyRingVault), LockPlainKeys locks the keys made before it, in the folder and in the copy.
//
// 681, the app's own folder (2026-11-26). Azure's file tool for the app (Kudu, the scm site) is an ASP.NET Core app too
// and keeps its own key ring in Data Protection's default folder on the same storage, the one the portal used. Once the
// portal's keys there were locked (Kudu cannot reach the vault) Kudu made a plain key of its own beside them, which the
// portal took in as its newest key (staging, 2026-11-26 14:29 UTC). The portal's ring now lives in a folder nothing else
// uses (AzureKeyFolder); the first time, it takes the keys from the old shared folder (SharedKeyFolder) and leaves a note
// there so it never reads that folder again; its own keys left there are locked like the rest.
public interface IKeyCopyStore
{
    IReadOnlyList<(string Name, XElement Element)> ReadAll();

    // Never replaces a copy already there.
    void Write(string name, XElement element);

    // 681: the one write that replaces a copy (a plain key by the same key locked); earlier versions the storage
    // account keeps are deleted. Returns how many were.
    int Replace(string name, XElement element);
}

public static class KeyRingCopy
{
    public const string Container = "data-protection-keys";

    // The portal's own folder on Azure App Service, beside the one Data Protection uses by default: only when
    // WEBSITE_INSTANCE_ID and HOME are both set. Null anywhere else, and then nothing here is used.
    public static DirectoryInfo? AzureKeyFolder(Func<string, string?> environment) => HomeFolder(environment, "iPro-DataProtection-Keys");

    // The folder Data Protection itself uses on Azure App Service (DefaultKeyStorageDirectories), which Kudu uses too: the
    // portal's ring was there until 681's own folder, and is taken from it once.
    public static DirectoryInfo? SharedKeyFolder(Func<string, string?> environment) => HomeFolder(environment, "DataProtection-Keys");

    private static DirectoryInfo? HomeFolder(Func<string, string?> environment, string name)
    {
        if (string.IsNullOrEmpty(environment("WEBSITE_INSTANCE_ID"))) return null;
        var home = environment("HOME");
        return string.IsNullOrEmpty(home) ? null : new DirectoryInfo(Path.Combine(home, "ASP.NET", name));
    }

    // A key's file name is its friendly name (key-<guid>, revocation-<time>); nothing else is ever written
    // from a copy into the folder.
    private static readonly Regex SafeName = new("^[A-Za-z0-9][A-Za-z0-9-]{0,99}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static bool IsSafeName(string name) => SafeName.IsMatch(name);
}

public sealed class BlobKeyCopyStore : IKeyCopyStore
{
    private readonly BlobContainerClient _container;
    private readonly string _prefix;

    public BlobKeyCopyStore(BlobContainerClient container, string appName)
    {
        _container = container;
        _prefix = appName.Trim().Trim('/') + "/";
    }

    public IReadOnlyList<(string Name, XElement Element)> ReadAll()
    {
        _container.CreateIfNotExists(PublicAccessType.None);
        var found = new List<(string, XElement)>();
        foreach (var item in _container.GetBlobs(prefix: _prefix))
        {
            var name = item.Name[_prefix.Length..];
            if (!name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
            using var stream = _container.GetBlobClient(item.Name).OpenRead();
            found.Add((name[..^4], XElement.Load(stream)));
        }
        return found;
    }

    public void Write(string name, XElement element)
    {
        _container.CreateIfNotExists(PublicAccessType.None);
        using var stream = new MemoryStream();
        element.Save(stream);
        stream.Position = 0;
        try
        {
            _container.GetBlobClient(_prefix + name + ".xml").Upload(stream, overwrite: false);
        }
        catch (RequestFailedException e) when (e.Status == 409)
        {
            // Already copied.
        }
    }

    // Production's account keeps every earlier version of a file (and deleted ones 30 days): the plain key would
    // stay readable as an earlier version, so each is deleted (then gone after the account's 30 days).
    public int Replace(string name, XElement element)
    {
        _container.CreateIfNotExists(PublicAccessType.None);
        var blobName = _prefix + name + ".xml";
        var blob = _container.GetBlobClient(blobName);
        using (var stream = new MemoryStream())
        {
            element.Save(stream);
            stream.Position = 0;
            blob.Upload(stream, overwrite: true);
        }
        var deleted = 0;
        foreach (var item in _container.GetBlobs(BlobTraits.None, BlobStates.Version, blobName))
        {
            if (item.Name != blobName || string.IsNullOrEmpty(item.VersionId) || item.IsLatestVersion == true) continue;
            if (blob.WithVersion(item.VersionId).DeleteIfExists().Value) deleted++;
        }
        return deleted;
    }
}

public sealed class MirroredKeyRepository : IXmlRepository
{
    // Left in the shared folder once the portal's keys are taken from it (not *.xml: Data Protection never reads it).
    public const string MovedNote = "ipro-keys-moved-to-own-folder.txt";

    private readonly DirectoryInfo _folder;
    private readonly DirectoryInfo? _sharedFolder;
    private readonly FileSystemXmlRepository _folderRepository;
    private readonly IKeyCopyStore _copy;
    private readonly ILogger _log;
    private readonly object _gate = new();

    public MirroredKeyRepository(DirectoryInfo folder, IKeyCopyStore copy, ILoggerFactory loggerFactory, DirectoryInfo? sharedFolder = null)
    {
        _folder = folder;
        _sharedFolder = sharedFolder;
        _folderRepository = new FileSystemXmlRepository(folder, loggerFactory);
        _copy = copy;
        _log = loggerFactory.CreateLogger<MirroredKeyRepository>();
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        lock (_gate) return ReadRing();
    }

    private List<XElement> ReadRing()
    {
        var inFolder = TakeFromSharedFolderOnce(ReadFolder(_folder));
        var ring = inFolder.Select(f => f.Element).ToList();

        IReadOnlyList<(string Name, XElement Element)> copied;
        try
        {
            copied = _copy.ReadAll();
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "635: the second copy of the key ring could not be read; going on with the folder's {Count} keys", inFolder.Count);
            return ring;
        }

        foreach (var (name, element) in inFolder)
        {
            if (copied.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            TryCopy(name, element);
        }

        var folderHoldsKeys = inFolder.Any(f => f.Element.Name.LocalName == "key");
        foreach (var (name, element) in copied)
        {
            if (!KeyRingCopy.IsSafeName(name) || inFolder.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
            if (folderHoldsKeys)
            {
                _log.LogWarning("681: the second copy of the key ring holds {Name}, which the folder lacks; it is not used (only an empty folder is filled from the copy)", name);
                continue;
            }
            ring.Add(new XElement(element));
            try
            {
                _folderRepository.StoreElement(new XElement(element), name);
                _log.LogWarning("635: key {Name} was missing from the folder and was put back from the second copy", name);
            }
            catch (Exception e)
            {
                _log.LogWarning(e, "635: key {Name} is used from the second copy but could not be put back into the folder", name);
            }
        }
        return ring;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        lock (_gate)
        {
            _folderRepository.StoreElement(element, friendlyName);
            TryCopy(friendlyName, element);
        }
    }

    // 681: every key in the folder still holding its secret in plain XML is locked with the encryptor (Key Vault);
    // readKey (Data Protection's own reader) must read the locked key exactly as it reads the plain one before the
    // file is replaced; then each copy still plain (or missing) is replaced by the locked key. Stops at the first key
    // that does not read back the same, leaving it as it was. Run again, it finds nothing to do. A copy out of reach
    // is reported after the folder is done.
    public KeyLockReport LockPlainKeys(IXmlEncryptor encryptor, Func<XElement, XElement> readKey)
    {
        lock (_gate)
        {
            IReadOnlyList<(string Name, XElement Element)>? copied = null;
            Exception? copyProblem = null;
            try
            {
                copied = _copy.ReadAll();
            }
            catch (Exception e)
            {
                copyProblem = e;
            }

            int lockedNow = 0, alreadyLocked = 0, copiesReplaced = 0, versionsDeleted = 0;
            var own = TakeFromSharedFolderOnce(ReadFolder(_folder));
            foreach (var (name, element) in own)
            {
                if (element.Name.LocalName != "key") continue;   // a revocation holds no secret
                var locked = element;
                if (KeyRingLocking.HoldsPlainSecret(element))
                {
                    locked = KeyRingLocking.Lock(element, encryptor);
                    if (!KeyRingLocking.SameXml(readKey(locked), readKey(element)))
                        throw new InvalidOperationException($"681: key {name} did not read back the same through Key Vault; it was left as it was");
                    ReplaceInFolder(_folder, name, locked);
                    lockedNow++;
                }
                else
                {
                    alreadyLocked++;
                }

                if (copied == null) continue;
                var inCopy = copied.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)).Element;
                if (inCopy != null && !KeyRingLocking.HoldsPlainSecret(inCopy)) continue;
                versionsDeleted += _copy.Replace(name, new XElement(locked));
                copiesReplaced++;
            }

            // The portal's own keys still plain in the shared folder it took them from are locked there too (each read back
            // the same first). A key only Kudu has there is Kudu's: never touched.
            var sharedLocked = 0;
            if (_sharedFolder != null)
            {
                foreach (var (name, element) in ReadFolder(_sharedFolder))
                {
                    if (element.Name.LocalName != "key" || !KeyRingLocking.HoldsPlainSecret(element)) continue;
                    if (!own.Any(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
                    var locked = KeyRingLocking.Lock(element, encryptor);
                    if (!KeyRingLocking.SameXml(readKey(locked), readKey(element)))
                        throw new InvalidOperationException($"681: key {name} in the shared folder did not read back the same through Key Vault; it was left as it was");
                    ReplaceInFolder(_sharedFolder, name, locked);
                    sharedLocked++;
                }
            }

            if (copyProblem != null)
                throw new IOException("681: the folder's keys are locked, but the second copy could not be read to replace its plain keys", copyProblem);
            return new KeyLockReport(lockedNow, alreadyLocked, copiesReplaced, versionsDeleted, sharedLocked);
        }
    }

    // 681: the first time (no note in the shared folder yet), every key file in the shared folder the portal used before
    // is taken into its own folder (the ring exactly as it was, a key Kudu made there included), then the note is left
    // there; never again after that, so a key Kudu makes there later is never taken in, and a lost folder is filled from
    // the copy (635). A file already in the own folder is never replaced. If anything fails, the note is not left and it
    // is tried again at the next read.
    private List<(string Name, XElement Element)> TakeFromSharedFolderOnce(List<(string Name, XElement Element)> inFolder)
    {
        if (_sharedFolder == null) return inFolder;
        var note = Path.Combine(_sharedFolder.FullName, MovedNote);
        if (File.Exists(note)) return inFolder;
        try
        {
            var taken = 0;
            foreach (var (name, element) in ReadFolder(_sharedFolder))
            {
                if (!KeyRingCopy.IsSafeName(name) || inFolder.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
                if (WriteNewInFolder(_folder, name, element)) taken++;
            }
            Directory.CreateDirectory(_sharedFolder.FullName);
            File.WriteAllText(note,
                $"The iPro portal's Data Protection keys were taken from this folder on {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC into {_folder.FullName}, " +
                "which nothing else uses (TODO 681). The portal never takes keys from this folder again; its keys left here are locked " +
                "once Key Vault is set (Azure__DataProtectionKeyId). Other keys here are Kudu's. Do not delete this note: without it the " +
                "portal would take this folder's keys again.\n");
            Console.WriteLine($"[KeyRing] the key ring now lives in its own folder {_folder.FullName}: {taken} key file(s) taken from {_sharedFolder.FullName}, which Kudu uses too.");
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "681: the keys could not all be taken from {Shared} into the portal's own folder; tried again at the next read", _sharedFolder.FullName);
        }
        return ReadFolder(_folder);
    }

    // A new file only (written beside, then moved without replacing): false when the name is already there.
    private static bool WriteNewInFolder(DirectoryInfo folder, string name, XElement element)
    {
        Directory.CreateDirectory(folder.FullName);
        var final = Path.Combine(folder.FullName, name + ".xml");
        if (File.Exists(final)) return false;
        var temp = Path.Combine(folder.FullName, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = File.Create(temp)) element.Save(stream);
            File.Move(temp, final, overwrite: false);
            return true;
        }
        catch (IOException) when (File.Exists(final))
        {
            return false;   // another instance wrote it first
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    // Written beside it, then moved over it: a reader sees the old file or the new one, never half of one.
    private static void ReplaceInFolder(DirectoryInfo folder, string name, XElement element)
    {
        var final = Path.Combine(folder.FullName, name + ".xml");
        var temp = Path.Combine(folder.FullName, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = File.Create(temp)) element.Save(stream);
            File.Move(temp, final, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private void TryCopy(string name, XElement element)
    {
        try
        {
            _copy.Write(name, new XElement(element));
        }
        catch (Exception e)
        {
            _log.LogWarning(e, "635: key {Name} could not be copied to the second copy of the key ring", name);
        }
    }

    // What FileSystemXmlRepository reads (every *.xml in the folder), with each file's name.
    private static List<(string Name, XElement Element)> ReadFolder(DirectoryInfo folder)
    {
        folder.Refresh();
        if (!folder.Exists) return new List<(string, XElement)>();
        return folder.EnumerateFiles("*.xml", SearchOption.TopDirectoryOnly)
            .Select(f => (Path.GetFileNameWithoutExtension(f.Name), XElement.Load(f.FullName)))
            .ToList();
    }
}
