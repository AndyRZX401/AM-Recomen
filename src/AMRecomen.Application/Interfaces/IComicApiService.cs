using System.Collections.Generic;
using System.Threading.Tasks;
using AMRecomen.Application.Dtos;

namespace AMRecomen.Application.Interfaces;

public interface IComicApiService
{
    Task<IEnumerable<ExternalMediaResult>> SearchComicsAsync(string query);
    Task<IEnumerable<ExternalMediaResult>> GetTrendingComicsAsync(bool isManhwa);
    Task<ExternalMediaResult?> GetComicByIdAsync(string externalId);
}
