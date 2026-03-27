using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RubberJointsAI.Data;
using System.Text.Json;

namespace RubberJointsAI.Pages
{
    [Authorize]
    [IgnoreAntiforgeryToken]
    public class ApiModel : PageModel
    {
        private readonly RubberJointsAIRepository _repository;

        public ApiModel(RubberJointsAIRepository repository)
        {
            _repository = repository;
        }

        public async Task<IActionResult> OnPostCheckAsync()
        {
            string userId = User.Identity?.Name ?? "default";
            string todayDate = DateTime.UtcNow.ToString("yyyy-MM-dd");

            try
            {
                using (var reader = new StreamReader(Request.Body))
                {
                    var body = await reader.ReadToEndAsync();
                    using (var doc = JsonDocument.Parse(body))
                    {
                        var root = doc.RootElement;
                        string itemType = root.GetProperty("itemType").GetString() ?? "";
                        string itemId = root.GetProperty("itemId").GetString() ?? "";
                        int stepIndex = root.TryGetProperty("stepIndex", out var stepIndexProp) ? stepIndexProp.GetInt32() : 0;
                        bool checkedState = root.TryGetProperty("checked", out var checkedProp) && checkedProp.GetBoolean();

                        await _repository.SetCheckAsync(userId, todayDate, itemType, itemId, stepIndex, checkedState);

                        return new JsonResult(new { success = true, userId, todayDate, itemType, itemId, stepIndex, checkedState });
                    }
                }
            }
            catch (Exception ex)
            {
                return new JsonResult(new { success = false, error = ex.Message }) { StatusCode = 500 };
            }
        }

        // Debug endpoint: GET /Api?handler=debug — shows today's checks
        public async Task<IActionResult> OnGetDebugAsync()
        {
            string userId = User.Identity?.Name ?? "default";
            string todayDate = DateTime.UtcNow.ToString("yyyy-MM-dd");
            try
            {
                var checks = await _repository.GetDailyChecksAsync(userId, todayDate);
                return new JsonResult(new
                {
                    userId,
                    todayDate,
                    utcNow = DateTime.UtcNow.ToString("o"),
                    checksCount = checks.Count,
                    checks = checks.Select(c => new
                    {
                        c.ItemType,
                        c.ItemId,
                        c.StepIndex,
                        c.Checked
                    })
                });
            }
            catch (Exception ex)
            {
                return new JsonResult(new { error = ex.Message }) { StatusCode = 500 };
            }
        }

        public async Task<IActionResult> OnPostMilestoneAsync()
        {
            string userId = User.Identity?.Name ?? "default";
            try
            {
                using (var reader = new StreamReader(Request.Body))
                {
                    var body = await reader.ReadToEndAsync();
                    using (var doc = JsonDocument.Parse(body))
                    {
                        var root = doc.RootElement;
                        string id = root.GetProperty("id").GetString() ?? "";
                        string today = DateTime.UtcNow.ToString("yyyy-MM-dd");

                        await _repository.CompleteUserMilestoneAsync(userId, id, today);

                        return new JsonResult(new { success = true });
                    }
                }
            }
            catch (Exception ex)
            {
                return new JsonResult(new { success = false, error = ex.Message }) { StatusCode = 500 };
            }
        }

        public async Task<IActionResult> OnPostLogsessionAsync()
        {
            string userId = User.Identity?.Name ?? "default";
            string todayDate = DateTime.UtcNow.ToString("yyyy-MM-dd");

            try
            {
                // Get today's steps to calculate total
                var dayType = GetDayType(DateTime.UtcNow.DayOfWeek);
                var sessionSteps = await _repository.GetSessionStepsAsync(dayType);
                var settings = await _repository.GetUserSettingsAsync(userId);
                var disabledToolIds = (settings?.DisabledTools ?? "").Split(',', System.StringSplitOptions.RemoveEmptyEntries).ToHashSet();

                int totalSteps = sessionSteps.Count(s => !disabledToolIds.Contains(s.ExerciseId));

                // Get completed steps for today
                var dailyChecks = await _repository.GetDailyChecksAsync(userId, todayDate);
                int completedSteps = dailyChecks.Count(c => c.ItemType == "step" && c.Checked);

                // Log the session
                await _repository.LogSessionAsync(userId, todayDate, completedSteps, totalSteps);

                return new JsonResult(new { success = true });
            }
            catch (Exception ex)
            {
                return new JsonResult(new { success = false, error = ex.Message }) { StatusCode = 500 };
            }
        }

        private string GetDayType(DayOfWeek dayOfWeek)
        {
            return dayOfWeek switch
            {
                DayOfWeek.Monday => "gym",
                DayOfWeek.Tuesday => "home",
                DayOfWeek.Wednesday => "gym",
                DayOfWeek.Thursday => "home",
                DayOfWeek.Friday => "gym",
                DayOfWeek.Saturday => "recovery",
                DayOfWeek.Sunday => "rest",
                _ => "rest"
            };
        }
    }
}
