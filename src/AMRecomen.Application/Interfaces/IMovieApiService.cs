using System.Collections.Generic;
using System.Threading.Tasks;
using AMRecomen.Application.Dtos;

namespace AMRecomen.Application.Interfaces;

public interface IMovieApiService
{
    Task<IEnumerable<ExternalMediaResult>> SearchMoviesAsync(string query);
    Task<IEnumerable<ExternalMediaResult>> GetTrendingMoviesAsync();
    Task<ExternalMediaResult?> GetMovieByIdAsync(string externalId);
    
    Task<IEnumerable<ExternalMediaResult>> SearchSeriesAsync(string query);
    Task<IEnumerable<ExternalMediaResult>> GetTrendingSeriesAsync();
    Task<ExternalMediaResult?> GetSeriesByIdAsync(string externalId);
}
