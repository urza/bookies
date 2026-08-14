using Bookies.Models;
using Bookies.Options;
using Bookies.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Bookies.Pages;

public sealed class IndexModel(BookmarkStore store, BookmarkService service, IOptions<AppOptions> options) : PageModel
{
    // Named PageNumber rather than Page because PageModel.Page() already exists.
    [BindProperty(SupportsGet = true, Name = "page")]
    public int PageNumber { get; set; } = 1;

    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Query { get; set; }

    [BindProperty(SupportsGet = true, Name = "tag")]
    public string? Tag { get; set; }

    /// <summary>Which bookmark is open for editing. Editing happens in the list, not on its own page.</summary>
    [BindProperty(SupportsGet = true, Name = "edit")]
    public string? EditId { get; set; }

    [BindProperty]
    public BookmarkInput Input { get; set; } = new();

    public PagedResult<Bookmark> Results { get; private set; } = new([], 0, 1, 1);
    public IReadOnlyList<TagCount> Tags { get; private set; } = [];
    public bool SignedIn { get; private set; }
    public string? Error { get; private set; }
    public bool TaggingEnabled => service.TaggingEnabled;

    public void OnGet()
    {
        LoadList();

        if (!SignedIn || EditId is not { Length: > 0 })
        {
            EditId = null;
            return;
        }

        var bookmark = store.Get(EditId);
        if (bookmark is null)
        {
            EditId = null;
            return;
        }

        Input = new BookmarkInput
        {
            Url = bookmark.Url,
            Title = bookmark.Title,
            Description = bookmark.Description,
            Tags = bookmark.TagString,
            Private = bookmark.Private,
        };
    }

    public IActionResult OnPostSave(string id)
    {
        if (NotSignedIn) return Challenge();
        if (store.Get(id) is null) return NotFound();

        if (!Bookmark.TryNormalizeUrl(Input.Url, out var url))
        {
            // Re-render with the form still open and whatever was typed still in it.
            Error = "That doesn't look like an http or https URL.";
            EditId = id;
            LoadList();
            return Page();
        }

        store.Update(id, url, Input.Title, Input.Description, Bookmark.ParseTags(Input.Tags), Input.Private);

        // Fragment lands you back on the row you just edited rather than at the top of the list.
        return RedirectToPage("/Index", null, CurrentRoute(), "b-" + id);
    }

    public IActionResult OnPostDelete(string id)
    {
        if (NotSignedIn) return Challenge();

        store.Delete(id);
        return RedirectToPage("/Index", CurrentRoute());
    }

    public async Task<IActionResult> OnPostSuggestTagsAsync([FromBody] SuggestTagsInput input, CancellationToken ct)
    {
        if (NotSignedIn) return Challenge();
        if (!Bookmark.TryNormalizeUrl(input.Url, out var url)) return new JsonResult(new { tags = Array.Empty<string>() });
        return new JsonResult(new { tags = await service.SuggestTagsAsync(url, input.Title, input.Description, ct) });
    }

    /// <summary>
    /// This page is AllowAnonymous so logged out visitors can read the public list — which exempts
    /// every handler on it, not just OnGet. The POST handlers mutate, so each one checks for itself.
    /// Antiforgery is no substitute: a token can be fetched from /login without logging in.
    /// </summary>
    private bool NotSignedIn => User.Identity?.IsAuthenticated != true;

    private void LoadList()
    {
        SignedIn = User.Identity?.IsAuthenticated == true;
        Results = store.Search(Query, Tag, PageNumber, options.Value.PageSize, includePrivate: SignedIn);
        Tags = store.TagCounts(includePrivate: SignedIn);
    }

    /// <summary>Carries the search, tag filter and page across a save so you land back where you were.</summary>
    private object CurrentRoute() => new
    {
        q = string.IsNullOrWhiteSpace(Query) ? null : Query,
        tag = string.IsNullOrWhiteSpace(Tag) ? null : Tag,
        page = PageNumber > 1 ? PageNumber : (int?)null,
    };
}
