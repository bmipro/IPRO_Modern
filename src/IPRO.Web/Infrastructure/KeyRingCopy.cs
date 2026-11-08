using System.Text.RegularExpressions;
using System.Xml.Linq;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.AspNetCore.DataProtection.Repositories;

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
public interface IKeyCopyStore
{
    IReadOnlyList<(string Name, XElement Element)> ReadAll();

    // Never replaces a copy already there.
    void Write(string name, XElement element);
}

public static class KeyRingCopy
{
    public const string Container = "data-protection-keys";

    // The folder Data Protection itself uses on Azure App Service (DefaultKeyStorageDirectories): only when
    // WEBSITE_INSTANCE_ID and HOME are both set. Null anywhere else, and then nothing here is used.
    public static DirectoryInfo? AzureKeyFolder(Func<string, string?> environment)
    {
        if (string.IsNullOrEmpty(environment("WEBSITE_INSTANCE_ID"))) return null;
        var home = environment("HOME");
        return string.IsNullOrEmpty(home) ? null : new DirectoryInfo(Path.Combine(home, "ASP.NET", "DataProtection-Keys"));
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
}

public sealed class MirroredKeyRepository : IXmlRepository
{
    private readonly DirectoryInfo _folder;
    private readonly FileSystemXmlRepository _folderRepository;
    private readonly IKeyCopyStore _copy;
    private readonly ILogger _log;

    public MirroredKeyRepository(DirectoryInfo folder, IKeyCopyStore copy, ILoggerFactory loggerFactory)
    {
        _folder = folder;
        _folderRepository = new FileSystemXmlRepository(folder, loggerFactory);
        _copy = copy;
        _log = loggerFactory.CreateLogger<MirroredKeyRepository>();
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        var inFolder = ReadFolder();
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

        foreach (var (name, element) in copied)
        {
            if (!KeyRingCopy.IsSafeName(name) || inFolder.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
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
        _folderRepository.StoreElement(element, friendlyName);
        TryCopy(friendlyName, element);
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
    private List<(string Name, XElement Element)> ReadFolder()
    {
        _folder.Refresh();
        if (!_folder.Exists) return new List<(string, XElement)>();
        return _folder.EnumerateFiles("*.xml", SearchOption.TopDirectoryOnly)
            .Select(f => (Path.GetFileNameWithoutExtension(f.Name), XElement.Load(f.FullName)))
            .ToList();
    }
}
