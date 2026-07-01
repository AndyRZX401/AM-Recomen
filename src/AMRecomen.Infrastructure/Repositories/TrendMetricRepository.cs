using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using AMRecomen.Domain.Entities;
using AMRecomen.Domain.Interfaces;
using AMRecomen.Infrastructure.Data;

namespace AMRecomen.Infrastructure.Repositories;

public class TrendMetricRepository : ITrendMetricRepository
{
    private readonly AppDbContext _context;

    public TrendMetricRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task AddAsync(TrendMetric metric)
    {
        await _context.TrendMetrics.AddAsync(metric);
    }

    public async Task<IEnumerable<TrendMetric>> GetByMediaItemIdAsync(Guid mediaItemId, int limit)
    {
        return await _context.TrendMetrics
            .Where(t => t.MediaItemId == mediaItemId)
            .OrderByDescending(t => t.RecordedAt)
            .Take(limit)
            .ToListAsync();
    }

    public async Task<bool> SaveChangesAsync()
    {
        return await _context.SaveChangesAsync() > 0;
    }
}
