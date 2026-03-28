using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RubberJointsAI.Data;
using RubberJointsAI.Models;

namespace RubberJointsAI.Pages
{
    [Authorize]
    public class SettingsModel : PageModel
    {
        private readonly RubberJointsAIRepository _repository;

        [BindProperty]
        public string? StartDate { get; set; }

        public string? ErrorMessage { get; set; }
        public string? SuccessMessage { get; set; }
        public int CurrentWeek { get; set; }
        public int CurrentPhase { get; set; }

        // Exercises grouped by category
        public List<Exercise> WarmupExercises { get; set; } = new();
        public List<Exercise> MobilityExercises { get; set; } = new();
        public List<Exercise> RecoveryExercises { get; set; } = new();
        public List<Supplement> AllSupplements { get; set; } = new();

        // Selected IDs from user preferences
        public HashSet<string> SelectedExerciseIds { get; set; } = new();
        public HashSet<string> SelectedSupplementIds { get; set; } = new();

        public SettingsModel(RubberJointsAIRepository repository)
        {
            _repository = repository;
        }

        public async Task OnGetAsync()
        {
            string userId = User.Identity?.Name ?? "default";

            try
            {
                var settings = await _repository.GetUserSettingsAsync(userId);
                StartDate = settings?.StartDate;

                var phaseInfo = CalculatePhaseAndWeek(StartDate);
                CurrentWeek = phaseInfo.week;
                CurrentPhase = phaseInfo.phase;

                // Load all exercises and supplements
                var allExercises = await _repository.GetAllExercisesAsync();
                WarmupExercises = allExercises.Where(e => e.Category == "warmup_tool").OrderBy(e => e.Name).ToList();
                MobilityExercises = allExercises.Where(e => e.Category == "mobility").OrderBy(e => e.Name).ToList();
                RecoveryExercises = allExercises.Where(e => e.Category == "recovery_tool").OrderBy(e => e.Name).ToList();
                AllSupplements = await _repository.GetSupplementsAsync();

                // Load user preferences
                var prefs = await _repository.GetUserPreferencesAsync(userId);
                if (prefs != null)
                {
                    SelectedExerciseIds = new HashSet<string>(
                        (prefs.SelectedExercises ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));
                    SelectedSupplementIds = new HashSet<string>(
                        (prefs.SelectedSupplements ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));
                }
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Database error: {ex.Message}";
            }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            string userId = User.Identity?.Name ?? "default";

            try
            {
                var settings = await _repository.GetUserSettingsAsync(userId) ?? new UserSettings { UserId = userId };
                settings.StartDate = StartDate;
                await _repository.SaveUserSettingsAsync(settings);

                SuccessMessage = "Settings saved!";

                var phaseInfo = CalculatePhaseAndWeek(StartDate);
                CurrentWeek = phaseInfo.week;
                CurrentPhase = phaseInfo.phase;

                // Reload data for the page
                await LoadPageData(userId);

                return Page();
            }
            catch (Exception ex)
            {
                ErrorMessage = "Failed to save settings. Please try again.";
                await LoadPageData(userId);
                return Page();
            }
        }

        public async Task<IActionResult> OnPostResetAsync()
        {
            string userId = User.Identity?.Name ?? "default";

            try
            {
                await _repository.ResetAllUserDataAsync(userId);
                return Redirect("/AI");
            }
            catch (Exception ex)
            {
                ErrorMessage = "Failed to reset progress. Please try again.";
                return Page();
            }
        }

        private async Task LoadPageData(string userId)
        {
            var allExercises = await _repository.GetAllExercisesAsync();
            WarmupExercises = allExercises.Where(e => e.Category == "warmup_tool").OrderBy(e => e.Name).ToList();
            MobilityExercises = allExercises.Where(e => e.Category == "mobility").OrderBy(e => e.Name).ToList();
            RecoveryExercises = allExercises.Where(e => e.Category == "recovery_tool").OrderBy(e => e.Name).ToList();
            AllSupplements = await _repository.GetSupplementsAsync();

            var prefs = await _repository.GetUserPreferencesAsync(userId);
            if (prefs != null)
            {
                SelectedExerciseIds = new HashSet<string>(
                    (prefs.SelectedExercises ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));
                SelectedSupplementIds = new HashSet<string>(
                    (prefs.SelectedSupplements ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));
            }
        }

        private (int week, int phase) CalculatePhaseAndWeek(string? startDateStr)
        {
            if (string.IsNullOrEmpty(startDateStr) || !DateTime.TryParse(startDateStr, out var startDate))
                return (1, 1);

            var today = DateTime.UtcNow;
            int daysSinceStart = (today - startDate).Days;
            int week = Math.Min(daysSinceStart / 7 + 1, 12);
            int phase = week <= 6 ? 1 : 2;
            return (week, phase);
        }
    }
}
