using System.Diagnostics;
using Collections.Web.Models;
using Collections.Web.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Collections.Web.Controllers;

public sealed class HomeController : AppController
{
    private readonly DashboardService _dashboard;

    public HomeController(DashboardService dashboard)
    {
        _dashboard = dashboard;
    }

    public async Task<IActionResult> Index(CancellationToken ct) => View(await _dashboard.GetAsync(ct));

    [AllowAnonymous, ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error() => View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });

    [AllowAnonymous]
    public IActionResult Status(int code)
    {
        Response.StatusCode = code is >= 400 and < 600 ? code : 404;
        return View(code);
    }
}
