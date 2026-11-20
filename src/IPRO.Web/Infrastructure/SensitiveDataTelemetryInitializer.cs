using Microsoft.ApplicationInsights.Channel;
using Microsoft.ApplicationInsights.Extensibility;

namespace IPRO.Web.Infrastructure;

// What is scrubbed, and why, is written in IPRO.Utility.SensitiveTelemetry: one scrub for both apps since 663
// (2026-11-20). Before, this file held its own copy, and only page requests were scrubbed.
public class SensitiveDataTelemetryInitializer : ITelemetryInitializer
{
    public void Initialize(ITelemetry telemetry) => IPRO.Utility.SensitiveTelemetry.Scrub(telemetry);
}
