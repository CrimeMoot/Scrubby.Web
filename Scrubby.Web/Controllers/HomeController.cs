using Microsoft.AspNetCore.Mvc;

namespace Scrubby.Web.Controllers;

public class HomeController : Controller
{
    [HttpGet("/")]
    public IActionResult Index() => RedirectToAction("SearchIcon", "Icon");
}
