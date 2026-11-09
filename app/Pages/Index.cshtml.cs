using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace App.Pages;

public class IndexModel(AppInfo info, BoardService boards) : PageModel
{
    public AppInfo Info { get; } = info;
    public Board Board { get; private set; } = new([], [], IsSample: false);
    public IReadOnlyList<Session> Results { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Board = await boards.GetAsync(cancellationToken);
        Results = Schedule.Sessions;
    }

    // htmx polls this and swaps the live section in place.
    public async Task<PartialViewResult> OnGetLiveAsync(CancellationToken cancellationToken) =>
        Partial("_Live", await boards.GetAsync(cancellationToken));

    // htmx requests this for the search box and swaps the list in place.
    public PartialViewResult OnGetResults(string? q) => Partial("_Results", Schedule.Filter(q));
}
