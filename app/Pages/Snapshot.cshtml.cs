using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace App.Pages;

public class SnapshotModel : PageModel
{
    [BindProperty(SupportsGet = true)]
    public string? Room { get; set; }
}
