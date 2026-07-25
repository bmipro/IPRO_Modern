using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IPRO.Web.Controllers;

// Unlike IPRO.Admin's equivalent, this never surfaces exception details - every authenticated
// Web user is a paying agent or client, not IPRO staff, so there's no elevated role to gate
// diagnostics behind here.
[AllowAnonymous]
public class HomeController : Controller
{
    public IActionResult Error() => View();
}
