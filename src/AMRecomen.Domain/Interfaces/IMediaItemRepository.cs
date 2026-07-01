using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AMRecomen.Domain.Entities;
using AMRecomen.Domain.Enums;

namespace AMRecomen.Domain.Interfaces;

public interface IMediaItemRepository
{
    Task<MediaItem?> GetByIdAsync(Guid id);
    Task<MediaItem?> GetBySlugAsync(string slug);
    Task<MediaItem?> GetByExternalIdAsync(string externalId);
    Task<IEnumerable<MediaItem>> GetTopTrendingAsync(MediaType type, int count);
    Task<(IEnumerable<MediaItem> Items, int TotalCount)> GetPagedByTypeAsync(MediaType type, int page, int pageSize);
    Task<IEnumerable<MediaItem>> SearchAsync(string query, int count);
    Task AddAsync(MediaItem item);
    Task UpdateAsync(MediaItem item);
    Task<bool> SaveChangesAsync();
}
