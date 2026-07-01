using System;
using System.Collections.Generic;
using AMRecomen.Domain.Enums;

namespace AMRecomen.Application.Dtos;

public class ExternalMediaResult
{
    public string ExternalId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string EnglishTitle { get; set; } = string.Empty;
    public string OriginalTitle { get; set; } = string.Empty;
    public List<string> AlternativeTitles { get; set; } = new();
    public bool NeedTranslation { get; set; }
    public string Synopsis { get; set; } = string.Empty;
    public string CoverImageUrl { get; set; } = string.Empty;
    public MediaType Type { get; set; }
    public double Rating { get; set; }
    public string Status { get; set; } = string.Empty;
    public DateTime? ReleaseDate { get; set; }
    public double PopularityScore { get; set; } // Representará popularidad cruda o rank para calcular HypeScore
    public List<string> Genres { get; set; } = new();
}
