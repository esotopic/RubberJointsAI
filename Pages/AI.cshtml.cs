using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RubberJointsAI.Data;

namespace RubberJointsAI.Pages;

[Authorize]
public class AIModel : PageModel
{
    private readonly RubberJointsAIRepository _repository;

    public AIModel(RubberJointsAIRepository repository)
    {
        _repository = repository;
    }

    public int Week { get; set; } = 1;
    public int TotalWeeks { get; set; } = 4;
    public string Phase { get; set; } = "Foundation";
    public bool IsOnboarding { get; set; } = true;
    public int OnboardingStep { get; set; } = 0;

    private static DateTime GetPacificNow()
    {
        var pst = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pst);
    }

    public async Task OnGetAsync()
    {
        string userId = User.Identity?.Name ?? "default";
        var pacificNow = GetPacificNow();

        try
        {
            // Check onboarding state
            var prefs = await _repository.GetUserPreferencesAsync(userId);
            IsOnboarding = prefs == null || prefs.OnboardingStep < 7;
            OnboardingStep = prefs?.OnboardingStep ?? 0;

            // Enrollment info (only if onboarded)
            if (!IsOnboarding)
            {
                var enrollment = await _repository.GetActiveEnrollmentAsync(userId);
                if (enrollment != null)
                {
                    if (DateTime.TryParse(enrollment.StartDate, out var enrollStart))
                    {
                        int daysSince = (pacificNow.Date - enrollStart.Date).Days;
                        Week = Math.Max(1, daysSince / 7 + 1);
                    }
                    Phase = Week <= 2 ? "Foundation" : "Progression";
                }
            }
        }
        catch
        {
            // Fail silently — page shows defaults
        }
    }
}
