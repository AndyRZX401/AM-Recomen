using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using AMRecomen.Application.Common;
using AMRecomen.Application.Dtos;
using AMRecomen.Application.Interfaces;
using AMRecomen.Domain.Entities;
using AMRecomen.Domain.Enums;
using AMRecomen.Domain.Interfaces;
using AMRecomen.Application.Services;

namespace AMRecomen.Infrastructure.BackgroundJobs;

public class TrendSyncBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<TrendSyncBackgroundService> _logger;

    public TrendSyncBackgroundService(IServiceScopeFactory scopeFactory, ILogger<TrendSyncBackgroundService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Servicio en segundo plano de sincronización de tendencias iniciado.");

        // Ejecutar inmediatamente al iniciar
        try
        {
            await SyncTrendsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error durante la sincronización inicial de tendencias.");
        }

        // Bucle de ejecución diaria
        while (!stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Esperando 24 horas para la próxima sincronización de tendencias...");
            try
            {
                // Esperar 24 horas
                await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
                await SyncTrendsAsync();
            }
            catch (TaskCanceledException)
            {
                break; // Aplicación apagándose
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error durante la sincronización periódica de tendencias.");
            }
        }

        _logger.LogInformation("Servicio en segundo plano de sincronización de tendencias detenido.");
    }

    private async Task SyncTrendsAsync()
    {
        _logger.LogInformation("Iniciando sincronización de tendencias externas...");

        using var scope = _scopeFactory.CreateScope();
        var animeService = scope.ServiceProvider.GetRequiredService<IAnimeApiService>();
        var movieService = scope.ServiceProvider.GetRequiredService<IMovieApiService>();
        var comicService = scope.ServiceProvider.GetRequiredService<IComicApiService>();
        
        var mediaRepository = scope.ServiceProvider.GetRequiredService<IMediaItemRepository>();
        var tagRepository = scope.ServiceProvider.GetRequiredService<ITagRepository>();
        var trendRepository = scope.ServiceProvider.GetRequiredService<ITrendMetricRepository>();
        var translationService = scope.ServiceProvider.GetRequiredService<TranslationService>();

        // Obtener tendencias de las 5 categorías en paralelo
        var animeTask = SafeFetchAsync(() => animeService.GetTrendingAnimeAsync());
        var movieTask = SafeFetchAsync(() => movieService.GetTrendingMoviesAsync());
        var seriesTask = SafeFetchAsync(() => movieService.GetTrendingSeriesAsync());
        var manhwaTask = SafeFetchAsync(() => comicService.GetTrendingComicsAsync(isManhwa: true));
        var manhuaTask = SafeFetchAsync(() => comicService.GetTrendingComicsAsync(isManhwa: false));

        await Task.WhenAll(animeTask, movieTask, seriesTask, manhwaTask, manhuaTask);

        var allTrends = new List<ExternalMediaResult>();
        allTrends.AddRange(animeTask.Result);
        allTrends.AddRange(movieTask.Result);
        allTrends.AddRange(seriesTask.Result);
        allTrends.AddRange(manhwaTask.Result);
        allTrends.AddRange(manhuaTask.Result);

        _logger.LogInformation("Sincronización: se obtuvieron {Count} elementos de tendencias de APIs externas.", allTrends.Count);

        foreach (var ext in allTrends)
        {
            try
            {
                var existing = await mediaRepository.GetByExternalIdAsync(ext.ExternalId);
                
                double hypeScore = CalculateHypeScore(ext);

                if (existing != null)
                {
                    // Actualizar métricas y rating del elemento existente
                    existing.HypeScore = hypeScore;
                    existing.Rating = ext.Rating > 0 ? ext.Rating : existing.Rating;
                    existing.Status = !string.IsNullOrEmpty(ext.Status) ? ext.Status : existing.Status;
                    
                    // Reparar portadas o sinopsis que hayan quedado vacías o con placeholder temporal
                    if (!string.IsNullOrEmpty(ext.CoverImageUrl) && (string.IsNullOrEmpty(existing.CoverImageUrl) || existing.CoverImageUrl.Contains("unsplash.com") || existing.CoverImageUrl.EndsWith("/covers/") || existing.CoverImageUrl.Contains("placeholder")))
                    {
                        existing.CoverImageUrl = ext.CoverImageUrl;
                    }
                    if (!string.IsNullOrEmpty(ext.Synopsis) && (string.IsNullOrEmpty(existing.Synopsis) || existing.Synopsis.Length < ext.Synopsis.Length))
                    {
                        existing.Synopsis = ext.Synopsis;
                    }
                    if (ext.ReleaseDate.HasValue && !existing.ReleaseDate.HasValue)
                    {
                        existing.ReleaseDate = ext.ReleaseDate;
                    }
                    if (!string.IsNullOrEmpty(ext.Title) && string.IsNullOrEmpty(existing.Title))
                    {
                        existing.Title = ext.Title;
                    }
                    
                    existing.UpdatedAt = DateTime.UtcNow;

                    await mediaRepository.UpdateAsync(existing);
                    
                    // Añadir métrica histórica
                    await trendRepository.AddAsync(new TrendMetric
                    {
                        MediaItemId = existing.Id,
                        Source = ext.ExternalId.Split('_')[0],
                        MetricType = "HypeScore",
                        Value = hypeScore,
                        RecordedAt = DateTime.UtcNow
                    });
                }
                else
                {
                    // Generar Slug SEO único
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
                    var existingSlug = await mediaRepository.GetBySlugAsync(slug);
                    if (existingSlug != null)
                    {
                        slug = $"{slug}-{Guid.NewGuid().ToString().Substring(0, 5)}";
                    }

                    // Resolver tags
                    var tags = new List<Tag>();
                    foreach (var gName in ext.Genres)
                    {
                        var tag = await tagRepository.GetOrCreateAsync(gName);
                        tags.Add(tag);
                    }

                    // Traducir sinopsis al español
                    var synopsis = ext.Synopsis;
                    if (ext.NeedTranslation && !string.IsNullOrWhiteSpace(synopsis))
                    {
                        synopsis = await translationService.TranslateToSpanishAsync(synopsis);
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

                    await mediaRepository.AddAsync(newItem);
                    await mediaRepository.SaveChangesAsync(); // Guardar para obtener el ID de la BD

                    // Añadir métrica inicial
                    await trendRepository.AddAsync(new TrendMetric
                    {
                        MediaItemId = newItem.Id,
                        Source = ext.ExternalId.Split('_')[0],
                        MetricType = "HypeScore",
                        Value = hypeScore,
                        RecordedAt = DateTime.UtcNow
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error al procesar la ingesta en segundo plano del elemento {Title}.", ext.Title);
            }
        }

        await mediaRepository.SaveChangesAsync();
        await trendRepository.SaveChangesAsync();
        _logger.LogInformation("Sincronización de tendencias finalizada correctamente.");
    }

    private double CalculateHypeScore(ExternalMediaResult ext)
    {
        double hype = 50.0;
        switch (ext.Type)
        {
            case MediaType.Anime:
                if (ext.PopularityScore > 0)
                    hype = Math.Min(98.0, 20.0 + Math.Log10(ext.PopularityScore + 1) * 13.0);
                break;
            case MediaType.Pelicula:
                if (ext.PopularityScore > 0)
                    hype = Math.Min(98.0, 30.0 + (ext.PopularityScore / 15.0));
                break;
            case MediaType.Manhwa:
            case MediaType.Manhua:
                if (ext.PopularityScore > 0)
                    hype = Math.Min(98.0, 25.0 + Math.Log10(ext.PopularityScore + 1) * 15.0);
                break;
        }

        if (ext.Rating > 0)
        {
            hype = (hype * 0.8) + (ext.Rating * 2.0);
        }

        return Math.Clamp(Math.Round(hype, 1), 10.0, 99.0);
    }

    private async Task<IEnumerable<ExternalMediaResult>> SafeFetchAsync(Func<Task<IEnumerable<ExternalMediaResult>>> fetchCall)
    {
        try
        {
            return await fetchCall();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fallo al obtener tendencias externas. Retornando vacío.");
            return Enumerable.Empty<ExternalMediaResult>();
        }
    }
}
