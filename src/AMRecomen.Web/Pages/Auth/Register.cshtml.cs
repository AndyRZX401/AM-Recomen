using System;
using System.ComponentModel.DataAnnotations;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Identity;
using AMRecomen.Domain.Entities;
using AMRecomen.Domain.Interfaces;

namespace AMRecomen.Web.Pages.Auth;

public class RegisterModel : PageModel
{
    private readonly IUserRepository _userRepository;

    public RegisterModel(IUserRepository userRepository)
    {
        _userRepository = userRepository;
    }

    [BindProperty]
    [Required(ErrorMessage = "El nombre de usuario es obligatorio.")]
    [StringLength(30, MinimumLength = 3, ErrorMessage = "El nombre de usuario debe tener entre 3 y 30 caracteres.")]
    [RegularExpression(@"^[a-zA-Z0-9_\-]+$", ErrorMessage = "El nombre de usuario solo puede contener letras, números, guiones y guiones bajos.")]
    public string Username { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "El correo electrónico es obligatorio.")]
    [EmailAddress(ErrorMessage = "El formato de correo electrónico no es válido.")]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "La contraseña es obligatoria.")]
    [StringLength(100, MinimumLength = 6, ErrorMessage = "La contraseña debe tener al menos 6 caracteres.")]
    public string Password { get; set; } = string.Empty;

    [BindProperty]
    [Required(ErrorMessage = "Confirmar contraseña es obligatorio.")]
    [Compare("Password", ErrorMessage = "Las contraseñas no coinciden.")]
    public string ConfirmPassword { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }

    public IActionResult OnGet()
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToPage("/Index");
        }
        return Page();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        // Verificar si el usuario ya existe
        var existingUser = await _userRepository.GetByUsernameAsync(Username);
        if (existingUser != null)
        {
            ErrorMessage = "El nombre de usuario ya está registrado.";
            return Page();
        }

        var existingEmail = await _userRepository.GetByEmailAsync(Email);
        if (existingEmail != null)
        {
            ErrorMessage = "El correo electrónico ya está registrado.";
            return Page();
        }

        // Crear usuario y hashear contraseña
        var user = new User
        {
            Username = Username.Trim(),
            Email = Email.ToLower().Trim(),
            CreatedAt = DateTime.UtcNow
        };

        var hasher = new PasswordHasher<User>();
        user.PasswordHash = hasher.HashPassword(user, Password);

        await _userRepository.AddAsync(user);
        await _userRepository.SaveChangesAsync();

        TempData["SuccessMessage"] = "¡Cuenta creada con éxito! Por favor, inicia sesión.";
        return RedirectToPage("/Auth/Login");
    }
}
