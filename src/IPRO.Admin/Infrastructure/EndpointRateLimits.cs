using System.Globalization;
using System.Threading.RateLimiting;
using IPRO.Utility;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.RateLimiting;

namespace IPRO.Admin.Infrastructure;

// 582 (2026-10-16). The sign-in limit belongs to the sign-in ACTION, not to an address. It was a rule in
// appsettings.json naming "POST:/Admin/Login", and the rate-limit library compares the path as typed,
// while the route reaches the same action at /Admin/Login/<anything> (its optional id): each suffix
// had its own allowance, so the 10-in-5-minutes brake on SuperAdmin sign-in (no lockout behind it) was
// a path suffix away from nothing. Found with the same weakness on IPRO.Web's public forms (578's
// security review); see IPRO.Web's EndpointRateLimits for the whole story.
//
// AdminController.Login carries [EnableRateLimiting(SignIn)] and ASP.NET Core's own limiter, right after
// routing (Program.cs), counts the action however its address was written, per visitor
// (RateLimitVisitor). appsettings.json keeps only the library's general rule.
public static class EndpointRateLimits
{
    public const string SignIn = "sign-in";

    // How many requests in how long, per action and visitor.
    public static readonly IReadOnlyDictionary<string, (int Limit, TimeSpan Window)> Policies =
        new Dictionary<string, (int Limit, TimeSpan Window)>
        {
            [SignIn] = (10, TimeSpan.FromMinutes(5)),
        };

    public static IServiceCollection AddEndpointRateLimits(this IServiceCollection services) =>
        services.AddRateLimiter(options =>
        {
            // No body: a browser gets the branded "Too many requests" page (StatusPagePolicy).
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

    // The action itself, the same whichever spelling reached it.
    private static string ActionOf(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        var action = endpoint?.Metadata.GetMetadata<ControllerActionDescriptor>();
        return action != null
            ? action.ControllerTypeInfo.FullName + "." + action.MethodInfo.Name
            : endpoint?.DisplayName ?? string.Empty;
    }
}
