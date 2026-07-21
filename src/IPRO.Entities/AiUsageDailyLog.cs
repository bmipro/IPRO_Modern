namespace IPRO.Entities;

public class AiUsageDailyLog
{
    public int Id { get; set; }
    public DateTime Date { get; set; }
    public int CallCount { get; set; }
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal EstimatedCostUsd { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
