using System;
using AMRecomen.Domain.Enums;

namespace AMRecomen.Domain.Entities;

public class LibraryItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;

    public Guid MediaItemId { get; set; }
    public MediaItem MediaItem { get; set; } = null!;

    public LibraryStatus Status { get; set; }
    public int? UserRating { get; set; } // Puntuación personalizada del 1 al 10
    public DateTime AddedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
