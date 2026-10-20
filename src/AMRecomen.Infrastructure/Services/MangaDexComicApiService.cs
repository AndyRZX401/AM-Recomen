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
        _httpClient.Timeout = TimeSpan.FromSeconds(8);
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

        // 1. Buscar en nuestro catálogo curado de los mejores Manhwas y Manhuas
        var curatedMatches = GetCuratedTopComics()
            .Where(m => m.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        m.EnglishTitle.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        m.OriginalTitle.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        m.AlternativeTitles.Any(a => a.Contains(q, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        results.AddRange(curatedMatches);

        // 2. Buscar en tiempo real en AniList GraphQL (portadas oficiales de s4.anilist.co sin bloqueo de hotlink y títulos en inglés/español)
        var krTask = SearchAniListComicsAsync(q, "KR", MediaType.Manhwa, 6);
        var cnTask = SearchAniListComicsAsync(q, "CN", MediaType.Manhua, 6);
        await Task.WhenAll(krTask, cnTask);

        foreach (var item in krTask.Result.Concat(cnTask.Result))
        {
            if (!results.Any(r => r.ExternalId == item.ExternalId ||
                                  r.Title.Equals(item.Title, StringComparison.OrdinalIgnoreCase) ||
                                  r.OriginalTitle.Equals(item.OriginalTitle, StringComparison.OrdinalIgnoreCase)))
            {
                results.Add(item);
            }
        }

        return results;
    }

    public async Task<IEnumerable<ExternalMediaResult>> GetTrendingComicsAsync(bool isManhwa)
    {
        var targetType = isManhwa ? MediaType.Manhwa : MediaType.Manhua;
        var results = GetCuratedTopComics()
            .Where(c => c.Type == targetType)
            .ToList();

        try
        {
            string country = isManhwa ? "KR" : "CN";
            var liveTop = await FetchAniListTopComicsAsync(country, targetType, 12);
            foreach (var item in liveTop)
            {
                if (!results.Any(r => r.ExternalId == item.ExternalId ||
                                      r.Title.Equals(item.Title, StringComparison.OrdinalIgnoreCase) ||
                                      r.EnglishTitle.Equals(item.EnglishTitle, StringComparison.OrdinalIgnoreCase) ||
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

    public async Task<ExternalMediaResult?> GetComicByIdAsync(string externalId)
    {
        await Task.CompletedTask;
        return GetCuratedTopComics().FirstOrDefault(c => c.ExternalId == externalId);
    }

    private async Task<List<ExternalMediaResult>> SearchAniListComicsAsync(string search, string countryOfOrigin, MediaType mediaType, int limit)
    {
        const string query = @"
        query ($search: String, $country: CountryCode, $perPage: Int) {
          Page(page: 1, perPage: $perPage) {
            media(search: $search, type: MANGA, countryOfOrigin: $country, sort: [POPULARITY_DESC, SCORE_DESC], isAdult: false) {
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

    private async Task<List<ExternalMediaResult>> FetchAniListTopComicsAsync(string countryOfOrigin, MediaType mediaType, int limit)
    {
        const string query = @"
        query ($country: CountryCode, $perPage: Int) {
          Page(page: 1, perPage: $perPage) {
            media(type: MANGA, countryOfOrigin: $country, sort: [POPULARITY_DESC, SCORE_DESC], isAdult: false) {
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

            // Priorizar siempre el título en inglés/occidental sobre la romanización coreana/china
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

    public static IEnumerable<ExternalMediaResult> GetCuratedTopComics()
    {
        return new List<ExternalMediaResult>
        {
            // ==================== MEJORES MANHWAS (COREA) ====================
            new()
            {
                ExternalId = "mangadex_solo_leveling",
                Title = "Solo Leveling",
                EnglishTitle = "Solo Leveling",
                OriginalTitle = "Na Honjaman Level Up",
                AlternativeTitles = new List<string> { "Na Honjaman Level-Up", "I Level Up Alone", "Only I Level Up", "Sung Jinwoo" },
                Synopsis = "En un mundo donde cazadores con habilidades mágicas deben luchar contra monstruos mortales para proteger a la humanidad, Sung Jinwoo, un cazador de rango E notoriamente débil, sobrevive a una mazmorra doble letal y despierta como el único 'Jugador' del Sistema capaz de subir de nivel sin límites.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/manga/cover/medium/bx105398-b673Vt5ZSuz3.jpg",
                Type = MediaType.Manhwa,
                Rating = 9.1,
                Status = "Finalizado",
                ReleaseDate = new DateTime(2018, 3, 4),
                PopularityScore = 287504.0,
                Genres = new List<string> { "Acción", "Fantasía", "Aventura", "Sobrenatural" }
            },
            new()
            {
                ExternalId = "mangadex_omniscient_reader",
                Title = "Lector Omnisciente (Omniscient Reader)",
                EnglishTitle = "Omniscient Reader's Viewpoint",
                OriginalTitle = "Jeonjijeok Dokja Sijeom",
                AlternativeTitles = new List<string> { "Jeonjijeok Dokja Sijeom", "ORV", "Kim Dokja", "Punto de Vista del Lector Omnisciente" },
                Synopsis = "Kim Dokja es un oficinista común cuyo único pasatiempo era leer una novela web desconocida llamada 'Tres formas de sobrevivir en un mundo en ruinas'. Cuando el mundo real se transforma repentinamente en el escenario apocalíptico de esa novela, Dokja es la única persona que conoce cómo terminará la historia.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/manga/cover/medium/bx119257-Pi21aq3ey9GG.jpg",
                Type = MediaType.Manhwa,
                Rating = 9.2,
                Status = "En publicación",
                ReleaseDate = new DateTime(2020, 5, 26),
                PopularityScore = 210000.0,
                Genres = new List<string> { "Acción", "Fantasía", "Aventura", "Psicológico" }
            },
            new()
            {
                ExternalId = "mangadex_greatest_estate_developer",
                Title = "El Mejor Ingeniero del Mundo",
                EnglishTitle = "The Greatest Estate Developer",
                OriginalTitle = "Yeokdaegeum Yeongji Seolgyesa",
                AlternativeTitles = new List<string> { "Lloyd Frontera", "The World's Best Engineer", "Yeokdaegeum Yeongji Seolgyesa" },
                Synopsis = "Cuando el estudiante de ingeniería civil Suho Kim despierta dentro de una novela de fantasía como el noble perezoso y endeudado Lloyd Frontera, decide usar sus conocimientos de ingeniería moderna y construcción para salvar a su familia de la ruina y transformar el continente.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/manga/cover/medium/bx140407-fJQr0fmqq1IO.png",
                Type = MediaType.Manhwa,
                Rating = 9.3,
                Status = "En publicación",
                ReleaseDate = new DateTime(2021, 8, 5),
                PopularityScore = 175000.0,
                Genres = new List<string> { "Comedia", "Fantasía", "Aventura", "Acción" }
            },
            new()
            {
                ExternalId = "mangadex_villains_destined_to_die",
                Title = "El Único Final de la Villana es la Muerte",
                EnglishTitle = "Villains Are Destined to Die",
                OriginalTitle = "Agyeogui Ending Jugeumppun",
                AlternativeTitles = new List<string> { "Agyeogui Ending Jugeumppun", "Death Is the Only Ending for the Villainess", "Penelope Eckhart" },
                Synopsis = "Una joven reencarna dentro de un juego de romance en modo difícil como Penélope Eckhart, la hija adoptiva odiada por todos y destinada a morir en cada ruta. Para sobrevivir antes de que aparezca la verdadera protagonista, debe conquistar estratégicamente a uno de los intereses amorosos sin que su afecto caiga a cero.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/manga/cover/medium/bx118408-zZuOINqjBZYn.jpg",
                Type = MediaType.Manhwa,
                Rating = 8.9,
                Status = "En publicación",
                ReleaseDate = new DateTime(2020, 5, 8),
                PopularityScore = 155000.0,
                Genres = new List<string> { "Fantasía", "Romance", "Drama", "Misterio" }
            },
            new()
            {
                ExternalId = "mangadex_tower_of_god",
                Title = "Torre de Dios (Tower of God)",
                EnglishTitle = "Tower of God",
                OriginalTitle = "Sinui Tap",
                AlternativeTitles = new List<string> { "Sin-ui Tap", "Kami no Tou", "Bam", "SIU" },
                Synopsis = "¿Qué deseas? ¿Dinero y riqueza? ¿Honor y orgullo? ¿Autoridad y poder? ¿O algo que trascienda todo lo demás? Todo se encuentra en la cima de la mítica Torre de Dios. Bam, un joven que vivió en la oscuridad, ingresa a la Torre arriesgando su vida para reencontrarse con Rachel.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/manga/cover/medium/bx85143-23oup3ETbFJk.jpg",
                Type = MediaType.Manhwa,
                Rating = 8.7,
                Status = "En publicación",
                ReleaseDate = new DateTime(2010, 6, 30),
                PopularityScore = 160000.0,
                Genres = new List<string> { "Fantasía", "Aventura", "Acción", "Misterio" }
            },
            new()
            {
                ExternalId = "mangadex_mount_hua",
                Title = "El Regreso de la Secta del Monte Hua",
                EnglishTitle = "Return of the Blossoming Blade",
                OriginalTitle = "Hwasangwihwan",
                AlternativeTitles = new List<string> { "Return of the Mount Hua Sect", "Chung Myung", "Hwasangwihwan" },
                Synopsis = "Chung Myung, el Gran Santo de la Espada de la Flor del Ciruelo de la Secta del Monte Hua, muere tras derrotar al líder del Culto Demoníaco y despierta cien años después en el cuerpo de un niño mendigo, descubriendo que su gloriosa secta ha caído en la ruina total.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/manga/cover/medium/bx132144-i5B4VnG9sRgh.png",
                Type = MediaType.Manhwa,
                Rating = 9.0,
                Status = "En publicación",
                ReleaseDate = new DateTime(2021, 3, 23),
                PopularityScore = 148000.0,
                Genres = new List<string> { "Acción", "Artes Marciales", "Aventura", "Comedia" }
            },
            new()
            {
                ExternalId = "mangadex_nano_machine",
                Title = "Nano Machine",
                EnglishTitle = "Nano Machine",
                OriginalTitle = "Nano Machine",
                AlternativeTitles = new List<string> { "Cheon Yeo-Woon", "Nanomáquinas Marciales" },
                Synopsis = "Humillado y con su vida en constante peligro como el hijo ilegítimo del Culto Demoníaco, el joven Cheon Yeo-Woon recibe la inesperada visita de un descendiente suyo del futuro, quien inyecta una avanzada nanomáquina de inteligencia artificial en su cuerpo.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/manga/cover/medium/bx120980-RZ9WLd0o9hyo.jpg",
                Type = MediaType.Manhwa,
                Rating = 8.6,
                Status = "En publicación",
                ReleaseDate = new DateTime(2020, 6, 10),
                PopularityScore = 135000.0,
                Genres = new List<string> { "Acción", "Artes Marciales", "Ciencia Ficción", "Fantasía" }
            },
            new()
            {
                ExternalId = "mangadex_sss_revival_hunter",
                Title = "Cazador Suicida de Clase SSS",
                EnglishTitle = "SSS-Class Revival Hunter",
                OriginalTitle = "SSS-geup Jugeoya Saneun Hunter",
                AlternativeTitles = new List<string> { "SSS-Class Suicide Hunter", "Kim Gong-Ja" },
                Synopsis = "Kim Gong-Ja es un cazador de rango inferior que envidia a los mejores héroes de la Torre. Cuando despierta una habilidad de rango S que copia el poder de quien lo mate, descubre el secreto del cazador número uno y gana la capacidad de retroceder 24 horas en el tiempo cada vez que muere.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/manga/cover/medium/bx128067-wnLBg6Cy1ncs.jpg",
                Type = MediaType.Manhwa,
                Rating = 8.8,
                Status = "En publicación",
                ReleaseDate = new DateTime(2020, 12, 14),
                PopularityScore = 130000.0,
                Genres = new List<string> { "Acción", "Fantasía", "Aventura", "Drama" }
            },

            // ==================== MEJORES MANHUAS (CHINA) ====================
            new()
            {
                ExternalId = "mangadex_devil_butler",
                Title = "El Emperador Mágico (The Devil Butler)",
                EnglishTitle = "The Devil Butler",
                OriginalTitle = "Mo Huang Da Guanjia",
                AlternativeTitles = new List<string> { "Magic Emperor", "Demonic Emperor", "Zhuo Yifan", "Mo Huang Da Guanjia" },
                Synopsis = "Zhuo Yifan, conocido y temido como el Emperador Demoníaco, es traicionado por su discípulo de mayor confianza tras obtener el Libro de los Nueve Secretos. Su alma renace en el cuerpo de Zhuo Fan, el mayordomo de una familia noble en decadencia, desde donde volverá a la cima.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/manga/cover/medium/bx116353-DYuIigXuPwpZ.jpg",
                Type = MediaType.Manhua,
                Rating = 8.8,
                Status = "En publicación",
                ReleaseDate = new DateTime(2019, 8, 15),
                PopularityScore = 145000.0,
                Genres = new List<string> { "Acción", "Artes Marciales", "Fantasía", "Cultivación" }
            },
            new()
            {
                ExternalId = "mangadex_tales_demons_gods",
                Title = "Leyendas de Demonios y Dioses",
                EnglishTitle = "Tales of Demons and Gods",
                OriginalTitle = "Yao Shen Ji",
                AlternativeTitles = new List<string> { "Yaoshenji", "TDG", "Historias de Demonios y Dioses", "Nie Li" },
                Synopsis = "Nie Li, el más poderoso Espiritista Demoníaco de la humanidad, cae en combate contra el Emperador Sabio pero renace en su cuerpo de 13 años gracias al Libro del Espíritu Demoníaco Temporal. Con su sabiduría de vidas pasadas, cambiará el destino de Ciudad Gloria.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/manga/cover/medium/bx86707-QD3UyAOHUEaT.jpg",
                Type = MediaType.Manhua,
                Rating = 8.6,
                Status = "En publicación",
                ReleaseDate = new DateTime(2015, 6, 20),
                PopularityScore = 140000.0,
                Genres = new List<string> { "Acción", "Fantasía", "Artes Marciales", "Aventura" }
            },
            new()
            {
                ExternalId = "mangadex_martial_peak",
                Title = "Cúspide Marcial (Martial Peak)",
                EnglishTitle = "Martial Peak",
                OriginalTitle = "Wu Lian Dianfeng",
                AlternativeTitles = new List<string> { "Wu Lian Dian Feng", "Martial Peak", "Yang Kai" },
                Synopsis = "El viaje hacia la cúspide marcial es solitario, largo e interminable. Yang Kai, un humilde discípulo de prueba del Pabellón del Cielo Alto, encuentra una misteriosa piedra negra que transforma su destino y lo impulsa a través de los reinos del cultivo marcial.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/manga/cover/medium/bx104494-xmWuGEwxzbfB.jpg",
                Type = MediaType.Manhua,
                Rating = 8.4,
                Status = "En publicación",
                ReleaseDate = new DateTime(2018, 1, 12),
                PopularityScore = 132000.0,
                Genres = new List<string> { "Acción", "Artes Marciales", "Aventura", "Fantasía" }
            },
            new()
            {
                ExternalId = "mangadex_btth",
                Title = "Batalla a Través de los Cielos",
                EnglishTitle = "Battle Through the Heavens",
                OriginalTitle = "Dou Po Cangqiong",
                AlternativeTitles = new List<string> { "Doupo Cangqiong", "Fights Break Sphere", "BTTH", "Xiao Yan" },
                Synopsis = "En un continente regido por el Dou Qi donde el fuerte impone la ley, el joven prodigio Xiao Yan pierde misteriosamente todos sus poderes durante tres años y sufre la humillación de su clan, hasta que despierta el espíritu del legendario maestro Yao Chen en su anillo.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/manga/cover/medium/bx85453-LTk6GztEMFaK.jpg",
                Type = MediaType.Manhua,
                Rating = 8.5,
                Status = "En publicación",
                ReleaseDate = new DateTime(2012, 4, 1),
                PopularityScore = 125000.0,
                Genres = new List<string> { "Acción", "Artes Marciales", "Fantasía", "Aventura" }
            },
            new()
            {
                ExternalId = "mangadex_heaven_officials_blessing",
                Title = "La Bendición del Oficial del Cielo",
                EnglishTitle = "Heaven Official's Blessing",
                OriginalTitle = "Tian Guan Ci Fu",
                AlternativeTitles = new List<string> { "TGCF", "Xie Lian", "Hua Cheng" },
                Synopsis = "Xie Lian, antiguo príncipe heredero del reino de Xianle, asciende al Reino Celestial por tercera vez tras ochocientos años de exilio, convirtiéndose en el hazmerreír de los tres reinos hasta que en su primera misión conoce al temido Rey Fantasma Hua Cheng.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/manga/cover/medium/bx112275-fh0u5cH2qB5b.jpg",
                Type = MediaType.Manhua,
                Rating = 8.9,
                Status = "En publicación",
                ReleaseDate = new DateTime(2019, 10, 19),
                PopularityScore = 120000.0,
                Genres = new List<string> { "Fantasía", "Aventura", "Drama", "Sobrenatural" }
            },
            new()
            {
                ExternalId = "mangadex_fated_villain",
                Title = "Soy el Villano Predestinado",
                EnglishTitle = "I Am The Fated Villain",
                OriginalTitle = "Wo! Tian Ming Da Fanpai",
                AlternativeTitles = new List<string> { "Gu Changge", "I Am the Fated Villain" },
                Synopsis = "Gu Changge transmigra a un mundo de cultivo fantástico como el joven maestro heredero de una familia inmortal suprema, pero descubre que es el antagonista destinado a caer frente a los 'Hijos del Destino'. Decidido a cambiar su suerte, usa el Sistema del Villano para arrebatarles su fortuna.",
                CoverImageUrl = "https://s4.anilist.co/file/anilistcdn/media/manga/cover/medium/bx142287-nlT7oSUzHnNv.jpg",
                Type = MediaType.Manhua,
                Rating = 8.5,
                Status = "En publicación",
                ReleaseDate = new DateTime(2021, 10, 16),
                PopularityScore = 115000.0,
                Genres = new List<string> { "Acción", "Fantasía", "Artes Marciales", "Psicológico" }
            }
        };
    }
}
