namespace IPRO.Utility;

public static class DomainErrorTranslator
{
    public static string ToAgentMessage(string? rawError)
    {
        if (string.IsNullOrWhiteSpace(rawError))
        {
            return string.Empty;
        }

        if (Contains(rawError, "AuthorizationFailed"))
        {
            return "IPRO could not complete the Azure setup for this domain due to a permissions issue on our side. Our team has been notified — no action needed from you.";
        }

        if (Contains(rawError, "Conflict"))
        {
            return "This domain is already bound to another site in our system. Contact support if this seems wrong.";
        }

        if (Contains(rawError, "InvalidOperation") || Contains(rawError, "BadRequest"))
        {
            return "IPRO could not verify this domain's configuration. Double-check the CNAME record and try again in a few minutes.";
        }

        if (Contains(rawError, "NotFound"))
        {
            return "IPRO's hosting configuration for this domain could not be found. Our team has been notified.";
        }

        if (Contains(rawError, "settings are incomplete") || Contains(rawError, "disabled"))
        {
            return "Domain automation is being finalized on our side. Please check back shortly.";
        }

        if (Contains(rawError, "Waiting for DNS propagation") || Contains(rawError, "DNS has not resolved") || Contains(rawError, "Confirm the CNAME"))
        {
            return rawError;
        }

        return "IPRO is still working on connecting this domain. If this continues for more than a day, contact support.";
    }

    private static bool Contains(string value, string search) =>
        value.Contains(search, StringComparison.OrdinalIgnoreCase);
}
