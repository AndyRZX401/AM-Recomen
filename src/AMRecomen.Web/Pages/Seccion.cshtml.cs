using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using AMRecomen.Domain.Entities;
using AMRecomen.Domain.Enums;
using AMRecomen.Domain.Interfaces;

namespace AMRecomen.Web.Pages;

public class SeccionModel : PageModel
{
    private readonly IMediaItemRepository _mediaRepository;

    public SeccionModel(IMediaItemRepository mediaRepository)
    {
        _mediaRepository = mediaRepository;
    }

    public string Tipo { get; set; } = "Anime";
    public MediaType MediaTypeEnum { get; set; }
    public IEnumerable<MediaItem> Items { get; set; } = new List<MediaItem>();
    public IEnumerable<string> AvailableTags { get; set; } = new List<string>();
    
    public string? SelectedTag { get; set; }
    public string SortBy { get; set; } = "hype";

    public int PageNumber { get; set; }
    public int PageSize { get; set; } = 12;
    public int TotalItems { get; set; }
    public int TotalPages => (int)Math.Ceiling((double)TotalItems / PageSize);

    public async Task OnGetAsync(string? tipo, string? tag = null, string? orden = null, int p = 1)
    {
        Tipo = tipo ?? "Anime";
        SelectedTag = string.IsNullOrWhiteSpace(tag) ? null : tag.Trim();
        SortBy = string.IsNullOrWhiteSpace(orden) ? "hype" : orden.ToLower().Trim();
        
        // Mapear el string a MediaType
        MediaTypeEnum = Tipo.ToLower() switch
        {
            "anime" => MediaType.Anime,
            "manhwa" => MediaType.Manhwa,
            "manhua" => MediaType.Manhua,
            "pelicula" => MediaType.Pelicula,
            "serie" => MediaType.Serie,
            _ => MediaType.Anime
        };

        // Normalizar nombre de tipo para la UI
        Tipo = MediaTypeEnum switch
        {
            MediaType.Anime => "Anime",
            MediaType.Manhwa => "Manhwa",
            MediaType.Manhua => "Manhua",
            MediaType.Pelicula => "Pelicula",
            MediaType.Serie => "Serie",
            _ => "Anime"
        };

        PageNumber = p < 1 ? 1 : p;

        // Cargar etiquetas disponibles para filtros de esta sección
        AvailableTags = await _mediaRepository.GetTagsByTypeAsync(MediaTypeEnum);

        // Cargar elementos paginados y filtrados
        var result = await _mediaRepository.GetPagedByTypeAsync(MediaTypeEnum, PageNumber, PageSize, SelectedTag, SortBy);
        Items = result.Items;
        TotalItems = result.TotalCount;
    }
}
