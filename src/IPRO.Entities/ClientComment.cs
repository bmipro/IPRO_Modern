namespace IPRO.Entities;

public class ClientComment
{
    public int Id { get; set; }
    public int ClientId { get; set; }
    public string Comment { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public Client Client { get; set; } = null!;
}
