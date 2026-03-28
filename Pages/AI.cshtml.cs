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

    // Joke of the day
    public string JokeOfTheDay { get; set; } = "";

    // Today's exercises for the preview list
    public List<ExercisePreview> TodaysExercises { get; set; } = new();

    // Streak & motivational data
    public int CompletedSessions { get; set; }
    public int DaysRemaining { get; set; }
    public string? NextMilestone { get; set; }

    // Supplement reminder
    public List<string> UpcomingSupplements { get; set; } = new();

    private static DateTime GetPacificNow()
    {
        var pst = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pst);
    }

    public async Task OnGetAsync()
    {
        string userId = User.Identity?.Name ?? "default";
        var pacificNow = GetPacificNow();
        string todayDate = pacificNow.ToString("yyyy-MM-dd");

        // Pick a random joke
        JokeOfTheDay = GetRandomJoke();

        try
        {
            // Enrollment & week
            var enrollment = await _repository.GetActiveEnrollmentAsync(userId);
            if (enrollment != null)
            {
                ProgramName = enrollment.ProgramName ?? "Mobility Program";
                if (DateTime.TryParse(enrollment.StartDate, out var enrollStart))
                {
                    int daysSince = (pacificNow.Date - enrollStart.Date).Days;
                    Week = Math.Max(1, daysSince / 7 + 1);
                    DaysRemaining = Math.Max(0, 28 - daysSince);
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
            var checkedExerciseIds = dailyChecks
                .Where(c => c.ItemType == "step" && c.Checked)
                .Select(c => c.ItemId)
                .ToHashSet();

            // Build today's exercise preview
            var allExercises = await _repository.GetAllExercisesAsync();
            var exerciseMap = allExercises.ToDictionary(e => e.Id, e => e);
            foreach (var entry in planEntries)
            {
                if (exerciseMap.TryGetValue(entry.ExerciseId, out var ex))
                {
                    TodaysExercises.Add(new ExercisePreview
                    {
                        Name = ex.Name,
                        Category = entry.Category ?? ex.Category,
                        Targets = ex.Targets ?? "",
                        Rx = entry.Rx ?? ex.DefaultRx ?? "",
                        Done = checkedExerciseIds.Contains(entry.ExerciseId)
                    });
                }
            }

            // Supplements
            var supplements = await _repository.GetUserSupplementsForDateAsync(userId, todayDate);
            SupplementsTotal = supplements.Count;
            SupplementsDone = dailyChecks.Count(c => c.ItemType == "supplement" && c.Checked);

            // Upcoming supplements (not yet taken)
            var takenSupIds = dailyChecks
                .Where(c => c.ItemType == "supplement" && c.Checked)
                .Select(c => c.ItemId)
                .ToHashSet();
            UpcomingSupplements = supplements
                .Where(s => !takenSupIds.Contains(s.Id))
                .Select(s => $"{s.Name} ({s.Dose})")
                .Take(3)
                .ToList();

            // Milestones
            var milestones = await _repository.GetUserMilestonesAsync(userId);
            MilestonesTotal = milestones.Count;
            MilestonesDone = milestones.Count(m => !string.IsNullOrEmpty(m.AchievedDate));
            NextMilestone = milestones.FirstOrDefault(m => string.IsNullOrEmpty(m.AchievedDate))?.Name;

            // Session streak (count of completed session logs)
            var sessions = await _repository.GetSessionLogsAsync(userId);
            CompletedSessions = sessions.Count(s => s.StepsDone > 0);
        }
        catch
        {
            // Fail silently — page shows defaults
        }
    }

    private static string GetRandomJoke()
    {
        var jokes = new[]
        {
            "My knees sound like a haunted house every time I use the stairs.",
            "I don't need an alarm clock. My joints crack so loud they wake up the whole house.",
            "At 20, I could touch my toes. At 40, I'm happy if I can see them.",
            "My doctor said I should do lunges to stay in shape. That would be a big step forward.",
            "I told my knee to stop making noise. It said it was just trying to get a-joint-ment.",
            "The only snaps, crackles, and pops I get now are from my body, not my cereal.",
            "I stretched this morning and my back made a sound like bubble wrap at a birthday party.",
            "My hip flexors are so tight, they have their own zip code.",
            "My body doesn't wake up anymore — it negotiates with gravity first.",
            "I used to be flexible. Now I pull a muscle reaching for the TV remote.",
            "My joints predict the weather better than any app on my phone.",
            "Getting out of bed used to take 2 seconds. Now it's a 5-stage process with sound effects.",
            "My physical therapist laughed when I said I wanted to touch my toes. Then I laughed. Then we both cried.",
            "I tried yoga once. The instructor said 'listen to your body.' My body said 'absolutely not.'",
            "Age is just a number. But so is your creak-per-squat ratio, and mine is off the charts.",
            "My knees are like popcorn — they pop at random and everyone around me can hear it.",
            "I used to party until 2 AM. Now my joints party every time I stand up.",
            "I don't stretch before exercise. I stretch before bending down to tie my shoes.",
            "My ankles crack so loud, my neighbors asked if I was making popcorn.",
            "Whoever said 'no pain, no gain' clearly never tried standing up after sitting on the floor.",
            "I'm not old, I'm just... pre-owned with some joint noise.",
            "My morning stretch routine sounds like someone stepping on Legos in a hallway.",
            "I went to the gym and the trainer asked my fitness level. I said 'I can open a jar without crying.'",
            "My joints have more complaints than a Yelp review section.",
            "I thought my knee cracked because of arthritis. Turns out it's just applauding me for trying.",
        };

        // Use the day of year so the joke changes daily but stays consistent within a day
        int index = GetPacificNow().DayOfYear % jokes.Length;
        return jokes[index];
    }
}

public class ExercisePreview
{
    public string Name { get; set; } = "";
    public string Category { get; set; } = "";
    public string Targets { get; set; } = "";
    public string Rx { get; set; } = "";
    public bool Done { get; set; }
}
