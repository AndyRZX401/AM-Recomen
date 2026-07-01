using System.Collections.Generic;
using System.Threading.Tasks;
using AMRecomen.Application.Dtos;

namespace AMRecomen.Application.Interfaces;

public interface IAnimeApiService
{
    Task<IEnumerable<ExternalMediaResult>> SearchAnimeAsync(string query);
    Task<IEnumerable<ExternalMediaResult>> GetTrendingAnimeAsync();
    Task<ExternalMediaResult?> GetAnimeByIdAsync(string externalId);
}
