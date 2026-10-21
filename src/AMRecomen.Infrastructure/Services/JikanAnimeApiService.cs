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
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
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

        // Consultar en paralelo los motores de anime en tiempo real (Kitsu + Jikan + AniList)
        var kitsuTask = SearchKitsuAnimeAsync(q, 12);
        var jikanTask = SearchJikanLiveAsync(q, 10);
        var aniListTask = SearchAniListAnimeAsync(q, 10);

        await Task.WhenAll(kitsuTask, jikanTask, aniListTask);

        foreach (var item in kitsuTask.Result.Concat(jikanTask.Result).Concat(aniListTask.Result))
        {
            if (!results.Any(r => r.ExternalId == item.ExternalId ||
                                  r.Title.Equals(item.Title, StringComparison.OrdinalIgnoreCase) ||
                                  (!string.IsNullOrWhiteSpace(r.OriginalTitle) && r.OriginalTitle.Equals(item.OriginalTitle, StringComparison.OrdinalIgnoreCase))))
            {
                results.Add(item);
            }
        }

        return results;
    }

    public async Task<IEnumerable<ExternalMediaResult>> GetTrendingAnimeAsync()
    {
        var results = new List<ExternalMediaResult>();

        // Consultar automáticamente las tendencias de anime en tiempo real desde Kitsu (en emisión y trending global), Jikan y AniList
        var kitsuTrendingTask = FetchKitsuUrlAsync("https://kitsu.io/api/edge/trending/anime?limit=15");
        var kitsuCurrentTask = FetchKitsuUrlAsync("https://kitsu.io/api/edge/anime?filter[status]=current&sort=-userCount&page[limit]=12");
        var jikanNowTask = FetchJikanSeasonsNowAsync();
        var aniListTrendingTask = FetchAniListTrendingAnimeAsync(12);

        await Task.WhenAll(kitsuTrendingTask, kitsuCurrentTask, jikanNowTask, aniListTrendingTask);

        var combined = jikanNowTask.Result
            .Concat(aniListTrendingTask.Result)
            .Concat(kitsuTrendingTask.Result)
            .Concat(kitsuCurrentTask.Result);

        foreach (var item in combined)
        {
            if (!results.Any(r => r.ExternalId == item.ExternalId ||
                                  r.Title.Equals(item.Title, StringComparison.OrdinalIgnoreCase) ||
                                  (!string.IsNullOrWhiteSpace(r.OriginalTitle) && r.OriginalTitle.Equals(item.OriginalTitle, StringComparison.OrdinalIgnoreCase))))
            {
                results.Add(item);
            }
        }

        return results;
    }

    public async Task<ExternalMediaResult?> GetAnimeByIdAsync(string externalId)
    {
        try
        {
            if (externalId.StartsWith("kitsu_anime_"))
            {
                var kitsuId = externalId.Replace("kitsu_anime_", "");
                var response = await _httpClient.GetAsync($"https://kitsu.io/api/edge/anime/{kitsuId}");
                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var dataElem))
                    return ParseKitsuAnimeItem(dataElem);
            }

            if (int.TryParse(externalId.Replace("jikan_", "").Replace("anilist_", ""), out var malId))
            {
                var response = await _httpClient.GetAsync($"https://api.jikan.moe/v4/anime/{malId}");
                if (response.IsSuccessStatusCode)
                {
                    var jsonString = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(jsonString);
                    if (doc.RootElement.TryGetProperty("data", out var dataElement))
                        return ParseJikanItem(dataElement);
                }
            }
        }
        catch
        {
            // Ignorar error de red individual
        }
        return null;
    }

    private async Task<List<ExternalMediaResult>> SearchKitsuAnimeAsync(string query, int limit)
    {
        var url = $"https://kitsu.io/api/edge/anime?filter[text]={Uri.EscapeDataString(query)}&page[limit]={limit}";
        return await FetchKitsuUrlAsync(url);
    }

    private async Task<List<ExternalMediaResult>> FetchKitsuUrlAsync(string url)
    {
        var list = new List<ExternalMediaResult>();
        try
        {
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return list;

            var jsonString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonString);
            if (!doc.RootElement.TryGetProperty("data", out var dataArr) || dataArr.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var item in dataArr.EnumerateArray())
            {
                var parsed = ParseKitsuAnimeItem(item);
                if (parsed != null)
                    list.Add(parsed);
            }
        }
        catch
        {
            // Continuar con las demás fuentes si Kitsu no responde
        }
        return list;
    }

    private ExternalMediaResult? ParseKitsuAnimeItem(JsonElement item)
    {
        try
        {
            var id = item.TryGetProperty("id", out var idP) ? idP.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(id) || !item.TryGetProperty("attributes", out var attr))
                return null;

            string canonical = attr.TryGetProperty("canonicalTitle", out var canP) && canP.ValueKind == JsonValueKind.String ? canP.GetString() ?? "" : "";
            string enTitle = "";
            string enJpTitle = "";
            string jaJpTitle = "";

            if (attr.TryGetProperty("titles", out var titlesObj) && titlesObj.ValueKind == JsonValueKind.Object)
            {
                enTitle = titlesObj.TryGetProperty("en", out var enP) && enP.ValueKind == JsonValueKind.String ? enP.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(enTitle))
                {
                    enTitle = titlesObj.TryGetProperty("en_us", out var enUsP) && enUsP.ValueKind == JsonValueKind.String ? enUsP.GetString() ?? "" : "";
                }
                enJpTitle = titlesObj.TryGetProperty("en_jp", out var ejP) && ejP.ValueKind == JsonValueKind.String ? ejP.GetString() ?? "" : "";
                jaJpTitle = titlesObj.TryGetProperty("ja_jp", out var jjP) && jjP.ValueKind == JsonValueKind.String ? jjP.GetString() ?? "" : "";
            }

            var displayTitle = !string.IsNullOrWhiteSpace(enTitle) ? enTitle : (!string.IsNullOrWhiteSpace(canonical) ? canonical : enJpTitle);
            if (string.IsNullOrWhiteSpace(displayTitle))
                return null;

            string coverUrl = "";
            if (attr.TryGetProperty("posterImage", out var posterObj) && posterObj.ValueKind == JsonValueKind.Object)
            {
                coverUrl = posterObj.TryGetProperty("large", out var lgP) && lgP.ValueKind == JsonValueKind.String ? lgP.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(coverUrl))
                {
                    coverUrl = posterObj.TryGetProperty("original", out var orP) && orP.ValueKind == JsonValueKind.String ? orP.GetString() ?? "" : "";
                }
            }
            if (string.IsNullOrWhiteSpace(coverUrl))
                return null;

            string synopsis = attr.TryGetProperty("synopsis", out var synP) && synP.ValueKind == JsonValueKind.String ? synP.GetString() ?? "" : "";
            synopsis = CleanDescription(synopsis);

            double rating = 8.0;
            if (attr.TryGetProperty("averageRating", out var arP) && arP.ValueKind == JsonValueKind.String &&
                double.TryParse(arP.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedRating))
            {
                rating = Math.Round(parsedRating / 10.0, 1);
            }

            double userCount = 50000;
            if (attr.TryGetProperty("userCount", out var ucP) && ucP.ValueKind == JsonValueKind.Number)
            {
                userCount = ucP.GetDouble() * 4.0;
            }

            string rawStatus = attr.TryGetProperty("status", out var stP) && stP.ValueKind == JsonValueKind.String ? stP.GetString() ?? "" : "";
            string status = rawStatus switch
            {
                "current" => "En emisión",
                "finished" => "Finalizado",
                "upcoming" => "Próximamente",
                _ => "En emisión"
            };

            DateTime? releaseDate = null;
            if (attr.TryGetProperty("startDate", out var sdP) && sdP.ValueKind == JsonValueKind.String && DateTime.TryParse(sdP.GetString(), out var dt))
            {
                releaseDate = dt;
            }

            var altTitles = new List<string>();
            if (!string.IsNullOrWhiteSpace(canonical) && canonical != displayTitle)
                altTitles.Add(canonical);
            if (!string.IsNullOrWhiteSpace(enJpTitle) && enJpTitle != displayTitle && !altTitles.Contains(enJpTitle))
                altTitles.Add(enJpTitle);
            if (!string.IsNullOrWhiteSpace(jaJpTitle) && !altTitles.Contains(jaJpTitle))
                altTitles.Add(jaJpTitle);

            if (attr.TryGetProperty("abbreviatedTitles", out var abbArr) && abbArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in abbArr.EnumerateArray())
                {
                    var s = a.GetString();
                    if (!string.IsNullOrWhiteSpace(s) && !altTitles.Contains(s))
                        altTitles.Add(s);
                }
            }

            return new ExternalMediaResult
            {
                ExternalId = $"kitsu_anime_{id}",
                Title = displayTitle,
                EnglishTitle = !string.IsNullOrWhiteSpace(enTitle) ? enTitle : displayTitle,
                OriginalTitle = !string.IsNullOrWhiteSpace(enJpTitle) ? enJpTitle : jaJpTitle,
                AlternativeTitles = altTitles,
                NeedTranslation = true,
                Synopsis = synopsis,
                CoverImageUrl = coverUrl,
                Type = MediaType.Anime,
                Rating = rating,
                Status = status,
                ReleaseDate = releaseDate,
                PopularityScore = userCount,
                Genres = new List<string> { "Anime", "Acción", "Fantasía" }
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<ExternalMediaResult>> SearchJikanLiveAsync(string query, int limit)
    {
        var list = new List<ExternalMediaResult>();
        try
        {
            var response = await _httpClient.GetAsync($"https://api.jikan.moe/v4/anime?q={Uri.EscapeDataString(query)}&order_by=members&sort=desc&limit={limit}");
            if (!response.IsSuccessStatusCode)
                return list;

            var jsonString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonString);
            if (doc.RootElement.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
            {
                list.AddRange(ParseJikanArray(dataElement));
            }
        }
        catch
        {
            // Ignorar si Jikan no está disponible
        }
        return list;
    }

    private async Task<List<ExternalMediaResult>> FetchJikanSeasonsNowAsync()
    {
        var list = new List<ExternalMediaResult>();
        try
        {
            var response = await _httpClient.GetAsync("https://api.jikan.moe/v4/seasons/now?limit=15");
            if (!response.IsSuccessStatusCode)
                return list;

            var jsonString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonString);
            if (doc.RootElement.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
            {
                list.AddRange(ParseJikanArray(dataElement));
            }
        }
        catch
        {
            // Ignorar si Jikan no está disponible
        }
        return list;
    }

    private async Task<List<ExternalMediaResult>> SearchAniListAnimeAsync(string search, int limit)
    {
        const string query = @"
        query ($search: String, $perPage: Int) {
          Page(page: 1, perPage: $perPage) {
            media(search: $search, type: ANIME, sort: POPULARITY_DESC, isAdult: false) {
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

    private async Task<List<ExternalMediaResult>> FetchAniListTrendingAnimeAsync(int limit)
    {
        const string query = @"
        query ($perPage: Int) {
          Page(page: 1, perPage: $perPage) {
            media(type: ANIME, sort: TRENDING_DESC, isAdult: false) {
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
                dataElem.ValueKind != JsonValueKind.Object ||
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
                popularity = popP.GetDouble() * 2.2;
            }

            string rawStatus = item.TryGetProperty("status", out var stP) && stP.ValueKind == JsonValueKind.String ? stP.GetString() ?? "" : "";
            string status = rawStatus switch
            {
                "FINISHED" => "Finalizado",
                "RELEASING" => "En emisión",
                "NOT_YET_RELEASED" => "Próximamente",
                _ => "En emisión"
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
        text = Regex.Replace(text, "\\[Written by MAL Rewrite\\]", string.Empty, RegexOptions.IgnoreCase);
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
            if (string.IsNullOrWhiteSpace(imageUrl))
                return null;

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
                Synopsis = CleanDescription(synopsis),
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
}
