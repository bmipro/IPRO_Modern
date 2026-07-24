namespace IPRO.Entities;

public class TrialSettings
{
    public int Id { get; set; }
    public int GracePeriodDays { get; set; } = 1;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
