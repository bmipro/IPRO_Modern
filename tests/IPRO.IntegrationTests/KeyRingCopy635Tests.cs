using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using IPRO.Web.Infrastructure;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace IPRO.IntegrationTests;

// 635. The Data Protection key ring (two-step secrets, Google tokens) lived only in Azure's folder on the
// app's own storage. The folder stays the ring read first; a second copy of every key now goes to iPro's
// storage, and whichever side lacks a key gets it from the other.
public class KeyRingCopy635Tests : IDisposable
{
    private readonly DirectoryInfo _folder = new(Path.Combine(Path.GetTempPath(), "ipro635-" + Guid.NewGuid().ToString("N")[..12]));

    public void Dispose()
    {
        _folder.Refresh();
        if (_folder.Exists) _folder.Delete(recursive: true);
    }

    [Fact]
    public void The_folder_is_the_one_Data_Protection_uses_on_App_Service_and_nothing_elsewhere()
    {
        var onAzure = KeyRingCopy.AzureKeyFolder(name => name switch { "WEBSITE_INSTANCE_ID" => "abc123", "HOME" => "/home", _ => null });
        Assert.NotNull(onAzure);
        Assert.Equal(Path.Combine("/home", "ASP.NET", "DataProtection-Keys"), onAzure!.ToString());
        Assert.Null(KeyRingCopy.AzureKeyFolder(name => name == "HOME" ? "/home" : null));               // not App Service
        Assert.Null(KeyRingCopy.AzureKeyFolder(name => name == "WEBSITE_INSTANCE_ID" ? "abc123" : null)); // no HOME
        Assert.Null(KeyRingCopy.AzureKeyFolder(_ => null));
    }

    [Fact]
    public void Keys_made_before_635_are_copied_and_the_ring_read_is_the_folders()
    {
        var folderOnly = new FileSystemOnly(_folder);
        folderOnly.Store(Key("11111111-1111-1111-1111-111111111111"), "key-11111111-1111-1111-1111-111111111111");
        folderOnly.Store(Key("22222222-2222-2222-2222-222222222222"), "key-22222222-2222-2222-2222-222222222222");
        var copy = new MemoryCopy();

        var ring = Repository(copy).GetAllElements();

        Assert.Equal(2, ring.Count);
        Assert.Equal(new[] { "key-11111111-1111-1111-1111-111111111111", "key-22222222-2222-2222-2222-222222222222" }, copy.Names());
        // Read again: nothing is copied twice.
        Assert.Equal(2, Repository(copy).GetAllElements().Count);
        Assert.Equal(2, copy.Writes);
    }

    [Fact]
    public void A_lost_folder_gets_its_keys_back_from_the_copy()
    {
        var copy = new MemoryCopy();
        copy.Put("key-33333333-3333-3333-3333-333333333333", Key("33333333-3333-3333-3333-333333333333"));

        var ring = Repository(copy).GetAllElements();

        Assert.Single(ring);
        Assert.Equal("33333333-3333-3333-3333-333333333333", ring.Single().Attribute("id")!.Value);
        Assert.True(File.Exists(Path.Combine(_folder.FullName, "key-33333333-3333-3333-3333-333333333333.xml")));
        Assert.Equal(0, copy.Writes);
    }

    [Fact]
    public void A_name_that_is_no_key_name_is_never_written_into_the_folder()
    {
        var copy = new MemoryCopy();
        copy.Put("../escape", Key("44444444-4444-4444-4444-444444444444"));
        copy.Put("key with spaces", Key("55555555-5555-5555-5555-555555555555"));

        Assert.Empty(Repository(copy).GetAllElements());
        _folder.Refresh();
        Assert.False(_folder.Exists && _folder.EnumerateFiles("*", SearchOption.AllDirectories).Any());
        Assert.True(KeyRingCopy.IsSafeName("key-64699a96-8725-4c2c-b3be-067426303fa0"));
        Assert.True(KeyRingCopy.IsSafeName("revocation-20261108T170000Z"));
        Assert.False(KeyRingCopy.IsSafeName("../escape"));
        Assert.False(KeyRingCopy.IsSafeName(""));
    }

    [Fact]
    public void Storage_out_of_reach_changes_nothing_the_app_does()
    {
        var folderOnly = new FileSystemOnly(_folder);
        folderOnly.Store(Key("66666666-6666-6666-6666-666666666666"), "key-66666666-6666-6666-6666-666666666666");
        var broken = new MemoryCopy { Broken = true };
        var repository = Repository(broken);

        Assert.Single(repository.GetAllElements());
        repository.StoreElement(Key("77777777-7777-7777-7777-777777777777"), "key-77777777-7777-7777-7777-777777777777");
        Assert.True(File.Exists(Path.Combine(_folder.FullName, "key-77777777-7777-7777-7777-777777777777.xml")));
        Assert.Equal(2, repository.GetAllElements().Count);
    }

