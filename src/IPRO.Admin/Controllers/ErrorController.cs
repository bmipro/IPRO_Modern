using System.Diagnostics;
using IPRO.Utility;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Admin.Controllers;

// 456 (2026-09-02): the branded page for any status the pipeline ends with and no body was written.
// Reached by UseStatusCodePagesWithReExecute (the original path is in IStatusCodeReExecuteFeature)
// and by UseExceptionHandler("/error/500"). Anonymous on purpose: a signed-out visitor's 404 is
// still a 404. The real status code is kept, so monitors and search engines see the truth.
[AllowAnonymous]
public class ErrorController : Controller
{
    [Route("error/{code:int}")]
    public IActionResult Status(int code)
    {
        var original = HttpContext.Features.Get<IStatusCodeReExecuteFeature>()?.OriginalPath
                       ?? HttpContext.Features.Get<IExceptionHandlerPathFeature>()?.Path
                       ?? Request.Path.Value;
        var text = StatusPagePolicy.Describe(code);
        var back = new StatusPageBackLink("/", "Back to the dashboard");

        Response.StatusCode = code;
        return View("Status", new StatusPageModel
        {
            Code = code,
            Title = text.Title,
            Message = text.Message,
            BackHref = back.Href,
            BackLabel = back.Label,
            RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier
        });
    }
}
