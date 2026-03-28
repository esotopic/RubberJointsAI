using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RubberJointsAI.Data;
using RubberJointsAI.Models;

namespace RubberJointsAI.Pages
{
    [Authorize]
    public class IndexModel : PageModel
    {
        private readonly RubberJointsAIRepository _repository;

        public TodayViewModel ViewModel { get; set; } = new();

        [BindProperty(SupportsGet = true)]
        public string? Date { get; set; }

        public IndexModel(RubberJointsAIRepository repository)
        {
            _repository = repository;
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
                // Check enrollment
                var enrollment = await _repository.GetActiveEnrollmentAsync(userId);
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

                // Run independent queries in parallel for speed
                var planTask = _repository.GetUserDailyPlanAsync(userId, selectedDateStr);
                var settingsTask = _repository.GetUserSettingsAsync(userId);
                var exercisesTask = _repository.GetAllExercisesAsync();
                var checksTask = _repository.GetDailyChecksAsync(userId, selectedDateStr);
                var supplementsTask = _repository.GetUserSupplementsForDateAsync(userId, selectedDateStr);

                await Task.WhenAll(planTask, settingsTask, exercisesTask, checksTask, supplementsTask);

                var planEntries = await planTask;
                var settings = await settingsTask;
                var disabledToolIds = (settings?.DisabledTools ?? "").Split(',', System.StringSplitOptions.RemoveEmptyEntries).ToHashSet();

                // Filter out disabled tools
                planEntries = planEntries.Where(e => !disabledToolIds.Contains(e.ExerciseId)).ToList();

                string dayType = planEntries.Count > 0 ? planEntries[0].DayType : "rest";
                var dayName = selectedDate.DayOfWeek.ToString();

                (string sessionType, int estMinutes, string location) = GetSessionDetails(dayType);

                var allExercises = await exercisesTask;
                var exerciseMap = allExercises.ToDictionary(e => e.Id);

                var dailyChecks = await checksTask;
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

                // Supplements already fetched in parallel above
                var userSupplements = await supplementsTask;
                var supplementChecks = new List<SupplementCheck>();
                foreach (var supp in userSupplements)
                {
                    // Use timeGroup index as StepIndex so same supplement in different groups gets separate checks
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
