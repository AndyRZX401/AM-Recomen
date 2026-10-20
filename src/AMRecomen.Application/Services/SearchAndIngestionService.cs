using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using AMRecomen.Application.Common;
using AMRecomen.Application.Dtos;
using AMRecomen.Application.Interfaces;
using AMRecomen.Domain.Entities;
using AMRecomen.Domain.Enums;
using AMRecomen.Domain.Interfaces;

namespace AMRecomen.Application.Services;

public class SearchAndIngestionService
{
    private readonly IMediaItemRepository _mediaRepository;
    private readonly ITagRepository _tagRepository;
    private readonly IAnimeApiService _animeService;
    private readonly IMovieApiService _movieService;
    private readonly IComicApiService _comicService;
    private readonly TranslationService _translationService;

    public SearchAndIngestionService(
        IMediaItemRepository mediaRepository,
        ITagRepository tagRepository,
        IAnimeApiService animeService,
        IMovieApiService movieService,
        IComicApiService comicService,
        TranslationService translationService)
    {
        _mediaRepository = mediaRepository;
        _tagRepository = tagRepository;
        _animeService = animeService;
        _movieService = movieService;
        _comicService = comicService;
        _translationService = translationService;
    }

    public async Task<IEnumerable<MediaItem>> SearchAndIngestAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Enumerable.Empty<MediaItem>();

        query = query.Trim();

        // 1. Buscar localmente primero
        var localResults = await _mediaRepository.SearchAsync(query, 12);
        var localList = localResults.ToList();

        // Solo omitir la consulta externa si ya hay coincidencia exacta de título o suficientes resultados muy relevantes (>= 6)
        bool hasExactMatch = localList.Any(m => 
            m.Title.Equals(query, StringComparison.OrdinalIgnoreCase) || 
            m.EnglishTitle.Equals(query, StringComparison.OrdinalIgnoreCase) ||
            m.OriginalTitle.Equals(query, StringComparison.OrdinalIgnoreCase));

        if (hasExactMatch && localList.Count >= 4 || localList.Count >= 8)
        {
            return localList;
        }

        // 2. Si no hay suficientes resultados locales, buscamos en caliente en las APIs externas en paralelo
        var animeTask = SafeExecuteAsync(() => _animeService.SearchAnimeAsync(query));
        var movieTask = SafeExecuteAsync(() => _movieService.SearchMoviesAsync(query));
        var seriesTask = SafeExecuteAsync(() => _movieService.SearchSeriesAsync(query));
        var comicTask = SafeExecuteAsync(() => _comicService.SearchComicsAsync(query));

        await Task.WhenAll(animeTask, movieTask, seriesTask, comicTask);

        var externalResults = new List<ExternalMediaResult>();
        externalResults.AddRange(animeTask.Result);
        externalResults.AddRange(movieTask.Result);
        externalResults.AddRange(seriesTask.Result);
        externalResults.AddRange(comicTask.Result);

        if (!externalResults.Any())
        {
            return localList; // Si no hay nada externo, retornamos lo que tengamos local (aunque sea poco o nada)
        }

        // 3. Ingerir los resultados externos en la base de datos
        var ingestedItems = new List<MediaItem>();
        foreach (var ext in externalResults)
        {
            // Evitar duplicación: verificar si ya existe por ExternalId
            var existing = await _mediaRepository.GetByExternalIdAsync(ext.ExternalId);
            if (existing != null)
            {
                ingestedItems.Add(existing);
                continue;
            }

            // Calcular HypeScore (Popularidad/Fama) usando calculadora compartida
            double hypeScore = HypeCalculator.CalculateHypeScore(ext);

            // Determinar sufijo de Slug para evitar colisiones y optimizar SEO
            string typeSuffix = ext.Type switch
            {
                MediaType.Anime => "anime",
                MediaType.Manhwa => "manhwa",
                MediaType.Manhua => "manhua",
                MediaType.Pelicula => "pelicula",
                MediaType.Serie => "serie",
                _ => "media"
            };
            var slug = SlugHelper.GenerateSlug(ext.Title, typeSuffix);

            // Asegurarnos de que el slug sea único agregando caracteres si colisiona
            var existingSlug = await _mediaRepository.GetBySlugAsync(slug);
            if (existingSlug != null)
            {
                slug = $"{slug}-{Guid.NewGuid().ToString().Substring(0, 5)}";
            }

            // Mapear géneros a entidades Tag
            var tags = new List<Tag>();
            foreach (var gName in ext.Genres)
            {
                if (!string.IsNullOrWhiteSpace(gName))
                {
                    var tag = await _tagRepository.GetOrCreateAsync(gName);
                    tags.Add(tag);
                }
            }

            // Traducir sinopsis al español de manera automática y resiliente si es necesario
            var synopsis = ext.Synopsis;
            if (ext.NeedTranslation && !string.IsNullOrWhiteSpace(synopsis))
            {
                synopsis = await _translationService.TranslateToSpanishAsync(synopsis);
            }

            var newItem = new MediaItem
            {
                Title = ext.Title,
                EnglishTitle = ext.EnglishTitle,
                OriginalTitle = ext.OriginalTitle,
                AlternativeTitles = string.Join(", ", ext.AlternativeTitles),
                Synopsis = synopsis,
                CoverImageUrl = ext.CoverImageUrl,
                Type = ext.Type,
                HypeScore = hypeScore,
                Status = ext.Status,
                ReleaseDate = ext.ReleaseDate,
                Rating = ext.Rating,
                ExternalId = ext.ExternalId,
                Slug = slug,
                Tags = tags,
                CreatedAt = DateTime.UtcNow,
                UpdatedAt = DateTime.UtcNow
            };

            await _mediaRepository.AddAsync(newItem);
            ingestedItems.Add(newItem);
        }

        // Guardar todos los nuevos elementos ingeridos
        await _mediaRepository.SaveChangesAsync();

        // 4. Volver a buscar localmente para retornar resultados con relaciones cargadas correctamente
        var finalResults = await _mediaRepository.SearchAsync(query, 12);
        return finalResults;
    }

    private async Task<IEnumerable<ExternalMediaResult>> SafeExecuteAsync(Func<Task<IEnumerable<ExternalMediaResult>>> apiCall)
    {
        try
        {
            return await apiCall();
        }
        catch
        {
            return Enumerable.Empty<ExternalMediaResult>();
        }
    }
}
