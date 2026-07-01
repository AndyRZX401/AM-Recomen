using System;
using System.Collections.Generic;

namespace AMRecomen.Domain.Entities;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Relación de navegación hacia sus elementos guardados en la biblioteca
    public List<LibraryItem> LibraryItems { get; set; } = new();
}
