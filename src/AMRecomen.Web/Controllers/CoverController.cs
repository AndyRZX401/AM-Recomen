using System;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;

namespace AMRecomen.Web.Controllers;

[ApiController]
[Route("api/[controller]")]
public class CoverController : ControllerBase
{
    private readonly IHttpClientFactory _httpClientFactory;

    public CoverController(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    [HttpGet]
    [ResponseCache(Duration = 604800, Location = ResponseCacheLocation.Any)]
    public async Task<IActionResult> GetCover([FromQuery] string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return Redirect("/images/placeholder.png");

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return Redirect("/images/placeholder.png");

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", "AMRecomen-App/2.0");
            request.Headers.Referrer = null;

            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if (!response.IsSuccessStatusCode)
                return Redirect("/images/placeholder.png");

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "image/jpeg";
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Response.Headers.CacheControl = "public, max-age=604800";
            return File(bytes, contentType);
        }
        catch
        {
            return Redirect("/images/placeholder.png");
        }
    }
}
