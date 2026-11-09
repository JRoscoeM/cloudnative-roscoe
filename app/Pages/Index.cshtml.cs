using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace App.Pages;

public class IndexModel(AppInfo info, BoardService boards, TalkFinder talks, TimeProvider time) : PageModel
{
    public AppInfo Info { get; } = info;
    public Board Board { get; private set; } = new([], [], IsSample: false);
    public IReadOnlyList<TalkResult> Results { get; private set; } = [];

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Board = await boards.GetAsync(cancellationToken);
        Results = TalkSuggestions.ForDisplay(Schedule.Sessions, Board, time.GetUtcNow());
    }

    // htmx polls this and swaps the live section in place.
    public async Task<PartialViewResult> OnGetLiveAsync(CancellationToken cancellationToken) =>
        Partial("_Live", await boards.GetAsync(cancellationToken));

    // htmx requests this for the search box and swaps the list in place.
    public async Task<PartialViewResult> OnGetResultsAsync(string? q, CancellationToken cancellationToken)
    {
        var board = await boards.GetAsync(cancellationToken);
        var sessions = await talks.SearchAsync(q, cancellationToken);
        var now = time.GetUtcNow();
        return Partial("_Results", string.IsNullOrWhiteSpace(q)
            ? TalkSuggestions.ForDisplay(sessions, board, now)
            : TalkSuggestions.ForSearch(sessions, board, now));
    }
}
