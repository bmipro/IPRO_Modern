using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPRO.Utility;

public interface IBlobStorageService
{
    Task<string> UploadAsync(Stream fileStream, string fileName, string containerName, string contentType, bool isPrivate);
    Task<bool> DeleteAsync(string blobUrl);
    Task<Stream?> DownloadAsync(string blobUrl);
    string GetPublicUrl(string containerName, string fileName);
    Task EnsureContainerAccessAsync(string containerName, bool isPrivate);
}

public class AzureBlobStorageService : IBlobStorageService
{
    private readonly BlobServiceClient _client;
    private readonly ILogger<AzureBlobStorageService> _logger;
    private readonly string _accountName;

    public AzureBlobStorageService(IConfiguration config, ILogger<AzureBlobStorageService> logger)
    {
        var connStr = config["Azure:StorageConnectionString"]!;
        _client = new BlobServiceClient(connStr);
        _accountName = config["Azure:StorageAccountName"]!;
        _logger = logger;
    }

    public async Task<string> UploadAsync(Stream fileStream, string fileName, string containerName, string contentType, bool isPrivate)
    {
        await EnsureContainerAccessAsync(containerName, isPrivate);

        var container = _client.GetBlobContainerClient(containerName);
        var safeName = $"{Guid.NewGuid():N}_{Path.GetFileName(fileName)}";
        var blob = container.GetBlobClient(safeName);

        await blob.UploadAsync(fileStream, new BlobHttpHeaders { ContentType = contentType });
        _logger.LogInformation("Uploaded blob {Name} to {Container}", safeName, containerName);
        return blob.Uri.ToString();
    }

    public async Task EnsureContainerAccessAsync(string containerName, bool isPrivate)
    {
        var container = _client.GetBlobContainerClient(containerName);
        var accessType = isPrivate ? PublicAccessType.None : PublicAccessType.Blob;
        await container.CreateIfNotExistsAsync(accessType);
        await container.SetAccessPolicyAsync(accessType);
    }

    public async Task<bool> DeleteAsync(string blobUrl)
    {
        try
        {
            var (containerName, blobName) = ParseBlobUrl(blobUrl);
            var container = _client.GetBlobContainerClient(containerName);
            var blob = container.GetBlobClient(blobName);
            return await blob.DeleteIfExistsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete blob {Url}", blobUrl);
            return false;
        }
    }

    public async Task<Stream?> DownloadAsync(string blobUrl)
    {
        var (containerName, blobName) = ParseBlobUrl(blobUrl);
        var container = _client.GetBlobContainerClient(containerName);
        var blob = container.GetBlobClient(blobName);
        var response = await blob.DownloadAsync();
        return response.Value.Content;
    }

    public string GetPublicUrl(string containerName, string fileName) =>
        $"https://{_accountName}.blob.core.windows.net/{containerName}/{fileName}";

    // Azurite uses path-style URLs (account name as a path segment) while real Azure Storage
    // uses virtual-hosted-style URLs (account name in the host). Strip the service client's own
    // base path so both formats resolve to the same {container}/{blob} split.
    private (string ContainerName, string BlobName) ParseBlobUrl(string blobUrl)
    {
        var uri = new Uri(blobUrl);
        var basePath = _client.Uri.AbsolutePath.TrimEnd('/');
        var relativePath = uri.AbsolutePath;
        if (basePath.Length > 0 && relativePath.StartsWith(basePath, StringComparison.Ordinal))
        {
            relativePath = relativePath[basePath.Length..];
        }
        var parts = relativePath.TrimStart('/').Split('/', 2);
        if (parts.Length != 2)
        {
            // A URL with no blob segment. DeleteAsync used to swallow the resulting
            // IndexOutOfRangeException while DownloadAsync turned it into a 500; fail the same
            // recognisable way in both.
            throw new ArgumentException($"Blob URL has no blob segment: {blobUrl}", nameof(blobUrl));
        }

        // AbsolutePath is percent-ENCODED, but the Azure SDK expects the blob's real name. Without
        // decoding, an upload called "logo-mobile copy.png" is looked up as "logo-mobile%20copy.png"
        // and simply isn't found -- DeleteIfExistsAsync returns false with no exception, and downloads
        // 404, so it fails silently in both directions. Found 2026-08-04 when an agent deletion
        // reported "2 uploaded files" against a preview that listed 3.
        //
        // Splitting BEFORE decoding is deliberate: a '/' inside a blob name arrives as %2F and must
        // survive the split, then decode back to a slash rather than being treated as a separator.
        var containerName = Uri.UnescapeDataString(parts[0]);
        var blobName = Uri.UnescapeDataString(parts[1]);

        // ROOT-CAUSE GUARD for the 2026-08-05 media-proxy traversal. This decode is the second one
        // a proxied path goes through (ASP.NET already decoded the request path once), so a
        // double-encoded "%252e%252e" arrives here as "%2e%2e" and becomes ".." right at this line.
        // GetBlobClient then resolves the dot segment and silently moves to ANOTHER container --
        // which is how an allowlisted public container reached agent-documents. No legitimate blob
        // name this system creates contains a dot segment or a leading slash (names are
        // "{guid:N}_{filename}"), so rejecting them here kills the whole class for every caller
        // rather than only the one that was noticed.
        if (HasTraversal(blobName) || HasTraversal(containerName))
        {
            throw new ArgumentException("Blob URL contains a path traversal segment.", nameof(blobUrl));
        }

        return (containerName, blobName);
    }

    private static bool HasTraversal(string value)
    {
        if (value.StartsWith('/') || value.StartsWith('\\')) return true;
        if (value.Contains('\\', StringComparison.Ordinal)) return true;
        return value.Split('/').Any(segment => segment == ".." || segment == ".");
    }
}
