using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AMRecomen.Domain.Entities;
using AMRecomen.Domain.Enums;
using AMRecomen.Domain.Interfaces;
using AMRecomen.Infrastructure.Data;

namespace AMRecomen.Infrastructure.Repositories;

public class MediaItemRepository : IMediaItemRepository
{
    private readonly AppDbContext _context;

    public MediaItemRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task<MediaItem?> GetByIdAsync(Guid id)
    {
        return await _context.MediaItems
            .Include(m => m.Tags)
            .FirstOrDefaultAsync(m => m.Id == id);
    }

    public async Task<MediaItem?> GetBySlugAsync(string slug)
    {
        var normalized = slug.ToLower().Trim();
        // Buscar primero en memoria en las entidades en seguimiento por guardarse (evita duplicados en inserción en lote)
        var local = _context.MediaItems.Local.FirstOrDefault(m => m.Slug == normalized);
        if (local != null)
            return local;

        return await _context.MediaItems
            .Include(m => m.Tags)
            .FirstOrDefaultAsync(m => m.Slug == normalized);
    }

    public async Task<MediaItem?> GetByExternalIdAsync(string externalId)
    {
        return await _context.MediaItems
            .Include(m => m.Tags)
            .FirstOrDefaultAsync(m => m.ExternalId == externalId);
    }

    public async Task<IEnumerable<MediaItem>> GetTopTrendingAsync(MediaType type, int count)
    {
        return await _context.MediaItems
            .AsNoTracking()
            .Include(m => m.Tags)
            .Where(m => m.Type == type)
            .OrderByDescending(m => m.HypeScore)
            .Take(count)
            .ToListAsync();
    }

    public async Task<(IEnumerable<MediaItem> Items, int TotalCount)> GetPagedByTypeAsync(MediaType type, int page, int pageSize, string? tag = null, string? sortBy = null)
    {
        var query = _context.MediaItems
            .AsNoTracking()
            .Include(m => m.Tags)
            .Where(m => m.Type == type);

        if (!string.IsNullOrWhiteSpace(tag))
        {
            var normalizedTag = tag.ToLower().Trim();
            query = query.Where(m => m.Tags.Any(t => t.NormalizedName == normalizedTag || t.Name.ToLower() == normalizedTag));
        }

        query = (sortBy?.ToLower()) switch
        {
            "rating" => query.OrderByDescending(m => m.Rating).ThenByDescending(m => m.HypeScore),
            "recientes" => query.OrderByDescending(m => m.ReleaseDate).ThenByDescending(m => m.HypeScore),
            "titulo" => query.OrderBy(m => m.Title),
            _ => query.OrderByDescending(m => m.HypeScore).ThenByDescending(m => m.Rating)
        };

        var totalCount = await query.CountAsync();
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (items, totalCount);
    }

    public async Task<IEnumerable<string>> GetTagsByTypeAsync(MediaType type)
    {
        return await _context.MediaItems
            .AsNoTracking()
            .Where(m => m.Type == type)
            .SelectMany(m => m.Tags.Select(t => t.Name))
            .Distinct()
            .OrderBy(name => name)
            .ToListAsync();
    }

    public async Task<IEnumerable<MediaItem>> GetSimilarMediaAsync(MediaItem currentMedia, int count)
    {
        var currentTagIds = currentMedia.Tags.Select(t => t.Id).ToList();

        var candidates = await _context.MediaItems
            .AsNoTracking()
            .Include(m => m.Tags)
            .Where(m => m.Id != currentMedia.Id)
            .ToListAsync();

        // Ordenar por afinidad: misma categoría + cantidad de etiquetas compartidas + HypeScore
        return candidates
            .Select(m => new
            {
                Item = m,
                SharedTags = m.Tags.Count(t => currentTagIds.Contains(t.Id)),
                SameTypeBonus = m.Type == currentMedia.Type ? 2 : 0
            })
            .OrderByDescending(x => x.SharedTags * 2 + x.SameTypeBonus)
            .ThenByDescending(x => x.Item.HypeScore)
            .Take(count)
            .Select(x => x.Item)
            .ToList();
    }

