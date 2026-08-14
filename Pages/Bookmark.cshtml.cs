using Bookies.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bookies.Pages;

public sealed class BookmarkModel(BookmarkStore store) : PageModel
{
    public Models.Bookmark Item { get; private set; } = null!;

    public IActionResult OnGet(string id)
    {
        var bookmark = store.Get(id);

        // 404 rather than 403 for private links: an anonymous visitor shouldn't be able to
        // discover which ids exist.
        if (bookmark is null || (bookmark.Private && User.Identity?.IsAuthenticated != true)) return NotFound();

        Item = bookmark;
        return Page();
    }
}
