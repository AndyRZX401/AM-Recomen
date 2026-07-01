using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using AMRecomen.Domain.Entities;

namespace AMRecomen.Domain.Interfaces;

public interface ITrendMetricRepository
{
    Task AddAsync(TrendMetric metric);
    Task<IEnumerable<TrendMetric>> GetByMediaItemIdAsync(Guid mediaItemId, int limit);
    Task<bool> SaveChangesAsync();
}
