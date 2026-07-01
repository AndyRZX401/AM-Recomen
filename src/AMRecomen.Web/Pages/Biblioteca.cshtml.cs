using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using AMRecomen.Domain.Entities;
using AMRecomen.Domain.Enums;
using AMRecomen.Domain.Interfaces;

namespace AMRecomen.Web.Pages;

[Authorize]
public class BibliotecaModel : PageModel
{
    private readonly ILibraryRepository _libraryRepository;

    public BibliotecaModel(ILibraryRepository libraryRepository)
    {
        _libraryRepository = libraryRepository;
    }

    public IEnumerable<LibraryItem> LibraryItems { get; set; } = new List<LibraryItem>();

    public async Task<IActionResult> OnGetAsync()
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return RedirectToPage("/Auth/Login");

        LibraryItems = await _libraryRepository.GetLibraryByUserAsync(userId);
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveAsync(Guid mediaItemId)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Challenge();

        var existing = await _libraryRepository.GetItemAsync(userId, mediaItemId);
        if (existing != null)
        {
            await _libraryRepository.RemoveAsync(existing);
            await _libraryRepository.SaveChangesAsync();
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostUpdateStatusAsync(Guid mediaItemId, LibraryStatus status)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return Challenge();

        var existing = await _libraryRepository.GetItemAsync(userId, mediaItemId);
        if (existing != null)
        {
            existing.Status = status;
            await _libraryRepository.AddOrUpdateAsync(existing);
            await _libraryRepository.SaveChangesAsync();
        }

        return RedirectToPage();
    }

    private Guid GetUserId()
    {
        var claim = User.FindFirst(ClaimTypes.NameIdentifier);
        if (claim != null && Guid.TryParse(claim.Value, out var id))
        {
            return id;
        }
        return Guid.Empty;
    }
}
