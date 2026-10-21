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

public class MangaDexComicApiService : IComicApiService
{
    private readonly HttpClient _httpClient;

    public MangaDexComicApiService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "AMRecomen-App/2.0");
        }
    }

    public async Task<IEnumerable<ExternalMediaResult>> SearchComicsAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Enumerable.Empty<ExternalMediaResult>();

        var q = query.Trim();
        var results = new List<ExternalMediaResult>();

        // Consultar en paralelo Kitsu, MangaDex y AniList en tiempo real
        var kitsuTask = SearchKitsuComicsAsync(q, 15);
        var mangaDexTask = SearchMangaDexLiveAsync(q, 10);
        var aniListKrTask = SearchAniListComicsAsync(q, "KR", MediaType.Manhwa, 6);
        var aniListCnTask = SearchAniListComicsAsync(q, "CN", MediaType.Manhua, 6);

        await Task.WhenAll(kitsuTask, mangaDexTask, aniListKrTask, aniListCnTask);

        var combined = kitsuTask.Result
            .Concat(mangaDexTask.Result)
            .Concat(aniListKrTask.Result)
            .Concat(aniListCnTask.Result);

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

    public async Task<IEnumerable<ExternalMediaResult>> GetTrendingComicsAsync(bool isManhwa)
    {
        var targetType = isManhwa ? MediaType.Manhwa : MediaType.Manhua;
        string subtype = isManhwa ? "manhwa" : "manhua";
        string country = isManhwa ? "KR" : "CN";

        var kitsuTask = FetchKitsuComicsAsync($"https://kitsu.io/api/edge/manga?filter[subtype]={subtype}&sort=-userCount&page[limit]=15", targetType);
        var mangaDexTask = FetchMangaDexTrendingLiveAsync(isManhwa);
        var aniListTask = FetchAniListTrendingComicsAsync(country, targetType, 12);

        await Task.WhenAll(kitsuTask, mangaDexTask, aniListTask);

        var results = new List<ExternalMediaResult>();
        var combined = mangaDexTask.Result
            .Concat(aniListTask.Result)
            .Concat(kitsuTask.Result);

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

    public async Task<ExternalMediaResult?> GetComicByIdAsync(string externalId)
    {
        try
        {
            if (externalId.StartsWith("kitsu_manga_"))
            {
                var id = externalId.Replace("kitsu_manga_", "");
                var response = await _httpClient.GetAsync($"https://kitsu.io/api/edge/manga/{id}");
                if (!response.IsSuccessStatusCode)
                    return null;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var dataElem))
                    return ParseKitsuComicItem(dataElem, null);
            }

            var cleanId = externalId.Replace("mangadex_", "");
            var mdResp = await _httpClient.GetAsync($"https://api.mangadex.org/manga/{cleanId}?includes%5B%5D=cover_art");
            if (mdResp.IsSuccessStatusCode)
            {
                var jsonString = await mdResp.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(jsonString);
                if (doc.RootElement.TryGetProperty("data", out var dataElement))
                    return ParseMangaDexItem(dataElement);
            }
        }
        catch
        {
            // Ignorar fallo individual
        }
        return null;
    }

    private async Task<List<ExternalMediaResult>> SearchKitsuComicsAsync(string query, int limit)
    {
        var url = $"https://kitsu.io/api/edge/manga?filter[text]={Uri.EscapeDataString(query)}&page[limit]={limit}";
        return await FetchKitsuComicsAsync(url, null);
    }

    private async Task<List<ExternalMediaResult>> FetchKitsuComicsAsync(string url, MediaType? forcedType)
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
                var parsed = ParseKitsuComicItem(item, forcedType);
                if (parsed != null)
                    list.Add(parsed);
            }
        }
        catch
        {
            // Continuar con las demás fuentes si Kitsu falla
        }
        return list;
    }

    private ExternalMediaResult? ParseKitsuComicItem(JsonElement item, MediaType? forcedType)
    {
        try
        {
            var id = item.TryGetProperty("id", out var idP) ? idP.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(id) || !item.TryGetProperty("attributes", out var attr))
                return null;

            string subtype = attr.TryGetProperty("subtype", out var subP) && subP.ValueKind == JsonValueKind.String ? subP.GetString()?.ToLowerInvariant() ?? "" : "";
            MediaType mediaType;
            if (forcedType.HasValue)
            {
                mediaType = forcedType.Value;
            }
            else if (subtype == "manhwa" || subtype == "oel")
            {
                mediaType = MediaType.Manhwa;
            }
            else if (subtype == "manhua")
            {
                mediaType = MediaType.Manhua;
            }
            else
            {
                return null;
            }

            string canonical = attr.TryGetProperty("canonicalTitle", out var canP) && canP.ValueKind == JsonValueKind.String ? canP.GetString() ?? "" : "";
            string enTitle = "";
            string enKrTitle = "";

            if (attr.TryGetProperty("titles", out var titlesObj) && titlesObj.ValueKind == JsonValueKind.Object)
            {
                enTitle = titlesObj.TryGetProperty("en", out var enP) && enP.ValueKind == JsonValueKind.String ? enP.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(enTitle))
                {
                    enTitle = titlesObj.TryGetProperty("en_us", out var enUsP) && enUsP.ValueKind == JsonValueKind.String ? enUsP.GetString() ?? "" : "";
                }
                enKrTitle = titlesObj.TryGetProperty("en_kr", out var ekP) && ekP.ValueKind == JsonValueKind.String ? ekP.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(enKrTitle))
                {
                    enKrTitle = titlesObj.TryGetProperty("en_cn", out var ecP) && ecP.ValueKind == JsonValueKind.String ? ecP.GetString() ?? "" : "";
                }
            }

            var displayTitle = !string.IsNullOrWhiteSpace(enTitle) ? enTitle : (!string.IsNullOrWhiteSpace(canonical) ? canonical : enKrTitle);
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

            double rating = 8.2;
            if (attr.TryGetProperty("averageRating", out var arP) && arP.ValueKind == JsonValueKind.String &&
                double.TryParse(arP.GetString(), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var parsedRating))
            {
                rating = Math.Round(parsedRating / 10.0, 1);
            }

            double userCount = 25000;
            if (attr.TryGetProperty("userCount", out var ucP) && ucP.ValueKind == JsonValueKind.Number)
            {
                userCount = ucP.GetDouble() * 4.5;
            }

            string rawStatus = attr.TryGetProperty("status", out var stP) && stP.ValueKind == JsonValueKind.String ? stP.GetString() ?? "" : "";
            string status = rawStatus switch
            {
                "current" => "En publicación",
                "finished" => "Finalizado",
                _ => "En publicación"
            };

            DateTime? releaseDate = null;
            if (attr.TryGetProperty("startDate", out var sdP) && sdP.ValueKind == JsonValueKind.String && DateTime.TryParse(sdP.GetString(), out var dt))
            {
                releaseDate = dt;
            }

            var altTitles = new List<string>();
            if (!string.IsNullOrWhiteSpace(canonical) && canonical != displayTitle)
                altTitles.Add(canonical);
            if (!string.IsNullOrWhiteSpace(enKrTitle) && enKrTitle != displayTitle && !altTitles.Contains(enKrTitle))
                altTitles.Add(enKrTitle);

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
                ExternalId = $"kitsu_manga_{id}",
                Title = displayTitle,
                EnglishTitle = !string.IsNullOrWhiteSpace(enTitle) ? enTitle : displayTitle,
                OriginalTitle = !string.IsNullOrWhiteSpace(enKrTitle) ? enKrTitle : canonical,
                AlternativeTitles = altTitles,
                NeedTranslation = true,
                Synopsis = synopsis,
                CoverImageUrl = coverUrl,
                Type = mediaType,
                Rating = rating,
                Status = status,
                ReleaseDate = releaseDate,
                PopularityScore = userCount,
                Genres = mediaType == MediaType.Manhwa
                    ? new List<string> { "Acción", "Fantasía", "Aventura" }
                    : new List<string> { "Artes Marciales", "Fantasía", "Cultivación" }
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<ExternalMediaResult>> SearchMangaDexLiveAsync(string query, int limit)
    {
        var list = new List<ExternalMediaResult>();
        try
        {
            var response = await _httpClient.GetAsync($"https://api.mangadex.org/manga?title={Uri.EscapeDataString(query)}&limit={limit}&includes%5B%5D=cover_art&originalLanguage%5B%5D=ko&originalLanguage%5B%5D=zh&originalLanguage%5B%5D=zh-hk");
            if (!response.IsSuccessStatusCode)
                return list;

            var jsonString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonString);
            if (doc.RootElement.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
            {
                var results = ParseMangaDexArray(dataElement).ToList();
                await EnrichWithStatisticsAsync(results);
                list.AddRange(results);
            }
        }
        catch
        {
            // Ignorar si MangaDex está en mantenimiento
        }
        return list;
    }

    private async Task<List<ExternalMediaResult>> FetchMangaDexTrendingLiveAsync(bool isManhwa)
    {
        var list = new List<ExternalMediaResult>();
        try
        {
            string langParam = isManhwa ? "originalLanguage%5B%5D=ko" : "originalLanguage%5B%5D=zh&originalLanguage%5B%5D=zh-hk";
            var response = await _httpClient.GetAsync($"https://api.mangadex.org/manga?limit=12&order%5BfollowedCount%5D=desc&includes%5B%5D=cover_art&{langParam}");
            if (!response.IsSuccessStatusCode)
                return list;

            var jsonString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonString);
            if (doc.RootElement.TryGetProperty("data", out var dataElement) && dataElement.ValueKind == JsonValueKind.Array)
            {
                var results = ParseMangaDexArray(dataElement).ToList();
                await EnrichWithStatisticsAsync(results);
                list.AddRange(results);
            }
        }
        catch
        {
            // Ignorar si MangaDex está en mantenimiento
        }
        return list;
    }

    private IEnumerable<ExternalMediaResult> ParseMangaDexArray(JsonElement arrayElement)
    {
        var list = new List<ExternalMediaResult>();
        int index = 0;
        foreach (var item in arrayElement.EnumerateArray())
        {
            var result = ParseMangaDexItem(item, index);
            if (result != null)
            {
                list.Add(result);
                index++;
            }
        }
        return list;
    }

    private async Task EnrichWithStatisticsAsync(List<ExternalMediaResult> items)
    {
        if (!items.Any())
            return;

        try
        {
            var queryParams = string.Join("&", items.Select(i => $"manga%5B%5D={Uri.EscapeDataString(i.ExternalId.Replace("mangadex_", ""))}"));
            var response = await _httpClient.GetAsync($"https://api.mangadex.org/statistics/manga?{queryParams}");
            if (!response.IsSuccessStatusCode)
                return;

            var jsonString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonString);
            if (!doc.RootElement.TryGetProperty("statistics", out var statsElem) || statsElem.ValueKind != JsonValueKind.Object)
                return;

            foreach (var item in items)
            {
                var rawId = item.ExternalId.Replace("mangadex_", "");
                if (statsElem.TryGetProperty(rawId, out var mangaStat))
                {
                    if (mangaStat.TryGetProperty("follows", out var followsProp) && followsProp.ValueKind == JsonValueKind.Number)
                    {
                        var follows = followsProp.GetDouble();
                        if (follows > 0)
                            item.PopularityScore = follows;
                    }

                    if (mangaStat.TryGetProperty("rating", out var ratingObj) && ratingObj.ValueKind == JsonValueKind.Object)
                    {
                        if (ratingObj.TryGetProperty("bayesian", out var bayesianProp) && bayesianProp.ValueKind == JsonValueKind.Number)
                        {
                            item.Rating = Math.Round(bayesianProp.GetDouble(), 1);
                        }
                        else if (ratingObj.TryGetProperty("average", out var avgProp) && avgProp.ValueKind == JsonValueKind.Number)
                        {
                            item.Rating = Math.Round(avgProp.GetDouble(), 1);
                        }
                    }
                }
            }
        }
        catch
        {
            // Mantener valores calculados por ranking de respaldo
        }
    }

    private ExternalMediaResult? ParseMangaDexItem(JsonElement item, int rankIndex = 0)
    {
        try
        {
            if (!item.TryGetProperty("id", out var idProp))
                return null;

            var mangaId = idProp.GetString() ?? "";
            var attributes = item.GetProperty("attributes");

            var originalLanguage = attributes.TryGetProperty("originalLanguage", out var langProp) ? langProp.GetString() ?? "" : "";
            MediaType mediaType;
            if (originalLanguage == "ko")
                mediaType = MediaType.Manhwa;
            else if (originalLanguage == "zh" || originalLanguage == "zh-hk")
                mediaType = MediaType.Manhua;
            else
                return null;

            string primaryTitle = "";
            string romajiTitle = "";
            if (attributes.TryGetProperty("title", out var titleProp) && titleProp.ValueKind == JsonValueKind.Object)
            {
                if (titleProp.TryGetProperty("es", out var esP))
                    primaryTitle = esP.GetString() ?? "";
                else if (titleProp.TryGetProperty("es-la", out var esLaP))
                    primaryTitle = esLaP.GetString() ?? "";
                else if (titleProp.TryGetProperty("en", out var enP))
                    primaryTitle = enP.GetString() ?? "";

                if (titleProp.EnumerateObject().Any())
                    romajiTitle = titleProp.EnumerateObject().First().Value.GetString() ?? "";
            }

            var alternativeTitles = new List<string>();
            string? altEs = null;
            string? altEn = null;

            if (attributes.TryGetProperty("altTitles", out var altTitlesProp) && altTitlesProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var alt in altTitlesProp.EnumerateArray())
                {
                    foreach (var prop in alt.EnumerateObject())
                    {
                        var altVal = prop.Value.GetString();
                        if (string.IsNullOrWhiteSpace(altVal))
                            continue;

                        if ((prop.Name == "es" || prop.Name == "es-la") && string.IsNullOrWhiteSpace(altEs))
                            altEs = altVal;
                        else if (prop.Name == "en" && string.IsNullOrWhiteSpace(altEn))
                            altEn = altVal;

                        if (!alternativeTitles.Contains(altVal))
                            alternativeTitles.Add(altVal);
                    }
                }
            }

            // Priorizar título en español o inglés real antes que romanización coreana/china (ko-ro/zh-ro)
            var title = !string.IsNullOrWhiteSpace(altEs)
                ? altEs
                : (!string.IsNullOrWhiteSpace(altEn)
                    ? altEn
                    : (!string.IsNullOrWhiteSpace(primaryTitle) ? primaryTitle : romajiTitle));

            if (string.IsNullOrWhiteSpace(title))
                return null;

            var synopsis = "";
            var needTranslation = false;
            if (attributes.TryGetProperty("description", out var descProp) && descProp.ValueKind == JsonValueKind.Object)
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

            if (string.IsNullOrEmpty(fileName))
                return null;

            // Usar el proxy local del servidor (/api/cover) para evitar el bloqueo de hotlink de uploads.mangadex.org
            var rawCoverUrl = $"https://uploads.mangadex.org/covers/{mangaId}/{fileName}.512.jpg";
            var coverUrl = $"/api/cover?url={Uri.EscapeDataString(rawCoverUrl)}";

            var status = attributes.TryGetProperty("status", out var statusProp) ? statusProp.GetString() ?? "" : "";

            DateTime? releaseDate = null;
            if (attributes.TryGetProperty("year", out var yearProp) && yearProp.ValueKind == JsonValueKind.Number)
            {
                var year = yearProp.GetInt32();
                if (year >= 1950 && year <= 2100)
                {
                    releaseDate = new DateTime(year, 1, 1);
                }
            }

            var genres = new List<string>();
            if (attributes.TryGetProperty("tags", out var tagsProp) && tagsProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var tag in tagsProp.EnumerateArray())
                {
                    if (tag.TryGetProperty("attributes", out var tagAttr) &&
                        tagAttr.TryGetProperty("name", out var nameObj) &&
                        nameObj.TryGetProperty("en", out var enTag))
                    {
                        var tagName = enTag.GetString();
                        if (!string.IsNullOrEmpty(tagName))
                        {
                            var translatedTag = GenreTranslations.TryGetValue(tagName, out var esTag) ? esTag : tagName;
                            if (!genres.Contains(translatedTag))
                                genres.Add(translatedTag);
                        }
                    }
                }
            }

            double fallbackPopularity = Math.Max(1500, 25000 - (rankIndex * 2200));
            double fallbackRating = Math.Round(Math.Max(7.5, 8.8 - (rankIndex * 0.1)), 1);

            return new ExternalMediaResult
            {
                ExternalId = $"mangadex_{mangaId}",
                Title = title,
                EnglishTitle = !string.IsNullOrWhiteSpace(altEn) ? altEn : title,
                OriginalTitle = !string.IsNullOrWhiteSpace(romajiTitle) ? romajiTitle : title,
                AlternativeTitles = alternativeTitles,
                NeedTranslation = needTranslation,
                Synopsis = CleanDescription(synopsis),
                CoverImageUrl = coverUrl,
                Type = mediaType,
                Rating = fallbackRating,
                Status = status,
                ReleaseDate = releaseDate,
                PopularityScore = fallbackPopularity,
                Genres = genres
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<ExternalMediaResult>> SearchAniListComicsAsync(string search, string countryOfOrigin, MediaType mediaType, int limit)
    {
        const string query = @"
        query ($search: String, $country: CountryCode, $perPage: Int) {
          Page(page: 1, perPage: $perPage) {
            media(search: $search, type: MANGA, countryOfOrigin: $country, sort: POPULARITY_DESC, isAdult: false) {
              id
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

        return await ExecuteAniListComicQueryAsync(query, new { search, country = countryOfOrigin, perPage = limit }, mediaType);
    }

    private async Task<List<ExternalMediaResult>> FetchAniListTrendingComicsAsync(string countryOfOrigin, MediaType mediaType, int limit)
    {
        const string query = @"
        query ($country: CountryCode, $perPage: Int) {
          Page(page: 1, perPage: $perPage) {
            media(type: MANGA, countryOfOrigin: $country, sort: TRENDING_DESC, isAdult: false) {
              id
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

        return await ExecuteAniListComicQueryAsync(query, new { country = countryOfOrigin, perPage = limit }, mediaType);
    }

    private async Task<List<ExternalMediaResult>> ExecuteAniListComicQueryAsync(string query, object variables, MediaType mediaType)
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
                var parsed = ParseAniListComicItem(item, mediaType);
                if (parsed != null)
                    list.Add(parsed);
            }
        }
        catch
        {
            // Ignorar errores de red
        }
        return list;
    }

    private ExternalMediaResult? ParseAniListComicItem(JsonElement item, MediaType mediaType)
    {
        try
        {
            int aniId = item.TryGetProperty("id", out var idProp) && idProp.ValueKind == JsonValueKind.Number ? idProp.GetInt32() : 0;
            if (aniId == 0)
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

            double rating = 8.2;
            if (item.TryGetProperty("averageScore", out var scoreP) && scoreP.ValueKind == JsonValueKind.Number)
            {
                rating = Math.Round(scoreP.GetDouble() / 10.0, 1);
            }

            double popularity = 35000;
            if (item.TryGetProperty("popularity", out var popP) && popP.ValueKind == JsonValueKind.Number)
            {
                popularity = popP.GetDouble();
            }

            string rawStatus = item.TryGetProperty("status", out var stP) && stP.ValueKind == JsonValueKind.String ? stP.GetString() ?? "" : "";
            string status = rawStatus switch
            {
                "FINISHED" => "Finalizado",
                "RELEASING" => "En publicación",
                _ => "En publicación"
            };

            DateTime? releaseDate = null;
            if (item.TryGetProperty("startDate", out var sdObj) && sdObj.ValueKind == JsonValueKind.Object)
            {
                int year = sdObj.TryGetProperty("year", out var yP) && yP.ValueKind == JsonValueKind.Number ? yP.GetInt32() : 0;
                int month = sdObj.TryGetProperty("month", out var mP) && mP.ValueKind == JsonValueKind.Number ? mP.GetInt32() : 1;
                int day = sdObj.TryGetProperty("day", out var dP) && dP.ValueKind == JsonValueKind.Number ? dP.GetInt32() : 1;
                if (year >= 1980 && year <= 2100)
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
                        var translated = GenreTranslations.TryGetValue(rawG, out var esG) ? esG : rawG;
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
                ExternalId = $"mangadex_ani_{aniId}",
                Title = displayTitle,
                EnglishTitle = !string.IsNullOrWhiteSpace(english) ? english : displayTitle,
                OriginalTitle = !string.IsNullOrWhiteSpace(romaji) ? romaji : nativeTitle,
                AlternativeTitles = altTitles,
                NeedTranslation = true,
                Synopsis = synopsis,
                CoverImageUrl = coverUrl,
                Type = mediaType,
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

    private static readonly Dictionary<string, string> GenreTranslations = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Action", "Acción" },
        { "Adventure", "Aventura" },
        { "Comedy", "Comedia" },
        { "Drama", "Drama" },
        { "Fantasy", "Fantasía" },
        { "Historical", "Histórico" },
        { "Horror", "Terror" },
        { "Martial Arts", "Artes Marciales" },
        { "Mystery", "Misterio" },
        { "Psychological", "Psicológico" },
        { "Reincarnation", "Reencarnación" },
        { "Romance", "Romance" },
        { "Sci-Fi", "Ciencia Ficción" },
        { "Slice of Life", "Recuentos de la Vida" },
        { "Supernatural", "Sobrenatural" },
        { "Thriller", "Suspenso" },
        { "Tragedy", "Tragedia" },
        { "Wuxia", "Cultivación" },
        { "Isekai", "Isekai" },
        { "Magic", "Magia" },
        { "Monsters", "Monstruos" },
        { "Demons", "Demonios" },
        { "School Life", "Vida Escolar" },
        { "Sports", "Deportes" }
    };
}