    [Fact]
    public void A_new_key_goes_to_the_folder_and_the_copy()
    {
        var copy = new MemoryCopy();
        Repository(copy).StoreElement(Key("88888888-8888-8888-8888-888888888888"), "key-88888888-8888-8888-8888-888888888888");
        Assert.True(File.Exists(Path.Combine(_folder.FullName, "key-88888888-8888-8888-8888-888888888888.xml")));
        Assert.Equal(new[] { "key-88888888-8888-8888-8888-888888888888" }, copy.Names());
    }

    // The point of it: something protected before the folder was lost can still be read after.
    [Fact]
    public void A_secret_protected_before_the_folder_was_lost_reads_back_after()
    {
        var copy = new MemoryCopy();
        string sealedText;
        using (var before = Provider(copy))
            sealedText = before.GetRequiredService<IDataProtectionProvider>().CreateProtector("IPRO.Web.GoogleCalendar.Tokens.v1").Protect("a Google refresh token");
        Assert.NotEmpty(copy.Names());

        _folder.Delete(recursive: true);   // the app's storage lost

        using var after = Provider(copy);
        Assert.Equal("a Google refresh token",
            after.GetRequiredService<IDataProtectionProvider>().CreateProtector("IPRO.Web.GoogleCalendar.Tokens.v1").Unprotect(sealedText));
        Assert.NotEmpty(Directory.GetFiles(_folder.FullName, "key-*.xml"));
    }

    [Fact]
    public void The_portal_uses_it_on_App_Service_only_and_keeps_Azures_folder()
    {
        var program = Read(@"src\IPRO.Web\Program.cs");
        Assert.Contains("var keyFolder = IPRO.Web.Infrastructure.KeyRingCopy.AzureKeyFolder(Environment.GetEnvironmentVariable);", program);
        Assert.Contains("if (keyFolder != null && !string.IsNullOrWhiteSpace(keyCopyName) && !string.IsNullOrWhiteSpace(keyCopyStorage))", program);
        Assert.Contains("var keyCopyName = Environment.GetEnvironmentVariable(\"WEBSITE_SITE_NAME\");", program);
        // Nowhere else does the ring move: no other store, no new application name.
        Assert.DoesNotContain("PersistKeysTo", program);
        Assert.DoesNotContain("SetApplicationName", program);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(program, @"options\.XmlRepository = "));
    }

    // ---- harness ------------------------------------------------------------------------------

    private MirroredKeyRepository Repository(IKeyCopyStore copy) => new(_folder, copy, NullLoggerFactory.Instance);

    private ServiceProvider Provider(IKeyCopyStore copy)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDataProtection().SetApplicationName("ipro635-test");
        services.AddOptions<KeyManagementOptions>().Configure(o => o.XmlRepository = Repository(copy));
        return services.BuildServiceProvider();
    }

    private static XElement Key(string id) =>
        new("key", new XAttribute("id", id), new XAttribute("version", "1"), new XElement("creationDate", "2026-11-08T17:00:00Z"));

    private static string Read(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln"))) dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return File.ReadAllText(Path.Combine(dir!, relative)).Replace("\r\n", "\n");
    }

    private sealed class FileSystemOnly
    {
        private readonly Microsoft.AspNetCore.DataProtection.Repositories.FileSystemXmlRepository _repository;
        public FileSystemOnly(DirectoryInfo folder) => _repository = new(folder, NullLoggerFactory.Instance);
        public void Store(XElement element, string name) => _repository.StoreElement(element, name);
    }

    private sealed class MemoryCopy : IKeyCopyStore
    {
        private readonly Dictionary<string, string> _items = new(StringComparer.OrdinalIgnoreCase);
        public bool Broken { get; init; }
        public int Writes { get; private set; }
        public void Put(string name, XElement element) => _items[name] = element.ToString();
        public string[] Names() => _items.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

        public IReadOnlyList<(string Name, XElement Element)> ReadAll()
        {
            if (Broken) throw new IOException("storage out of reach");
            return _items.Select(kv => (kv.Key, XElement.Parse(kv.Value))).ToList();
        }

        public void Write(string name, XElement element)
        {
            if (Broken) throw new IOException("storage out of reach");
            if (_items.ContainsKey(name)) return;
            _items[name] = element.ToString();
            Writes++;
        }
    }
}
