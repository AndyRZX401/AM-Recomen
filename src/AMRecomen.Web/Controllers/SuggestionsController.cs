using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using AMRecomen.Domain.Interfaces;
using AMRecomen.Application.Services;

namespace AMRecomen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SuggestionsController : ControllerBase
{
    private readonly IMediaItemRepository _mediaRepository;
    private readonly SearchAndIngestionService _searchService;
    private readonly IMemoryCache _cache;

    public SuggestionsController(
        IMediaItemRepository mediaRepository,
        SearchAndIngestionService searchService,
        IMemoryCache cache)
    {
        _mediaRepository = mediaRepository;
        _searchService = searchService;
        _cache = cache;
    }

    [HttpGet]
    public async Task<IActionResult> GetSuggestions([FromQuery] string q)
    {
        if (string.IsNullOrWhiteSpace(q) || q.Length < 2)
            return Ok(Enumerable.Empty<object>());

        var queryKey = q.ToLower().Trim();
        var cacheKey = $"Suggestions_{queryKey}";

        if (!_cache.TryGetValue(cacheKey, out object? cachedSuggestions) || cachedSuggestions == null)
        {
            // Buscar coincidencias locales rápidas con AsNoTracking
            var items = (await _mediaRepository.SearchAsync(queryKey, 6)).ToList();

            // Si hay pocas coincidencias locales y el término tiene al menos 3 letras, buscar e ingerir en vivo desde las APIs globales
            if (items.Count < 3 && queryKey.Length >= 3)
            {
                items = (await _searchService.SearchAndIngestAsync(queryKey)).Take(6).ToList();
            }
            
            cachedSuggestions = items.Select(i => new
            {
                title = i.Title,
                slug = i.Slug,
                type = i.Type.ToString(),
                cover = i.CoverImageUrl
            }).ToList();

            if (items.Any())
            {
                var cacheOptions = new MemoryCacheEntryOptions()
                    .SetAbsoluteExpiration(TimeSpan.FromMinutes(5));
                _cache.Set(cacheKey, cachedSuggestions, cacheOptions);
            }
        }

        return Ok(cachedSuggestions);
    }
}
