using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Scrubby.Web.Models;

namespace Scrubby.Web.Controllers;

public class ErrorController : Controller
{
    [HttpGet("exception")]
    public IActionResult Error()
    {
        var errorModel = new ErrorModel { StatusCode = 500, TraceIdentifier = HttpContext.TraceIdentifier };
        return View("ExceptionError", errorModel);
    }

    [HttpGet("error")]
    public IActionResult GenericError(int statusCode) =>
        View("GenericError", new ErrorModel { StatusCode = statusCode, TraceIdentifier = HttpContext.TraceIdentifier });
}
