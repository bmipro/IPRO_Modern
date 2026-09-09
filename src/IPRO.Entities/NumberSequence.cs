namespace IPRO.Entities;

// 418 (2026-09-09): a counter that only goes up, one row per key. Invoice numbers come from here,
// never from MAX(existing rows) + 1, so a number is never issued twice however the rows that once
// carried it were deleted.
public class NumberSequence
{
    public string Key { get; set; } = string.Empty;
    public long LastValue { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
