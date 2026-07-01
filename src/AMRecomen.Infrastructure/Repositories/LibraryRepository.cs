using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AMRecomen.Domain.Entities;
using AMRecomen.Domain.Interfaces;
using AMRecomen.Infrastructure.Data;

namespace AMRecomen.Infrastructure.Repositories;

public class LibraryRepository : ILibraryRepository
{
    private readonly AppDbContext _context;

    public LibraryRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task<IEnumerable<LibraryItem>> GetLibraryByUserAsync(Guid userId)
    {
        return await _context.LibraryItems
            .AsNoTracking()
            .Include(l => l.MediaItem)
            .Where(l => l.UserId == userId)
            .OrderByDescending(l => l.UpdatedAt)
            .ToListAsync();
    }

    public async Task<LibraryItem?> GetItemAsync(Guid userId, Guid mediaItemId)
    {
        // Buscar primero localmente en memoria en caso de adiciones consecutivas
        var local = _context.LibraryItems.Local
            .FirstOrDefault(l => l.UserId == userId && l.MediaItemId == mediaItemId);
        if (local != null)
            return local;

        return await _context.LibraryItems
            .FirstOrDefaultAsync(l => l.UserId == userId && l.MediaItemId == mediaItemId);
    }

    public async Task AddOrUpdateAsync(LibraryItem item)
    {
        var existing = await GetItemAsync(item.UserId, item.MediaItemId);
        if (existing == null)
        {
            item.AddedAt = DateTime.UtcNow;
            item.UpdatedAt = DateTime.UtcNow;
            await _context.LibraryItems.AddAsync(item);
        }
        else
        {
            existing.Status = item.Status;
            if (item.UserRating.HasValue)
            {
                existing.UserRating = item.UserRating.Value;
            }
            existing.UpdatedAt = DateTime.UtcNow;
            _context.LibraryItems.Update(existing);
        }
    }

    public async Task RemoveAsync(LibraryItem item)
    {
        var tracked = await GetItemAsync(item.UserId, item.MediaItemId);
        if (tracked != null)
        {
            _context.LibraryItems.Remove(tracked);
        }
    }

    public async Task SaveChangesAsync()
    {
        await _context.SaveChangesAsync();
    }
}
