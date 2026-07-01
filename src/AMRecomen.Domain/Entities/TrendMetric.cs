using System;

namespace AMRecomen.Domain.Entities;

public class TrendMetric
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MediaItemId { get; set; }
    public MediaItem MediaItem { get; set; } = null!;
    
    // Origen de la métrica (ej. "TMDb", "MyAnimeList", "GoogleTrends", "MangaDex")
    public string Source { get; set; } = string.Empty;
    
    // Tipo de métrica (ej. "Views", "PopularityScore", "SearchVolume")
    public string MetricType { get; set; } = string.Empty;
    
    // Valor numérico de la métrica
    public double Value { get; set; }
    
    public DateTime RecordedAt { get; set; } = DateTime.UtcNow;
}
