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
    public string ActiveTab { get; set; } = "por-ver";

    public async Task<IActionResult> OnGetAsync(string? tab = null)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
            return RedirectToPage("/Auth/Login");

        ActiveTab = NormalizeTab(tab);
        LibraryItems = await _libraryRepository.GetLibraryByUserAsync(userId);
        return Page();
    }

    public async Task<IActionResult> OnPostRemoveAsync(Guid mediaItemId, string? tab = null)
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

        return RedirectToPage(new { tab = NormalizeTab(tab) });
    }

    public async Task<IActionResult> OnPostUpdateStatusAsync(Guid mediaItemId, LibraryStatus status, string? tab = null)
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

        return RedirectToPage(new { tab = NormalizeTab(tab) });
    }

    private static string NormalizeTab(string? tab)
    {
        return (tab?.ToLower().Trim()) switch
        {
            "visto" => "visto",
            "valorados" => "valorados",
            _ => "por-ver"
        };
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
