using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using AMRecomen.Domain.Interfaces;

namespace AMRecomen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SuggestionsController : ControllerBase
{
    private readonly IMediaItemRepository _mediaRepository;
    private readonly IMemoryCache _cache;

    public SuggestionsController(IMediaItemRepository mediaRepository, IMemoryCache cache)
    {
        _mediaRepository = mediaRepository;
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
            var items = await _mediaRepository.SearchAsync(queryKey, 6);
            
            cachedSuggestions = items.Select(i => new
            {
                title = i.Title,
                slug = i.Slug,
                type = i.Type.ToString(),
                cover = i.CoverImageUrl
            }).ToList();

            var cacheOptions = new MemoryCacheEntryOptions()
                .SetAbsoluteExpiration(TimeSpan.FromMinutes(5)); // 5 minutos de duración de caché
            _cache.Set(cacheKey, cachedSuggestions, cacheOptions);
        }

        return Ok(cachedSuggestions);
    }
}
