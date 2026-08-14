using Bookies.Models;
using Bookies.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bookies.Pages;

public sealed class EditModel(BookmarkStore store, BookmarkService service) : PageModel
{
    [BindProperty]
    public BookmarkInput Input { get; set; } = new();

    public string Id { get; private set; } = "";
    public string? Error { get; private set; }
    public bool Saved { get; private set; }
    public bool TaggingEnabled => service.TaggingEnabled;

    public IActionResult OnGet(string id, bool popup)
    {
        var bookmark = store.Get(id);
        if (bookmark is null) return NotFound();

        Id = id;
        Input = new BookmarkInput
        {
            Url = bookmark.Url,
            Title = bookmark.Title,
            Description = bookmark.Description,
            Tags = bookmark.TagString,
            Private = bookmark.Private,
            Popup = popup,
        };
        return Page();
    }

    public IActionResult OnPost(string id)
    {
        Id = id;
        if (store.Get(id) is null) return NotFound();

        if (!Bookmark.TryNormalizeUrl(Input.Url, out var url))
        {
            Error = "That doesn't look like an http or https URL.";
            return Page();
        }

        store.Update(id, url, Input.Title, Input.Description, Bookmark.ParseTags(Input.Tags), Input.Private);

        if (!Input.Popup) return RedirectToPage("/Index");

        Saved = true;
        return Page();
    }

    public IActionResult OnPostDelete(string id)
    {
        store.Delete(id);
        return RedirectToPage("/Index");
    }

    public async Task<IActionResult> OnPostSuggestTagsAsync([FromBody] SuggestTagsInput input, CancellationToken ct)
    {
        if (!Bookmark.TryNormalizeUrl(input.Url, out var url)) return new JsonResult(new { tags = Array.Empty<string>() });
        return new JsonResult(new { tags = await service.SuggestTagsAsync(url, input.Title, input.Description, ct) });
    }
}
