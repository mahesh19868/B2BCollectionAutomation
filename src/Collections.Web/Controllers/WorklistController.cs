using Collections.Web.Models;
using Collections.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Collections.Web.Controllers;

public sealed class WorklistController : AppController
{
    private readonly WorklistService _worklist;

    public WorklistController(WorklistService worklist)
    {
        _worklist = worklist;
    }

    public async Task<IActionResult> Index(WorklistFilter filter, CancellationToken ct)
    {
        var result = await _worklist.GetAsync(filter, ct);
        return View(new WorklistModel
        {
            Filter = filter,
            Result = result,
            // Passed to quick actions so the user returns to the same filtered page.
            CurrentUrl = Request.Path + Request.QueryString,
        });
    }
}
