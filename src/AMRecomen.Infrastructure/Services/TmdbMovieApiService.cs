using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
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

    public TmdbMovieApiService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri("https://api.themoviedb.org/3/");
        _apiKey = configuration["TMDb:ApiKey"] ?? string.Empty;
    }

    public async Task<IEnumerable<ExternalMediaResult>> SearchMoviesAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            // Retornar coincidencia mockeada si no hay API Key
            return GetMockMovies()
                .Where(m => m.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || 
                            m.EnglishTitle.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        try
        {
            var response = await _httpClient.GetAsync($"search/movie?api_key={_apiKey}&query={Uri.EscapeDataString(query)}&language=es-ES");
            if (!response.IsSuccessStatusCode)
                return Enumerable.Empty<ExternalMediaResult>();

            var jsonString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonString);
            if (!doc.RootElement.TryGetProperty("results", out var resultsElement) || resultsElement.ValueKind != JsonValueKind.Array)
                return Enumerable.Empty<ExternalMediaResult>();

            return ParseTmdbArray(resultsElement);
        }
        catch
        {
            return Enumerable.Empty<ExternalMediaResult>();
        }
    }

    public async Task<IEnumerable<ExternalMediaResult>> GetTrendingMoviesAsync()
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return GetMockMovies();
        }

        try
        {
            var response = await _httpClient.GetAsync($"trending/movie/day?api_key={_apiKey}&language=es-ES");
            if (!response.IsSuccessStatusCode)
                return Enumerable.Empty<ExternalMediaResult>();

            var jsonString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonString);
            if (!doc.RootElement.TryGetProperty("results", out var resultsElement) || resultsElement.ValueKind != JsonValueKind.Array)
                return Enumerable.Empty<ExternalMediaResult>();

            return ParseTmdbArray(resultsElement);
        }
        catch
        {
            return Enumerable.Empty<ExternalMediaResult>();
        }
    }

    public async Task<ExternalMediaResult?> GetMovieByIdAsync(string externalId)
    {
        var cleanId = externalId.Replace("tmdb_", "");
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return GetMockMovies().FirstOrDefault(m => m.ExternalId == externalId);
        }

        try
        {
            var response = await _httpClient.GetAsync($"movie/{cleanId}?api_key={_apiKey}&language=es-ES");
            if (!response.IsSuccessStatusCode)
                return null;

            var jsonString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonString);
            
            return ParseTmdbItem(doc.RootElement);
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
            var coverUrl = string.IsNullOrEmpty(posterPath) 
                ? "https://images.unsplash.com/photo-1536440136628-849c177e76a1?w=500" // Fallback
                : $"https://image.tmdb.org/t/p/w500{posterPath}";

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
                Rating = rating,
                Status = "Released",
                ReleaseDate = releaseDate,
                PopularityScore = popularity,
                Genres = new List<string> { "Película" } // La API básica de trending no da nombres de género directamente sin otra llamada, agregamos uno genérico
            };
        }
        catch
        {
            return null;
        }
    }

    private IEnumerable<ExternalMediaResult> GetMockMovies()
    {
        return new List<ExternalMediaResult>
        {
            new()
            {
                ExternalId = "tmdb_mock1",
                Title = "Dune: Parte Dos",
                EnglishTitle = "Dune: Part Two",
                OriginalTitle = "Dune: Part Two",
                AlternativeTitles = new List<string> { "Dune 2", "Duna: Parte 2" },
                Synopsis = "Paul Atreides se une a Chani y a los Fremen mientras busca venganza contra los conspiradores que destruyeron a su familia. Enfrentando una elección entre el amor de su vida y el destino del universo, se esfuerza por evitar un futuro terrible que solo él puede prever.",
                CoverImageUrl = "https://images.unsplash.com/photo-1534447677768-be436bb09401?w=500",
                Type = MediaType.Pelicula,
                Rating = 8.4,
                Status = "Released",
                ReleaseDate = new DateTime(2024, 2, 27),
                PopularityScore = 950.0,
                Genres = new List<string> { "Ciencia Ficción", "Aventura", "Acción" }
            },
            new()
            {
                ExternalId = "tmdb_mock2",
                Title = "Spider-Man: A Través del Spider-Verso",
                EnglishTitle = "Spider-Man: Across the Spider-Verse",
                OriginalTitle = "Spider-Man: Across the Spider-Verse",
                AlternativeTitles = new List<string> { "Across the Spider-Verse", "Spider-Man: Un nuevo universo 2", "Spiderverse 2" },
                Synopsis = "Tras reencontrarse con Gwen Stacy, el amigable vecindario de Spider-Man es catapultado a través del Multiverso, donde se encuentra con un equipo de Spider-People encargados de proteger su existencia.",
                CoverImageUrl = "https://images.unsplash.com/photo-1635805737707-575885ab0820?w=500",
                Type = MediaType.Pelicula,
                Rating = 8.5,
                Status = "Released",
                ReleaseDate = new DateTime(2023, 5, 31),
                PopularityScore = 800.0,
                Genres = new List<string> { "Animación", "Acción", "Aventura" }
            },
            new()
            {
                ExternalId = "tmdb_mock3",
                Title = "Interestelar",
                EnglishTitle = "Interstellar",
                OriginalTitle = "Interstellar",
                AlternativeTitles = new List<string> { "Interstellar" },
                Synopsis = "Un grupo de científicos y exploradores viaja a través de un agujero de gusano en el espacio para encontrar un nuevo hogar para la humanidad ante el inminente colapso de la Tierra.",
                CoverImageUrl = "https://images.unsplash.com/photo-1451187580459-43490279c0fa?w=500",
                Type = MediaType.Pelicula,
                Rating = 8.6,
                Status = "Released",
                ReleaseDate = new DateTime(2014, 11, 5),
                PopularityScore = 720.0,
                Genres = new List<string> { "Ciencia Ficción", "Drama", "Aventura" }
            },
            new()
            {
                ExternalId = "tmdb_mock4",
                Title = "Intensa-Mente 2",
                EnglishTitle = "Inside Out 2",
                OriginalTitle = "Inside Out 2",
                AlternativeTitles = new List<string> { "Del revés 2", "Inside Out 2" },
                Synopsis = "De regreso a la mente de una Riley recién entrada en la adolescencia, justo en el momento en que el Cuartel General está sufriendo una repentina reforma para hacer espacio a algo totalmente inesperado: ¡nuevas emociones! Ansiedad, Envidia, Aburrimiento y Vergüenza se unen al equipo.",
                CoverImageUrl = "https://images.unsplash.com/photo-1608889175123-8ec330b86f84?w=500",
                Type = MediaType.Pelicula,
                Rating = 7.9,
                Status = "Released",
                ReleaseDate = new DateTime(2024, 6, 12),
                PopularityScore = 1200.0,
                Genres = new List<string> { "Animación", "Familia", "Comedia", "Fantasía" }
            },
            new()
            {
                ExternalId = "tmdb_mock5",
                Title = "El Origen",
                EnglishTitle = "Inception",
                OriginalTitle = "Inception",
                AlternativeTitles = new List<string> { "Inception" },
                Synopsis = "Dom Cobb es un hábil ladrón, el mejor en el peligroso arte de la extracción: robar secretos valiosos desde lo profundo del subconsciente durante el estado de sueño, cuando la mente es más vulnerable.",
                CoverImageUrl = "https://images.unsplash.com/photo-1509198397868-475647b2a1e5?w=500",
                Type = MediaType.Pelicula,
                Rating = 8.3,
                Status = "Released",
                ReleaseDate = new DateTime(2010, 7, 15),
                PopularityScore = 500.0,
                Genres = new List<string> { "Acción", "Ciencia Ficción", "Suspenso" }
            }
        };
    }

    public async Task<IEnumerable<ExternalMediaResult>> SearchSeriesAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return GetMockSeries()
                .Where(m => m.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || 
                            m.EnglishTitle.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        try
        {
            var response = await _httpClient.GetAsync($"search/tv?api_key={_apiKey}&query={Uri.EscapeDataString(query)}&language=es-ES");
            if (!response.IsSuccessStatusCode)
                return Enumerable.Empty<ExternalMediaResult>();

            var jsonString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonString);
            if (!doc.RootElement.TryGetProperty("results", out var resultsElement) || resultsElement.ValueKind != JsonValueKind.Array)
                return Enumerable.Empty<ExternalMediaResult>();

            return ParseTmdbTvArray(resultsElement);
        }
        catch
        {
            return Enumerable.Empty<ExternalMediaResult>();
        }
    }

    public async Task<IEnumerable<ExternalMediaResult>> GetTrendingSeriesAsync()
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return GetMockSeries();
        }

        try
        {
            var response = await _httpClient.GetAsync($"trending/tv/day?api_key={_apiKey}&language=es-ES");
            if (!response.IsSuccessStatusCode)
                return Enumerable.Empty<ExternalMediaResult>();

            var jsonString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonString);
            if (!doc.RootElement.TryGetProperty("results", out var resultsElement) || resultsElement.ValueKind != JsonValueKind.Array)
                return Enumerable.Empty<ExternalMediaResult>();

            return ParseTmdbTvArray(resultsElement);
        }
        catch
        {
            return Enumerable.Empty<ExternalMediaResult>();
        }
    }

    public async Task<ExternalMediaResult?> GetSeriesByIdAsync(string externalId)
    {
        var cleanId = externalId.Replace("tmdb_tv_", "");
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            return GetMockSeries().FirstOrDefault(m => m.ExternalId == externalId);
        }

        try
        {
            var response = await _httpClient.GetAsync($"tv/{cleanId}?api_key={_apiKey}&language=es-ES");
            if (!response.IsSuccessStatusCode)
                return null;

            var jsonString = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(jsonString);
            
            return ParseTmdbTvItem(doc.RootElement);
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
            var coverUrl = string.IsNullOrEmpty(posterPath) 
                ? "https://images.unsplash.com/photo-1536440136628-849c177e76a1?w=500"
                : $"https://image.tmdb.org/t/p/w500{posterPath}";

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
                Rating = rating,
                Status = "Running",
                ReleaseDate = releaseDate,
                PopularityScore = popularity,
                Genres = new List<string> { "Serie" }
            };
        }
        catch
        {
            return null;
        }
    }

    private IEnumerable<ExternalMediaResult> GetMockSeries()
    {
        return new List<ExternalMediaResult>
        {
            new()
            {
                ExternalId = "tmdb_tv_mock1",
                Title = "Stranger Things",
                EnglishTitle = "Stranger Things",
                OriginalTitle = "Stranger Things",
                AlternativeTitles = new List<string> { "Cosas Extrañas", "Montauk" },
                Synopsis = "A raíz de la misteriosa desaparición de un niño, un pequeño pueblo se encuentra ante un misterio que revela experimentos secretos, fuerzas sobrenaturales terroríficas y a una extraña niña llamada Once (Eleven) con poderes telequinéticos.",
                CoverImageUrl = "https://images.unsplash.com/photo-1626814026160-2237a95fc5a0?w=500",
                Type = MediaType.Serie,
                Rating = 8.6,
                Status = "Running",
                ReleaseDate = new DateTime(2016, 7, 15),
                PopularityScore = 850.0,
                Genres = new List<string> { "Ciencia Ficción", "Misterio", "Drama" }
            },
            new()
            {
                ExternalId = "tmdb_tv_mock2",
                Title = "La Casa de Papel",
                EnglishTitle = "Money Heist",
                OriginalTitle = "La Casa de Papel",
                AlternativeTitles = new List<string> { "Money Heist", "LCDP" },
                Synopsis = "Un misterioso hombre, conocido como 'El Profesor', recluta a un grupo de ocho ladrones con habilidades únicas para llevar a cabo el atraco más grande de la historia: entrar en la Fábrica Nacional de Moneda y Timbre de España e imprimir miles de millones de euros.",
                CoverImageUrl = "https://images.unsplash.com/photo-1509281373149-e957c6296406?w=500",
                Type = MediaType.Serie,
                Rating = 8.3,
                Status = "Completed",
                ReleaseDate = new DateTime(2017, 5, 2),
                PopularityScore = 600.0,
                Genres = new List<string> { "Crimen", "Drama", "Suspenso" }
            },
            new()
            {
                ExternalId = "tmdb_tv_mock3",
                Title = "Breaking Bad",
                EnglishTitle = "Breaking Bad",
                OriginalTitle = "Breaking Bad",
                AlternativeTitles = new List<string> { "Breaking Bad: Química del Mal" },
                Synopsis = "Walter White, un frustrado profesor de química de secundaria al que le diagnostican cáncer de pulmón terminal, decide asociarse con su antiguo estudiante Jesse Pinkman para cocinar y vender metanfetamina para asegurar el futuro financiero de su familia.",
                CoverImageUrl = "https://images.unsplash.com/photo-1594909122845-11baa439b7bf?w=500",
                Type = MediaType.Serie,
                Rating = 9.5,
                Status = "Completed",
                ReleaseDate = new DateTime(2008, 1, 20),
                PopularityScore = 950.0,
                Genres = new List<string> { "Drama", "Crimen" }
            },
            new()
            {
                ExternalId = "tmdb_tv_mock4",
                Title = "Merlina",
                EnglishTitle = "Wednesday",
                OriginalTitle = "Wednesday",
                AlternativeTitles = new List<string> { "Wednesday", "Los Locos Addams: Merlina" },
                Synopsis = "Inteligente, sarcástica y un poco muerta por dentro, Merlina Addams investiga una ola de asesinatos mientras hace nuevos amigos (y enemigos) en la Academia Nunca Más (Nevermore).",
                CoverImageUrl = "https://images.unsplash.com/photo-1509248961158-e54f6934749c?w=500",
                Type = MediaType.Serie,
                Rating = 8.5,
                Status = "Running",
                ReleaseDate = new DateTime(2022, 11, 23),
                PopularityScore = 780.0,
                Genres = new List<string> { "Fantasía", "Misterio", "Comedia" }
            },
            new()
            {
                ExternalId = "tmdb_tv_mock5",
                Title = "El Juego del Calamar",
                EnglishTitle = "Squid Game",
                OriginalTitle = "Squid Game",
                AlternativeTitles = new List<string> { "Ojing-eo Geim", "Round Six" },
                Synopsis = "Cientos de jugadores con dificultades económicas aceptan una extraña invitación a competir en juegos infantiles tradicionales. Dentro les espera un premio tentador de 45.600 millones de wones, pero los desafíos conllevan riesgos mortales.",
                CoverImageUrl = "https://images.unsplash.com/photo-1542751371-adc38448a05e?w=500",
                Type = MediaType.Serie,
                Rating = 8.7,
                Status = "Running",
                ReleaseDate = new DateTime(2021, 9, 17),
                PopularityScore = 880.0,
                Genres = new List<string> { "Acción", "Suspenso", "Drama" }
            }
        };
    }
}
