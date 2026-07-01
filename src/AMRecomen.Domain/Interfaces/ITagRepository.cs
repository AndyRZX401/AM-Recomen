using System.Collections.Generic;
using System.Threading.Tasks;
using AMRecomen.Domain.Entities;

namespace AMRecomen.Domain.Interfaces;

public interface ITagRepository
{
    Task<Tag?> GetByNameAsync(string name);
    Task<Tag> GetOrCreateAsync(string name);
    Task<IEnumerable<Tag>> GetAllAsync();
    Task<bool> SaveChangesAsync();
}
