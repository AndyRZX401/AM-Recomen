using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using AMRecomen.Application.Dtos;
using AMRecomen.Application.Interfaces;
using AMRecomen.Domain.Enums;

namespace AMRecomen.Infrastructure.Services;

public class TmdbMovieApiService : IMovieApiService
{
    private readonly HttpClient _httpClient;
    private readonly string _apiKey;

    private static readonly Dictionary<int, string> MovieGenres = new()
    {
        { 28, "Acción" },
        { 12, "Aventura" },
        { 16, "Animación" },
        { 35, "Comedia" },
        { 80, "Crimen" },
        { 99, "Documental" },
        { 18, "Drama" },
        { 10751, "Familia" },
        { 14, "Fantasía" },
        { 36, "Historia" },
        { 27, "Terror" },
        { 10402, "Música" },
        { 9648, "Misterio" },
        { 10749, "Romance" },
        { 878, "Ciencia Ficción" },
        { 10770, "Película de TV" },
        { 53, "Suspenso" },
        { 10752, "Bélica" },
        { 37, "Western" }
    };

    private static readonly Dictionary<int, string> TvGenres = new()
    {
        { 10759, "Acción y Aventura" },
        { 16, "Animación" },
        { 35, "Comedia" },
        { 80, "Crimen" },
        { 99, "Documental" },
        { 18, "Drama" },
        { 10751, "Familia" },
        { 10762, "Infantil" },
        { 9648, "Misterio" },
        { 10763, "Noticias" },
        { 10764, "Reality" },
        { 10765, "Ciencia Ficción y Fantasía" },
        { 10766, "Telenovela" },
        { 10767, "Talk Show" },
        { 10768, "Guerra y Política" },
        { 37, "Western" }
    };

    private static readonly Dictionary<string, string> EnglishGenreTranslations = new(StringComparer.OrdinalIgnoreCase)
    {
        { "Action", "Acción" },
        { "Adventure", "Aventura" },
        { "Anime", "Animación" },
        { "Comedy", "Comedia" },
        { "Crime", "Crimen" },
        { "Drama", "Drama" },
        { "Espionage", "Espionaje" },
        { "Family", "Familia" },
        { "Fantasy", "Fantasía" },
        { "History", "Historia" },
        { "Horror", "Terror" },
        { "Legal", "Drama Legal" },
        { "Medical", "Drama Médico" },
        { "Music", "Música" },
        { "Mystery", "Misterio" },
        { "Romance", "Romance" },
        { "Science-Fiction", "Ciencia Ficción" },
        { "Sci-Fi & Fantasy", "Ciencia Ficción" },
        { "Sports", "Deportes" },
        { "Supernatural", "Sobrenatural" },
        { "Thriller", "Suspenso" },
        { "War", "Bélica" },
        { "Western", "Western" }
    };

