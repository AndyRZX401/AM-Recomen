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

        // Si tenemos resultados locales decentes (ej: 3 o más), los retornamos para evitar sobrecargar las APIs externas
        if (localList.Count >= 3)
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

            // Calcular HypeScore (Popularidad/Fama)
            double hypeScore = CalculateHypeScore(ext);

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

    private double CalculateHypeScore(ExternalMediaResult ext)
    {
        // Puntuación de Hype entre 15% y 98% basada en el tipo de contenido y métrica
        double hype = 50.0; // Base

        switch (ext.Type)
        {
            case MediaType.Anime:
                // MAL members suele ir de 0 a 3,000,000. Escalamos logarítmicamente.
                if (ext.PopularityScore > 0)
                {
                    hype = Math.Min(98.0, 20.0 + Math.Log10(ext.PopularityScore + 1) * 13.0);
                }
                break;

            case MediaType.Pelicula:
                // TMDb popularity suele ser de 0 a 2000+.
                if (ext.PopularityScore > 0)
                {
                    hype = Math.Min(98.0, 30.0 + (ext.PopularityScore / 15.0));
                }
                break;

            case MediaType.Manhwa:
            case MediaType.Manhua:
                // MangaDex followedCount va de 0 a 100,000+.
                if (ext.PopularityScore > 0)
                {
                    hype = Math.Min(98.0, 25.0 + Math.Log10(ext.PopularityScore + 1) * 15.0);
                }
                break;
        }

        // Añadimos una pequeña variación basada en la valoración (Rating) si existe
        if (ext.Rating > 0)
        {
            hype = (hype * 0.8) + (ext.Rating * 2.0); // 20% peso al Rating
        }

        return Math.Clamp(Math.Round(hype, 1), 10.0, 99.0);
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
