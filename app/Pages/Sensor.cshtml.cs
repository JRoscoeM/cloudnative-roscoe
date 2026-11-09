using Microsoft.AspNetCore.Mvc.RazorPages;

namespace App.Pages;

public class SensorModel(VenueService venues) : PageModel
{
    public VenueSnapshot Venue { get; private set; } = App.Venue.Default;

    public async Task OnGetAsync(CancellationToken cancellationToken) =>
        Venue = await venues.GetAsync(cancellationToken);
}
