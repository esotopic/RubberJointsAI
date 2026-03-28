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

    // Status banner data (server-rendered, no AI call)
    public int Week { get; set; } = 1;
    public int TotalWeeks { get; set; } = 4;
    public string Phase { get; set; } = "Foundation";
    public string DayType { get; set; } = "rest";
    public int ExercisesTotal { get; set; }
    public int ExercisesDone { get; set; }
    public int SupplementsTotal { get; set; }
    public int SupplementsDone { get; set; }
    public int MilestonesTotal { get; set; }
    public int MilestonesDone { get; set; }
    public string ProgramName { get; set; } = "";

    public async Task OnGetAsync()
    {
        string userId = User.Identity?.Name ?? "default";
        string todayDate = DateTime.UtcNow.ToString("yyyy-MM-dd");

        try
        {
            // Enrollment & week
            var enrollment = await _repository.GetActiveEnrollmentAsync(userId);
            if (enrollment != null)
            {
                ProgramName = enrollment.ProgramName ?? "Mobility Program";
                if (DateTime.TryParse(enrollment.StartDate, out var enrollStart))
                {
                    int daysSince = (DateTime.UtcNow.Date - enrollStart.Date).Days;
                    Week = Math.Max(1, daysSince / 7 + 1);
                }
                Phase = Week <= 2 ? "Foundation" : "Progression";
            }

            // Today's plan
            var planEntries = await _repository.GetUserDailyPlanAsync(userId, todayDate);
            var settings = await _repository.GetUserSettingsAsync(userId);
            var disabledIds = (settings?.DisabledTools ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
            planEntries = planEntries.Where(e => !disabledIds.Contains(e.ExerciseId)).ToList();
            DayType = planEntries.FirstOrDefault()?.DayType ?? "rest";
            ExercisesTotal = planEntries.Count;

            // Completion
            var dailyChecks = await _repository.GetDailyChecksAsync(userId, todayDate);
            ExercisesDone = dailyChecks.Count(c => c.ItemType == "step" && c.Checked);

            // Supplements
            var supplements = await _repository.GetUserSupplementsForDateAsync(userId, todayDate);
            SupplementsTotal = supplements.Count;
            SupplementsDone = dailyChecks.Count(c => c.ItemType == "supplement" && c.Checked);

            // Milestones
            var milestones = await _repository.GetUserMilestonesAsync(userId);
            MilestonesTotal = milestones.Count;
            MilestonesDone = milestones.Count(m => !string.IsNullOrEmpty(m.AchievedDate));
        }
        catch
        {
            // Fail silently — banner just shows defaults
        }
    }
}
