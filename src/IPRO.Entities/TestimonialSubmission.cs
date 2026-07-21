namespace IPRO.Entities;

public enum TestimonialStatus
{
    Pending,
    Approved,
    Rejected
}

public class TestimonialSubmission
{
    public int Id { get; set; }
    public int AgentUserId { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public TestimonialStatus Status { get; set; } = TestimonialStatus.Pending;
    public DateTime SubmittedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ReviewedAt { get; set; }
    public int? ClientId { get; set; }
    public string? RequestToken { get; set; }
}
