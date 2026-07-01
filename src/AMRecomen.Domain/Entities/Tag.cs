using System;
using System.Collections.Generic;

namespace AMRecomen.Domain.Entities;

public class Tag
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string NormalizedName { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;

    // Relación de muchos a muchos con MediaItem
    public ICollection<MediaItem> MediaItems { get; set; } = new List<MediaItem>();
}
