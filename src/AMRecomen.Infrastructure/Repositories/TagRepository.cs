using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AMRecomen.Domain.Entities;
using AMRecomen.Domain.Interfaces;
using AMRecomen.Infrastructure.Data;

namespace AMRecomen.Infrastructure.Repositories;

public class TagRepository : ITagRepository
{
    private readonly AppDbContext _context;

    public TagRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task<Tag?> GetByNameAsync(string name)
    {
        var normalized = name.ToLower().Trim();
        var local = _context.Tags.Local.FirstOrDefault(t => t.NormalizedName == normalized);
        if (local != null)
            return local;

        return await _context.Tags.FirstOrDefaultAsync(t => t.NormalizedName == normalized);
    }

    public async Task<Tag> GetOrCreateAsync(string name)
    {
        var normalized = name.ToLower().Trim();
        var existing = await GetByNameAsync(name);
        if (existing != null)
            return existing;

        var newTag = new Tag
        {
            Name = name.Trim(),
            NormalizedName = normalized
        };

        await _context.Tags.AddAsync(newTag);
        await _context.SaveChangesAsync(); // Guardamos de inmediato para evitar colisiones en llamadas paralelas
        return newTag;
    }

    public async Task<IEnumerable<Tag>> GetAllAsync()
    {
        return await _context.Tags.AsNoTracking().ToListAsync();
    }

    public async Task<bool> SaveChangesAsync()
    {
        return await _context.SaveChangesAsync() > 0;
    }
}
