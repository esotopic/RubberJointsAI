using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RubberJointsAI.Data;
using System.Security.Claims;

namespace RubberJointsAI.Pages
{
    public class RegisterModel : PageModel
    {
        private readonly RubberJointsAIRepository _repository;

        [BindProperty]
        public string Username { get; set; } = "";

        [BindProperty]
        public string Password { get; set; } = "";

        [BindProperty]
        public string ConfirmPassword { get; set; } = "";

        public string? ErrorMessage { get; set; }

        public RegisterModel(RubberJointsAIRepository repository)
        {
            _repository = repository;
        }

        public void OnGet() { }

        public async Task<IActionResult> OnPostAsync()
        {
            if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
            {
                ErrorMessage = "Username and password are required.";
                return Page();
            }

            if (Username.Trim().Length < 3)
            {
                ErrorMessage = "Username must be at least 3 characters.";
                return Page();
            }

            if (Password.Length < 6)
            {
                ErrorMessage = "Password must be at least 6 characters.";
                return Page();
            }

            if (Password != ConfirmPassword)
            {
                ErrorMessage = "Passwords do not match.";
                return Page();
            }

            try
            {
                var user = await _repository.CreateUserAsync(Username.Trim(), Password);
                if (user == null)
                {
                    ErrorMessage = "Username already taken. Choose another.";
                    return Page();
                }

                // Auto-login after registration
                var claims = new List<Claim>
                {
                    new Claim(ClaimTypes.Name, user.Username),
                    new Claim(ClaimTypes.NameIdentifier, user.Id.ToString())
                };

                var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                var principal = new ClaimsPrincipal(identity);

                await HttpContext.SignInAsync(
                    CookieAuthenticationDefaults.AuthenticationScheme,
                    principal,
                    new AuthenticationProperties
                    {
                        IsPersistent = true,
                        ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
                    });

                return RedirectToPage("/Index");
            }
            catch (Exception)
            {
                ErrorMessage = "Unable to connect. Please try again.";
                return Page();
            }
        }
    }
}
