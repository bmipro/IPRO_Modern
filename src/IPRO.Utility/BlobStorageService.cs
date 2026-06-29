using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPRO.Utility;

public interface IBlobStorageService
{
    Task<string> UploadAsync(Stream fileStream, string fileName, string containerName, string contentType);
    Task<bool> DeleteAsync(string blobUrl);
    Task<Stream?> DownloadAsync(string blobUrl);
    string GetPublicUrl(string containerName, string fileName);
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

    public async Task<string> UploadAsync(Stream fileStream, string fileName, string containerName, string contentType)
    {
        var container = _client.GetBlobContainerClient(containerName);
        await container.CreateIfNotExistsAsync(PublicAccessType.Blob);

        var safeName = $"{Guid.NewGuid():N}_{Path.GetFileName(fileName)}";
        var blob = container.GetBlobClient(safeName);

        await blob.UploadAsync(fileStream, new BlobHttpHeaders { ContentType = contentType });
        _logger.LogInformation("Uploaded blob {Name} to {Container}", safeName, containerName);
        return blob.Uri.ToString();
    }

    public async Task<bool> DeleteAsync(string blobUrl)
    {
        try
        {
            var uri = new Uri(blobUrl);
            var parts = uri.AbsolutePath.TrimStart('/').Split('/', 2);
            var container = _client.GetBlobContainerClient(parts[0]);
            var blob = container.GetBlobClient(parts[1]);
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
        var uri = new Uri(blobUrl);
        var parts = uri.AbsolutePath.TrimStart('/').Split('/', 2);
        var container = _client.GetBlobContainerClient(parts[0]);
        var blob = container.GetBlobClient(parts[1]);
        var response = await blob.DownloadAsync();
        return response.Value.Content;
    }

    public string GetPublicUrl(string containerName, string fileName) =>
        $"https://{_accountName}.blob.core.windows.net/{containerName}/{fileName}";
}
