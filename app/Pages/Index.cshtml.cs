using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace App.Pages;

public class IndexModel(AppInfo info, Backends backends) : PageModel
{
    public AppInfo Info { get; } = info;
    public Backends Backends { get; } = backends;
    public IReadOnlyList<Session> Results { get; private set; } = [];

    public void OnGet() => Results = Schedule.Sessions;

    // htmx requests this for the search box and swaps the list in place.
    public PartialViewResult OnGetResults(string? q) => Partial("_Results", Schedule.Filter(q));
}
