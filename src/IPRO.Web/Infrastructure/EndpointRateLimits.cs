using System.Globalization;
using System.Threading.RateLimiting;
using IPRO.Utility;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.RateLimiting;

namespace IPRO.Web.Infrastructure;

// 582 (2026-10-16). The limits on sign-in, registration, password reset, the promo-code check, the
// public forms, poll votes, domain retries and the prospect preview belong to the ACTIONS, not to
// addresses. They were rules in appsettings.json naming exact paths ("POST:/PublicWebsite/SubmitLead"),
// and the rate-limit library compares the path as typed, while routing reaches the same action from
// other spellings: /PublicWebsite/SubmitLead/x (the default route's optional id), /portal/... Each
// spelling had an allowance of its own, and an unlisted one had none beyond the general 120 a minute
// per address and path -- so the 3-in-5-minutes brake on the contact forms (and on guessing their
// security sum) and the 10-in-5-minutes brake on sign-in (with no lockout behind it) were a path
// suffix away from nothing. The domain-retry rule never applied at all: the button posts to
// /portal/Website/RetryDomain/<id>. Found by the 578 security review.
//
// Now each limited action carries [EnableRateLimiting(<policy>)], and ASP.NET Core's own limiter,
// which runs right after routing (Program.cs), counts the action the request reached, however its
// address was written. The allowance is per action and visitor (RateLimitVisitor): the contact form
// and the testimonial form keep separate allowances as before, and every address of one action
// shares one. The numbers are the ones the rules had. appsettings.json keeps only the library's
// general rule; a rule naming an address there would bring the weakness back (RateLimitByAction582Tests).
public static class EndpointRateLimits
{
    public const string SignIn = "sign-in";
    public const string Registration = "registration";
    public const string PasswordReset = "password-reset";
    public const string PromoCode = "promo-code";
    public const string PublicForm = "public-form";
    public const string PollVote = "poll-vote";
    public const string DomainRetry = "domain-retry";
    public const string Preview = "prospect-preview";
    public const string DomainGuide = "domain-guide";

    // How many requests in how long, per action and visitor.
    public static readonly IReadOnlyDictionary<string, (int Limit, TimeSpan Window)> Policies =
        new Dictionary<string, (int Limit, TimeSpan Window)>
        {
            [SignIn] = (10, TimeSpan.FromMinutes(5)),
            [Registration] = (5, TimeSpan.FromHours(1)),
            [PasswordReset] = (5, TimeSpan.FromMinutes(5)),
            [PromoCode] = (5, TimeSpan.FromMinutes(5)),
            // Well under the 100-an-hour sending cap: the forms email the adviser.
            [PublicForm] = (3, TimeSpan.FromMinutes(5)),
            [PollVote] = (20, TimeSpan.FromMinutes(5)),
            // Each retry calls Azure.
            [DomainRetry] = (5, TimeSpan.FromMinutes(1)),
            [Preview] = (30, TimeSpan.FromMinutes(1)),
            // 595: the Domain page's guide looks again every 20 seconds per guide on screen, plus Check now and I need
            // help; the looks are cached and the Azure check spaced out behind it. Per address, generously (an office's
            // advisers share one); each adviser is held to 20 a minute by WebsiteController.GuideLooks.
            [DomainGuide] = (60, TimeSpan.FromMinutes(1)),
        };

    public static IServiceCollection AddEndpointRateLimits(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            // No body: a browser gets the branded "Too many requests" page (StatusPagePolicy), as for any
            // other status without one; anything else gets the bare status and when to come back.
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    context.HttpContext.Response.Headers.RetryAfter =
                        Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger(typeof(EndpointRateLimits).FullName!)
                    .LogInformation("Rate limit reached: {Method} {Path} for {Action} from {Visitor}",
                        context.HttpContext.Request.Method, context.HttpContext.Request.Path.Value,
                        ActionOf(context.HttpContext), RateLimitVisitor.Key(context.HttpContext));
                return ValueTask.CompletedTask;
            };
            foreach (var (name, (limit, window)) in Policies)
            {
                options.AddPolicy(name, context => RateLimitPartition.GetFixedWindowLimiter(
                    ActionOf(context) + " " + RateLimitVisitor.Key(context),
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = limit,
                        Window = window,
                        QueueLimit = 0,
                        AutoReplenishment = true,
                    }));
            }
        });

    // The action itself, the same whichever route or spelling reached it.
    private static string ActionOf(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        var action = endpoint?.Metadata.GetMetadata<ControllerActionDescriptor>();
        return action != null
            ? action.ControllerTypeInfo.FullName + "." + action.MethodInfo.Name
            : endpoint?.DisplayName ?? string.Empty;
    }
}
