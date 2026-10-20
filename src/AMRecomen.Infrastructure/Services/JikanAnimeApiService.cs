using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        _httpClient.Timeout = TimeSpan.FromSeconds(8);
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "AMRecomen-App/2.0");
        }
    }

    public async Task<IEnumerable<ExternalMediaResult>> SearchAnimeAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Enumerable.Empty<ExternalMediaResult>();

        var q = query.Trim();
        var results = new List<ExternalMediaResult>();

        // 1. Coincidencias directas en nuestro catálogo curado de los mejores animes (título en español, inglés, japonés o alternativos)
        var curatedMatches = GetCuratedTopAnime()
            .Where(m => m.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        m.EnglishTitle.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        m.OriginalTitle.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        m.AlternativeTitles.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        results.AddRange(curatedMatches);

        // 2. Búsqueda en tiempo real con AniList GraphQL (rápido, sin bloqueos de portada ni caídas)
        var aniListResults = await SearchAniListAnimeAsync(q, 10);
        foreach (var item in aniListResults)
        {
            if (!results.Any(r => r.ExternalId == item.ExternalId || r.Title.Equals(item.Title, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(item);
            }
        }

        // 3. Respaldo adicional con Jikan si aún tenemos pocos resultados
        if (results.Count < 4)
        {
            try
            {
                var response = await _httpClient.GetAsync($"https://api.jikan.moe/v4/anime?q={Uri.EscapeDataString(q)}&order_by=members&sort=desc&limit=8");
                if (response.IsSuccessStatusCode)
                {
                    var jsonString = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(jsonString);
                    if (doc.RootElement.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in ParseJikanArray(dataElement))
                        {
                            if (!results.Any(r => r.ExternalId == item.ExternalId || r.Title.Equals(item.Title, StringComparison.OrdinalIgnoreCase)))
                            {
                                results.Add(item);
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignorar fallo de Jikan si está caído
            }
        }

        return results;
    }

    public async Task<IEnumerable<ExternalMediaResult>> GetTrendingAnimeAsync()
    {
        var results = new List<ExternalMediaResult>();

        // 1. Incluir siempre la base curada de los mejores animes con portadas verificadas y sinopsis en español
        results.AddRange(GetCuratedTopAnime());

        // 2. Enriquecer con el Top de AniList en tiempo real
        try
        {
            var aniListTop = await FetchAniListTopAnimeAsync(20);
            foreach (var item in aniListTop)
            {
                if (!results.Any(r => r.ExternalId == item.ExternalId ||
                                      r.Title.Equals(item.Title, StringComparison.OrdinalIgnoreCase) ||
                                      r.OriginalTitle.Equals(item.OriginalTitle, StringComparison.OrdinalIgnoreCase)))
                {
                    results.Add(item);
                }
            }
        }
        catch
        {
            // Mantener catálogo curado si falla la red
        }

        return results;
    }

    public async Task<ExternalMediaResult?> GetAnimeByIdAsync(string externalId)
    {
        var curated = GetCuratedTopAnime().FirstOrDefault(m => m.ExternalId == externalId);
        if (curated != null)
            return curated;

        try
        {
            if (!int.TryParse(externalId.Replace("jikan_", "").Replace("anilist_", ""), out var malId))
                return null;

            var response = await _httpClient.GetAsync($"https://api.jikan.moe/v4/anime/{malId}");
            if (!response.IsSuccessStatusCode)
                return null;

            var jsonString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonString);
            if (!doc.RootElement.TryGetProperty("data", out var dataElement))
                return null;

            return ParseJikanItem(dataElement);
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<ExternalMediaResult>> SearchAniListAnimeAsync(string search, int limit)
    {
        const string query = @"
        query ($search: String, $perPage: Int) {
          Page(page: 1, perPage: $perPage) {
            media(search: $search, type: ANIME, sort: [POPULARITY_DESC, SCORE_DESC], isAdult: false) {
              id
              idMal
              title { romaji english native }
              synonyms
              description(asHtml: false)
              coverImage { extraLarge large }
              averageScore
              popularity
              status
              startDate { year month day }
              genres
            }
          }
        }";

        return await ExecuteAniListQueryAsync(query, new { search, perPage = limit });
    }

    private async Task<List<ExternalMediaResult>> FetchAniListTopAnimeAsync(int limit)
    {
        const string query = @"
        query ($perPage: Int) {
          Page(page: 1, perPage: $perPage) {
            media(type: ANIME, sort: [POPULARITY_DESC, SCORE_DESC], isAdult: false) {
              id
              idMal
              title { romaji english native }
              synonyms
              description(asHtml: false)
              coverImage { extraLarge large }
              averageScore
              popularity
              status
              startDate { year month day }
              genres
            }
          }
        }";

        return await ExecuteAniListQueryAsync(query, new { perPage = limit });
    }

    private async Task<List<ExternalMediaResult>> ExecuteAniListQueryAsync(string query, object variables)
    {
        var list = new List<ExternalMediaResult>();
        try
        {
            var payload = JsonSerializer.Serialize(new { query, variables });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            var response = await _httpClient.PostAsync("https://graphql.anilist.co", content);
            if (!response.IsSuccessStatusCode)
                return list;

            var jsonString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonString);
            if (!doc.RootElement.TryGetProperty("data", out var dataElem) ||
                !dataElem.TryGetProperty("Page", out var pageElem) ||
                !pageElem.TryGetProperty("media", out var mediaArray) ||
                mediaArray.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var item in mediaArray.EnumerateArray())
            {
                var parsed = ParseAniListItem(item);
                if (parsed != null)
                    list.Add(parsed);
            }
        }
        catch
        {
            // Ignorar errores de red en consulta GraphQL
        }
        return list;
    }

    private ExternalMediaResult? ParseAniListItem(JsonElement item)
    {
        try
        {
            int aniId = item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number ? idProp.GetInt32() : 0;
            int malId = item.TryGetProperty("idMal", out var malProp) && malProp.ValueKind == JsonValueKind.Number ? malProp.GetInt32() : aniId;
            if (malId == 0 && aniId == 0)
                return null;

            string english = "";
            string romaji = "";
            string nativeTitle = "";
            if (item.TryGetProperty("title", out var titleObj) && titleObj.ValueKind == JsonValueKind.Object)
            {
                english = titleObj.TryGetProperty("english", out var enP) && enP.ValueKind == JsonValueKind.String ? enP.GetString() ?? "" : "";
                romaji = titleObj.TryGetProperty("romaji", out var roP) && roP.ValueKind == JsonValueKind.String ? roP.GetString() ?? "" : "";
                nativeTitle = titleObj.TryGetProperty("native", out var naP) && naP.ValueKind == JsonValueKind.String ? naP.GetString() ?? "" : "";
            }

            var displayTitle = !string.IsNullOrWhiteSpace(english) ? english : (!string.IsNullOrWhiteSpace(romaji) ? romaji : nativeTitle);
            if (string.IsNullOrWhiteSpace(displayTitle))
                return null;

            string coverUrl = "";
            if (item.TryGetProperty("coverImage", out var coverObj) && coverObj.ValueKind == JsonValueKind.Object)
            {
                coverUrl = coverObj.TryGetProperty("large", out var lgP) && lgP.ValueKind == JsonValueKind.String ? lgP.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(coverUrl))
                {
                    coverUrl = coverObj.TryGetProperty("extraLarge", out var xlP) && xlP.ValueKind == JsonValueKind.String ? xlP.GetString() ?? "" : "";
                }
            }
            if (string.IsNullOrWhiteSpace(coverUrl))
                return null;

            string rawDesc = item.TryGetProperty("description", out var descP) && descP.ValueKind == JsonValueKind.String ? descP.GetString() ?? "" : "";
            var synopsis = CleanDescription(rawDesc);

            double rating = 8.0;
            if (item.TryGetProperty("averageScore", out var scoreP) && scoreP.ValueKind == JsonValueKind.Number)
            {
                rating = Math.Round(scoreP.GetDouble() / 10.0, 1);
            }

            double popularity = 100000;
            if (item.TryGetProperty("popularity", out var popP) && popP.ValueKind == JsonValueKind.Number)
            {
                // Escalar popularidad de AniList al equivalente de miembros de MAL
                popularity = popP.GetDouble() * 2.2;
            }

            string rawStatus = item.TryGetProperty("status", out var stP) && stP.ValueKind == JsonValueKind.String ? stP.GetString() ?? "" : "";
            string status = rawStatus switch
            {
                "FINISHED" => "Finalizado",
                "RELEASING" => "En emisión",
                "NOT_YET_RELEASED" => "Próximamente",
                _ => "Finalizado"
            };

            DateTime? releaseDate = null;
            if (item.TryGetProperty("startDate", out var sdObj) && sdObj.ValueKind == JsonValueKind.Object)
            {
                int year = sdObj.TryGetProperty("year", out var yP) && yP.ValueKind == JsonValueKind.Number ? yP.GetInt32() : 0;
                int month = sdObj.TryGetProperty("month", out var mP) && mP.ValueKind == JsonValueKind.Number ? mP.GetInt32() : 1;
                int day = sdObj.TryGetProperty("day", out var dP) && dP.ValueKind == JsonValueKind.Number ? dP.GetInt32() : 1;
                if (year >= 1960 && year <= 2100)
                {
                    releaseDate = new DateTime(year, Math.Clamp(month, 1, 12), Math.Clamp(day, 1, 28));
                }
            }

            var genres = new List<string>();
            if (item.TryGetProperty("genres", out var genArr) && genArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in genArr.EnumerateArray())
                {
                    var rawG = g.GetString();
                    if (!string.IsNullOrWhiteSpace(rawG))
                    {
                        var translated = AnimeGenreTranslations.TryGetValue(rawG, out var esG) ? esG : rawG;
                        if (!genres.Contains(translated))
                            genres.Add(translated);
                    }
                }
            }

            var altTitles = new List<string>();
            if (!string.IsNullOrWhiteSpace(romaji) && romaji != displayTitle)
                altTitles.Add(romaji);
            if (!string.IsNullOrWhiteSpace(nativeTitle) && nativeTitle != displayTitle)
                altTitles.Add(nativeTitle);
            if (item.TryGetProperty("synonyms", out var synArr) && synArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in synArr.EnumerateArray())
                {
                    var sVal = s.GetString();
                    if (!string.IsNullOrWhiteSpace(sVal) && !altTitles.Contains(sVal))
                        altTitles.Add(sVal);
                }
            }

            return new ExternalMediaResult
            {
                ExternalId = $"jikan_{malId}",
                Title = displayTitle,
                EnglishTitle = !string.IsNullOrWhiteSpace(english) ? english : displayTitle,
                OriginalTitle = !string.IsNullOrWhiteSpace(romaji) ? romaji : nativeTitle,
                AlternativeTitles = altTitles,
                NeedTranslation = true,
                Synopsis = synopsis,
                CoverImageUrl = coverUrl,
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

    private static string CleanDescription(string htmlText)
    {
        if (string.IsNullOrWhiteSpace(htmlText))
            return string.Empty;

        var text = Regex.Replace(htmlText, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<.*?>", string.Empty);
        text = Regex.Replace(text, "\\(Source:.*?\\)", string.Empty, RegexOptions.IgnoreCase);
        return System.Net.WebUtility.HtmlDecode(text).Trim();
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

    private static readonly Dictionary<string, string> AnimeGenreTranslations = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Action", "Acción" },
        { "Adventure", "Aventura" },
        { "Avant Garde", "Vanguardia" },
        { "Award Winning", "Premiado" },
        { "Boys Love", "Boys Love" },
        { "Comedy", "Comedia" },
        { "Drama", "Drama" },
        { "Fantasy", "Fantasía" },
        { "Girls Love", "Girls Love" },
        { "Gourmet", "Gastronomía" },
        { "Horror", "Terror" },
        { "Mystery", "Misterio" },
        { "Romance", "Romance" },
        { "Sci-Fi", "Ciencia Ficción" },
        { "Slice of Life", "Recuentos de la Vida" },
        { "Sports", "Deportes" },
        { "Supernatural", "Sobrenatural" },
        { "Suspense", "Suspenso" },
        { "Thriller", "Suspenso" },
        { "Ecchi", "Ecchi" },
        { "Mecha", "Mecha" },
        { "Music", "Música" },
        { "Psychological", "Psicológico" },
        { "School", "Vida Escolar" },
        { "Historical", "Histórico" },
        { "Isekai", "Isekai" },
        { "Military", "Militar" },
        { "Mahou Shoujo", "Magia" }
    };

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
                    {
                        var rawName = gName.GetString() ?? "";
                        if (!string.IsNullOrWhiteSpace(rawName))
                        {
                            var translated = AnimeGenreTranslations.TryGetValue(rawName, out var esName) ? esName : rawName;
                            if (!genres.Contains(translated))
                                genres.Add(translated);
                        }
                    }
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
                Title = !string.IsNullOrWhiteSpace(englishTitle) ? englishTitle : title,
                EnglishTitle = englishTitle,
                OriginalTitle = !string.IsNullOrWhiteSpace(title) ? title : japaneseTitle,
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

    public static IEnumerable<ExternalMediaResult> GetCuratedTopAnime()
    {
        return new List<ExternalMediaResult>
        {
            new()
            {
                ExternalId = "jikan_52991",
                Title = "Frieren: Tras el final del viaje",
                EnglishTitle = "Frieren: Beyond Journey's End",
                OriginalTitle = "Sousou no Frieren",
                AlternativeTitles = new List<string> { "Sousou no Frieren", "Frieren", "Frieren Beyond Journeys End" },
                Synopsis = "La maga elfa Frieren y sus valientes compañeros de aventura han derrotado al Rey Demonio, trayendo la paz a la tierra. Tras el fin de la gran contienda y el paso de las décadas, Frieren emprende un nuevo viaje para comprender el corazón humano y honrar la memoria de sus antiguos amigos.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/anime/cover/medium/bx154587-qQTzQnEJJ3oB.jpg",
                Type = MediaType.Anime,
                Rating = 9.3,
                Status = "Finalizado",
                ReleaseDate = new DateTime(2023, 9, 29),
                PopularityScore = 2400000.0,
                Genres = new List<string> { "Fantasía", "Aventura", "Drama" }
            },
            new()
            {
                ExternalId = "jikan_16498",
                Title = "Attack on Titan (Shingeki no Kyojin)",
                EnglishTitle = "Attack on Titan",
                OriginalTitle = "Shingeki no Kyojin",
                AlternativeTitles = new List<string> { "Ataque a los Titanes", "Shingeki no Kyojin", "SNK", "AOT", "Eren Yeager" },
                Synopsis = "Hace siglos, la humanidad fue masacrada casi hasta la extinción por monstruosas criaturas humanoides llamadas Titanes, obligando a los sobrevivientes a esconderse detrás de enormes muros concéntricos. Cuando un Titán Colosal destruye su hogar, el joven Eren Yeager jura exterminar a cada Titán del mundo.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/anime/cover/medium/bx16498-buvcRTBx4NSm.jpg",
                Type = MediaType.Anime,
                Rating = 9.1,
                Status = "Finalizado",
                ReleaseDate = new DateTime(2013, 4, 7),
                PopularityScore = 3900000.0,
                Genres = new List<string> { "Acción", "Drama", "Misterio", "Suspenso" }
            },
            new()
            {
                ExternalId = "jikan_5114",
                Title = "Fullmetal Alchemist: Brotherhood",
                EnglishTitle = "Fullmetal Alchemist: Brotherhood",
                OriginalTitle = "Hagane no Renkinjutsushi: Fullmetal Alchemist",
                AlternativeTitles = new List<string> { "FMAB", "El Alquimista de Acero", "Edward Elric", "Fullmetal Alchemist" },
                Synopsis = "Después de que un terrible experimento de alquimia sale mal en la casa de los Elric, los hermanos Edward y Alphonse son llevados a una nueva y catastrófica realidad. Para recuperar sus cuerpos, ambos emprenden la búsqueda de la legendaria Piedra Filosofal.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/anime/cover/medium/bx5114-nSWCgQlmOMtj.jpg",
                Type = MediaType.Anime,
                Rating = 9.2,
                Status = "Finalizado",
                ReleaseDate = new DateTime(2009, 4, 5),
                PopularityScore = 3400000.0,
                Genres = new List<string> { "Acción", "Aventura", "Fantasía", "Drama" }
            },
            new()
            {
                ExternalId = "jikan_21",
                Title = "One Piece",
                EnglishTitle = "One Piece",
                OriginalTitle = "One Piece",
                AlternativeTitles = new List<string> { "OP", "Monkey D. Luffy", "Luffy", "Sombrero de Paja" },
                Synopsis = "Riqueza, fama, poder... el Rey de los Piratas Gold Roger obtuvo todo en este mundo. Antes de morir, sus últimas palabras inspiraron a miles de valientes a hacerse a la mar en busca de su legendario tesoro: el One Piece. Monkey D. Luffy reúne a su tripulación para convertirse en el próximo Rey de los Piratas.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/anime/cover/medium/bx21-ELSYx3yMPcKM.jpg",
                Type = MediaType.Anime,
                Rating = 9.0,
                Status = "En emisión",
                ReleaseDate = new DateTime(1999, 10, 20),
                PopularityScore = 3100000.0,
                Genres = new List<string> { "Acción", "Aventura", "Fantasía", "Comedia" }
            },
            new()
            {
                ExternalId = "jikan_38000",
                Title = "Demon Slayer: Kimetsu no Yaiba",
                EnglishTitle = "Demon Slayer: Kimetsu no Yaiba",
                OriginalTitle = "Kimetsu no Yaiba",
                AlternativeTitles = new List<string> { "Kimetsu no Yaiba", "Guardianes de la Noche", "Tanjiro Kamado", "KNY" },
                Synopsis = "Tanjiro Kamado es un joven inteligente y de buen corazón que vive con su familia en las montañas. Todo cambia cuando su familia es atacada y asesinada por demonios, sobreviviendo únicamente su hermana Nezuko, quien ha sido transformada en un demonio.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/anime/cover/medium/bx101922-WBsBl0ClmgYL.jpg",
                Type = MediaType.Anime,
                Rating = 8.9,
                Status = "En emisión",
                ReleaseDate = new DateTime(2019, 4, 6),
                PopularityScore = 3200000.0,
                Genres = new List<string> { "Acción", "Fantasía", "Sobrenatural", "Histórico" }
            },
            new()
            {
                ExternalId = "jikan_40748",
                Title = "Jujutsu Kaisen",
                EnglishTitle = "Jujutsu Kaisen",
                OriginalTitle = "Jujutsu Kaisen",
                AlternativeTitles = new List<string> { "JJK", "Yuji Itadori", "Satoru Gojo", "Ryomen Sukuna" },
                Synopsis = "Yuji Itadori es un estudiante de secundaria con una fuerza física sobrehumana. Para salvar a sus amigos del ataque de espíritus malditos, traga un objeto maldito de grado especial: el dedo de Ryomen Sukuna, convirtiéndose en el recipiente del Rey de las Maldiciones.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/anime/cover/medium/bx113415-LHBAeoZDIsnF.jpg",
                Type = MediaType.Anime,
                Rating = 8.8,
                Status = "En emisión",
                ReleaseDate = new DateTime(2020, 10, 3),
                PopularityScore = 2900000.0,
                Genres = new List<string> { "Acción", "Fantasía", "Sobrenatural" }
            },
            new()
            {
                ExternalId = "jikan_1535",
                Title = "Death Note",
                EnglishTitle = "Death Note",
                OriginalTitle = "Death Note",
                AlternativeTitles = new List<string> { "Cuaderno de Muerte", "Light Yagami", "Kira", "Ryuk" },
                Synopsis = "Light Yagami, un brillante estudiante japonés, encuentra un misterioso cuaderno llamado Death Note que permite matar a cualquier persona cuyo nombre sea escrito en él. Decidido a limpiar el mundo del mal bajo el alias de Kira, se enfrenta al legendario detective L en un duelo psicológico mortal.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/anime/cover/medium/bx1535-kUgkcrfOrkUM.jpg",
                Type = MediaType.Anime,
                Rating = 8.9,
                Status = "Finalizado",
                ReleaseDate = new DateTime(2006, 10, 4),
                PopularityScore = 3800000.0,
                Genres = new List<string> { "Misterio", "Psicológico", "Sobrenatural", "Suspenso" }
            },
            new()
            {
                ExternalId = "jikan_11061",
                Title = "Hunter x Hunter",
                EnglishTitle = "Hunter x Hunter (2011)",
                OriginalTitle = "Hunter x Hunter",
                AlternativeTitles = new List<string> { "HxH", "Cazador X", "Gon Freecss", "Killua Zoldyck" },
                Synopsis = "Gon Freecss descubre que su padre, a quien creía muerto, es en realidad uno de los Cazadores más legendarios del mundo. Decidido a seguir sus pasos, Gon se presenta al peligroso Examen de Cazador junto a sus inseparables amigos Killua, Kurapika y Leorio.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/anime/cover/medium/bx11061-y5gsT1hoHuHw.png",
                Type = MediaType.Anime,
                Rating = 9.1,
                Status = "Finalizado",
                ReleaseDate = new DateTime(2011, 10, 2),
                PopularityScore = 2950000.0,
                Genres = new List<string> { "Acción", "Aventura", "Fantasía" }
            },
            new()
            {
                ExternalId = "jikan_52299",
                Title = "Solo Leveling (Anime)",
                EnglishTitle = "Solo Leveling",
                OriginalTitle = "Ore dake Level Up na Ken",
                AlternativeTitles = new List<string> { "Na Honjaman Level Up", "Sung Jinwoo", "Ore dake Level Up na Ken" },
                Synopsis = "En un mundo conectado a mazmorras llenas de monstruos, Sung Jinwoo es conocido como el cazador más débil de toda la humanidad. Tras sobrevivir al borde de la muerte en una mazmorra doble oculta, recibe el poder exclusivo del 'Sistema', que le permite subir de nivel sin límites.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/anime/cover/medium/bx151807-it355ZgzquUd.png",
                Type = MediaType.Anime,
                Rating = 8.8,
                Status = "En emisión",
                ReleaseDate = new DateTime(2024, 1, 7),
                PopularityScore = 2100000.0,
                Genres = new List<string> { "Acción", "Aventura", "Fantasía" }
            },
            new()
            {
                ExternalId = "jikan_1735",
                Title = "Naruto: Shippuden",
                EnglishTitle = "Naruto: Shippuden",
                OriginalTitle = "Naruto: Shippuuden",
                AlternativeTitles = new List<string> { "Naruto", "Naruto Uzumaki", "Sasuke Uchiha", "Konoha", "Akatsuki" },
                Synopsis = "Tras dos años y medio de intenso entrenamiento con Jiraiya, Naruto Uzumaki regresa a la Aldea Oculta de la Hoja más fuerte que nunca, listo para rescatar a su amigo Sasuke Uchiha y enfrentar la creciente amenaza de la organización criminal Akatsuki.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/anime/cover/medium/bx1735-kGfVm0YqCPcu.png",
                Type = MediaType.Anime,
                Rating = 8.7,
                Status = "Finalizado",
                ReleaseDate = new DateTime(2007, 2, 15),
                PopularityScore = 2850000.0,
                Genres = new List<string> { "Acción", "Aventura", "Fantasía", "Artes Marciales" }
            },
            new()
            {
                ExternalId = "jikan_813",
                Title = "Dragon Ball Z",
                EnglishTitle = "Dragon Ball Z",
                OriginalTitle = "Dragon Ball Z",
                AlternativeTitles = new List<string> { "DBZ", "Goku", "Vegeta", "Dragon Ball Super", "Saiyajin" },
                Synopsis = "Cinco años después de ganar el Torneo Mundial de las Artes Marciales, Son Goku lleva una vida pacífica junto a su esposa e hijo Gohan, hasta la llegada de Raditz, quien le revela su origen Saiyajin y desata batallas épicas por el destino del universo.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/anime/cover/medium/bx813-ZhnFNOeCU5dQ.png",
                Type = MediaType.Anime,
                Rating = 8.8,
                Status = "Finalizado",
                ReleaseDate = new DateTime(1989, 4, 26),
                PopularityScore = 2600000.0,
                Genres = new List<string> { "Acción", "Aventura", "Fantasía", "Comedia" }
            },
            new()
            {
                ExternalId = "jikan_9253",
                Title = "Steins;Gate",
                EnglishTitle = "Steins;Gate",
                OriginalTitle = "Steins;Gate",
                AlternativeTitles = new List<string> { "Steins Gate", "Rintaro Okabe", "Kurisu Makise", "El Psy Kongroo" },
                Synopsis = "El autoproclamado científico loco Rintaro Okabe y los miembros de su laboratorio en Akihabara descubren accidentalmente cómo enviar mensajes de texto al pasado, desencadenando una conspiración mundial en torno a los viajes en el tiempo.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/anime/cover/medium/bx9253-tIUXF2gfU8Sg.jpg",
                Type = MediaType.Anime,
                Rating = 9.1,
                Status = "Finalizado",
                ReleaseDate = new DateTime(2011, 4, 6),
                PopularityScore = 2550000.0,
                Genres = new List<string> { "Ciencia Ficción", "Suspenso", "Psicológico", "Drama" }
            },
            new()
            {
                ExternalId = "jikan_37521",
                Title = "Vinland Saga",
                EnglishTitle = "Vinland Saga",
                OriginalTitle = "Vinland Saga",
                AlternativeTitles = new List<string> { "Thorfinn", "Askeladd", "Vikingos Anime" },
                Synopsis = "El joven Thorfinn crece escuchando las historias de los viejos marineros sobre una tierra legendaria llamada Vinland. Sin embargo, cuando su padre es asesinado por el líder mercenario Askeladd, Thorfinn se une a la banda de su enemigo buscando venganza en un duelo honorable.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/anime/cover/medium/bx101348-2fhDFPCuMNiz.jpg",
                Type = MediaType.Anime,
                Rating = 8.9,
                Status = "En emisión",
                ReleaseDate = new DateTime(2019, 7, 8),
                PopularityScore = 1950000.0,
                Genres = new List<string> { "Acción", "Aventura", "Drama", "Histórico" }
            },
            new()
            {
                ExternalId = "jikan_44511",
                Title = "Chainsaw Man",
                EnglishTitle = "Chainsaw Man",
                OriginalTitle = "Chainsaw Man",
                AlternativeTitles = new List<string> { "El Hombre Motosierra", "Denji", "Makima", "Pochita" },
                Synopsis = "Denji es un adolescente atrapado en la pobreza extrema que caza demonios junto a su mascota demonio Pochita para pagar las deudas de su difunto padre. Tras una traición mortal, Pochita se fusiona con el corazón de Denji, otorgándole el poder de transformarse en Chainsaw Man.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/anime/cover/medium/bx127230-DdP4vAdssLoz.png",
                Type = MediaType.Anime,
                Rating = 8.6,
                Status = "En emisión",
                ReleaseDate = new DateTime(2022, 10, 12),
                PopularityScore = 2200000.0,
                Genres = new List<string> { "Acción", "Fantasía", "Sobrenatural" }
            },
            new()
            {
                ExternalId = "jikan_30276",
                Title = "One-Punch Man",
                EnglishTitle = "One-Punch Man",
                OriginalTitle = "One Punch Man",
                AlternativeTitles = new List<string> { "OPM", "Saitama", "Genos", "El Calvo con Capa" },
                Synopsis = "Saitama es un héroe por pasatiempo que ha entrenado tan duro que puede derrotar a cualquier enemigo de un solo golpe. Ahora, aburrido por su abrumador poder, busca un oponente digno mientras se une a la Asociación de Héroes junto a su discípulo cíborg Genos.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/anime/cover/medium/bx21087-B5DHjqZ3kW4b.jpg",
                Type = MediaType.Anime,
                Rating = 8.6,
                Status = "En emisión",
                ReleaseDate = new DateTime(2015, 10, 5),
                PopularityScore = 2700000.0,
                Genres = new List<string> { "Acción", "Comedia", "Ciencia Ficción" }
            }
        };
    }
}
