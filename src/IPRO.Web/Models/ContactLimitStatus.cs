namespace IPRO.Web.Models;

public class ContactLimitStatus
{
    public int CurrentCount { get; set; }
    public int? Limit { get; set; }
    public string PackageName { get; set; } = string.Empty;
    public bool CanAdd { get; set; } = true;
    public string Message { get; set; } = string.Empty;
    public bool IsUnlimited => !Limit.HasValue;
    public int Remaining => Limit.HasValue ? Math.Max(0, Limit.Value - CurrentCount) : int.MaxValue;
    public string LimitLabel => IsUnlimited ? "Unlimited" : Limit.GetValueOrDefault().ToString("N0");
}
