using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RubberJointsAI.Data;
using RubberJointsAI.Models;

namespace RubberJointsAI.Pages
{
    [Authorize]
    public class EnrollModel : PageModel
    {
        private readonly RubberJointsAIRepository _repository;

        public List<TrainingProgram> Programs { get; set; } = new();
        public UserEnrollment? ActiveEnrollment { get; set; }
        public string? ErrorMessage { get; set; }

        public EnrollModel(RubberJointsAIRepository repository)
        {
            _repository = repository;
        }

        public async Task OnGetAsync()
        {
            string userId = User.Identity?.Name ?? "default";
            try
            {
                Programs = await _repository.GetProgramsAsync();
                ActiveEnrollment = await _repository.GetActiveEnrollmentAsync(userId);
            }
            catch (Exception ex)
            {
                ErrorMessage = "Unable to load programs.";
            }
        }

        public async Task<IActionResult> OnPostAsync(int programId, string startDate)
        {
            string userId = User.Identity?.Name ?? "default";
            try
            {
                if (string.IsNullOrEmpty(startDate))
                    startDate = DateTime.UtcNow.ToString("yyyy-MM-dd");

                await _repository.EnrollUserAsync(userId, programId, startDate);
                return RedirectToPage("/Index");
            }
            catch (Exception ex)
            {
                ErrorMessage = "Failed to enroll: " + ex.Message;
                Programs = await _repository.GetProgramsAsync();
                ActiveEnrollment = await _repository.GetActiveEnrollmentAsync(userId);
                return Page();
            }
        }
    }
}
