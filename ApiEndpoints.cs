using System.Text.Json;
using Bookies.Models;
using Bookies.Services;
using Microsoft.Extensions.Primitives;

namespace Bookies;

/// <summary>
/// The machine-facing surface, used by the bookmarklet's silent path, the iOS Shortcut and curl.
/// These endpoints authenticate with the API token only — never the session cookie — so a GET
/// that creates a bookmark cannot be triggered by another site riding your browser session.
/// </summary>
public static class ApiEndpoints
{
    public static void MapApi(this IEndpointRouteBuilder routes)
    {
        var api = routes.MapGroup("/api").AddEndpointFilter<ApiTokenFilter>();

        // The one-action iOS Shortcut path: everything in the query string.
        api.MapGet("/add", async (
            HttpContext http, string? url, string? title, string? description, string? tags, bool? @private,
            BookmarkService service, CancellationToken ct) =>
        {
            var request = new CreateRequest(
                ResolveUrl(http.Request, url), title, description, Bookmark.ParseTags(tags), @private);
            return Respond(await service.CreateAsync(request, ct));
        });

        api.MapPost("/bookmarks", async (HttpContext http, BookmarkService service, CancellationToken ct) =>
        {
            var request = await ReadCreateRequestAsync(http.Request, ct);
            if (request is null)
                return Error("Could not read a bookmark from the request. Send JSON with a \"url\" field.");

            return Respond(await service.CreateAsync(request, ct));
        });

        api.MapGet("/bookmarks", (string? q, BookmarkStore store) =>
        {
            var results = store.Search(q, null, page: 1, pageSize: 200, includePrivate: true);
            return Results.Json(new { total = results.Total, items = results.Items.Select(Dto) });
        });

        api.MapGet("/suggest-tags", async (
            string? url, string? title, string? description, BookmarkService service, CancellationToken ct) =>
        {
            if (!Bookmark.TryNormalizeUrl(url, out var normalized)) return Error("A valid http(s) url is required.");
            var tags = await service.SuggestTagsAsync(normalized, title, description, ct);
            return Results.Json(new { enabled = service.TaggingEnabled, tags });
        });

        api.MapGet("/export", (BookmarkStore store) => Results.Text(store.ExportJson(), "application/json"));
    }

    /// <summary>
    /// iOS Shortcuts drops the shared URL into the query string without encoding it, so a link
    /// that carries its own ?a=1&amp;b=2 gets split across query keys and the bound value is a
    /// silent truncation. When that has happened — the bound value is a strict prefix of the raw
    /// remainder, and the shared link already had a query of its own — trust the raw text instead.
    /// This is why "url" must be the last parameter in the query string.
    /// </summary>
    private static string? ResolveUrl(HttpRequest request, string? bound)
    {
        if (string.IsNullOrWhiteSpace(bound) || !bound.Contains('?')) return bound;

        var query = request.QueryString.Value ?? "";
        var start = query.IndexOf("url=", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return bound;

        var remainder = Uri.UnescapeDataString(query[(start + "url=".Length)..]);
        return remainder.Length > bound.Length && remainder.StartsWith(bound, StringComparison.Ordinal)
            ? remainder
            : bound;
    }

    private static IResult Respond(CreateResult? result)
    {
        if (result is null) return Error("A valid http(s) url is required.");

        var dto = Dto(result.Bookmark);
        dto["created"] = result.Created;
        return Results.Json(dto);
    }

    private static Dictionary<string, object?> Dto(Bookmark bookmark) => new()
    {
        ["id"] = bookmark.Id,
        ["url"] = bookmark.Url,
        ["title"] = bookmark.Title,
        ["description"] = bookmark.Description,
        ["tags"] = bookmark.Tags,
        ["private"] = bookmark.Private,
        ["createdAt"] = bookmark.Created,
        ["updatedAt"] = bookmark.Updated,
    };

    private static IResult Error(string message) =>
        Results.Json(new { error = message }, statusCode: StatusCodes.Status400BadRequest);

    /// <summary>
    /// Deliberately does not use [FromBody] model binding. iOS Shortcuts can only attach a JSON
    /// body as a *file*, so the content type is whatever it feels like sending — read the bytes
    /// and try to make sense of them regardless.
    /// </summary>
    private static async Task<CreateRequest?> ReadCreateRequestAsync(HttpRequest request, CancellationToken ct)
    {
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(ct);

            foreach (var file in form.Files)
            {
                using var reader = new StreamReader(file.OpenReadStream());
                var fromFile = FromJson(await reader.ReadToEndAsync(ct));
                if (fromFile is not null) return fromFile;
            }

            var url = form["url"].ToString();
            if (string.IsNullOrWhiteSpace(url)) return null;

            return new CreateRequest(
                url,
                form["title"].ToString(),
                First(form["description"], form["notes"]),
                Bookmark.ParseTags(First(form["tags"], form["tag_names"])),
                ParseBool(form["private"].ToString()));
        }

        using var bodyReader = new StreamReader(request.Body);
        var body = (await bodyReader.ReadToEndAsync(ct)).Trim();
        return body.StartsWith('{') ? FromJson(body) : null;
    }

