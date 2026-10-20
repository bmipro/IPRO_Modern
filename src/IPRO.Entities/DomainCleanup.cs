namespace IPRO.Entities;

public static class DomainCleanupReasons
{
    public const string RemovedByAdviser = "removed by the adviser";
    public const string AgentDeleted = "adviser deleted";
    public const string UnprovenClaimMoved = "unproven, moved to its proven owner";
}

public static class DomainCleanupOutcomes
{
    public const string Removed = "removed";
    public const string KeptForAnotherRow = "kept: another row names it";
    public const string Refused = "refused: not an adviser's name";
}

/// <summary>
/// 592: an address that stopped being an adviser's (My Website's Remove, SuperAdmin deleting the adviser) and
/// whose hostname binding and certificate are to be removed from the web app in Azure. Written in the same save
/// as the domain row's removal and kept until Azure confirms, so a removal that does not get through is tried
/// again (IPRO.Scheduler.DomainCleanupQueue) instead of being forgotten -- www.411trades.com stayed bound for
/// weeks after it left its adviser's account, and the next adviser could take it over.
/// </summary>
public class DomainCleanup
{
    public int Id { get; set; }
    public string HostName { get; set; } = string.Empty;

    /// <summary>The adviser the address belonged to (no foreign key: the adviser may be erased).</summary>
    public int FormerAgentUserId { get; set; }

    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public int Attempts { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? NextAttemptAt { get; set; }
    public string LastError { get; set; } = string.Empty;

    /// <summary>Null while Azure still has to be asked.</summary>
    public DateTime? DoneAt { get; set; }

    /// <summary>What happened in the end: removed, kept (another domain row names it), or refused.</summary>
    public string Outcome { get; set; } = string.Empty;
}
