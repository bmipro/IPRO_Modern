namespace IPRO.Entities;

public class PortalDocument
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public bool UploadedByClient { get; set; }
    public string FileName { get; set; } = string.Empty;
    public string BlobUrl { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;

    public Client Client { get; set; } = null!;
}
