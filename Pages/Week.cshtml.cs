using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RubberJointsAI.Data;
using RubberJointsAI.Models;

namespace RubberJointsAI.Pages
{
    [Authorize]
    public class WeekModel : PageModel
    {
        private readonly RubberJointsAIRepository _repository;

        public List<WeekDayData> Days { get; set; } = new();
        public int Week { get; set; } = 1;
        public int Phase { get; set; } = 1;
        public int WeekOffset { get; set; } = 0;
        public string? ErrorMessage { get; set; }

        public WeekModel(RubberJointsAIRepository repository)
        {
            _repository = repository;
        }

        public async Task OnGetAsync(int? wo)
        {
            WeekOffset = wo ?? 0;
            string userId = User.Identity?.Name ?? "default";

            try
            {
                // Get userId
                // Get active enrollment
                var enrollment = await _repository.GetActiveEnrollmentAsync(userId);
                if (enrollment == null)
                {
                    ErrorMessage = "No active enrollment found.";
                    return;
                }

                // Calculate week from enrollment.StartDate
                var today = DateTime.UtcNow;
                if (DateTime.TryParse(enrollment.StartDate, out var enrollStart))
                {
                    int daysSinceStart = (today.AddDays(WeekOffset * 7) - enrollStart).Days;
                    Week = Math.Max(1, daysSinceStart / 7 + 1);
                    Phase = Week <= 2 ? 1 : 2;
                }

                // Get Monday and Sunday of the target week
                var refDate = today.AddDays(WeekOffset * 7);
                var dayOfWeek = refDate.DayOfWeek;
                var monday = refDate.AddDays(-(int)dayOfWeek + (int)DayOfWeek.Monday);
                if (dayOfWeek == DayOfWeek.Sunday) monday = monday.AddDays(-7);
                var sunday = monday.AddDays(6);

                string mondayStr = monday.ToString("yyyy-MM-dd");
                string sundayStr = sunday.ToString("yyyy-MM-dd");

                // Get all plan entries for the week
                var planEntries = await _repository.GetUserDailyPlanRangeAsync(userId, mondayStr, sundayStr);

                // Group plan entries by Date
                var groupedByDate = planEntries
                    .GroupBy(p => p.Date)
                    .ToDictionary(g => g.Key, g => g.ToList());

                // Get user settings for disabled tools filtering
                var settings = await _repository.GetUserSettingsAsync(userId);
                var disabledToolIds = (settings?.DisabledTools ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();

                var allExercises = await _repository.GetAllExercisesAsync();
                var exerciseMap = allExercises.ToDictionary(e => e.Id);
                var allSupplements = await _repository.GetSupplementsAsync();

                string[] dayNames = { "Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun" };

                for (int i = 0; i < 7; i++)
                {
                    var date = monday.AddDays(i);
                    var dateStr = date.ToString("yyyy-MM-dd");
                    var isToday = date.Date == today.Date;
                    var isFuture = date.Date > today.Date;

                    // Get plan entries for this date
                    var dayPlanEntries = new List<UserDailyPlanEntry>();
                    if (groupedByDate.TryGetValue(dateStr, out var entries))
                    {
                        dayPlanEntries = entries;
                    }

                    // DayType from plan entries, or "rest" if none
                    string dayType = dayPlanEntries.FirstOrDefault()?.DayType ?? "rest";
                    var sessionLabel = GetSessionLabel(dayType);
                    var estMinutes = GetEstMinutes(dayType);

                    var dayData = new WeekDayData
                    {
                        DayName = dayNames[i],
                        DateLabel = date.ToString("MMM d"),
                        DateStr = dateStr,
                        DayType = dayType,
                        SessionLabel = sessionLabel,
                        EstMinutes = estMinutes,
                        IsToday = isToday,
                        IsFuture = isFuture,
                        Categories = new List<CategoryProgress>()
                    };

                    if (!isFuture)
                    {
                        // Filter out disabled tools
                        var filteredEntries = dayPlanEntries
                            .Where(p => !disabledToolIds.Contains(p.ExerciseId))
                            .ToList();

                        // Get daily checks for that date
                        var dailyChecks = await _repository.GetDailyChecksAsync(userId, dateStr);
                        var checkMap = dailyChecks.ToDictionary(c => $"{c.ItemType}:{c.ItemId}:{c.StepIndex}", c => c.Checked);

                        // Calculate category progress using plan entries instead of SessionSteps
                        var catStats = new Dictionary<string, (int total, int done)>();
                        for (int j = 0; j < filteredEntries.Count; j++)
                        {
                            var entry = filteredEntries[j];
                            if (!exerciseMap.TryGetValue(entry.ExerciseId, out var exercise)) continue;
                            var cat = exercise.Category;

                            if (!catStats.ContainsKey(cat)) catStats[cat] = (0, 0);
                            var curr = catStats[cat];
                            curr.total++;

                            // Check key uses entry.Id instead of step.Id
                            string checkKey = $"step:{entry.Id}:{j}";
                            if (checkMap.TryGetValue(checkKey, out var isChecked) && isChecked)
                                curr.done++;

                            catStats[cat] = curr;
                        }

                        // Supplement progress
                        var suppChecks = dailyChecks.Where(c => c.ItemType == "supplement" && c.Checked).Count();
                        var suppTotal = allSupplements.Count;

                        // Build category list in order
                        var catOrder = new[] {
                            ("warmup_tool", "Warm-up", "#ff9500"),
                            ("mobility", "Mobility", "#34c759"),
                            ("strength", "Strength", "#af52de"),
                            ("recovery_tool", "Recovery", "#4a6cf7"),
                        };

                        foreach (var (catKey, label, color) in catOrder)
                        {
                            if (catStats.TryGetValue(catKey, out var stat) && stat.total > 0)
                            {
                                dayData.Categories.Add(new CategoryProgress
                                {
                                    Label = label,
                                    Color = color,
                                    Done = stat.done,
                                    Total = stat.total
                                });
                            }
                        }

                        // Add vitamins
                        if (suppTotal > 0)
                        {
                            dayData.Categories.Add(new CategoryProgress
                            {
                                Label = "Vitamins",
                                Color = "#ffcc00",
                                Done = suppChecks,
                                Total = suppTotal
                            });
                        }
                    }

                    Days.Add(dayData);
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = "Unable to load weekly data. Some features may be unavailable.";
            }
        }

        private string GetSessionLabel(string dayType)
        {
            return dayType switch
            {
                "gym" => "Full Gym Session",
                "home" => "Home Mobility + Recovery",
                "recovery" => "Active Recovery",
                "rest" => "Rest + Passive Recovery",
                _ => ""
            };
        }

        private int GetEstMinutes(string dayType)
        {
            return dayType switch
            {
                "gym" => 85,
                "home" => 55,
                "recovery" => 60,
                "rest" => 40,
                _ => 0
            };
        }
    }

    public class WeekDayData
    {
        public string DayName { get; set; } = "";
        public string DateLabel { get; set; } = "";
        public string DateStr { get; set; } = "";
        public string DayType { get; set; } = "";
        public string SessionLabel { get; set; } = "";
        public int EstMinutes { get; set; }
        public bool IsToday { get; set; }
        public bool IsFuture { get; set; }
        public List<CategoryProgress> Categories { get; set; } = new();
    }

    public class CategoryProgress
    {
        public string Label { get; set; } = "";
        public string Color { get; set; } = "";
        public int Done { get; set; }
        public int Total { get; set; }
        public int Percent => Total > 0 ? (int)Math.Round((double)Done / Total * 100) : 0;
    }
}
