using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using AMRecomen.Domain.Entities;
using AMRecomen.Domain.Enums;
using AMRecomen.Domain.Interfaces;

namespace AMRecomen.Web.Pages;

public class DetalleModel : PageModel
{
    private readonly IMediaItemRepository _mediaRepository;
    private readonly ILibraryRepository _libraryRepository;

    public DetalleModel(IMediaItemRepository mediaRepository, ILibraryRepository libraryRepository)
    {
        _mediaRepository = mediaRepository;
        _libraryRepository = libraryRepository;
    }

    public MediaItem Media { get; set; } = null!;
    public IEnumerable<MediaItem> Recomendaciones { get; set; } = new List<MediaItem>();
    
    public LibraryItem? LibraryEntry { get; set; }

    public async Task<IActionResult> OnGetAsync(string slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return RedirectToPage("/Index");

        var mediaItem = await _mediaRepository.GetBySlugAsync(slug);
        if (mediaItem == null)
            return RedirectToPage("/Index"); // Opcionalmente NotFound(), pero redirigir es más amigable para el SEO

        Media = mediaItem;

        // Verificar estado de la biblioteca si el usuario inició sesión
        var claim = User.FindFirst(ClaimTypes.NameIdentifier);
        if (claim != null && Guid.TryParse(claim.Value, out var userId))
        {
            LibraryEntry = await _libraryRepository.GetItemAsync(userId, Media.Id);
        }

        // Cargar recomendaciones: los más populares de la misma categoría que no sean el actual
        var trendingOfSameType = await _mediaRepository.GetTopTrendingAsync(Media.Type, 5);
        Recomendaciones = trendingOfSameType
            .Where(m => m.Id != Media.Id)
            .Take(4)
            .ToList();

        return Page();
    }

    public async Task<IActionResult> OnPostAddToLibraryAsync(Guid mediaItemId, LibraryStatus status, string returnSlug)
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier);
        if (claim == null || !Guid.TryParse(claim.Value, out var userId))
            return RedirectToPage("/Auth/Login", new { returnUrl = Url.Page("/Detalle", new { slug = returnSlug }) });

        var libItem = new LibraryItem
        {
            UserId = userId,
            MediaItemId = mediaItemId,
            Status = status
        };

        await _libraryRepository.AddOrUpdateAsync(libItem);
        await _libraryRepository.SaveChangesAsync();

        return RedirectToPage("/Detalle", new { slug = returnSlug });
    }

    public async Task<IActionResult> OnPostRemoveFromLibraryAsync(Guid mediaItemId, string returnSlug)
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier);
        if (claim == null || !Guid.TryParse(claim.Value, out var userId))
            return RedirectToPage("/Auth/Login");

        var existing = await _libraryRepository.GetItemAsync(userId, mediaItemId);
        if (existing != null)
        {
            await _libraryRepository.RemoveAsync(existing);
            await _libraryRepository.SaveChangesAsync();
        }

        return RedirectToPage("/Detalle", new { slug = returnSlug });
    }

    public async Task<IActionResult> OnPostUpdateRatingAsync(Guid mediaItemId, int rating, string returnSlug)
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier);
        if (claim == null || !Guid.TryParse(claim.Value, out var userId))
            return RedirectToPage("/Auth/Login", new { returnUrl = Url.Page("/Detalle", new { slug = returnSlug }) });

        if (rating < 1 || rating > 10)
            return RedirectToPage("/Detalle", new { slug = returnSlug });

        var libItem = await _libraryRepository.GetItemAsync(userId, mediaItemId);
        if (libItem == null)
        {
            libItem = new LibraryItem
            {
                UserId = userId,
                MediaItemId = mediaItemId,
                Status = LibraryStatus.Visto,
                UserRating = rating
            };
        }
        else
        {
            libItem.UserRating = rating;
        }

        await _libraryRepository.AddOrUpdateAsync(libItem);
        await _libraryRepository.SaveChangesAsync();

        return RedirectToPage("/Detalle", new { slug = returnSlug });
    }
}
