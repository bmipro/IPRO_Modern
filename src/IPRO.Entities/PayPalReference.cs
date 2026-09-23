using System;
using System.Collections.Generic;
using System.Linq;

namespace IPRO.Entities;

// 516 (2026-09-22): what Invoice.PayPalTransactionId holds, read back apart. The field carries the
// subscription id from creation ("I-..."), every settling sale id appended after a comma, and a
// "PAYPAL_FAILED:" marker for each failed attempt -- an audit trail, not one number. The page and the
// email show the subscription and the transaction as two rows; the failed markers stay in the data.
public static class PayPalReference
{
    public const string FailedPrefix = "PAYPAL_FAILED:";

    public static (string SubscriptionId, IReadOnlyList<string> TransactionIds, IReadOnlyList<string> FailedIds) Split(string? raw)
    {
        var parts = (raw ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var subscription = parts.FirstOrDefault(p => p.StartsWith("I-", StringComparison.Ordinal)) ?? string.Empty;
        var failed = parts.Where(p => p.StartsWith(FailedPrefix, StringComparison.Ordinal)).Select(p => p[FailedPrefix.Length..].Trim()).Where(p => p.Length > 0).ToList();
        var transactions = parts.Where(p => p != subscription && !p.StartsWith(FailedPrefix, StringComparison.Ordinal)).ToList();
        return (subscription, transactions, failed);
    }
}