    public async Task<IEnumerable<MediaItem>> GetPersonalizedRecommendationsAsync(Guid userId, int count)
    {
        var userLibrary = await _context.LibraryItems
            .AsNoTracking()
            .Include(l => l.MediaItem)
                .ThenInclude(m => m.Tags)
            .Where(l => l.UserId == userId)
            .ToListAsync();

        if (!userLibrary.Any())
            return Enumerable.Empty<MediaItem>();

        var userMediaIds = userLibrary.Select(l => l.MediaItemId).ToHashSet();

        // Calcular peso de cada Tag y de cada MediaType según lo que el usuario ve y califica bien
        var tagWeights = new Dictionary<Guid, double>();
        var typeWeights = new Dictionary<MediaType, double>();

        foreach (var entry in userLibrary)
        {
            // Si el usuario calificó con 4 o menos, no recomendamos cosas similares a esa obra
            if (entry.UserRating.HasValue && entry.UserRating.Value <= 4)
                continue;

            double weight = entry.UserRating.HasValue
                ? (entry.UserRating.Value - 4.0) // De +1 a +6 para notas de 5 a 10
                : (entry.Status == LibraryStatus.Visto ? 3.0 : 2.0);

            typeWeights[entry.MediaItem.Type] = typeWeights.GetValueOrDefault(entry.MediaItem.Type) + (weight * 0.5);

            foreach (var tag in entry.MediaItem.Tags)
            {
                tagWeights[tag.Id] = tagWeights.GetValueOrDefault(tag.Id) + weight;
            }
        }

        if (!tagWeights.Any() && !typeWeights.Any())
            return Enumerable.Empty<MediaItem>();

        var candidates = await _context.MediaItems
            .AsNoTracking()
            .Include(m => m.Tags)
            .Where(m => !userMediaIds.Contains(m.Id))
            .ToListAsync();

        return candidates
            .Select(m =>
            {
                double tagScore = m.Tags.Sum(t => tagWeights.GetValueOrDefault(t.Id));
                double typeScore = typeWeights.GetValueOrDefault(m.Type);
                double totalAffinity = (tagScore * 3.0) + typeScore + (m.HypeScore * 0.1) + (m.Rating * 0.5);
                return new { Item = m, Affinity = totalAffinity, TagMatches = tagScore };
            })
            .Where(x => x.TagMatches > 0 || typeWeights.ContainsKey(x.Item.Type))
            .OrderByDescending(x => x.Affinity)
            .Take(count)
            .Select(x => x.Item)
            .ToList();
    }

    public async Task<IEnumerable<MediaItem>> SearchAsync(string query, int count)
    {
        if (string.IsNullOrWhiteSpace(query))
            return Enumerable.Empty<MediaItem>();

        var normalizedQuery = query.ToLower().Trim();
        var isSqlite = _context.Database.ProviderName == "Microsoft.EntityFrameworkCore.Sqlite";

        if (isSqlite)
        {
            return await _context.MediaItems
                .AsNoTracking()
                .Include(m => m.Tags)
                .Where(m => EF.Functions.Like(m.Title, $"%{normalizedQuery}%") ||
                            EF.Functions.Like(m.EnglishTitle, $"%{normalizedQuery}%") ||
                            EF.Functions.Like(m.OriginalTitle, $"%{normalizedQuery}%") ||
                            EF.Functions.Like(m.AlternativeTitles, $"%{normalizedQuery}%"))
                .OrderByDescending(m => m.HypeScore)
                .Take(count)
                .ToListAsync();
        }
        else
        {
            return await _context.MediaItems
                .AsNoTracking()
                .Include(m => m.Tags)
                .Where(m => EF.Functions.ILike(m.Title, $"%{normalizedQuery}%") ||
                            EF.Functions.ILike(m.EnglishTitle, $"%{normalizedQuery}%") ||
                            EF.Functions.ILike(m.OriginalTitle, $"%{normalizedQuery}%") ||
                            EF.Functions.ILike(m.AlternativeTitles, $"%{normalizedQuery}%"))
                .OrderByDescending(m => m.HypeScore)
                .Take(count)
                .ToListAsync();
        }
    }

    public async Task AddAsync(MediaItem item)
    {
        await _context.MediaItems.AddAsync(item);
    }

    public async Task UpdateAsync(MediaItem item)
    {
        _context.MediaItems.Update(item);
        await Task.CompletedTask;
    }

    public async Task<bool> SaveChangesAsync()
    {
        return await _context.SaveChangesAsync() > 0;
    }
}
