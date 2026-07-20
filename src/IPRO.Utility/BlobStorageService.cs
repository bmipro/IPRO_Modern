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
        return (parts[0], parts[1]);
    }
}
