using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace App.Pages;

public class SnapshotModel(VenueService venues) : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Room { get; set; }

    public VenueSnapshot Venue { get; private set; } = App.Venue.Default;

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        Venue = await venues.GetAsync(cancellationToken);
}
