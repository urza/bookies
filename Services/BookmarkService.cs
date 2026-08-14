using Bookies.Models;

namespace Bookies.Services;

public sealed record CreateRequest(
    string? Url,
    string? Title = null,
    string? Description = null,
    IEnumerable<string>? Tags = null,
    bool? Private = null);

public sealed record CreateResult(Bookmark Bookmark, bool Created);

/// <summary>
/// The shared create pipeline: normalise, dedupe by URL, fill in missing metadata, tag.
/// Used by both API entry points, which is why it doesn't live in a page handler.
/// </summary>
public sealed class BookmarkService(BookmarkStore store, MetadataFetcher metadata, AiTagger tagger)
{
    private const int VocabularySize = 60;

    /// <summary>Returns null when the URL is missing or not http/https.</summary>
    public async Task<CreateResult?> CreateAsync(CreateRequest request, CancellationToken ct)
    {
        if (!Bookmark.TryNormalizeUrl(request.Url, out var url)) return null;

        // Idempotent by URL, so double-tapping the iOS Shortcut can't produce duplicates.
        var existing = store.FindByUrl(url);
        if (existing is not null) return new CreateResult(existing, false);

        var title = (request.Title ?? "").Trim();
        var description = (request.Description ?? "").Trim();

        if (title.Length == 0 || description.Length == 0)
        {
            var fetched = await metadata.FetchAsync(url, ct);
            if (fetched is not null)
            {
                if (title.Length == 0) title = fetched.Title;
                if (description.Length == 0) description = fetched.Description;
            }
        }

        var tags = Bookmark.NormalizeTags(request.Tags);
        if (tags.Count == 0 && tagger.Enabled)
            tags = await tagger.SuggestTagsAsync(url, title, description, Vocabulary(), ct);

        return new CreateResult(store.Add(url, title, description, tags, request.Private ?? true), true);
    }

    /// <summary>Tag suggestions for a page that hasn't been saved yet.</summary>
    public Task<List<string>> SuggestTagsAsync(string url, string? title, string? description, CancellationToken ct) =>
        tagger.Enabled
            ? tagger.SuggestTagsAsync(url, title ?? "", description ?? "", Vocabulary(), ct)
            : Task.FromResult(new List<string>());

    public bool TaggingEnabled => tagger.Enabled;

    /// <summary>The most-used existing tags, so the model reuses your vocabulary.</summary>
    private List<string> Vocabulary() =>
        store.TagCounts(includePrivate: true).Take(VocabularySize).Select(t => t.Tag).ToList();
}
