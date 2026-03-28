using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using RubberJointsAI.Data;
using System.Security.Claims;

namespace RubberJointsAI.Pages
{
    public class LoginModel : PageModel
    {
        private readonly RubberJointsAIRepository _repository;

        [BindProperty]
        public string Username { get; set; } = "";

        [BindProperty]
        public string Password { get; set; } = "";

        public string? ErrorMessage { get; set; }
        public string? ReturnUrl { get; set; }

        public LoginModel(RubberJointsAIRepository repository)
        {
            _repository = repository;
        }

        public void OnGet(string? returnUrl = null)
        {
            ReturnUrl = returnUrl;
        }

        public async Task<IActionResult> OnPostAsync(string? returnUrl = null)
        {
            ReturnUrl = returnUrl;

            if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
            {
                ErrorMessage = "Username and password are required.";
                return Page();
            }

            try
            {
                var user = await _repository.ValidateUserAsync(Username.Trim(), Password);
                if (user == null)
                {
                    ErrorMessage = "Invalid username or password.";
                    return Page();
                }

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

                // Always go to AI page after login — it's the home experience
                return LocalRedirect("/AI");
            }
            catch (Exception)
            {
                ErrorMessage = "Unable to connect. Please try again.";
                return Page();
            }
        }
    }
}
