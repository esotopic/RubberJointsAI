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

        [BindProperty]
        public Dictionary<string, bool> RecoveryTools { get; set; } = new();

        public string? ErrorMessage { get; set; }
        public string? SuccessMessage { get; set; }
        public int CurrentWeek { get; set; }
        public int CurrentPhase { get; set; }

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

                // Initialize recovery tools
                var allTools = new[] { "hot-tub", "vibration-plate", "hydro-massager", "steam-sauna", "dry-sauna", "compex-warmup", "compex-recovery", "compression-boots" };
                var disabledTools = (settings?.DisabledTools ?? "").Split(',', System.StringSplitOptions.RemoveEmptyEntries).ToHashSet();

                foreach (var tool in allTools)
                {
                    RecoveryTools[tool] = !disabledTools.Contains(tool);
                }

                // Calculate current week and phase
                var phaseInfo = CalculatePhaseAndWeek(StartDate);
                CurrentWeek = phaseInfo.week;
                CurrentPhase = phaseInfo.phase;
            }
            catch (Exception ex)
            {
                ErrorMessage = "Unable to connect to the database.";
            }
        }

        public async Task<IActionResult> OnPostAsync()
        {
            string userId = User.Identity?.Name ?? "default";

            try
            {
                var settings = new UserSettings
                {
                    UserId = userId,
                    StartDate = StartDate,
                    DisabledTools = string.Join(',', RecoveryTools
                        .Where(kv => !kv.Value)
                        .Select(kv => kv.Key))
                };

                await _repository.SaveUserSettingsAsync(settings);

                SuccessMessage = "Settings saved successfully!";

                // Recalculate phase info
                var phaseInfo = CalculatePhaseAndWeek(StartDate);
                CurrentWeek = phaseInfo.week;
                CurrentPhase = phaseInfo.phase;

                return Page();
            }
            catch (Exception ex)
            {
                ErrorMessage = "Failed to save settings. Please try again.";
                return Page();
            }
        }

        public async Task<IActionResult> OnPostResetAsync()
        {
            string userId = User.Identity?.Name ?? "default";

            try
            {
                var settings = new UserSettings
                {
                    UserId = userId,
                    StartDate = null,
                    DisabledTools = ""
                };

                await _repository.SaveUserSettingsAsync(settings);
                SuccessMessage = "All progress has been reset.";

                StartDate = null;
                CurrentWeek = 1;
                CurrentPhase = 1;

                // Reset recovery tools to all available
                var allTools = new[] { "hot-tub", "vibration-plate", "hydro-massager", "steam-sauna", "dry-sauna", "compex-warmup", "compex-recovery", "compression-boots" };
                foreach (var tool in allTools)
                {
                    RecoveryTools[tool] = true;
                }

                return Page();
            }
            catch (Exception ex)
            {
                ErrorMessage = "Failed to reset progress. Please try again.";
                return Page();
            }
        }

        private (int week, int phase) CalculatePhaseAndWeek(string? startDateStr)
        {
            if (string.IsNullOrEmpty(startDateStr) || !DateTime.TryParse(startDateStr, out var startDate))
            {
                return (1, 1);
            }

            var today = DateTime.UtcNow;
            int daysSinceStart = (today - startDate).Days;

            // 12-week program, 6 weeks per phase
            int week = Math.Min(daysSinceStart / 7 + 1, 12);
            int phase = week <= 6 ? 1 : 2;

            return (week, phase);
        }
    }
}
