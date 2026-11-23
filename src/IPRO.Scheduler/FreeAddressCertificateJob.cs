using Hangfire;
using IPRO.Utility;
using Microsoft.Extensions.Logging;

namespace IPRO.Scheduler;

// 675 (2026-11-23): once a day, FreeAddressCertificateService renews the *.247advisers.com certificate when it is
// within 30 days of expiry (or was not uploaded by this job), from Let's Encrypt. With FreeAddressCertificate__Mode
// Off (the default) it does nothing.
//
// A failure throws, so the row on the Job Scheduler goes red; the next morning tries again, and
// CertificateExpiryJob warns 21 days before expiry if it keeps failing. No automatic retry: a failed check counts
// against Let's Encrypt's limits, and one try a day is plenty with 30 days in hand.
[AutomaticRetry(Attempts = 0)]
public class FreeAddressCertificateJob
{
    private readonly FreeAddressCertificateService _service;
    private readonly ILogger<FreeAddressCertificateJob> _logger;

    public FreeAddressCertificateJob(FreeAddressCertificateService service, ILogger<FreeAddressCertificateJob> logger)
    {
        _service = service;
        _logger = logger;
    }

    [DisableConcurrentExecution(timeoutInSeconds: 20 * 60)]
    public async Task RunAsync()
    {
        var outcome = await _service.RunAsync(CancellationToken.None);
        // Warning when something changed or was tested, so it reaches the container log (Information does not).
        if (!outcome.Routine)
            _logger.LogWarning("Free-address certificate: {Summary}", outcome.Summary);
        else
            _logger.LogInformation("Free-address certificate: {Summary}", outcome.Summary);
    }
}
