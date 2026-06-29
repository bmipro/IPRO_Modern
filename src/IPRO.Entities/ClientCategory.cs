namespace IPRO.Entities;

public class ClientCategory
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public ICollection<Client> Clients { get; set; } = new List<Client>();
}
