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

    public async Task<(IEnumerable<MediaItem> Items, int TotalCount)> GetPagedByTypeAsync(MediaType type, int page, int pageSize)
    {
        // Limitamos la consulta a un máximo de 30 elementos para indexar solo 30 por apartado
        var query = _context.MediaItems
            .AsNoTracking()
            .Include(m => m.Tags)
            .Where(m => m.Type == type)
            .OrderByDescending(m => m.HypeScore)
            .Take(30);

        var totalCount = await query.CountAsync();
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (items, totalCount);
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
