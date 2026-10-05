using IPRO.Billing;
using IPRO.DataAccess;
using IPRO.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

// 532: Refer a Friend, hourly. A friend whose subscription started is "joined" (the same-PayPal-payer block, then the
// referrer's "joined" email); a friend who stayed makes the reward "earned" (the second monthly payment, or 30 days
// into an annual plan), and its refund is worked out against the referrer's own payments for SuperAdmin -> Refunds.
// Nothing here moves money (ReferralProgram says so at length).
public class ReferralJob
{
    private readonly IPRODbContext _db;
    private readonly IEmailService _email;
    private readonly IConfiguration _configuration;
    private readonly IReferralPayerLookup _payers;
    private readonly ILogger<ReferralJob> _logger;

    public ReferralJob(IPRODbContext db, IEmailService email, IConfiguration configuration, IReferralPayerLookup payers, ILogger<ReferralJob> logger)
    {
        _db = db;
        _email = email;
        _configuration = configuration;
        _payers = payers;
        _logger = logger;
    }

    public async Task RunAsync()
    {
        var report = await ReferralProgram.AdvanceAsync(_db, _email, _configuration, _logger, _payers, DateTime.UtcNow);
        if (report.Joined + report.Blocked + report.Earned + report.NotEarned + report.Planned > 0)
        {
            // Warning: the container log keeps no Information lines, and a money ledger moving is worth a line.
            _logger.LogWarning(
                "Refer a Friend: {Joined} joined, {Blocked} blocked (same PayPal payer), {Emails} joined email(s), {Earned} earned, {NotEarned} not earned, {Planned} refund(s) ready, {Waiting} waiting for a payment to refund against.",
                report.Joined, report.Blocked, report.JoinedEmails, report.Earned, report.NotEarned, report.Planned, report.Waiting);
        }
    }
}
