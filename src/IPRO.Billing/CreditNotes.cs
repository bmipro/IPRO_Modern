using System.Globalization;
using System.Net;
using IPRO.DataAccess;
using IPRO.Email;
using IPRO.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPRO.Billing;

// 600 (2026-10-24): one credit note, either kind, as SuperAdmin's Revenue ledger, its CSV, its print view and the
// adviser's email read it. The amounts are what was credited (positive); the ledger and the CSV show them negative.
// BillTo* is the credited invoice's frozen bill-to when there is one (invoices outlive their adviser), else the
// adviser's account as it is now.
public sealed record CreditNoteEntry(
    string Kind, int SourceId, string Number, DateTime IssuedAt, int AgentUserId, int? BillingId, string Package, string Description,
    decimal Net, decimal Tax, decimal Gross, decimal TaxRate, string TaxRegion, string Currency,
    string CreditsInvoice, string RefundedAgainst, string PayPalRefundId,
    string BillToName, string BillToCompany, string BillToEmail, IReadOnlyList<string> BillToAddress, string AdviserTimeZone, bool AdviserDeleted)
{
    public string TaxLabel => CreditNotes.TaxLabel(TaxRegion, TaxRate, Tax);
    public string KindLabel => Kind == CreditNotes.ReferralKind ? "Refer a Friend reward" : "Refund";
}

// 600 (2026-10-24): credit notes for money iPro gives back. The owner, 2026-10-24: "Refunds don't reduce Revenue and get
// no credit note -> Add credit notes and net them in Revenue and the CSV (matters for tax filing)." Two kinds share the
// one CN-YYYY-NNNN sequence (ReferralProgram.NextCreditNoteNumberAsync): a Refer a Friend reward refunded (532), and a
// refund marked Refunded in SuperAdmin -> Refunds -- a cancellation's unused prepaid value, or money PayPal captured
// after a subscription had ended (DOCS/22). A waived refund gets none. No code here moves money: the owner refunds at
// PayPal, then marks the row.
public static class CreditNotes
{
    public const string RefundKind = "refund";
    public const string ReferralKind = "referral";
    // What PayPalBillingService's sale handler writes on the refund row for a payment taken after the end.
    public const string CapturedAfterEndMarker = "[captured-after-end net]";
    private const int ResolutionNoteLength = 500;   // SubscriptionChanges.RefundResolutionNote is varchar(500)

    // ---- issuing one ---------------------------------------------------------------------------------------------

