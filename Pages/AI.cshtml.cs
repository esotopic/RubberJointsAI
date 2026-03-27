using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace RubberJointsAI.Pages;

[Authorize]
public class AIModel : PageModel
{
    public void OnGet()
    {
    }
}
