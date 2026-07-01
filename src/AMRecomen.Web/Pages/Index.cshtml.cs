using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Caching.Memory;
using AMRecomen.Domain.Entities;
using AMRecomen.Domain.Enums;
using AMRecomen.Domain.Interfaces;
using AMRecomen.Application.Services;

namespace AMRecomen.Web.Pages;

public class IndexModel : PageModel
{
    private readonly IMediaItemRepository _mediaRepository;
    private readonly SearchAndIngestionService _searchService;
    private readonly IMemoryCache _cache;

    public IndexModel(
        IMediaItemRepository mediaRepository, 
        SearchAndIngestionService searchService,
        IMemoryCache cache)
    {
        _mediaRepository = mediaRepository;
        _searchService = searchService;
        _cache = cache;
    }

    public bool IsSearch { get; set; }
    
    [BindProperty(SupportsGet = true)]
    public string? Q { get; set; }

    public IEnumerable<MediaItem> SearchResults { get; set; } = Enumerable.Empty<MediaItem>();

    public IEnumerable<MediaItem> TopAnime { get; set; } = Enumerable.Empty<MediaItem>();
    public IEnumerable<MediaItem> TopManhwa { get; set; } = Enumerable.Empty<MediaItem>();
    public IEnumerable<MediaItem> TopManhua { get; set; } = Enumerable.Empty<MediaItem>();
    public IEnumerable<MediaItem> TopMovies { get; set; } = Enumerable.Empty<MediaItem>();
    public IEnumerable<MediaItem> TopSeries { get; set; } = Enumerable.Empty<MediaItem>();

    public async Task OnGetAsync()
    {
        if (!string.IsNullOrWhiteSpace(Q))
        {
            IsSearch = true;
            // Ejecutar búsqueda e ingesta en caliente
            SearchResults = await _searchService.SearchAndIngestAsync(Q);
        }
        else
        {
            IsSearch = false;
            // Cargar los 3 mejores para cada sección usando caché de 10 minutos
            TopAnime = await GetCachedTopTrendingAsync(MediaType.Anime, 3);
            TopManhwa = await GetCachedTopTrendingAsync(MediaType.Manhwa, 3);
            TopManhua = await GetCachedTopTrendingAsync(MediaType.Manhua, 3);
            TopMovies = await GetCachedTopTrendingAsync(MediaType.Pelicula, 3);
            TopSeries = await GetCachedTopTrendingAsync(MediaType.Serie, 3);
        }
    }

    private async Task<IEnumerable<MediaItem>> GetCachedTopTrendingAsync(MediaType type, int count)
    {
        var cacheKey = $"TopTrending_{type}_{count}";
        if (!_cache.TryGetValue(cacheKey, out object? cached) || cached is not IEnumerable<MediaItem> items)
        {
            items = await _mediaRepository.GetTopTrendingAsync(type, count);
            var cacheOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromMinutes(10));
            _cache.Set(cacheKey, items, cacheOptions);
        }
        return items;
    }
}
