using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using IPRO.Business.Interfaces;
using IPRO.Utility;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace IPRO.IntegrationTests;

// TODO 454 (rest) and 476 (2026-09-11), shipped together.
//
// 454: the last two senders that discarded the provider's answer -- the certificate-expiry and
//      domain-automation operations mails. Both caught a thrown failure but ignored a returned
//      false, so a refused send logged nothing. They now log a warning when the provider says no.
// 476: the Backups page shows each dump's size and the change against the previous dump, so a
//      swing in the database stands out (the owner's ask after the first on-demand dump).
public class OpsMailAndBackupSizeTests
{
    [Theory]
    [InlineData(@"src\IPRO.Scheduler\CertificateExpiryJob.cs")]
    [InlineData(@"src\IPRO.Scheduler\DomainAutomationJob.cs")]
    public void The_operations_mails_keep_the_providers_answer(string relative)
    {
        var source = File.ReadAllText(FindRepoFile(relative));
        Assert.DoesNotMatch(@"^\s*await _email\.SendAsync\(", source.Replace("\r\n", "\n"));
        Assert.Contains("var sent = await _email.SendAsync(", source);
        Assert.Contains("if (!sent)", source);
    }

    [Fact]
    public async Task The_backups_page_shows_each_dumps_size_and_the_change_against_the_previous_one()
    {
        var now = DateTime.UtcNow;
        var store = new SizedBlobStore
        {
            Files =
            {
                new BlobFileInfo($"https://blobs/db-backups/aaaa_ipro_crm-{now.AddDays(-2):yyyyMMdd-HHmmss}.sql.gz", $"aaaa_ipro_crm-{now.AddDays(-2):yyyyMMdd-HHmmss}.sql.gz", 1_000_000, now.AddDays(-2)),
                new BlobFileInfo($"https://blobs/db-backups/bbbb_ipro_crm-{now.AddDays(-1):yyyyMMdd-HHmmss}.sql.gz", $"bbbb_ipro_crm-{now.AddDays(-1):yyyyMMdd-HHmmss}.sql.gz", 1_400_000, now.AddDays(-1)),
                new BlobFileInfo("https://blobs/db-backups/cccc_not-a-dump.txt", "cccc_not-a-dump.txt", 10, now),
            }
        };
        var controller = new IPRO.Admin.Controllers.BackupsController(store, new ServiceCollection().BuildServiceProvider(), new NoAudit(), new ConfigurationBuilder().Build());
        var ctx = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "1") }, "test")) };
        controller.ControllerContext = new ControllerContext { HttpContext = ctx };

        var result = Assert.IsType<ViewResult>(await controller.Index());
        var rows = Assert.IsAssignableFrom<List<IPRO.Admin.Controllers.BackupsController.BackupFileRow>>(result.Model);

        Assert.Equal(3, rows.Count);
        Assert.Equal(1_400_000, rows[0].SizeBytes);          // newest first
        Assert.Equal(40, Math.Round(rows[0].ChangePercent!.Value));
        Assert.Equal(1_000_000, rows[1].SizeBytes);
        Assert.Null(rows[1].ChangePercent);                  // nothing older to compare with
        Assert.Null(rows[2].TakenAtUtc);                     // the stray file has no dump timestamp

        var view = File.ReadAllText(FindRepoFile(@"src\IPRO.Admin\Views\Backups\Index.cshtml"));
        Assert.Contains("<th>Size</th>", view);
        Assert.Contains("ChangePercent", view);
    }

    // ---- harness ------------------------------------------------------------------------------

    private sealed class SizedBlobStore : IBlobStorageService
    {
        public List<BlobFileInfo> Files { get; } = new();
        public Task<string> UploadAsync(Stream fileStream, string fileName, string containerName, string contentType, bool isPrivate) => Task.FromResult($"https://blobs/{containerName}/{fileName}");
        public Task<bool> DeleteAsync(string blobUrl) => Task.FromResult(true);
        public Task<Stream?> DownloadAsync(string blobUrl) => Task.FromResult<Stream?>(null);
        public Task<List<string>> ListAsync(string containerName) => Task.FromResult(Files.Select(f => f.Url).ToList());
        public Task<List<BlobFileInfo>> ListDetailedAsync(string containerName) => Task.FromResult(Files.ToList());
        public string GetPublicUrl(string containerName, string fileName) => $"https://blobs/{containerName}/{fileName}";
        public Task EnsureContainerAccessAsync(string containerName, bool isPrivate) => Task.CompletedTask;
    }

    private sealed class NoAudit : IAdminAuditLogService
    {
        public Task LogAsync(int adminUserId, string adminUsername, string action, string details) => Task.CompletedTask;
    }

    private static string FindRepoFile(string relative)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "IPRO.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return Path.Combine(dir!, relative);
    }
}
