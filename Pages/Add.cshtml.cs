using Bookies.Models;
using Bookies.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Bookies.Pages;

public sealed class AddModel(BookmarkStore store, BookmarkService service, MetadataFetcher metadata) : PageModel
{
    [BindProperty]
    public BookmarkInput Input { get; set; } = new();

    public string? Error { get; private set; }
    public bool Saved { get; private set; }
    public bool TaggingEnabled => service.TaggingEnabled;

    public async Task<IActionResult> OnGetAsync(
        string? url, string? title, string? description, bool popup, CancellationToken ct)
    {
        Input.Popup = popup;
        Input.Title = title ?? "";
        Input.Description = description ?? "";

        if (!Bookmark.TryNormalizeUrl(url, out var normalized))
        {
            Input.Url = url ?? "";
            return Page();
        }

        Input.Url = normalized;

        // Already saved: edit it instead of creating a second copy of the same link.
        var existing = store.FindByUrl(normalized);
        if (existing is not null)
            return RedirectToPage("/Edit", new { id = existing.Id, popup = popup ? true : (bool?)null });

        if (Input.Title.Length == 0 || Input.Description.Length == 0)
        {
            var fetched = await metadata.FetchAsync(normalized, ct);
            if (fetched is not null)
            {
                if (Input.Title.Length == 0) Input.Title = fetched.Title;
                if (Input.Description.Length == 0) Input.Description = fetched.Description;
            }
        }

        return Page();
    }

    public IActionResult OnPost()
    {
        if (!Bookmark.TryNormalizeUrl(Input.Url, out var url))
        {
            Error = "That doesn't look like an http or https URL.";
            return Page();
        }

        var tags = Bookmark.ParseTags(Input.Tags);
        var existing = store.FindByUrl(url);

        if (existing is not null) store.Update(existing.Id, url, Input.Title, Input.Description, tags, Input.Private);
        else store.Add(url, Input.Title, Input.Description, tags, Input.Private);

        if (!Input.Popup) return RedirectToPage("/Index");

        Saved = true;
        return Page();
    }

    public async Task<IActionResult> OnPostSuggestTagsAsync([FromBody] SuggestTagsInput input, CancellationToken ct)
    {
        if (!Bookmark.TryNormalizeUrl(input.Url, out var url)) return new JsonResult(new { tags = Array.Empty<string>() });
        return new JsonResult(new { tags = await service.SuggestTagsAsync(url, input.Title, input.Description, ct) });
    }
}
