using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Bookies.Services;

public sealed record PageMetadata(string Title, string Description);

/// <summary>
/// Pulls the title and description out of a page so the bookmarklet and the iOS Shortcut only
/// have to send a URL. Every failure here is non-fatal: the bookmark gets saved either way.
/// </summary>
public sealed partial class MetadataFetcher(IHttpClientFactory factory, ILogger<MetadataFetcher> log)
{
    public const string HttpClientName = "metadata";
    private const int MaxBytes = 512 * 1024;

    public async Task<PageMetadata?> FetchAsync(string url, CancellationToken ct)
    {
        try
        {
            var client = factory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                log.LogInformation("Metadata fetch for {Url} returned {Status}.", url, (int)response.StatusCode);
                return null;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is not null && !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                log.LogInformation("Skipping metadata for {Url}: content type is {Type}.", url, mediaType);
                return null;
            }

            var html = await ReadCappedAsync(response, ct);
            return new PageMetadata(ExtractTitle(html), ExtractDescription(html));
        }
        catch (Exception ex)
        {
            log.LogWarning("Could not fetch metadata for {Url}: {Message}", url, ex.Message);
            return null;
        }
    }

    /// <summary>Reads at most <see cref="MaxBytes"/> — the head is all we need, and a hostile
    /// or merely enormous page shouldn't be able to exhaust memory.</summary>
    private static async Task<string> ReadCappedAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[MaxBytes];
        var total = 0;

        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (read == 0) break;
            total += read;
        }

        var charset = response.Content.Headers.ContentType?.CharSet;
        var encoding = Encoding.UTF8;
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
                // Unknown or bogus charset label — UTF-8 is the right guess in 2026.
            }
        }

        return encoding.GetString(buffer, 0, total);
    }

    private static string ExtractTitle(string html)
    {
        var match = TitleRegex().Match(html);
        return match.Success ? Clean(match.Groups[1].Value) : "";
    }

    /// <summary>og:description first, then the plain meta description.</summary>
    private static string ExtractDescription(string html)
    {
        string? fallback = null;

        foreach (Match tag in MetaTagRegex().Matches(html))
        {
            var attributes = ParseAttributes(tag.Value);
            if (!attributes.TryGetValue("content", out var content) || string.IsNullOrWhiteSpace(content)) continue;

            var key = attributes.GetValueOrDefault("property") ?? attributes.GetValueOrDefault("name");
            if (key is null) continue;

            if (key.Equals("og:description", StringComparison.OrdinalIgnoreCase)) return Clean(content);
            if (key.Equals("description", StringComparison.OrdinalIgnoreCase)) fallback ??= Clean(content);
        }

        return fallback ?? "";
    }

    private static Dictionary<string, string> ParseAttributes(string tag)
    {
        var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match attribute in AttributeRegex().Matches(tag))
        {
            var value = attribute.Groups[3].Success ? attribute.Groups[3].Value
                : attribute.Groups[4].Success ? attribute.Groups[4].Value
                : attribute.Groups[5].Value;
            attributes.TryAdd(attribute.Groups[1].Value, value);
        }
        return attributes;
    }

    private static string Clean(string value) =>
        WhitespaceRegex().Replace(WebUtility.HtmlDecode(value), " ").Trim();

    [GeneratedRegex("<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex("<meta\\s+[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTagRegex();

    [GeneratedRegex("""([\w:-]+)\s*=\s*(?:"([^"]*)"|'([^']*)'|([^\s"'>]+))""", RegexOptions.IgnoreCase)]
    private static partial Regex AttributeRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRegex();
}
