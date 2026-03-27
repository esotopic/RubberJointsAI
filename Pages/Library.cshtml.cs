using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RubberJointsAI.Data;
using RubberJointsAI.Models;

namespace RubberJointsAI.Pages
{
    [Authorize]
    public class LibraryModel : PageModel
    {
        private readonly RubberJointsAIRepository _repository;

        public List<Exercise> Exercises { get; set; } = new();
        public string? ErrorMessage { get; set; }

        public LibraryModel(RubberJointsAIRepository repository)
        {
            _repository = repository;
        }

        public async Task OnGetAsync()
        {
            try
            {
                Exercises = await _repository.GetAllExercisesAsync();
            }
            catch (Exception ex)
            {
                ErrorMessage = "Unable to connect to the database. Library may be unavailable.";
            }
        }
    }
}
