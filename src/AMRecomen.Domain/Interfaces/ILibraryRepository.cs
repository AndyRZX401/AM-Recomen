using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AMRecomen.Domain.Entities;

namespace AMRecomen.Domain.Interfaces;

public interface ILibraryRepository
{
    Task<IEnumerable<LibraryItem>> GetLibraryByUserAsync(Guid userId);
    Task<LibraryItem?> GetItemAsync(Guid userId, Guid mediaItemId);
    Task AddOrUpdateAsync(LibraryItem item);
    Task RemoveAsync(LibraryItem item);
    Task SaveChangesAsync();
}