    public TmdbMovieApiService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(10);
        if (!_httpClient.DefaultRequestHeaders.Contains("User-Agent"))
        {
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "AMRecomen-App/2.0");
        }
        _apiKey = configuration["TMDb:ApiKey"] 
                  ?? configuration["TMDB_API_KEY"] 
                  ?? configuration["NEXT_PUBLIC_TMDB_API_KEY"]
                  ?? Environment.GetEnvironmentVariable("TMDB_API_KEY") 
                  ?? Environment.GetEnvironmentVariable("TMDb__ApiKey")
                  ?? string.Empty;
    }

    private bool HasTmdbKey()
    {
        return !string.IsNullOrWhiteSpace(_apiKey) &&
               _apiKey.Length >= 15 &&
               !_apiKey.Contains("clave", StringComparison.OrdinalIgnoreCase) &&
               !_apiKey.Contains("coloca", StringComparison.OrdinalIgnoreCase) &&
               !_apiKey.Contains("placeholder", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IEnumerable<ExternalMediaResult>> SearchMoviesAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Enumerable.Empty<ExternalMediaResult>();

        if (HasTmdbKey())
        {
            try
            {
                var response = await _httpClient.GetAsync($"https://api.themoviedb.org/3/search/movie?api_key={_apiKey}&query={Uri.EscapeDataString(query)}&language=es-ES");
                if (response.IsSuccessStatusCode)
                {
                    var jsonString = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(jsonString);
                    if (doc.RootElement.TryGetProperty("results", out var resultsElement) && resultsElement.ValueKind == JsonValueKind.Array)
                    {
                        var tmdbList = ParseTmdbArray(resultsElement).ToList();
                        if (tmdbList.Any())
                            return tmdbList;
                    }
                }
            }
            catch
            {
                // Respaldo automático en vivo si TMDb falla
            }
        }

        // Búsqueda en tiempo real sin API key mediante Apple iTunes Movie Search (en español)
        return await SearchItunesMoviesLiveAsync(query, 12);
    }

    public async Task<IEnumerable<ExternalMediaResult>> GetTrendingMoviesAsync()
    {
        if (HasTmdbKey())
        {
            try
            {
                var response = await _httpClient.GetAsync($"https://api.themoviedb.org/3/trending/movie/day?api_key={_apiKey}&language=es-ES");
                if (response.IsSuccessStatusCode)
                {
                    var jsonString = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(jsonString);
                    if (doc.RootElement.TryGetProperty("results", out var resultsElement) && resultsElement.ValueKind == JsonValueKind.Array)
                    {
                        var tmdbList = ParseTmdbArray(resultsElement).ToList();
                        if (tmdbList.Any())
                            return tmdbList;
                    }
                }
            }
            catch
            {
                // Respaldo automático en vivo si TMDb falla
            }
        }

        // Obtener automáticamente las películas en tendencia del momento en tiempo real desde el feed oficial de Apple iTunes (en español)
        return await FetchItunesTrendingMoviesLiveAsync(15);
    }

    public async Task<ExternalMediaResult?> GetMovieByIdAsync(string externalId)
    {
        if (HasTmdbKey() && externalId.StartsWith("tmdb_"))
        {
            var cleanId = externalId.Replace("tmdb_", "");
            try
            {
                var response = await _httpClient.GetAsync($"https://api.themoviedb.org/3/movie/{cleanId}?api_key={_apiKey}&language=es-ES");
                if (response.IsSuccessStatusCode)
                {
                    var jsonString = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(jsonString);
                    return ParseTmdbItem(doc.RootElement);
                }
            }
            catch
            {
                return null;
            }
        }

        if (externalId.StartsWith("itunes_movie_"))
        {
            var trackId = externalId.Replace("itunes_movie_", "");
            try
            {
                var response = await _httpClient.GetAsync($"https://itunes.apple.com/lookup?id={Uri.EscapeDataString(trackId)}&country=MX&lang=es_mx");
                if (response.IsSuccessStatusCode)
                {
                    var jsonString = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(jsonString);
                    if (doc.RootElement.TryGetProperty("results", out var arr) && arr.ValueKind == JsonValueKind.Array && arr.GetArrayLength() > 0)
                    {
                        return ParseItunesSearchMovieItem(arr[0], 0);
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private async Task<IEnumerable<ExternalMediaResult>> FetchItunesTrendingMoviesLiveAsync(int limit)
    {
        var list = new List<ExternalMediaResult>();
        try
        {
            var response = await _httpClient.GetAsync($"https://itunes.apple.com/mx/rss/topmovies/limit={limit}/json");
            if (!response.IsSuccessStatusCode)
                return list;

            var jsonString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonString);
            if (!doc.RootElement.TryGetProperty("feed", out var feedElem) ||
                !feedElem.TryGetProperty("entry", out var entryArr) ||
                entryArr.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            int rank = 0;
            foreach (var entry in entryArr.EnumerateArray())
            {
                var parsed = ParseItunesRssMovieEntry(entry, rank);
                if (parsed != null)
                {
                    list.Add(parsed);
                    rank++;
                }
            }
        }
        catch
        {
            // Ignorar error de red
        }
        return list;
    }

    private ExternalMediaResult? ParseItunesRssMovieEntry(JsonElement entry, int rank)
    {
        try
        {
            string id = "";
            if (entry.TryGetProperty("id", out var idObj) &&
                idObj.TryGetProperty("attributes", out var idAttr) &&
                idAttr.TryGetProperty("im:id", out var imIdProp))
            {
                id = imIdProp.GetString() ?? "";
            }
            if (string.IsNullOrWhiteSpace(id))
                return null;

            string title = "";
            if (entry.TryGetProperty("im:name", out var nameObj) && nameObj.TryGetProperty("label", out var nameLbl))
            {
                title = nameLbl.GetString() ?? "";
            }
            if (string.IsNullOrWhiteSpace(title))
                return null;

            string synopsis = "";
            if (entry.TryGetProperty("summary", out var sumObj) && sumObj.TryGetProperty("label", out var sumLbl))
            {
                synopsis = sumLbl.GetString() ?? "";
            }

            string coverUrl = "";
            if (entry.TryGetProperty("im:image", out var imgArr) && imgArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var img in imgArr.EnumerateArray())
                {
                    if (img.TryGetProperty("label", out var lbl))
                        coverUrl = lbl.GetString() ?? "";
                }
            }
            if (string.IsNullOrWhiteSpace(coverUrl))
                return null;

            // Escalar póster oficial de Apple TV/iTunes a alta resolución (600x900)
            coverUrl = Regex.Replace(coverUrl, "/\\d+x\\d+bb\\.", "/600x900bb.");

            DateTime? releaseDate = null;
            if (entry.TryGetProperty("im:releaseDate", out var relObj) &&
                relObj.TryGetProperty("label", out var relLbl) &&
                DateTime.TryParse(relLbl.GetString(), out var dt))
            {
                releaseDate = dt;
            }

            var genres = new List<string>();
            if (entry.TryGetProperty("category", out var catObj) &&
                catObj.TryGetProperty("attributes", out var catAttr) &&
                catAttr.TryGetProperty("label", out var catLbl))
            {
                var g = catLbl.GetString();
                if (!string.IsNullOrWhiteSpace(g))
                    genres.Add(g);
            }
            if (!genres.Any())
                genres.Add("Película");

            double popularity = Math.Max(450.0, 980.0 - (rank * 35.0));
            double rating = Math.Round(Math.Max(7.6, 8.9 - (rank * 0.08)), 1);

            return new ExternalMediaResult
            {
                ExternalId = $"itunes_movie_{id}",
                Title = title,
                EnglishTitle = title,
                OriginalTitle = title,
                AlternativeTitles = new List<string>(),
                NeedTranslation = false,
                Synopsis = synopsis,
                CoverImageUrl = coverUrl,
                Type = MediaType.Pelicula,
                Rating = rating,
                Status = "En cartelera / Streaming",
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

    private async Task<IEnumerable<ExternalMediaResult>> SearchItunesMoviesLiveAsync(string query, int limit)
    {
        var list = new List<ExternalMediaResult>();
        try
        {
            var url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(query)}&media=movie&entity=movie&country=MX&lang=es_mx&limit={limit}";
            var response = await _httpClient.GetAsync(url);
            if (!response.IsSuccessStatusCode)
                return list;

            var jsonString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonString);
            if (!doc.RootElement.TryGetProperty("results", out var resultsArr) || resultsArr.ValueKind != JsonValueKind.Array)
                return list;

            int rank = 0;
            foreach (var item in resultsArr.EnumerateArray())
            {
                var parsed = ParseItunesSearchMovieItem(item, rank);
                if (parsed != null)
                {
                    list.Add(parsed);
                    rank++;
                }
            }
        }
        catch
        {
            // Ignorar fallo de red
        }
        return list;
    }

    private ExternalMediaResult? ParseItunesSearchMovieItem(JsonElement item, int rank)
    {
        try
        {
            if (!item.TryGetProperty("trackId", out var idProp))
                return null;

            var id = idProp.GetInt64().ToString();
            var title = item.TryGetProperty("trackName", out var nameProp) ? nameProp.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(title))
                return null;

            var synopsis = item.TryGetProperty("longDescription", out var longDesc) ? longDesc.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(synopsis) && item.TryGetProperty("shortDescription", out var shortDesc))
            {
                synopsis = shortDesc.GetString() ?? "";
            }

            var coverUrl = item.TryGetProperty("artworkUrl100", out var artProp) ? artProp.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(coverUrl))
                return null;

            coverUrl = Regex.Replace(coverUrl, "/\\d+x\\d+bb\\.", "/600x900bb.");

            DateTime? releaseDate = null;
            if (item.TryGetProperty("releaseDate", out var relProp) && DateTime.TryParse(relProp.GetString(), out var dt))
            {
                releaseDate = dt;
            }

            var genres = new List<string>();
            if (item.TryGetProperty("primaryGenreName", out var gProp))
            {
                var g = gProp.GetString();
                if (!string.IsNullOrWhiteSpace(g))
                    genres.Add(g);
            }
            if (!genres.Any())
                genres.Add("Película");

            return new ExternalMediaResult
            {
                ExternalId = $"itunes_movie_{id}",
                Title = title,
                EnglishTitle = title,
                OriginalTitle = title,
                AlternativeTitles = new List<string>(),
                NeedTranslation = false,
                Synopsis = synopsis,
                CoverImageUrl = coverUrl,
                Type = MediaType.Pelicula,
                Rating = Math.Round(Math.Max(7.5, 8.7 - (rank * 0.1)), 1),
                Status = "Estrenada",
                ReleaseDate = releaseDate,
                PopularityScore = Math.Max(400.0, 880.0 - (rank * 40.0)),
                Genres = genres
            };
        }
        catch
        {
            return null;
        }
    }

    public async Task<IEnumerable<ExternalMediaResult>> SearchSeriesAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Enumerable.Empty<ExternalMediaResult>();

        if (HasTmdbKey())
        {
            try
            {
                var response = await _httpClient.GetAsync($"https://api.themoviedb.org/3/search/tv?api_key={_apiKey}&query={Uri.EscapeDataString(query)}&language=es-ES");
                if (response.IsSuccessStatusCode)
                {
                    var jsonString = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(jsonString);
                    if (doc.RootElement.TryGetProperty("results", out var resultsElement) && resultsElement.ValueKind == JsonValueKind.Array)
                    {
                        var tmdbSeries = ParseTmdbTvArray(resultsElement).ToList();
                        if (tmdbSeries.Any())
                            return tmdbSeries;
                    }
                }
            }
            catch
            {
                // Respaldo automático en vivo con TVMaze
            }
        }

        return await SearchTvMazeSeriesLiveAsync(query, 12);
    }

    public async Task<IEnumerable<ExternalMediaResult>> GetTrendingSeriesAsync()
    {
        if (HasTmdbKey())
        {
            try
            {
                var response = await _httpClient.GetAsync($"https://api.themoviedb.org/3/trending/tv/day?api_key={_apiKey}&language=es-ES");
                if (response.IsSuccessStatusCode)
                {
                    var jsonString = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(jsonString);
                    if (doc.RootElement.TryGetProperty("results", out var resultsElement) && resultsElement.ValueKind == JsonValueKind.Array)
                    {
                        var tmdbSeries = ParseTmdbTvArray(resultsElement).ToList();
                        if (tmdbSeries.Any())
                            return tmdbSeries;
                    }
                }
            }
            catch
            {
                // Respaldo automático en vivo con TVMaze
            }
        }

        // Obtener automáticamente las series en tendencia y emisión actual desde TVMaze en tiempo real
        return await FetchTvMazeTrendingSeriesLiveAsync(15);
    }

    public async Task<ExternalMediaResult?> GetSeriesByIdAsync(string externalId)
    {
        if (HasTmdbKey() && externalId.StartsWith("tmdb_tv_"))
        {
            var cleanId = externalId.Replace("tmdb_tv_", "");
            try
            {
                var response = await _httpClient.GetAsync($"https://api.themoviedb.org/3/tv/{cleanId}?api_key={_apiKey}&language=es-ES");
                if (response.IsSuccessStatusCode)
                {
                    var jsonString = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(jsonString);
                    return ParseTmdbTvItem(doc.RootElement);
                }
            }
            catch
            {
                return null;
            }
        }

        if (externalId.StartsWith("tvmaze_"))
        {
            var showId = externalId.Replace("tvmaze_", "");
            try
            {
                var response = await _httpClient.GetAsync($"https://api.tvmaze.com/shows/{showId}");
                if (response.IsSuccessStatusCode)
                {
                    var jsonString = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(jsonString);
                    return ParseTvMazeShowItem(doc.RootElement);
                }
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private async Task<IEnumerable<ExternalMediaResult>> FetchTvMazeTrendingSeriesLiveAsync(int limit)
    {
        var results = new List<ExternalMediaResult>();
        try
        {
            var webTask = _httpClient.GetAsync("https://api.tvmaze.com/schedule/web");
            var tvTask = _httpClient.GetAsync("https://api.tvmaze.com/schedule");
            await Task.WhenAll(webTask, tvTask);

            var candidates = new List<ExternalMediaResult>();

            if (webTask.Result.IsSuccessStatusCode)
            {
                var json = await webTask.Result.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ep in doc.RootElement.EnumerateArray())
                    {
                        if (ep.TryGetProperty("_embedded", out var emb) && emb.TryGetProperty("show", out var showElem))
                        {
                            var parsed = ParseTvMazeShowItem(showElem);
                            if (parsed != null)
                                candidates.Add(parsed);
                        }
                    }
                }
            }

            if (tvTask.Result.IsSuccessStatusCode)
            {
                var json = await tvTask.Result.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ep in doc.RootElement.EnumerateArray())
                    {
                        if (ep.TryGetProperty("show", out var showElem))
                        {
                            var parsed = ParseTvMazeShowItem(showElem);
                            if (parsed != null)
                                candidates.Add(parsed);
                        }
                    }
                }
            }

            results = candidates
                .GroupBy(s => s.ExternalId)
                .Select(g => g.First())
                .OrderByDescending(s => s.PopularityScore)
                .ThenByDescending(s => s.Rating)
                .Take(limit)
                .ToList();
        }
        catch
        {
            // Ignorar fallo de red
        }
        return results;
    }

    private async Task<IEnumerable<ExternalMediaResult>> SearchTvMazeSeriesLiveAsync(string query, int limit)
    {
        var list = new List<ExternalMediaResult>();
        try
        {
            var response = await _httpClient.GetAsync($"https://api.tvmaze.com/search/shows?q={Uri.EscapeDataString(query)}");
            if (!response.IsSuccessStatusCode)
                return list;

            var jsonString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonString);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return list;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.TryGetProperty("show", out var showElem))
                {
                    var parsed = ParseTvMazeShowItem(showElem);
                    if (parsed != null)
                    {
                        list.Add(parsed);
                        if (list.Count >= limit)
                            break;
                    }
                }
            }
        }
        catch
        {
            // Ignorar fallo de red
        }
        return list;
    }

    private ExternalMediaResult? ParseTvMazeShowItem(JsonElement show)
    {
        try
        {
            if (!show.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.Number)
                return null;

            var id = idProp.GetInt32().ToString();
            var name = show.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String ? nameProp.GetString() ?? "" : "";
            if (string.IsNullOrWhiteSpace(name))
                return null;

            string coverUrl = "";
            if (show.TryGetProperty("image", out var imgObj) && imgObj.ValueKind == JsonValueKind.Object)
            {
                coverUrl = imgObj.TryGetProperty("original", out var origP) && origP.ValueKind == JsonValueKind.String ? origP.GetString() ?? "" : "";
                if (string.IsNullOrWhiteSpace(coverUrl))
                {
                    coverUrl = imgObj.TryGetProperty("medium", out var medP) && medP.ValueKind == JsonValueKind.String ? medP.GetString() ?? "" : "";
                }
            }
            if (string.IsNullOrWhiteSpace(coverUrl))
                return null;

            string rawSummary = show.TryGetProperty("summary", out var sumP) && sumP.ValueKind == JsonValueKind.String ? sumP.GetString() ?? "" : "";
            var synopsis = Regex.Replace(rawSummary, "<.*?>", string.Empty).Trim();
            synopsis = System.Net.WebUtility.HtmlDecode(synopsis);

            double weight = show.TryGetProperty("weight", out var wP) && wP.ValueKind == JsonValueKind.Number ? wP.GetDouble() : 75.0;
            double popularity = Math.Max(200.0, weight * 9.8);

            double rating = 7.8;
            if (show.TryGetProperty("rating", out var ratObj) &&
                ratObj.ValueKind == JsonValueKind.Object &&
                ratObj.TryGetProperty("average", out var avgP) &&
                avgP.ValueKind == JsonValueKind.Number)
            {
                rating = Math.Round(avgP.GetDouble(), 1);
            }

            string rawStatus = show.TryGetProperty("status", out var stP) && stP.ValueKind == JsonValueKind.String ? stP.GetString() ?? "" : "";
            string status = rawStatus switch
            {
                "Running" => "En emisión",
                "Ended" => "Finalizada",
                _ => "En emisión"
            };

            DateTime? releaseDate = null;
            if (show.TryGetProperty("premiered", out var premP) && premP.ValueKind == JsonValueKind.String && DateTime.TryParse(premP.GetString(), out var dt))
            {
                releaseDate = dt;
            }

            var genres = new List<string>();
            if (show.TryGetProperty("genres", out var genArr) && genArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var g in genArr.EnumerateArray())
                {
                    var rawG = g.GetString();
                    if (!string.IsNullOrWhiteSpace(rawG))
                    {
                        var translated = EnglishGenreTranslations.TryGetValue(rawG, out var esG) ? esG : rawG;
                        if (!genres.Contains(translated))
                            genres.Add(translated);
                    }
                }
            }
            if (!genres.Any())
                genres.Add("Serie");

            return new ExternalMediaResult
            {
                ExternalId = $"tvmaze_{id}",
                Title = name,
                EnglishTitle = name,
                OriginalTitle = name,
                AlternativeTitles = new List<string>(),
                NeedTranslation = true,
                Synopsis = synopsis,
                CoverImageUrl = coverUrl,
                Type = MediaType.Serie,
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

    private IEnumerable<ExternalMediaResult> ParseTmdbArray(JsonElement arrayElement)
    {
        var list = new List<ExternalMediaResult>();
        foreach (var item in arrayElement.EnumerateArray())
        {
            var result = ParseTmdbItem(item);
            if (result != null)
                list.Add(result);
        }
        return list;
    }

    private ExternalMediaResult? ParseTmdbItem(JsonElement item)
    {
        try
        {
            if (!item.TryGetProperty("id", out var idProp))
                return null;

            var tmdbId = idProp.GetInt32().ToString();
            var title = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() ?? "" : "";
            var originalTitle = item.TryGetProperty("original_title", out var origProp) ? origProp.GetString() ?? "" : "";
            var synopsis = item.TryGetProperty("overview", out var overProp) ? overProp.GetString() ?? "" : "";
            
            var posterPath = item.TryGetProperty("poster_path", out var posterProp) ? posterProp.GetString() : null;
            if (string.IsNullOrEmpty(posterPath))
                return null;

            var coverUrl = $"https://image.tmdb.org/t/p/w500{posterPath}";

            var rating = item.TryGetProperty("vote_average", out var voteProp) && voteProp.ValueKind == JsonValueKind.Number 
                ? voteProp.GetDouble() 
                : 0.0;

            var popularity = item.TryGetProperty("popularity", out var popProp) && popProp.ValueKind == JsonValueKind.Number
                ? popProp.GetDouble()
                : 0.0;

            DateTime? releaseDate = null;
            if (item.TryGetProperty("release_date", out var dateProp) && dateProp.ValueKind == JsonValueKind.String)
            {
                if (DateTime.TryParse(dateProp.GetString(), out var date))
                    releaseDate = date;
            }

            var alternativeTitles = new List<string>();
            if (!string.IsNullOrWhiteSpace(originalTitle) && originalTitle != title)
                alternativeTitles.Add(originalTitle);

            var genres = ExtractGenres(item, MovieGenres, "Película");

            return new ExternalMediaResult
            {
                ExternalId = $"tmdb_{tmdbId}",
                Title = title,
                EnglishTitle = title,
                OriginalTitle = originalTitle,
                AlternativeTitles = alternativeTitles,
                Synopsis = synopsis,
                CoverImageUrl = coverUrl,
                Type = MediaType.Pelicula,
                Rating = Math.Round(rating, 1),
                Status = "Estrenada",
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

    private IEnumerable<ExternalMediaResult> ParseTmdbTvArray(JsonElement arrayElement)
    {
        var list = new List<ExternalMediaResult>();
        foreach (var item in arrayElement.EnumerateArray())
        {
            var result = ParseTmdbTvItem(item);
            if (result != null)
                list.Add(result);
        }
        return list;
    }

    private ExternalMediaResult? ParseTmdbTvItem(JsonElement item)
    {
        try
        {
            if (!item.TryGetProperty("id", out var idProp))
                return null;

            var tmdbId = idProp.GetInt32().ToString();
            var title = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
            var originalTitle = item.TryGetProperty("original_name", out var origProp) ? origProp.GetString() ?? "" : "";
            var synopsis = item.TryGetProperty("overview", out var overProp) ? overProp.GetString() ?? "" : "";
            
            var posterPath = item.TryGetProperty("poster_path", out var posterProp) ? posterProp.GetString() : null;
            if (string.IsNullOrEmpty(posterPath))
                return null;

            var coverUrl = $"https://image.tmdb.org/t/p/w500{posterPath}";

            var rating = item.TryGetProperty("vote_average", out var voteProp) && voteProp.ValueKind == JsonValueKind.Number 
                ? voteProp.GetDouble() 
                : 0.0;

            var popularity = item.TryGetProperty("popularity", out var popProp) && popProp.ValueKind == JsonValueKind.Number
                ? popProp.GetDouble()
                : 0.0;

            DateTime? releaseDate = null;
            if (item.TryGetProperty("first_air_date", out var dateProp) && dateProp.ValueKind == JsonValueKind.String)
            {
                if (DateTime.TryParse(dateProp.GetString(), out var date))
                    releaseDate = date;
            }

            var alternativeTitles = new List<string>();
            if (!string.IsNullOrWhiteSpace(originalTitle) && originalTitle != title)
                alternativeTitles.Add(originalTitle);

            var genres = ExtractGenres(item, TvGenres, "Serie");

            return new ExternalMediaResult
            {
                ExternalId = $"tmdb_tv_{tmdbId}",
                Title = title,
                EnglishTitle = title,
                OriginalTitle = originalTitle,
                AlternativeTitles = alternativeTitles,
                Synopsis = synopsis,
                CoverImageUrl = coverUrl,
                Type = MediaType.Serie,
                Rating = Math.Round(rating, 1),
                Status = "En emisión",
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

    private static List<string> ExtractGenres(JsonElement item, Dictionary<int, string> genreMap, string fallbackGenre)
    {
        var genres = new List<string>();
        if (item.TryGetProperty("genre_ids", out var genreIdsProp) && genreIdsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var gIdElem in genreIdsProp.EnumerateArray())
            {
                if (gIdElem.ValueKind == JsonValueKind.Number && genreMap.TryGetValue(gIdElem.GetInt32(), out var gName))
                {
                    if (!genres.Contains(gName))
                        genres.Add(gName);
                }
            }
        }
        else if (item.TryGetProperty("genres", out var genresObjProp) && genresObjProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var gObj in genresObjProp.EnumerateArray())
            {
                if (gObj.TryGetProperty("id", out var idElem) && idElem.ValueKind == JsonValueKind.Number && genreMap.TryGetValue(idElem.GetInt32(), out var gName))
                {
                    if (!genres.Contains(gName))
                        genres.Add(gName);
                }
                else if (gObj.TryGetProperty("name", out var nameElem))
                {
                    var name = nameElem.GetString();
                    if (!string.IsNullOrWhiteSpace(name) && !genres.Contains(name))
                        genres.Add(name);
                }
            }
        }

        if (!genres.Any())
        {
            genres.Add(fallbackGenre);
        }
        return genres;
    }
}
