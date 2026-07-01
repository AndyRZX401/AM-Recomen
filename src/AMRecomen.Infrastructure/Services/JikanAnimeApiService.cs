using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using AMRecomen.Application.Dtos;
using AMRecomen.Application.Interfaces;
using AMRecomen.Domain.Enums;

namespace AMRecomen.Infrastructure.Services;

public class JikanAnimeApiService : IAnimeApiService
{
    private readonly HttpClient _httpClient;

    public JikanAnimeApiService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri("https://api.jikan.moe/v4/");
        // Jikan requiere User-Agent
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "AMRecomen-App/1.0");
    }

    public async Task<IEnumerable<ExternalMediaResult>> SearchAnimeAsync(string query)
    {
        try
        {
            var response = await _httpClient.GetAsync($"anime?q={Uri.EscapeDataString(query)}&limit=10");
            if (!response.IsSuccessStatusCode)
                return GetMockAnime().Where(m => m.Title.Contains(query, StringComparison.OrdinalIgnoreCase));

            var jsonString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonString);
            if (!doc.RootElement.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
                return Enumerable.Empty<ExternalMediaResult>();

            return ParseJikanArray(dataElement);
        }
        catch
        {
            return GetMockAnime().Where(m => m.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
    }

    public async Task<IEnumerable<ExternalMediaResult>> GetTrendingAnimeAsync()
    {
        try
        {
            // Espera ligera para respetar el rate limit de Jikan
            await Task.Delay(500);
            var response = await _httpClient.GetAsync("top/anime?filter=bypopularity&limit=10");
            if (!response.IsSuccessStatusCode)
                return GetMockAnime();

            var jsonString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonString);
            if (!doc.RootElement.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
                return Enumerable.Empty<ExternalMediaResult>();

            return ParseJikanArray(dataElement);
        }
        catch
        {
            return GetMockAnime();
        }
    }

    public async Task<ExternalMediaResult?> GetAnimeByIdAsync(string externalId)
    {
        try
        {
            // El externalId debe ser el mal_id numérico. Ej: "50265"
            if (!int.TryParse(externalId.Replace("jikan_", ""), out var malId))
                return null;

            await Task.Delay(500);
            var response = await _httpClient.GetAsync($"anime/{malId}");
            if (!response.IsSuccessStatusCode)
                return GetMockAnime().FirstOrDefault(m => m.ExternalId == externalId);

            var jsonString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonString);
            if (!doc.RootElement.TryGetProperty("data", out var dataElement))
                return null;

            return ParseJikanItem(dataElement);
        }
        catch
        {
            return GetMockAnime().FirstOrDefault(m => m.ExternalId == externalId);
        }
    }

    private IEnumerable<ExternalMediaResult> ParseJikanArray(JsonElement arrayElement)
    {
        var list = new List<ExternalMediaResult>();
        foreach (var item in arrayElement.EnumerateArray())
        {
            var result = ParseJikanItem(item);
            if (result != null)
                list.Add(result);
        }
        return list;
    }

    private ExternalMediaResult? ParseJikanItem(JsonElement item)
    {
        try
        {
            if (!item.TryGetProperty("mal_id", out var idProp))
                return null;

            var malId = idProp.GetInt32().ToString();
            
            var title = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
            var englishTitle = item.TryGetProperty("title_english", out var engProp) ? engProp.GetString() ?? "" : "";
            var japaneseTitle = item.TryGetProperty("title_japanese", out var japProp) ? japProp.GetString() ?? "" : "";
            
            var synopsis = item.TryGetProperty("synopsis", out var synProp) ? synProp.GetString() ?? "" : "";
            
            // Buscar imagen de portada
            var imageUrl = "";
            if (item.TryGetProperty("images", out var imagesProp) && 
                imagesProp.TryGetProperty("jpg", out var jpgProp) && 
                jpgProp.TryGetProperty("large_image_url", out var largeImgProp))
            {
                imageUrl = largeImgProp.GetString() ?? "";
            }

            var rating = item.TryGetProperty("score", out var scoreProp) && scoreProp.ValueKind == JsonValueKind.Number 
                ? scoreProp.GetDouble() 
                : 0.0;

            var status = item.TryGetProperty("status", out var statusProp) ? statusProp.GetString() ?? "" : "";
            
            DateTime? releaseDate = null;
            if (item.TryGetProperty("aired", out var airedProp) && 
                airedProp.TryGetProperty("from", out var fromProp) && 
                fromProp.ValueKind == JsonValueKind.String)
            {
                if (DateTime.TryParse(fromProp.GetString(), out var date))
                    releaseDate = date;
            }

            var popularity = item.TryGetProperty("members", out var membersProp) && membersProp.ValueKind == JsonValueKind.Number
                ? membersProp.GetDouble()
                : 0.0;

            var genres = new List<string>();
            if (item.TryGetProperty("genres", out var genresProp) && genresProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in genresProp.EnumerateArray())
                {
                    if (g.TryGetProperty("name", out var gName))
                        genres.Add(gName.GetString() ?? "");
                }
            }

            var alternativeTitles = new List<string>();
            if (item.TryGetProperty("titles", out var titlesProp) && titlesProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in titlesProp.EnumerateArray())
                {
                    if (t.TryGetProperty("title", out var titleValue))
                    {
                        var tStr = titleValue.GetString();
                        if (!string.IsNullOrWhiteSpace(tStr) && !alternativeTitles.Contains(tStr))
                            alternativeTitles.Add(tStr);
                    }
                }
            }

            return new ExternalMediaResult
            {
                ExternalId = $"jikan_{malId}",
                Title = string.IsNullOrWhiteSpace(title) ? englishTitle : title,
                EnglishTitle = englishTitle,
                OriginalTitle = japaneseTitle,
                AlternativeTitles = alternativeTitles,
                NeedTranslation = true,
                Synopsis = synopsis,
                CoverImageUrl = imageUrl,
                Type = MediaType.Anime,
                Rating = rating,
                Status = status,
                ReleaseDate = releaseDate,
                PopularityScore = popularity,
                Genres = genres
            };
        }
        catch
        {
            return null;
        }
    }

    private IEnumerable<ExternalMediaResult> GetMockAnime()
    {
        return new List<ExternalMediaResult>
        {
            new()
            {
                ExternalId = "jikan_mock1",
                Title = "Frieren: Tras el final del viaje",
                EnglishTitle = "Frieren: Beyond Journey's End",
                OriginalTitle = "Sousou no Frieren",
                AlternativeTitles = new List<string> { "Sousou no Frieren", "Frieren" },
                Synopsis = "El mago elfo Frieren y sus valientes compañeros de aventura han derrotado al Rey Demonio, trayendo la paz a la tierra. Tras el fin de la gran contienda, todos toman caminos separados para vivir una vida tranquila.",
                CoverImageUrl = "https://images.unsplash.com/photo-1607604276583-eef5d076aa5f?w=500",
                Type = MediaType.Anime,
                Rating = 9.4,
                Status = "Finished",
                ReleaseDate = new DateTime(2023, 9, 29),
                PopularityScore = 950.0,
                Genres = new List<string> { "Fantasía", "Aventura", "Drama" }
            },
            new()
            {
                ExternalId = "jikan_mock2",
                Title = "Demon Slayer: Kimetsu no Yaiba",
                EnglishTitle = "Demon Slayer: Kimetsu no Yaiba",
                OriginalTitle = "Kimetsu no Yaiba",
                AlternativeTitles = new List<string> { "Kimetsu no Yaiba", "Guardianes de la Noche" },
                Synopsis = "Tanjiro Kamado es un chico inteligente y de buen corazón que vive con su familia en las montañas. Todo cambia cuando su familia es atacada y asesinada por un demonio, sobreviviendo solo su hermana Nezuko, quien se ha transformado en demonio.",
                CoverImageUrl = "https://images.unsplash.com/photo-1578632767115-351597cf2477?w=500",
                Type = MediaType.Anime,
                Rating = 8.7,
                Status = "Running",
                ReleaseDate = new DateTime(2019, 4, 6),
                PopularityScore = 920.0,
                Genres = new List<string> { "Acción", "Fantasía", "Histórico" }
            },
            new()
            {
                ExternalId = "jikan_mock3",
                Title = "One Piece",
                EnglishTitle = "One Piece",
                OriginalTitle = "One Piece",
                AlternativeTitles = new List<string> { "OP", "Monkey D. Luffy" },
                Synopsis = "Rica, fama, poder... el Rey de los Piratas Gold Roger obtuvo todo en este mundo. Antes de morir, sus últimas palabras inspiraron a miles a hacerse a la mar en busca de su gran tesoro: el One Piece.",
                CoverImageUrl = "https://images.unsplash.com/photo-1607604276583-eef5d076aa5f?w=500",
                Type = MediaType.Anime,
                Rating = 8.9,
                Status = "Running",
                ReleaseDate = new DateTime(1999, 10, 20),
                PopularityScore = 990.0,
                Genres = new List<string> { "Aventura", "Fantasía", "Comedia" }
            }
        };
    }
}
