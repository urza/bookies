namespace Bookies.Models;

/// <summary>A single saved link. <see cref="Id"/> is the permalink and is never reused.</summary>
public sealed class Bookmark
{
    public required string Id { get; init; }
    public required string Url { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Tags { get; set; } = [];
    public bool Private { get; set; } = true;
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset? Updated { get; set; }

    /// <summary>Host without "www.", for display under the title.</summary>
    public string Host =>
        Uri.TryCreate(Url, UriKind.Absolute, out var uri)
            ? uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host
            : Url;

    /// <summary>Title if it has one, otherwise something readable to click on.</summary>
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? Url : Title;

    /// <summary>
    /// Accepts only http and https. A bare "example.com" is upgraded to https.
    /// Everything else — javascript:, data:, file: — is rejected, since these strings end up
    /// in href attributes.
    /// </summary>
    public static bool TryNormalizeUrl(string? input, out string url)
    {
        url = "";
        if (string.IsNullOrWhiteSpace(input)) return false;
        var candidate = input.Trim();

        if (Uri.TryCreate(candidate, UriKind.Absolute, out var parsed))
        {
            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        }
        else
        {
            // No scheme at all: assume https rather than rejecting what the user typed.
            if (!Uri.TryCreate("https://" + candidate, UriKind.Absolute, out parsed)) return false;
            if (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps) return false;
        }

        if (string.IsNullOrEmpty(parsed.Host)) return false;
        url = parsed.AbsoluteUri;
        return true;
    }

    /// <summary>
    /// Splits a user-typed tag string. Commas win when present, so "web dev, ai" gives two tags;
    /// otherwise whitespace splits, matching Shaarli's habit of space-separated tags.
    /// </summary>
    public static List<string> ParseTags(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return [];
        var parts = input.Contains(',')
            ? input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : input.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return NormalizeTags(parts);
    }

    /// <summary>Lowercased, trimmed, deduped, sorted — so comparison is always string equality.</summary>
    public static List<string> NormalizeTags(IEnumerable<string>? tags) =>
        (tags ?? [])
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();

    /// <summary>Tags as the single string the edit form shows.</summary>
    public string TagString => string.Join(", ", Tags);
}
