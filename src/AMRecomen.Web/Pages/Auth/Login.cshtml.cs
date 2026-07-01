using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Identity;
using AMRecomen.Domain.Entities;
using AMRecomen.Domain.Interfaces;

namespace AMRecomen.Web.Pages.Auth;

public class LoginModel : PageModel
{
    private readonly IUserRepository _userRepository;

    public LoginModel(IUserRepository userRepository)
    {
        _userRepository = userRepository;
    }

    [BindProperty]
    [Required(ErrorMessage = "El usuario o correo electrónico es obligatorio.")]
    public string UsernameOrEmail { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "La contraseña es obligatoria.")]
    public string Password { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }
    public string? SuccessMessage { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToPage("/Index");
        }

        if (TempData.TryGetValue("SuccessMessage", out var msg))
        {
            SuccessMessage = msg as string;
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        User? user = null;
        if (UsernameOrEmail.Contains("@"))
        {
            user = await _userRepository.GetByEmailAsync(UsernameOrEmail);
        }
        else
        {
            user = await _userRepository.GetByUsernameAsync(UsernameOrEmail);
        }

        if (user == null)
        {
            ErrorMessage = "El nombre de usuario o correo electrónico no está registrado.";
            return Page();
        }

        // Verificar contraseña
        var hasher = new PasswordHasher<User>();
        var verificationResult = hasher.VerifyHashedPassword(user, user.PasswordHash, Password);

        if (verificationResult == PasswordVerificationResult.Failed)
        {
            ErrorMessage = "Contraseña incorrecta. Por favor, intenta de nuevo.";
            return Page();
        }

        // Crear Claims e iniciar sesión con cookies
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Email, user.Email)
        };

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
        {
            IsPersistent = true, // Recordar sesión
            ExpiresUtc = DateTimeOffset.UtcNow.AddDays(30)
        });

        if (!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
        {
            return Redirect(ReturnUrl);
        }

        return RedirectToPage("/Index");
    }
}
