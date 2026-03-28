using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using RubberJointsAI.Data;
using RubberJointsAI.Models;

namespace RubberJointsAI.Pages
{
    [Authorize]
    public class IndexModel : PageModel
    {
        private readonly RubberJointsAIRepository _repository;
        private readonly IMemoryCache _cache;

        public TodayViewModel ViewModel { get; set; } = new();

        [BindProperty(SupportsGet = true)]
        public string? Date { get; set; }

        public IndexModel(RubberJointsAIRepository repository, IMemoryCache cache)
        {
            _repository = repository;
            _cache = cache;
        }

        private static DateTime GetPacificNow()
        {
            var pst = TimeZoneInfo.FindSystemTimeZoneById("America/Los_Angeles");
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, pst);
        }

        public async Task OnGetAsync()
        {
            string userId = User.Identity?.Name ?? "default";
            var actualToday = GetPacificNow();
            string todayDateStr = actualToday.ToString("yyyy-MM-dd");

            // Parse selected date from query param, default to today
            DateTime selectedDate = actualToday;
            if (!string.IsNullOrEmpty(Date) && DateTime.TryParse(Date, out var parsed))
            {
                selectedDate = parsed;
            }
            string selectedDateStr = selectedDate.ToString("yyyy-MM-dd");
            bool isFuture = selectedDate.Date > actualToday.Date;
            bool isToday = selectedDate.Date == actualToday.Date;

            try
            {
                // Try prefetch cache first (only for today's date — AI page warms this in background)
                string cacheKey = $"today-prefetch:{userId}:{selectedDateStr}";
                Dictionary<string, object?>? cached = null;
                if (isToday && _cache.TryGetValue(cacheKey, out cached) && cached != null)
                {
                    _cache.Remove(cacheKey); // one-time use
                }

                // === Resolve all data: from cache if available, otherwise from DB ===
                UserEnrollment? enrollment;
                List<UserDailyPlanEntry> planEntries;
                UserSettings? settings;
                List<Exercise> allExercises;
                List<DailyCheck> dailyChecks;
                List<Supplement> userSupplements;

                if (cached != null)
                {
                    // Use prefetched data from AI page background cache
                    enrollment = cached.TryGetValue("enrollment", out var e) ? e as UserEnrollment : null;
                    planEntries = cached.TryGetValue("plan", out var p) && p is List<UserDailyPlanEntry> pl ? pl : new();
                    settings = cached.TryGetValue("settings", out var s) ? s as UserSettings : null;
                    allExercises = cached.TryGetValue("exercises", out var ex) && ex is List<Exercise> exl ? exl : new();
                    dailyChecks = cached.TryGetValue("checks", out var ch) && ch is List<DailyCheck> chl ? chl : new();
                    userSupplements = cached.TryGetValue("supplements", out var su) && su is List<Supplement> sul ? sul : new();
                }
                else
                {
                    // Normal path: run all independent queries in parallel
                    var enrollmentTask = _repository.GetActiveEnrollmentAsync(userId);
                    var planTask = _repository.GetUserDailyPlanAsync(userId, selectedDateStr);
                    var settingsTask = _repository.GetUserSettingsAsync(userId);
                    var exercisesTask = _repository.GetAllExercisesAsync();
                    var checksTask = _repository.GetDailyChecksAsync(userId, selectedDateStr);
                    var supplementsTask = _repository.GetUserSupplementsForDateAsync(userId, selectedDateStr);

                    await Task.WhenAll(enrollmentTask, planTask, settingsTask, exercisesTask, checksTask, supplementsTask);

                    enrollment = await enrollmentTask;
                    planEntries = await planTask;
                    settings = await settingsTask;
                    allExercises = await exercisesTask;
                    dailyChecks = await checksTask;
                    userSupplements = await supplementsTask;
                }

                if (enrollment == null)
                {
                    ViewModel.ErrorMessage = "NO_ENROLLMENT";
                    return;
                }

                // Calculate week from enrollment start date
                int week = 1;
                if (DateTime.TryParse(enrollment.StartDate, out var enrollStart))
                {
                    int daysSince = (selectedDate - enrollStart).Days;
                    week = Math.Max(1, daysSince / 7 + 1);
                }

                var disabledToolIds = (settings?.DisabledTools ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

                // Filter out disabled tools
                planEntries = planEntries.Where(e => !disabledToolIds.Contains(e.ExerciseId)).ToList();

                string dayType = planEntries.Count > 0 ? planEntries[0].DayType : "rest";
                var dayName = selectedDate.DayOfWeek.ToString();

                (string sessionType, int estMinutes, string location) = GetSessionDetails(dayType);

                var exerciseMap = allExercises.ToDictionary(e => e.Id);
                var checkMap = dailyChecks.ToDictionary(c => $"{c.ItemType}:{c.ItemId}:{c.StepIndex}", c => c.Checked);

                // Build steps from plan entries
                var todaySteps = new List<TodayStep>();
                for (int i = 0; i < planEntries.Count; i++)
                {
                    var entry = planEntries[i];
                    if (exerciseMap.TryGetValue(entry.ExerciseId, out var exercise))
                    {
                        string checkKey = $"step:{entry.Id}:{i}";
                        bool isChecked = checkMap.TryGetValue(checkKey, out var val) && val;

                        todaySteps.Add(new TodayStep
                        {
                            Index = i,
                            SessionStepId = entry.Id,
                            Exercise = exercise,
                            Rx = entry.Rx ?? "",
                            Section = entry.Category,
                            Checked = isChecked
                        });
                    }
                }

                // Build supplement checks
                var supplementChecks = new List<SupplementCheck>();
                foreach (var supp in userSupplements)
                {
                    int tgIndex = supp.TimeGroup switch { "am" => 0, "mid" => 1, "pm" => 2, _ => 0 };
                    string checkKey = $"supplement:{supp.Id}:{tgIndex}";
                    bool isChecked = checkMap.TryGetValue(checkKey, out var val) && val;
                    supplementChecks.Add(new SupplementCheck
                    {
                        Supplement = supp,
                        Checked = isChecked
                    });
                }

                // Navigation dates
                var prevDate = selectedDate.AddDays(-1);
                var nextDate = selectedDate.AddDays(1);

                ViewModel = new TodayViewModel
                {
                    Week = week,
                    Phase = week <= 2 ? 1 : 2,
                    DayName = dayName,
                    SessionType = sessionType,
                    DayKey = dayType,
                    EstMinutes = estMinutes,
                    Steps = todaySteps,
                    Supplements = supplementChecks,
                    SelectedDate = selectedDateStr,
                    TodayDate = todayDateStr,
                    SelectedDateLabel = selectedDate.ToString("dddd, MMM d"),
                    PrevDate = prevDate.ToString("yyyy-MM-dd"),
                    NextDate = nextDate.ToString("yyyy-MM-dd"),
                    IsFuture = isFuture,
                    IsToday = isToday
                };
            }
            catch (Exception ex)
            {
                if (ex.Message.Contains("TimeGroup"))
                {
                    // TimeGroup column missing - try adding it directly
                    try { await _repository.AddTimeGroupColumnAsync(); } catch { }
                    ViewModel.ErrorMessage = "Database schema updated. Please refresh the page.";
                }
                else
                {
                    ViewModel.ErrorMessage = "Unable to connect to the database. Please try again in a moment.";
                }
            }
        }

        private (string sessionType, int estMinutes, string location) GetSessionDetails(string dayType)
        {
            return dayType switch
            {
                "gym" => ("Gym Session", 60, "Gym"),
                "home" => ("Home Session", 40, "Home"),
                "recovery" => ("Recovery", 30, "Home"),
                "rest" => ("Rest Day", 0, "Rest"),
                _ => ("Unknown", 0, "")
            };
        }
    }
}