    /// <summary>Accepts Linkding's field names too, so shortcuts written for it mostly work.</summary>
    private static CreateRequest? FromJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            var fields = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in document.RootElement.EnumerateObject()) fields[property.Name] = property.Value;

            var url = Text(fields, "url");
            if (string.IsNullOrWhiteSpace(url)) return null;

            // Linkding-shaped clients send description *and* notes, and routinely leave description
            // an empty string — so fall back on blank, not merely on absent, or the text the user
            // actually selected is dropped. This is the rule the form path already uses.
            var description = Text(fields, "description");
            if (string.IsNullOrWhiteSpace(description)) description = Text(fields, "notes");

            // Everything is read before the document is disposed.
            return new CreateRequest(
                url,
                Text(fields, "title"),
                description,
                Tags(fields),
                Flag(fields, "private") ?? Flag(fields, "is_private"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(Dictionary<string, JsonElement> fields, string name) =>
        fields.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool? Flag(Dictionary<string, JsonElement> fields, string name)
    {
        if (!fields.TryGetValue(name, out var value)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => ParseBool(value.GetString()),
            _ => null,
        };
    }

    private static List<string>? Tags(Dictionary<string, JsonElement> fields)
    {
        foreach (var name in (string[])["tags", "tag_names"])
        {
            if (!fields.TryGetValue(name, out var value)) continue;

            if (value.ValueKind == JsonValueKind.Array)
            {
                return Bookmark.NormalizeTags(
                    value.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString() ?? ""));
            }

            if (value.ValueKind == JsonValueKind.String) return Bookmark.ParseTags(value.GetString());
        }

        return null;
    }

    private static bool? ParseBool(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "true" or "1" or "yes" or "on" => true,
        "false" or "0" or "no" or "off" => false,
        _ => null,
    };

    private static string? First(StringValues a, StringValues b)
    {
        var first = a.ToString();
        return string.IsNullOrWhiteSpace(first) ? b.ToString() : first;
    }
}

/// <summary>Rejects anything without a valid API token.</summary>
public sealed class ApiTokenFilter(CredentialStore credentials) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        if (!credentials.VerifyToken(ExtractToken(context.HttpContext.Request)))
        {
            return Results.Json(
                new { error = "Missing or invalid API token." },
                statusCode: StatusCodes.Status401Unauthorized);
        }

        return await next(context);
    }

    /// <summary>
    /// Header forms first, because that is what Linkding-shaped shortcuts already send; the query
    /// string form exists because it makes the iOS Shortcut a single action.
    /// </summary>
    private static string? ExtractToken(HttpRequest request)
    {
        var authorization = request.Headers.Authorization.ToString();
        foreach (var scheme in (string[])["Token ", "Bearer "])
        {
            if (authorization.StartsWith(scheme, StringComparison.OrdinalIgnoreCase))
                return authorization[scheme.Length..].Trim();
        }

        if (request.Headers.TryGetValue("X-Token", out var header) && !StringValues.IsNullOrEmpty(header))
            return header.ToString().Trim();

        if (request.Query.TryGetValue("token", out var query) && !StringValues.IsNullOrEmpty(query))
            return query.ToString().Trim();

        return null;
    }
}
