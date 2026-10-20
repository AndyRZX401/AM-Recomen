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

    public TmdbMovieApiService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _httpClient.BaseAddress = new Uri("https://api.themoviedb.org/3/");
        _apiKey = configuration["TMDb:ApiKey"] 
                  ?? configuration["TMDB_API_KEY"] 
                  ?? configuration["NEXT_PUBLIC_TMDB_API_KEY"]
                  ?? Environment.GetEnvironmentVariable("TMDB_API_KEY") 
                  ?? Environment.GetEnvironmentVariable("TMDb__ApiKey")
                  ?? string.Empty;
    }

    private bool UseMock()
    {
        return string.IsNullOrWhiteSpace(_apiKey) || 
               _apiKey.Length < 15 || 
               _apiKey.Contains("clave", StringComparison.OrdinalIgnoreCase) ||
               _apiKey.Contains("placeholder", StringComparison.OrdinalIgnoreCase);
    }

    public async Task<IEnumerable<ExternalMediaResult>> SearchMoviesAsync(string query)
    {
        if (UseMock())
        {
            // Retornar coincidencia curada si no hay API Key
            return GetMockMovies()
                .Where(m => m.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || 
                            m.EnglishTitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            m.OriginalTitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            m.AlternativeTitles.Any(a => a.Contains(query, StringComparison.OrdinalIgnoreCase)))
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
        if (UseMock())
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
        if (UseMock())
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
                AlternativeTitles = new List<string> { "Dune 2", "Duna: Parte 2", "Paul Atreides" },
                Synopsis = "Paul Atreides se une a Chani y a los Fremen mientras busca venganza contra los conspiradores que destruyeron a su familia. Enfrentando una elección entre el amor de su vida y el destino del universo, se esfuerza por evitar un futuro terrible que solo él puede prever.",
                CoverImageUrl = "https://image.tmdb.org/t/p/w500/1pdfLvkbY9ohJlCjQH2CZjjYVvJ.jpg",
                Type = MediaType.Pelicula,
                Rating = 8.8,
                Status = "Estrenada",
                ReleaseDate = new DateTime(2024, 2, 27),
                PopularityScore = 980.0,
                Genres = new List<string> { "Ciencia Ficción", "Aventura", "Acción" }
            },
            new()
            {
                ExternalId = "tmdb_mock3",
                Title = "Interestelar (Interstellar)",
                EnglishTitle = "Interstellar",
                OriginalTitle = "Interstellar",
                AlternativeTitles = new List<string> { "Interstellar", "Christopher Nolan", "Cooper" },
                Synopsis = "Un grupo de científicos y exploradores liderados por el piloto Cooper viaja a través de un agujero de gusano en el espacio para encontrar un nuevo planeta habitable para la humanidad ante el inminente colapso ecológico de la Tierra.",
                CoverImageUrl = "https://image.tmdb.org/t/p/w500/gEU2QniE6E77NI6lCU6MxlNBvIx.jpg",
                Type = MediaType.Pelicula,
                Rating = 9.0,
                Status = "Estrenada",
                ReleaseDate = new DateTime(2014, 11, 5),
                PopularityScore = 960.0,
                Genres = new List<string> { "Ciencia Ficción", "Drama", "Aventura" }
            },
            new()
            {
                ExternalId = "tmdb_mock2",
                Title = "Spider-Man: A Través del Spider-Verso",
                EnglishTitle = "Spider-Man: Across the Spider-Verse",
                OriginalTitle = "Spider-Man: Across the Spider-Verse",
                AlternativeTitles = new List<string> { "Across the Spider-Verse", "Spider-Man: Un nuevo universo 2", "Spiderverse 2", "Miles Morales" },
                Synopsis = "Tras reencontrarse con Gwen Stacy, el amigable vecindario de Spider-Man de Brooklyn es catapultado a través del Multiverso, donde se encuentra con la Sociedad Arácnida encargada de proteger la existencia misma del canon.",
                CoverImageUrl = "https://image.tmdb.org/t/p/w500/8Vt6mWEReuy4Of61Lnj5Xj704m8.jpg",
                Type = MediaType.Pelicula,
                Rating = 8.7,
                Status = "Estrenada",
                ReleaseDate = new DateTime(2023, 5, 31),
                PopularityScore = 920.0,
                Genres = new List<string> { "Animación", "Acción", "Aventura", "Ciencia Ficción" }
            },
            new()
            {
                ExternalId = "tmdb_mock6",
                Title = "Batman: El Caballero de la Noche",
                EnglishTitle = "The Dark Knight",
                OriginalTitle = "The Dark Knight",
                AlternativeTitles = new List<string> { "El Caballero Oscuro", "Batman", "Joker", "The Dark Knight" },
                Synopsis = "Con la ayuda del teniente Jim Gordon y el fiscal de distrito Harvey Dent, Batman se propone desmantelar las organizaciones criminales que plagan Ciudad Gótica, pero pronto se ven acorralados por una mente maestra anárquica conocida como el Guasón (Joker).",
                CoverImageUrl = "https://image.tmdb.org/t/p/w500/qJ2tW6WMUDux911r6m7haRef0WH.jpg",
                Type = MediaType.Pelicula,
                Rating = 9.1,
                Status = "Estrenada",
                ReleaseDate = new DateTime(2008, 7, 16),
                PopularityScore = 950.0,
                Genres = new List<string> { "Acción", "Crimen", "Drama", "Suspenso" }
            },
            new()
            {
                ExternalId = "tmdb_mock5",
                Title = "El Origen (Inception)",
                EnglishTitle = "Inception",
                OriginalTitle = "Inception",
                AlternativeTitles = new List<string> { "Inception", "Origen", "Dom Cobb" },
                Synopsis = "Dom Cobb es un hábil ladrón, el mejor en el peligroso arte de la extracción: robar secretos valiosos desde lo profundo del subconsciente durante el estado de sueño. Ahora debe lograr lo imposible: la implantación de una idea.",
                CoverImageUrl = "https://image.tmdb.org/t/p/w500/oYuLEt3zVCKq57qu2F8dT7NIa6f.jpg",
                Type = MediaType.Pelicula,
                Rating = 8.8,
                Status = "Estrenada",
                ReleaseDate = new DateTime(2010, 7, 15),
                PopularityScore = 890.0,
                Genres = new List<string> { "Acción", "Ciencia Ficción", "Suspenso" }
            },
            new()
            {
                ExternalId = "tmdb_mock7",
                Title = "Oppenheimer",
                EnglishTitle = "Oppenheimer",
                OriginalTitle = "Oppenheimer",
                AlternativeTitles = new List<string> { "J. Robert Oppenheimer", "Proyecto Manhattan" },
                Synopsis = "La historia del físico estadounidense J. Robert Oppenheimer, su papel al frente del Laboratorio de Los Álamos durante la Segunda Guerra Mundial y el desarrollo de la bomba atómica en el Proyecto Manhattan que cambió el mundo para siempre.",
                CoverImageUrl = "https://image.tmdb.org/t/p/w500/8Gxv8gSFCU0XGDykEGv7zR1n2ua.jpg",
                Type = MediaType.Pelicula,
                Rating = 8.6,
                Status = "Estrenada",
                ReleaseDate = new DateTime(2023, 7, 19),
                PopularityScore = 880.0,
                Genres = new List<string> { "Drama", "Historia" }
            },
            new()
            {
                ExternalId = "tmdb_mock8",
                Title = "Avengers: Endgame",
                EnglishTitle = "Avengers: Endgame",
                OriginalTitle = "Avengers: Endgame",
                AlternativeTitles = new List<string> { "Vengadores: Endgame", "Marvel", "Iron Man", "Thanos" },
                Synopsis = "Después de los devastadores eventos de Infinity War, el universo está en ruinas. Con la ayuda de los aliados que quedaron, los Vengadores se reúnen una vez más para intentar deshacer las acciones de Thanos y restaurar el equilibrio del cosmos.",
                CoverImageUrl = "https://image.tmdb.org/t/p/w500/or06FN3Dka5tukK1e9sl16pB3iy.jpg",
                Type = MediaType.Pelicula,
                Rating = 8.5,
                Status = "Estrenada",
                ReleaseDate = new DateTime(2019, 4, 24),
                PopularityScore = 910.0,
                Genres = new List<string> { "Acción", "Ciencia Ficción", "Aventura" }
            },
            new()
            {
                ExternalId = "tmdb_mock9",
                Title = "El Señor de los Anillos: El Retorno del Rey",
                EnglishTitle = "The Lord of the Rings: The Return of the King",
                OriginalTitle = "The Lord of the Rings: The Return of the King",
                AlternativeTitles = new List<string> { "LOTR", "Frodo Bolsón", "Aragorn", "Tierra Media" },
                Synopsis = "Mientras los ejércitos de Sauron asedian Minas Tirith en la batalla decisiva por el destino de la Tierra Media, Frodo y Sam se adentran en el corazón de Mordor para destruir el Anillo Único en los fuegos del Monte del Destino.",
                CoverImageUrl = "https://image.tmdb.org/t/p/w500/rCzpDGLbOoPwLjy3OAm5NUPOTrC.jpg",
                Type = MediaType.Pelicula,
                Rating = 9.0,
                Status = "Estrenada",
                ReleaseDate = new DateTime(2003, 12, 17),
                PopularityScore = 900.0,
                Genres = new List<string> { "Aventura", "Fantasía", "Acción" }
            },
            new()
            {
                ExternalId = "tmdb_mock4",
                Title = "Intensa-Mente 2 (Inside Out 2)",
                EnglishTitle = "Inside Out 2",
                OriginalTitle = "Inside Out 2",
                AlternativeTitles = new List<string> { "Del revés 2", "Inside Out 2", "Intensamente 2" },
                Synopsis = "De regreso a la mente de una Riley recién entrada en la adolescencia, el Cuartel General sufre una repentina reforma para hacer espacio a nuevas emociones inesperadas: Ansiedad, Envidia, Aburrimiento y Vergüenza.",
                CoverImageUrl = "https://image.tmdb.org/t/p/w500/vpnVM9B6NMmQpWeZvzLvDESb2QY.jpg",
                Type = MediaType.Pelicula,
                Rating = 8.1,
                Status = "Estrenada",
                ReleaseDate = new DateTime(2024, 6, 12),
                PopularityScore = 870.0,
                Genres = new List<string> { "Animación", "Familia", "Comedia", "Fantasía" }
            },
            new()
            {
                ExternalId = "tmdb_mock10",
                Title = "Parásitos (Parasite)",
                EnglishTitle = "Parasite",
                OriginalTitle = "Gisaengchung",
                AlternativeTitles = new List<string> { "Parasite", "Bong Joon-ho", "Gisaengchung" },
                Synopsis = "Tanto Gi Taek como su familia están sin trabajo. Cuando su hijo mayor, Gi Woo, empieza a dar clases particulares en la adinerada casa de la familia Park, las dos familias comienzan una interrelación de resultados imprevisibles.",
                CoverImageUrl = "https://image.tmdb.org/t/p/w500/7IiTTgloJzvGI1TAYymCfbfl3vT.jpg",
                Type = MediaType.Pelicula,
                Rating = 8.6,
                Status = "Estrenada",
                ReleaseDate = new DateTime(2019, 5, 30),
                PopularityScore = 860.0,
                Genres = new List<string> { "Comedia", "Suspenso", "Drama" }
            }
        };
    }

    public async Task<IEnumerable<ExternalMediaResult>> SearchSeriesAsync(string query)
    {
        if (UseMock())
        {
            return GetMockSeries()
                .Where(m => m.Title.Contains(query, StringComparison.OrdinalIgnoreCase) || 
                            m.EnglishTitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            m.OriginalTitle.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                            m.AlternativeTitles.Any(a => a.Contains(query, StringComparison.OrdinalIgnoreCase)))
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
        if (UseMock())
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
        if (UseMock())
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

    private IEnumerable<ExternalMediaResult> GetMockSeries()
    {
        return new List<ExternalMediaResult>
        {
            new()
            {
                ExternalId = "tmdb_tv_mock3",
                Title = "Breaking Bad",
                EnglishTitle = "Breaking Bad",
                OriginalTitle = "Breaking Bad",
                AlternativeTitles = new List<string> { "Breaking Bad: Química del Mal", "Walter White", "Heisenberg", "Jesse Pinkman" },
                Synopsis = "Walter White, un profesor de química de secundaria al que le diagnostican cáncer de pulmón inoperable, decide asociarse con su antiguo alumno Jesse Pinkman para fabricar y vender la metanfetamina más pura del mercado con el fin de asegurar el futuro económico de su familia.",
                CoverImageUrl = "https://static.tvmaze.com/uploads/images/original_untouched/501/1253519.jpg",
                Type = MediaType.Serie,
                Rating = 9.5,
                Status = "Finalizada",
                ReleaseDate = new DateTime(2008, 1, 20),
                PopularityScore = 990.0,
                Genres = new List<string> { "Drama", "Crimen", "Suspenso" }
            },
            new()
            {
                ExternalId = "tmdb_tv_mock6",
                Title = "Arcane: League of Legends",
                EnglishTitle = "Arcane",
                OriginalTitle = "Arcane",
                AlternativeTitles = new List<string> { "Arcane", "Jinx", "Vi", "League of Legends" },
                Synopsis = "En medio del creciente conflicto entre la rica y utópica ciudad de Piltover y el oprimido mundo subterráneo de Zaun, las hermanas Vi y Powder se encuentran en bandos opuestos de una guerra desatada por tecnologías arcanas y convicciones enfrentadas.",
                CoverImageUrl = "https://static.tvmaze.com/uploads/images/original_untouched/536/1340287.jpg",
                Type = MediaType.Serie,
                Rating = 9.1,
                Status = "Finalizada",
                ReleaseDate = new DateTime(2021, 11, 6),
                PopularityScore = 960.0,
                Genres = new List<string> { "Animación", "Acción y Aventura", "Ciencia Ficción y Fantasía", "Drama" }
            },
            new()
            {
                ExternalId = "tmdb_tv_mock7",
                Title = "Juego de Tronos (Game of Thrones)",
                EnglishTitle = "Game of Thrones",
                OriginalTitle = "Game of Thrones",
                AlternativeTitles = new List<string> { "GOT", "Jon Snow", "Daenerys Targaryen", "Westeros" },
                Synopsis = "Nueve familias nobles luchan por el control de las míticas tierras de Poniente (Westeros) y el Trono de Hierro, mientras un antiguo y letal enemigo regresa tras permanecer dormido durante miles de años más allá del Muro.",
                CoverImageUrl = "https://static.tvmaze.com/uploads/images/original_untouched/498/1245274.jpg",
                Type = MediaType.Serie,
                Rating = 9.2,
                Status = "Finalizada",
                ReleaseDate = new DateTime(2011, 4, 17),
                PopularityScore = 970.0,
                Genres = new List<string> { "Ciencia Ficción y Fantasía", "Drama", "Acción y Aventura" }
            },
            new()
            {
                ExternalId = "tmdb_tv_mock1",
                Title = "Stranger Things",
                EnglishTitle = "Stranger Things",
                OriginalTitle = "Stranger Things",
                AlternativeTitles = new List<string> { "Cosas Extrañas", "Eleven", "Once", "Hawkins" },
                Synopsis = "A raíz de la misteriosa desaparición de un niño en el pueblo de Hawkins, sus amigos y su familia descubren experimentos gubernamentales secretos, fuerzas sobrenaturales del Upside Down y a una niña con poderes telequinéticos llamada Once.",
                CoverImageUrl = "https://static.tvmaze.com/uploads/images/original_untouched/595/1489169.jpg",
                Type = MediaType.Serie,
                Rating = 8.7,
                Status = "En emisión",
                ReleaseDate = new DateTime(2016, 7, 15),
                PopularityScore = 940.0,
                Genres = new List<string> { "Ciencia Ficción y Fantasía", "Misterio", "Drama" }
            },
            new()
            {
                ExternalId = "tmdb_tv_mock8",
                Title = "The Last of Us",
                EnglishTitle = "The Last of Us",
                OriginalTitle = "The Last of Us",
                AlternativeTitles = new List<string> { "TLOU", "Joel y Ellie" },
                Synopsis = "Veinte años después de que una pandemia fúngica destruyera la civilización moderna, el endurecido superviviente Joel es contratado para sacar de contrabando a Ellie, una niña de 14 años, fuera de una opresiva zona de cuarentena.",
                CoverImageUrl = "https://static.tvmaze.com/uploads/images/original_untouched/563/1409008.jpg",
                Type = MediaType.Serie,
                Rating = 8.8,
                Status = "En emisión",
                ReleaseDate = new DateTime(2023, 1, 15),
                PopularityScore = 920.0,
                Genres = new List<string> { "Drama", "Acción y Aventura", "Ciencia Ficción y Fantasía" }
            },
            new()
            {
                ExternalId = "tmdb_tv_mock9",
                Title = "Dark",
                EnglishTitle = "Dark",
                OriginalTitle = "Dark",
                AlternativeTitles = new List<string> { "Jonas Kahnwald", "Winden" },
                Synopsis = "Tras la desaparición de dos niños en el pueblo alemán de Winden, salen a la luz las fracturadas relaciones, las dobles vidas y el oscuro pasado de cuatro familias conectadas por un misterio de viajes en el tiempo que abarca varias generaciones.",
                CoverImageUrl = "https://static.tvmaze.com/uploads/images/original_untouched/504/1262352.jpg",
                Type = MediaType.Serie,
                Rating = 8.8,
                Status = "Finalizada",
                ReleaseDate = new DateTime(2017, 12, 1),
                PopularityScore = 900.0,
                Genres = new List<string> { "Ciencia Ficción y Fantasía", "Misterio", "Drama" }
            },
            new()
            {
                ExternalId = "tmdb_tv_mock10",
                Title = "Peaky Blinders",
                EnglishTitle = "Peaky Blinders",
                OriginalTitle = "Peaky Blinders",
                AlternativeTitles = new List<string> { "Thomas Shelby", "Tommy Shelby" },
                Synopsis = "En el Birmingham de 1919 tras la Primera Guerra Mundial, el ambicioso y calculador jefe criminal Thomas Shelby lidera a la temida pandilla de los Peaky Blinders mientras expande su imperio familiar a cualquier costo.",
                CoverImageUrl = "https://static.tvmaze.com/uploads/images/original_untouched/48/122213.jpg",
                Type = MediaType.Serie,
                Rating = 8.8,
                Status = "Finalizada",
                ReleaseDate = new DateTime(2013, 9, 12),
                PopularityScore = 890.0,
                Genres = new List<string> { "Drama", "Crimen" }
            },
            new()
            {
                ExternalId = "tmdb_tv_mock5",
                Title = "El Juego del Calamar (Squid Game)",
                EnglishTitle = "Squid Game",
                OriginalTitle = "Ojing-eo Geim",
                AlternativeTitles = new List<string> { "Ojing-eo Geim", "Round Six", "Squid Game" },
                Synopsis = "Cientos de jugadores con graves dificultades económicas aceptan una extraña invitación a competir en juegos infantiles tradicionales. Dentro les espera un premio de 45.600 millones de wones, pero perder significa la muerte.",
                CoverImageUrl = "https://static.tvmaze.com/uploads/images/original_untouched/576/1440521.jpg",
                Type = MediaType.Serie,
                Rating = 8.4,
                Status = "En emisión",
                ReleaseDate = new DateTime(2021, 9, 17),
                PopularityScore = 910.0,
                Genres = new List<string> { "Acción y Aventura", "Misterio", "Drama" }
            },
            new()
            {
                ExternalId = "tmdb_tv_mock11",
                Title = "The Boys",
                EnglishTitle = "The Boys",
                OriginalTitle = "The Boys",
                AlternativeTitles = new List<string> { "Homelander", "Billy Butcher" },
                Synopsis = "En un mundo donde los superhéroes abusan de sus superpoderes y son corrompidos por la fama corporativa de Vought International, un grupo de vigilantes conocidos como 'The Boys' se propone descubrir la verdad y detenerlos.",
                CoverImageUrl = "https://static.tvmaze.com/uploads/images/original_untouched/619/1547768.jpg",
                Type = MediaType.Serie,
                Rating = 8.7,
                Status = "En emisión",
                ReleaseDate = new DateTime(2019, 7, 26),
                PopularityScore = 885.0,
                Genres = new List<string> { "Acción y Aventura", "Ciencia Ficción y Fantasía", "Comedia" }
            },
            new()
            {
                ExternalId = "tmdb_tv_mock4",
                Title = "Merlina (Wednesday)",
                EnglishTitle = "Wednesday",
                OriginalTitle = "Wednesday",
                AlternativeTitles = new List<string> { "Wednesday", "Los Locos Addams: Merlina", "Miércoles" },
                Synopsis = "Inteligente, sarcástica y un poco muerta por dentro, Merlina Addams investiga una ola de asesinatos monstruosos mientras hace nuevos amigos y enemigos en la Academia Nunca Más.",
                CoverImageUrl = "https://static.tvmaze.com/uploads/images/original_untouched/586/1466410.jpg",
                Type = MediaType.Serie,
                Rating = 8.2,
                Status = "En emisión",
                ReleaseDate = new DateTime(2022, 11, 23),
                PopularityScore = 850.0,
                Genres = new List<string> { "Ciencia Ficción y Fantasía", "Misterio", "Comedia" }
            },
            new()
            {
                ExternalId = "tmdb_tv_mock2",
                Title = "La Casa de Papel",
                EnglishTitle = "Money Heist",
                OriginalTitle = "La Casa de Papel",
                AlternativeTitles = new List<string> { "Money Heist", "LCDP", "El Profesor" },
                Synopsis = "Un misterioso hombre conocido como 'El Profesor' recluta a una banda de ocho atracadores con habilidades únicas para llevar a cabo el mayor robo de la historia en la Fábrica Nacional de Moneda y Timbre de España.",
                CoverImageUrl = "https://static.tvmaze.com/uploads/images/original_untouched/430/1076004.jpg",
                Type = MediaType.Serie,
                Rating = 8.3,
                Status = "Finalizada",
                ReleaseDate = new DateTime(2017, 5, 2),
                PopularityScore = 830.0,
                Genres = new List<string> { "Crimen", "Drama", "Acción y Aventura" }
            }
        };
    }
}
