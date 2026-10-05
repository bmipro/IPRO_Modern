using IPRO.Entities;

namespace IPRO.Admin.Models;

// 532: SuperAdmin -> Referrals. The program's switch and amounts, this month and all time, the top referrers, what
// needs a person, and the ledger itself (searched, filtered by stage, a page at a time).
public sealed class ReferralsIndexModel
{
    public ReferralProgramSettings Settings { get; init; } = new();
    public string Query { get; init; } = string.Empty;
    public string Stage { get; init; } = string.Empty;
    public int? ReferrerId { get; init; }
    public int Page { get; init; } = 1;
    public int PageCount { get; init; } = 1;
    public int TotalMatching { get; init; }
    public IReadOnlyList<Referral> Rows { get; init; } = Array.Empty<Referral>();
    public IReadOnlyDictionary<int, AgentUser> Advisers { get; init; } = new Dictionary<int, AgentUser>();
    public IReadOnlyList<Referral> NeedsAttention { get; init; } = Array.Empty<Referral>();
    public IReadOnlyList<TopReferrer> TopReferrers { get; init; } = Array.Empty<TopReferrer>();
    public Totals Month { get; init; } = new();
    public Totals AllTime { get; init; } = new();
    public ReferralCode? ReferrerCode { get; init; }

    public string AdviserName(int id) =>
        Advisers.TryGetValue(id, out var a) ? $"{a.FirstName} {a.LastName}".Trim() : $"Adviser #{id} (deleted)";

    public sealed record TopReferrer(int AgentUserId, int Referred, int Joined, decimal Earned);

    public sealed class Totals
    {
        public int SignedUp { get; init; }
        public int Joined { get; init; }
        public decimal GiftsGiven { get; init; }
        public decimal RewardsEarned { get; init; }
        public decimal RewardsPaid { get; init; }
    }
}
