namespace IPRO.Web.Infrastructure;

// 496 (2026-09-18): the platform's own contact details, in one place. The home page and both landing
// pages have always said "One provider. One login. One number to call" -- and no number appeared on
// any public page. The owner's decision: publish it, so the line is true. Every public surface and
// the welcome email render it from here.
public static class PlatformContact
{
    public const string SupportPhone = "1-416-363-2220";
    public const string SupportPhoneHref = "tel:+14163632220";
}
