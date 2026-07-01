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

public class MangaDexComicApiService : IComicApiService
{
    private readonly HttpClient _httpClient;

    public MangaDexComicApiService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri("https://api.mangadex.org/");
        _httpClient.DefaultRequestHeaders.Add("User-Agent", "AMRecomen-App/1.0");
    }

    public async Task<IEnumerable<ExternalMediaResult>> SearchComicsAsync(string query)
    {
        try
        {
            // Buscamos mangas que sean ko (manhwa) o zh/zh-hk (manhua) con corchetes codificados
            var response = await _httpClient.GetAsync($"manga?title={Uri.EscapeDataString(query)}&limit=10&includes%5B%5D=cover_art&originalLanguage%5B%5D=ko&originalLanguage%5B%5D=zh&originalLanguage%5B%5D=zh-hk");
            if (!response.IsSuccessStatusCode)
                return GetMockComics().Where(m => m.Title.Contains(query, StringComparison.OrdinalIgnoreCase));

            var jsonString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonString);
            if (!doc.RootElement.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
                return GetMockComics().Where(m => m.Title.Contains(query, StringComparison.OrdinalIgnoreCase));

            var results = ParseMangaDexArray(dataElement).ToList();
            if (!results.Any())
            {
                return GetMockComics().Where(m => m.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
            }
            return results;
        }
        catch
        {
            return GetMockComics().Where(m => m.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
        }
    }

    public async Task<IEnumerable<ExternalMediaResult>> GetTrendingComicsAsync(bool isManhwa)
    {
        try
        {
            string langParam = isManhwa ? "originalLanguage%5B%5D=ko" : "originalLanguage%5B%5D=zh&originalLanguage%5B%5D=zh-hk";
            
            var response = await _httpClient.GetAsync($"manga?limit=10&order%5BfollowedCount%5D=desc&includes%5B%5D=cover_art&{langParam}");
            if (!response.IsSuccessStatusCode)
                return GetMockComics().Where(c => c.Type == (isManhwa ? MediaType.Manhwa : MediaType.Manhua));

            var jsonString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonString);
            if (!doc.RootElement.TryGetProperty("data", out var dataElement) || dataElement.ValueKind != JsonValueKind.Array)
                return GetMockComics().Where(c => c.Type == (isManhwa ? MediaType.Manhwa : MediaType.Manhua));

            var results = ParseMangaDexArray(dataElement).ToList();
            if (!results.Any())
            {
                return GetMockComics().Where(c => c.Type == (isManhwa ? MediaType.Manhwa : MediaType.Manhua));
            }
            return results;
        }
        catch
        {
            return GetMockComics().Where(c => c.Type == (isManhwa ? MediaType.Manhwa : MediaType.Manhua));
        }
    }

    public async Task<ExternalMediaResult?> GetComicByIdAsync(string externalId)
    {
        var cleanId = externalId.Replace("mangadex_", "");
        try
        {
            var response = await _httpClient.GetAsync($"manga/{cleanId}?includes%5B%5D=cover_art");
            if (!response.IsSuccessStatusCode)
                return GetMockComics().FirstOrDefault(c => c.ExternalId == externalId);

            var jsonString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonString);
            if (!doc.RootElement.TryGetProperty("data", out var dataElement))
                return null;

            return ParseMangaDexItem(dataElement);
        }
        catch
        {
            return GetMockComics().FirstOrDefault(c => c.ExternalId == externalId);
        }
    }

    private IEnumerable<ExternalMediaResult> ParseMangaDexArray(JsonElement arrayElement)
    {
        var list = new List<ExternalMediaResult>();
        foreach (var item in arrayElement.EnumerateArray())
        {
            var result = ParseMangaDexItem(item);
            if (result != null)
                list.Add(result);
        }
        return list;
    }

    private ExternalMediaResult? ParseMangaDexItem(JsonElement item)
    {
        try
        {
            if (!item.TryGetProperty("id", out var idProp))
                return null;

            var mangaId = idProp.GetString() ?? "";
            
            var attributes = item.GetProperty("attributes");
            
            // Títulos alternativos
            var title = "";
            if (attributes.TryGetProperty("title", out var titleProp))
            {
                // MangaDex guarda los títulos por idioma, buscamos en o en-US o ja-ro
                if (titleProp.TryGetProperty("en", out var enProp))
                    title = enProp.GetString() ?? "";
                else if (titleProp.TryGetProperty("ja-ro", out var jaRoProp))
                    title = jaRoProp.GetString() ?? "";
                else if (titleProp.EnumerateObject().Any())
                    title = titleProp.EnumerateObject().First().Value.GetString() ?? "";
            }

            var originalLanguage = attributes.TryGetProperty("originalLanguage", out var langProp) ? langProp.GetString() ?? "" : "";
            
            MediaType mediaType;
            if (originalLanguage == "ko")
                mediaType = MediaType.Manhwa;
            else if (originalLanguage == "zh" || originalLanguage == "zh-hk")
                mediaType = MediaType.Manhua;
            else
                return null; // Filtramos manga japonés tradicional u otros idiomas

            var alternativeTitles = new List<string>();
            if (attributes.TryGetProperty("altTitles", out var altTitlesProp) && altTitlesProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var alt in altTitlesProp.EnumerateArray())
                {
                    foreach (var prop in alt.EnumerateObject())
                    {
                        var altVal = prop.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(altVal) && !alternativeTitles.Contains(altVal))
                            alternativeTitles.Add(altVal);
                    }
                }
            }

            // Sinopsis en español si existe, si no, en inglés (activando traducción si cae en inglés)
            var synopsis = "";
            var needTranslation = false;
            if (attributes.TryGetProperty("description", out var descProp))
            {
                if (descProp.TryGetProperty("es", out var esDesc))
                    synopsis = esDesc.GetString() ?? "";
                else if (descProp.TryGetProperty("es-la", out var esLaDesc))
                    synopsis = esLaDesc.GetString() ?? "";
                else if (descProp.TryGetProperty("en", out var enDesc))
                {
                    synopsis = enDesc.GetString() ?? "";
                    needTranslation = true;
                }
                else if (descProp.EnumerateObject().Any())
                {
                    synopsis = descProp.EnumerateObject().First().Value.GetString() ?? "";
                    needTranslation = true;
                }
            }

            // Buscar portada
            var fileName = "";
            if (item.TryGetProperty("relationships", out var relsProp) && relsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var rel in relsProp.EnumerateArray())
                {
                    if (rel.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "cover_art")
                    {
                        if (rel.TryGetProperty("attributes", out var attrProp) && attrProp.TryGetProperty("fileName", out var fileProp))
                        {
                            fileName = fileProp.GetString() ?? "";
                        }
                    }
                }
            }

            var coverUrl = string.IsNullOrEmpty(fileName) 
                ? "https://images.unsplash.com/photo-1607604276583-eef5d076aa5f?w=500" // Fallback
                : $"https://uploads.mangadex.org/covers/{mangaId}/{fileName}";

            var status = attributes.TryGetProperty("status", out var statusProp) ? statusProp.GetString() ?? "" : "";
            
            // Puntuación ficticia/seguidores
            var score = 0.0;
            var popularity = 0.0;
            
            // Intentar leer tags
            var genres = new List<string>();
            if (attributes.TryGetProperty("tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var tag in tagsProp.EnumerateArray())
                {
                    var tagAttr = tag.GetProperty("attributes");
                    var tagName = tagAttr.GetProperty("name").GetProperty("en").GetString();
                    if (!string.IsNullOrEmpty(tagName))
                        genres.Add(tagName);
                }
            }

            return new ExternalMediaResult
            {
                ExternalId = $"mangadex_{mangaId}",
                Title = title,
                EnglishTitle = title,
                OriginalTitle = title,
                AlternativeTitles = alternativeTitles,
                NeedTranslation = needTranslation,
                Synopsis = synopsis,
                CoverImageUrl = coverUrl,
                Type = mediaType,
                Rating = 8.0, // MangaDex no da rating en listado básico
                Status = status,
                ReleaseDate = null,
                PopularityScore = 500,
                Genres = genres
            };
        }
        catch
        {
            return null;
        }
    }

    private IEnumerable<ExternalMediaResult> GetMockComics()
    {
        return new List<ExternalMediaResult>
        {
            // Manhwas
            new()
            {
                ExternalId = "mangadex_solo_leveling",
                Title = "Solo Leveling",
                EnglishTitle = "Solo Leveling",
                OriginalTitle = "나 혼자만 레벨업",
                AlternativeTitles = new List<string> { "I Level Up Alone", "Na Honjaman Rebeleob", "Only I Level Up" },
                Synopsis = "En un mundo donde cazadores con habilidades mágicas deben luchar contra monstruos mortales para proteger a la humanidad, Sung Jinwoo, un cazador de rango E notoriamente débil, se encuentra en una lucha desesperada por la supervivencia en una mazmorra doble extremadamente peligrosa. Al sobrevivir, despierta como un 'Jugador' del Sistema, obteniendo la habilidad única de subir de nivel sin límites.",
                CoverImageUrl = "https://images.unsplash.com/photo-1607604276583-eef5d076aa5f?w=500",
                Type = MediaType.Manhwa,
                Rating = 8.9,
                Status = "completed",
                ReleaseDate = new DateTime(2018, 3, 4),
                PopularityScore = 15000.0,
                Genres = new List<string> { "Acción", "Fantasía", "Aventura", "Sistema" }
            },
            new()
            {
                ExternalId = "mangadex_tower_of_god",
                Title = "Torre de Dios",
                EnglishTitle = "Tower of God",
                OriginalTitle = "신의 탑",
                AlternativeTitles = new List<string> { "Sin-ui Tap", "Kami no Tou" },
                Synopsis = "¿Qué deseas? ¿Dinero y riqueza? ¿Honor y orgullo? ¿Autoridad y poder? ¿Venganza? ¿O algo que trascienda todo lo demás? Lo que sea que desees, está aquí en la Torre de Dios. Bam, un chico que ha vivido solo toda su vida, entra en la Torre persiguiendo a su única amiga Rachel.",
                CoverImageUrl = "https://images.unsplash.com/photo-1614850523459-c2f4c699c52e?w=500",
                Type = MediaType.Manhwa,
                Rating = 8.5,
                Status = "ongoing",
                ReleaseDate = new DateTime(2010, 6, 30),
                PopularityScore = 12000.0,
                Genres = new List<string> { "Fantasía", "Aventura", "Misterio", "Acción" }
            },
            new()
            {
                ExternalId = "mangadex_tbate",
                Title = "El Principio Después del Fin",
                EnglishTitle = "The Beginning After the End",
                OriginalTitle = "The Beginning After the End",
                AlternativeTitles = new List<string> { "TBATE", "El inicio después del fin" },
                Synopsis = "El Rey Grey tiene una fuerza, riqueza y prestigio incomparables en un mundo gobernado por la habilidad marcial. Sin embargo, la soledad permanece de cerca detrás de aquellos con gran poder. Bajo la apariencia de un rey fuerte, yace el cascarón de un hombre, desprovisto de propósito y voluntad. Renacido en un nuevo mundo lleno de magia y monstruos, el rey tiene una segunda oportunidad de revivir su vida.",
                CoverImageUrl = "https://images.unsplash.com/photo-1579783902614-a3fb3927b6a5?w=500",
                Type = MediaType.Manhwa,
                Rating = 8.7,
                Status = "ongoing",
                ReleaseDate = new DateTime(2018, 7, 7),
                PopularityScore = 9800.0,
                Genres = new List<string> { "Acción", "Fantasía", "Reencarnación", "Magia" }
            },
            // Manhuas
            new()
            {
                ExternalId = "mangadex_btth",
                Title = "Batalla a Través de los Cielos",
                EnglishTitle = "Battle Through the Heavens",
                OriginalTitle = "斗破苍穹",
                AlternativeTitles = new List<string> { "Doupo Cangqiong", "Fights Break Sphere", "BTTH" },
                Synopsis = "En una tierra donde no hay magia, una tierra donde el fuerte hace las reglas y el débil debe obedecer. Una tierra llena de tesoros atrayentes y belleza, pero también de peligros mortales. Xiao Yan, quien había mostrado un talento que nadie había visto en décadas, de repente hace tres años perdió todo: sus poderes, su reputación y la promesa a su madre. ¿Qué magia causó que perdiera sus poderes y por qué su prometida apareció de repente?",
                CoverImageUrl = "https://images.unsplash.com/photo-1541701494587-cb58502866ab?w=500",
                Type = MediaType.Manhua,
                Rating = 8.1,
                Status = "ongoing",
                ReleaseDate = new DateTime(2012, 4, 1),
                PopularityScore = 6500.0,
                Genres = new List<string> { "Cultivación", "Artes Marciales", "Fantasía", "Acción" }
            },
            new()
            {
                ExternalId = "mangadex_tales_demons_gods",
                Title = "Leyendas de Demonios y Dioses",
                EnglishTitle = "Tales of Demons and Gods",
                OriginalTitle = "妖神记",
                AlternativeTitles = new List<string> { "Yaoshenji", "TDG", "Historias de Demonios y Dioses" },
                Synopsis = "Nie Li, el más fuerte de los Espiritistas Demoníacos, en su vida pasada estuvo en la cima del mundo marcial, pero perdió la vida durante la batalla con el Emperador Sabio. Al despertar, descubre que ha regresado a su cuerpo de 13 años de edad, cuando todavía era el estudiante más débil de su clase. Con el conocimiento acumulado de su vida anterior, decide proteger su ciudad natal y a sus seres queridos de la destrucción inminente.",
                CoverImageUrl = "https://images.unsplash.com/photo-1580136579312-94651dfd596d?w=500",
                Type = MediaType.Manhua,
                Rating = 8.4,
                Status = "ongoing",
                ReleaseDate = new DateTime(2015, 6, 20),
                PopularityScore = 8000.0,
                Genres = new List<string> { "Reencarnación", "Cultivación", "Artes Marciales", "Aventura" }
            },
            new()
            {
                ExternalId = "mangadex_martial_peak",
                Title = "Cúspide Marcial",
                EnglishTitle = "Martial Peak",
                OriginalTitle = "武炼巅峰",
                AlternativeTitles = new List<string> { "Wu Lian Dian Feng", "Martial Peak" },
                Synopsis = "El viaje a la cumbre marcial es un camino solitario, largo e interminable. Frente a la adversidad, debes sobrevivir y permanecer inquebrantable. Solo entonces podrás abrirte camino y continuar en tu viaje para convertirte en el más fuerte. Yang Kai, un discípulo de prueba de la secta High Heaven, logró obtener un misterioso libro negro que lo puso en el camino de la cúspide del mundo marcial.",
                CoverImageUrl = "https://images.unsplash.com/photo-1518709268805-4e9042af9f23?w=500",
                Type = MediaType.Manhua,
                Rating = 7.9,
                Status = "ongoing",
                ReleaseDate = new DateTime(2018, 6, 12),
                PopularityScore = 11000.0,
                Genres = new List<string> { "Cultivación", "Artes Marciales", "Harem", "Acción" }
            }
        };
    }
}