    // Refunded at PayPal (the owner's step): the row is claimed Pending -> Refunded and numbered in ONE transaction.
    // The claim is a conditional UPDATE, so a second click, or a second admin at the same moment, finds nothing left
    // to claim (InnoDB queues the second UPDATE behind the first's row lock, then it matches no row) and gets null:
    // one refund, one number, none skipped. Null too for no refund id, or a row that is not a pending refund.
    public static async Task<SubscriptionChange?> MarkRefundedAsync(IPRODbContext db, int changeId, string payPalRefundId, DateTime now)
    {
        var refundId = Clip((payPalRefundId ?? string.Empty).Trim(), 200);
        if (refundId.Length == 0) return null;
        var row = await db.SubscriptionChanges.AsNoTracking().FirstOrDefaultAsync(c =>
            c.Id == changeId && c.ChangeType == SubscriptionChangeType.Cancel && c.RefundStatus == RefundStatus.Pending);
        if (row == null) return null;

        // The invoice it credits, and that invoice's tax -- the rate the refund's tax was worked out at (DOCS/22).
        // With no invoice to match, the rate and region come from the subscription's latest paid invoice, as the
        // cancellation's own arithmetic did, and the credit note names no invoice rather than a wrong one.
        var credited = await CreditedInvoiceAsync(db, row);
        var rateSource = credited ?? (row.BillingId == null ? null : await db.Invoices.AsNoTracking()
            .Where(i => i.BillingId == row.BillingId && i.IsPaid)
            .OrderByDescending(i => i.IssuedAt)
            .FirstOrDefaultAsync());
        var invoiceNumber = Clip(credited?.InvoiceNumber ?? string.Empty, 64);
        var taxRate = rateSource?.TaxRate ?? (row.RefundNetAmount > 0m ? Math.Round(row.RefundTaxAmount / row.RefundNetAmount, 5) : 0m);
        var taxRegion = Clip(rateSource?.TaxRegion ?? string.Empty, 100);
        var note = WithSuffix(row.RefundResolutionNote, $" | Refunded at PayPal, refund txn {refundId}.");

        // The year's counter row exists before the transaction opens, so the increment inside it is a plain row lock
        // (NumberSequences.EnsureSeededAsync says why that matters).
        await ReferralProgram.EnsureCreditNoteSequenceAsync(db, now);

        await using var transaction = await db.Database.BeginTransactionAsync();
        var claimed = await db.SubscriptionChanges
            .Where(c => c.Id == changeId && c.RefundStatus == RefundStatus.Pending && c.CreditNoteNumber == "")
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.RefundStatus, RefundStatus.Refunded)
                .SetProperty(c => c.RefundResolvedAt, now)
                .SetProperty(c => c.RefundResolutionNote, note)
                .SetProperty(c => c.PayPalRefundId, refundId));
        if (claimed == 0)
        {
            await transaction.RollbackAsync();
            return null;
        }
        var number = await ReferralProgram.NextCreditNoteNumberAsync(db, now);
        var numbered = await db.SubscriptionChanges
            .Where(c => c.Id == changeId && c.RefundStatus == RefundStatus.Refunded && c.CreditNoteNumber == "")
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.CreditNoteNumber, number)
                .SetProperty(c => c.CreditNoteIssuedAt, now)
                .SetProperty(c => c.CreditNoteInvoiceNumber, invoiceNumber)
                .SetProperty(c => c.CreditNoteTaxRate, taxRate)
                .SetProperty(c => c.CreditNoteTaxRegion, taxRegion));
        if (numbered != 1)
            throw new InvalidOperationException($"Refund {changeId} lost its claim before credit note {number} was written; nothing was marked.");
        await transaction.CommitAsync();
        return await db.SubscriptionChanges.AsNoTracking().FirstAsync(c => c.Id == changeId);
    }

    // The invoice a refund credits: the one paid by the PayPal sale the refund is made against. The cancellation and
    // the captured-after-end net both store that sale as PayPalBillingService.SettlingTransactionRef left it; an
    // invoice's PayPal reference can hold the subscription id and several sales, comma-separated.
    internal static async Task<Invoice?> CreditedInvoiceAsync(IPRODbContext db, SubscriptionChange row)
    {
        var sale = (row.RefundPayPalTransactionId ?? string.Empty).Trim();
        if (sale.Length == 0) return null;
        var candidates = await db.Invoices.AsNoTracking()
            .Where(i => i.AgentUserId == row.AgentUserId && i.PayPalTransactionId.Contains(sale))
            .ToListAsync();
        return candidates
            .Where(i => SaleIds(i.PayPalTransactionId).Contains(sale, StringComparer.Ordinal))
            .OrderByDescending(i => i.BillingId == row.BillingId)
            .ThenByDescending(i => i.IsPaid)
            .ThenByDescending(i => i.IssuedAt)
            .FirstOrDefault();
    }

    internal static List<string> SaleIds(string? reference)
    {
        const string failed = "PAYPAL_FAILED:";
        return (reference ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(id => id.StartsWith(failed, StringComparison.Ordinal) ? id[failed.Length..] : id)
            .ToList();
    }

    // ---- reading them back -----------------------------------------------------------------------------------------

    // Every credit note issued in [from, endExclusive): refunds by the day they were marked, rewards by the day paid.
    public static async Task<List<CreditNoteEntry>> ListAsync(IPRODbContext db, DateTime from, DateTime endExclusive)
    {
        var refunds = await db.SubscriptionChanges.AsNoTracking()
            .Where(c => c.CreditNoteNumber != "" && c.CreditNoteIssuedAt >= from && c.CreditNoteIssuedAt < endExclusive)
            .ToListAsync();
        var rewards = await db.Referrals.AsNoTracking()
            .Where(r => r.Stage == ReferralStages.Paid && r.CreditNoteNumber != "" && r.PaidAt >= from && r.PaidAt < endExclusive)
            .ToListAsync();
        return await BuildAsync(db, refunds, rewards);
    }

    public static async Task<CreditNoteEntry?> FindAsync(IPRODbContext db, string? kind, int id)
    {
        if (string.Equals(kind, ReferralKind, StringComparison.OrdinalIgnoreCase))
        {
            var reward = await db.Referrals.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == id && r.Stage == ReferralStages.Paid && r.CreditNoteNumber != "");
            return reward == null ? null : (await BuildAsync(db, new List<SubscriptionChange>(), new List<Referral> { reward })).Single();
        }
        var refund = await db.SubscriptionChanges.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.CreditNoteNumber != "");
        return refund == null ? null : (await BuildAsync(db, new List<SubscriptionChange> { refund }, new List<Referral>())).Single();
    }

    private static async Task<List<CreditNoteEntry>> BuildAsync(IPRODbContext db, List<SubscriptionChange> refunds, List<Referral> rewards)
    {
        var agentIds = refunds.Select(c => c.AgentUserId).Concat(rewards.Select(r => r.AgentUserId)).Distinct().ToList();
        var agents = agentIds.Count == 0
            ? new Dictionary<int, AgentUser>()
            : await db.AgentUsers.AsNoTracking().Where(a => agentIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id);

        var invoiceNumbers = refunds.Select(c => c.CreditNoteInvoiceNumber).Where(n => n != "").Distinct().ToList();
        var creditedInvoices = invoiceNumbers.Count == 0
            ? new Dictionary<string, Invoice>()
            : await db.Invoices.AsNoTracking().Where(i => invoiceNumbers.Contains(i.InvoiceNumber)).ToDictionaryAsync(i => i.InvoiceNumber);

        var ruleIds = refunds.Where(c => c.CurrentBillingRuleId.HasValue).Select(c => c.CurrentBillingRuleId!.Value).Distinct().ToList();
        var packages = ruleIds.Count == 0
            ? new Dictionary<int, string>()
            : await db.BillingRules.AsNoTracking().Where(p => ruleIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.PackageName);

        // A reward was refunded against the referrer's own payments; the invoices those sales paid are what it credits.
        var referrerIds = rewards.Select(r => r.AgentUserId).Distinct().ToList();
        var referrerInvoices = referrerIds.Count == 0
            ? new List<Invoice>()
            : await db.Invoices.AsNoTracking().Where(i => referrerIds.Contains(i.AgentUserId) && i.PayPalTransactionId != "").ToListAsync();

        var entries = new List<CreditNoteEntry>();
        foreach (var c in refunds)
        {
            agents.TryGetValue(c.AgentUserId, out var agent);
            creditedInvoices.TryGetValue(c.CreditNoteInvoiceNumber, out var invoice);
            var package = c.CurrentBillingRuleId.HasValue && packages.TryGetValue(c.CurrentBillingRuleId.Value, out var name) ? name : string.Empty;
            var subscription = package.Length == 0 ? "the subscription" : package;
            var description = (c.RefundResolutionNote ?? string.Empty).Contains(CapturedAfterEndMarker, StringComparison.Ordinal)
                ? $"Refund of a payment PayPal took after the subscription had ended ({subscription})"
                : $"Refund on cancelling {subscription} ({(c.Period == BillingPeriod.Annually ? "annual" : "monthly")}): the unused part of the paid period";
            var (billToName, billToCompany, billToEmail, billToAddress) = invoice != null && !string.IsNullOrWhiteSpace(invoice.BillToName)
                ? (invoice.BillToName, invoice.BillToCompany, invoice.BillToEmail,
                   AddressText.JoinCityAndProvinceLines((invoice.BillToAddress ?? string.Empty).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))
                : BillTo(agent, c.AgentUserId);
            entries.Add(new CreditNoteEntry(
                RefundKind, c.Id, c.CreditNoteNumber, c.CreditNoteIssuedAt ?? c.RefundResolvedAt ?? c.CreatedAt, c.AgentUserId, c.BillingId, package, description,
                c.RefundNetAmount, c.RefundTaxAmount, c.RefundGrossAmount, c.CreditNoteTaxRate, c.CreditNoteTaxRegion, string.IsNullOrWhiteSpace(c.Currency) ? "CAD" : c.Currency,
                c.CreditNoteInvoiceNumber, c.RefundPayPalTransactionId, c.PayPalRefundId,
                billToName, billToCompany, billToEmail, billToAddress, agent?.TimeZone ?? string.Empty, agent == null));
        }
        foreach (var r in rewards)
        {
            agents.TryGetValue(r.AgentUserId, out var agent);
            var sales = ReferralProgram.ParsePlan(r.RefundPlan).Select(p => p.Sale).ToList();
            var credits = string.Join(", ", sales
                .Select(sale => referrerInvoices
                    .Where(i => i.AgentUserId == r.AgentUserId && SaleIds(i.PayPalTransactionId).Contains(sale, StringComparer.Ordinal))
                    .OrderByDescending(i => i.IssuedAt)
                    .Select(i => i.InvoiceNumber)
                    .FirstOrDefault())
                .Where(n => !string.IsNullOrEmpty(n))
                .Distinct());
            var friend = string.IsNullOrWhiteSpace(r.FriendName) ? "your friend" : r.FriendName.Trim();
            var (billToName, billToCompany, billToEmail, billToAddress) = BillTo(agent, r.AgentUserId);
            entries.Add(new CreditNoteEntry(
                ReferralKind, r.Id, r.CreditNoteNumber, r.PaidAt ?? r.UpdatedAt, r.AgentUserId, null, "Refer a Friend",
                $"Refer a Friend reward: {friend} joined iPro with your gift",
                r.RewardNet, r.RewardTax, r.RewardGross, r.RewardTaxRate, r.RewardTaxRegion, "CAD",
                credits, string.Join(", ", sales), r.RefundTransactionId,
                billToName, billToCompany, billToEmail, billToAddress, agent?.TimeZone ?? string.Empty, agent == null));
        }
        return entries;
    }

    // The adviser's account as invoices print it: street, "City, Province PostalCode", country -- a lone country
    // (the default) is no address.
    private static (string Name, string Company, string Email, IReadOnlyList<string> Address) BillTo(AgentUser? agent, int agentUserId)
    {
        if (agent == null) return ($"Adviser #{agentUserId} (account deleted)", string.Empty, string.Empty, Array.Empty<string>());
        var lines = new[] { agent.CompanyAddress, AddressText.CityLine(agent.City, agent.Province, agent.PostalCode), agent.Country }
            .Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => l!.Trim()).ToList();
        if (lines.Count == 1 && string.Equals(lines[0], (agent.Country ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase)) lines.Clear();
        var name = $"{agent.FirstName} {agent.LastName}".Trim();
        return (name.Length == 0 ? agent.UserName : name, agent.CompanyName ?? string.Empty, agent.Email ?? string.Empty, lines);
    }

    // ---- the adviser's email ------------------------------------------------------------------------------------------

    // Sent when the refund is marked, as the Refer a Friend "paid" email is. A failure is logged and answered false; it
    // never undoes the refund or its number -- the caller tells the admin to reach the adviser another way.
    public static async Task<bool> SendRefundEmailAsync(IPRODbContext db, IEmailService email, IConfiguration configuration, ILogger logger, int changeId, DateTime now)
    {
        try
        {
            var note = await FindAsync(db, RefundKind, changeId);
            if (note == null) return false;
            var adviser = await db.AgentUsers.AsNoTracking().FirstOrDefaultAsync(a => a.Id == note.AgentUserId);
            var to = !string.IsNullOrWhiteSpace(adviser?.Email) ? adviser!.Email : note.BillToEmail;
            if (string.IsNullOrWhiteSpace(to)) return false;
            var company = await BillingCompanyDetails.LoadAsync(db, key => configuration[key]);
            var mail = RefundEmail(note, adviser, company);
            var toName = adviser == null ? note.BillToName : $"{adviser.FirstName} {adviser.LastName}".Trim();
            if (!await email.SendAsync(to, toName, mail.Subject, mail.Html, mail.Text))
            {
                logger.LogWarning("Credit note {Number} for refund {ChangeId} was not emailed: the email service did not send it.", note.Number, changeId);
                return false;
            }
            await db.SubscriptionChanges.Where(c => c.Id == changeId).ExecuteUpdateAsync(s => s.SetProperty(c => c.CreditNoteEmailSentAt, now));
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Credit note email for refund {ChangeId} failed.", changeId);
            return false;
        }
    }

    // The credit note itself, in the paid-invoice email's shape (PayPalBillingService.BuildPaidInvoiceEmailHtml): the
    // supplier from SuperAdmin -> Company Details, who it is to, what it credits, and the amounts, negative.
    public static SignupNotice.Notice RefundEmail(CreditNoteEntry note, AgentUser? adviser, BillingCompanyDetails company)
    {
        static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
        var cur = note.Currency;
        string Credit(decimal amount) => $"{Money(-amount)} {cur}";
        var date = AgentLocalTime.FromUtc(note.IssuedAt, adviser?.TimeZone ?? note.AdviserTimeZone).ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
        var name = adviser == null ? note.BillToName : $"{adviser.FirstName} {adviser.LastName}".Trim();
        var hello = string.IsNullOrWhiteSpace(adviser?.FirstName) ? "Hello," : $"Hello {adviser!.FirstName.Trim()},";
        var website = BrandText.WithCapitals(company.Website);
        var companyEmail = BrandText.WithCapitals(company.Email);
        var companyAddress = string.Join(", ", company.AddressLines);
        var refunded = $"{Money(note.Gross)} {cur} was refunded today to the card or PayPal account you paid iPro with. PayPal shows it right away; a card can take a few business days.";
        var credits = note.CreditsInvoice.Length > 0 ? $"This credit note credits invoice {note.CreditsInvoice}." : "This credit note records the refund.";
        var subject = note.CreditsInvoice.Length > 0 ? $"IPRO credit note {note.Number} for invoice {note.CreditsInvoice}" : $"IPRO credit note {note.Number}";

        var details = new List<(string Label, string Value)> { ("Credit note #", note.Number), ("Date", date) };
        if (note.CreditsInvoice.Length > 0) details.Add(("Credits invoice", note.CreditsInvoice));
        if (note.PayPalRefundId.Length > 0) details.Add(("PayPal refund", note.PayPalRefundId));
        if (note.RefundedAgainst.Length > 0) details.Add(("Against PayPal payment", note.RefundedAgainst));
        var totals = new List<(string Label, string Value)> { ("Amount credited before tax", Credit(note.Net)) };
        totals.Add((note.Tax > 0m ? note.TaxLabel : "Tax", Credit(note.Tax)));
        totals.Add(("Total credited", Credit(note.Gross)));

        var detailHtml = string.Concat(details.Select(d => $"<div><strong>{E(d.Label)}:</strong> {E(d.Value)}</div>"));
        var billTo = string.Concat(new[] { note.BillToCompany, note.BillToEmail }.Concat(note.BillToAddress)
            .Where(l => !string.IsNullOrWhiteSpace(l)).Select(l => $"<div style=\"color:#64748b;font-size:13px;\">{E(l)}</div>"));
        var totalRows = string.Concat(totals.Select((t, i) => i == totals.Count - 1
            ? $"<tr><td style=\"padding:12px 0;color:#1457d9;font-size:20px;\"><strong>{E(t.Label)}</strong></td><td style=\"padding:12px 0;color:#1457d9;font-size:20px;text-align:right;\"><strong>{E(t.Value)}</strong></td></tr>"
            : $"<tr><td style=\"border-bottom:1px solid #e5edf7;padding:8px 0;\">{E(t.Label)}</td><td style=\"border-bottom:1px solid #e5edf7;padding:8px 0;text-align:right;\"><strong>{E(t.Value)}</strong></td></tr>"));

        var html = $"""
            <div style="font-family:Arial,sans-serif;background:#f4f7fb;padding:24px;">
              <div style="max-width:680px;margin:0 auto;background:#ffffff;border-radius:14px;overflow:hidden;border:1px solid #dbe5f2;">
                <div style="background:#102a5c;color:#ffffff;padding:26px 30px;">
                  <h1 style="margin:0;font-size:24px;">{E(company.Name)}</h1>
                  <p style="margin:8px 0 0;color:#dbeafe;">Credit note</p>
                  <p style="margin:8px 0 0;color:#dbeafe;font-size:13px;">{E(website)} &nbsp; | &nbsp; {E(companyEmail)}</p>
                  {(companyAddress.Length == 0 ? "" : $"<p style=\"margin:6px 0 0;color:#dbeafe;font-size:12px;\">{E(companyAddress)}</p>")}
                  {(string.IsNullOrWhiteSpace(company.TaxRegistrationNumber) ? "" : $"<p style=\"margin:6px 0 0;color:#dbeafe;font-size:12px;\">GST/HST registration no. {E(company.TaxRegistrationNumber)}</p>")}
                </div>
                <div style="padding:30px;color:#1f2937;">
                  <p>{E(hello)}</p>
                  <p>{E(refunded)}</p>
                  <p>{E(credits)}</p>
                  <div style="display:table;width:100%;border-spacing:0 0;margin:20px 0;">
                    <div style="display:table-cell;width:50%;background:#f8fafc;border:1px solid #dbe5f2;border-radius:12px;padding:16px;vertical-align:top;">
                      <div style="color:#64748b;font-size:12px;font-weight:bold;text-transform:uppercase;letter-spacing:.08em;margin-bottom:8px;">Credit To</div>
                      <div style="font-weight:bold;">{E(name)}</div>
                      {billTo}
                    </div>
                    <div style="display:table-cell;width:16px;"></div>
                    <div style="display:table-cell;width:50%;background:#f8fafc;border:1px solid #dbe5f2;border-radius:12px;padding:16px;vertical-align:top;">
                      <div style="color:#64748b;font-size:12px;font-weight:bold;text-transform:uppercase;letter-spacing:.08em;margin-bottom:8px;">Credit Note Details</div>
                      {detailHtml}
                    </div>
                  </div>
                  <table style="width:100%;border-collapse:collapse;margin-top:10px;">
                    <thead>
                      <tr>
                        <th style="text-align:left;color:#64748b;font-size:12px;text-transform:uppercase;letter-spacing:.08em;border-bottom:2px solid #dbe5f2;padding-bottom:10px;">Description</th>
                        <th style="text-align:right;color:#64748b;font-size:12px;text-transform:uppercase;letter-spacing:.08em;border-bottom:2px solid #dbe5f2;padding-bottom:10px;">Amount</th>
                      </tr>
                    </thead>
                    <tbody>
                      <tr>
                        <td style="padding:12px 0;border-bottom:1px solid #e5edf7;">{E(note.Description)}</td>
                        <td style="padding:12px 0;border-bottom:1px solid #e5edf7;text-align:right;">{E(Credit(note.Net))}</td>
                      </tr>
                    </tbody>
                  </table>
                  <table style="width:100%;max-width:360px;margin-left:auto;margin-top:20px;border-collapse:collapse;">
                    {totalRows}
                  </table>
                  <p style="margin-top:26px;">Please keep this credit note with your invoices.</p>
                  <p style="margin-top:16px;">{E(company.Name)}</p>
                </div>
              </div>
            </div>
            """;

        var text = new System.Text.StringBuilder();
        text.Append(hello).Append("\n\n").Append(refunded).Append("\n\n").Append(credits).Append("\n\n");
        foreach (var d in details) text.Append(d.Label).Append(": ").Append(d.Value).Append('\n');
        text.Append('\n').Append(note.Description).Append(": ").Append(Credit(note.Net)).Append("\n\n");
        foreach (var t in totals) text.Append(t.Label).Append(": ").Append(t.Value).Append('\n');
        text.Append("\nPlease keep this credit note with your invoices.\n\n").Append(company.Name).Append('\n');
        if (companyAddress.Length > 0) text.Append(companyAddress).Append('\n');
        if (!string.IsNullOrWhiteSpace(company.TaxRegistrationNumber)) text.Append("GST/HST registration no. ").Append(company.TaxRegistrationNumber).Append('\n');
        text.Append(website).Append(" | ").Append(companyEmail).Append('\n');
        return new SignupNotice.Notice(subject, html, text.ToString());
    }

    // ---- words ------------------------------------------------------------------------------------------------------

    // "ON HST 13%": the region as the invoice carries it plus the rate, unless the region already names it; "No tax"
    // when none was charged. The rule IPRO.Web's InvoiceText.TaxLabel prints invoices with (that project is out of reach
    // from here).
    public static string TaxLabel(string? region, decimal rate, decimal taxAmount)
    {
        var name = (region ?? string.Empty).Trim();
        if (taxAmount <= 0 && rate <= 0) return "No tax";
        if (name.Length == 0) return "Tax";
        if (name.Contains('%')) return name;
        return $"{name} {(rate * 100m).ToString("0.###", CultureInfo.InvariantCulture)}%";
    }

    // "-$339.00": two decimals always (a tax document), the sign before the dollar.
    public static string Money(decimal amount) =>
        (amount < 0m ? "-$" : "$") + Math.Abs(amount).ToString("#,0.00", CultureInfo.InvariantCulture);

    private static string Clip(string value, int length) => value.Length <= length ? value : value[..length];

    // The resolution note keeps what it said and gains the refund id; when the column would overflow, the older text
    // gives way, never the id.
    private static string WithSuffix(string? note, string suffix)
    {
        var start = note ?? string.Empty;
        var room = ResolutionNoteLength - suffix.Length;
        if (room <= 0) return Clip(suffix.Trim(), ResolutionNoteLength);
        return (start.Length <= room ? start : start[..room]) + suffix;
    }
}
