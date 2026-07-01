using System;
using System.Collections.Generic;
using AMRecomen.Domain.Enums;

namespace AMRecomen.Domain.Entities;

public class MediaItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string EnglishTitle { get; set; } = string.Empty;
    public string OriginalTitle { get; set; } = string.Empty;
    public string AlternativeTitles { get; set; } = string.Empty;
    public string Synopsis { get; set; } = string.Empty;
    public string CoverImageUrl { get; set; } = string.Empty;
    public MediaType Type { get; set; }
    
    // Métrica calculada de popularidad/fama (0.0 a 100.0)
    public double HypeScore { get; set; }
    public int PopularityRank { get; set; }
    
    public string Status { get; set; } = string.Empty;
    public DateTime? ReleaseDate { get; set; }
    public double Rating { get; set; }
    
    // Identificador en API externa (ej. "jikan_123", "tmdb_550", "mangadex_uuid")
    public string ExternalId { get; set; } = string.Empty;
    
    // Slug amigable para URLs SEO (ej. "solo-leveling-manhwa")
    public string Slug { get; set; } = string.Empty;
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    // Relaciones
    public ICollection<Tag> Tags { get; set; } = new List<Tag>();
    public ICollection<TrendMetric> TrendMetrics { get; set; } = new List<TrendMetric>();
}
