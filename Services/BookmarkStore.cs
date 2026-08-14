using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Bookies.Models;
using Bookies.Options;
using Microsoft.Extensions.Options;

namespace Bookies.Services;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Number, int PageCount);

public sealed record TagCount(string Tag, int Count);

/// <summary>
/// The entire data layer. Everything lives in memory; every mutation rewrites bookmarks.json
/// atomically. Fine at personal scale — if this ever needs to hold a 50k import, this class is
/// the only one that should have to change, so keep file and JSON concerns inside it.
/// </summary>
public sealed class BookmarkStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // Crockford-ish base32: no i, l, o or u, so ids can't spell anything or be misread aloud.
    private const string IdAlphabet = "0123456789abcdefghjkmnpqrstvwxyz";
    private const int IdLength = 8;

    private readonly string _path;
    private readonly string _tempPath;
    private readonly string _backupPath;
    private readonly ILogger<BookmarkStore> _log;
    private readonly Lock _gate = new();
    private List<Bookmark> _items = [];

    public BookmarkStore(IOptions<AppOptions> options, ILogger<BookmarkStore> log)
    {
        _log = log;
        var dir = options.Value.DataDir;
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "bookmarks.json");
        _tempPath = _path + ".tmp";
        _backupPath = _path + ".bak";
        Load();
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            _log.LogInformation("No bookmarks file at {Path} yet, starting empty.", _path);
            return;
        }

        try
        {
            _items = JsonSerializer.Deserialize<List<Bookmark>>(File.ReadAllText(_path), JsonOptions) ?? [];
        }
        catch (JsonException ex)
        {
            // Refusing to start is the right call: starting empty would let the next save
            // overwrite a recoverable file with nothing.
            _log.LogCritical(ex, "{Path} is not valid JSON. The previous save is at {Backup}.", _path, _backupPath);
            throw;
        }

        _items.Sort(NewestFirst);
        _log.LogInformation("Loaded {Count} bookmarks from {Path}.", _items.Count, _path);
    }

    private static int NewestFirst(Bookmark a, Bookmark b) => b.Created.CompareTo(a.Created);

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    public Bookmark? Get(string id)
    {
        lock (_gate) return _items.FirstOrDefault(b => b.Id == id);
    }

    public Bookmark? FindByUrl(string url)
    {
        lock (_gate) return _items.FirstOrDefault(b => string.Equals(b.Url, url, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Free-text search across title, description, url and tags. All terms must match.</summary>
    public PagedResult<Bookmark> Search(string? query, string? tag, int page, int pageSize, bool includePrivate)
    {
        var terms = (query ?? "").Split(default(char[]),
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        lock (_gate)
        {
            IEnumerable<Bookmark> results = _items;

            if (!includePrivate) results = results.Where(b => !b.Private);

            if (!string.IsNullOrWhiteSpace(tag))
            {
                var wanted = tag.Trim().ToLowerInvariant();
                results = results.Where(b => b.Tags.Contains(wanted, StringComparer.Ordinal));
            }

            foreach (var term in terms) results = results.Where(b => Matches(b, term));

            var matched = results.ToList();
            var pageCount = Math.Max(1, (int)Math.Ceiling(matched.Count / (double)pageSize));
            var number = Math.Clamp(page, 1, pageCount);
            var items = matched.Skip((number - 1) * pageSize).Take(pageSize).ToList();
            return new PagedResult<Bookmark>(items, matched.Count, number, pageCount);
        }
    }

    private static bool Matches(Bookmark b, string term) =>
        b.Title.Contains(term, StringComparison.OrdinalIgnoreCase)
        || b.Description.Contains(term, StringComparison.OrdinalIgnoreCase)
        || b.Url.Contains(term, StringComparison.OrdinalIgnoreCase)
        || b.Tags.Any(t => t.Contains(term, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<TagCount> TagCounts(bool includePrivate)
    {
        lock (_gate)
        {
            return _items
                .Where(b => includePrivate || !b.Private)
                .SelectMany(b => b.Tags)
                .GroupBy(t => t, StringComparer.Ordinal)
                .Select(g => new TagCount(g.Key, g.Count()))
                .OrderByDescending(t => t.Count)
                .ThenBy(t => t.Tag, StringComparer.Ordinal)
                .ToList();
        }
    }

    public Bookmark Add(string url, string title, string description, IEnumerable<string>? tags, bool isPrivate)
    {
        lock (_gate)
        {
            var bookmark = new Bookmark
            {
                Id = NewId(),
                Url = url,
                Title = (title ?? "").Trim(),
                Description = (description ?? "").Trim(),
                Tags = Bookmark.NormalizeTags(tags),
                Private = isPrivate,
                Created = DateTimeOffset.UtcNow,
            };
            _items.Insert(0, bookmark);
            Save();
            return bookmark;
        }
    }

    public bool Update(string id, string url, string title, string description, IEnumerable<string>? tags, bool isPrivate)
    {
        lock (_gate)
        {
            var bookmark = _items.FirstOrDefault(b => b.Id == id);
            if (bookmark is null) return false;

            bookmark.Url = url;
            bookmark.Title = (title ?? "").Trim();
            bookmark.Description = (description ?? "").Trim();
            bookmark.Tags = Bookmark.NormalizeTags(tags);
            bookmark.Private = isPrivate;
            bookmark.Updated = DateTimeOffset.UtcNow;
            Save();
            return true;
        }
    }

    public bool Delete(string id)
    {
        lock (_gate)
        {
            var removed = _items.RemoveAll(b => b.Id == id) > 0;
            if (removed) Save();
            return removed;
        }
    }

    public string ExportJson()
    {
        lock (_gate) return JsonSerializer.Serialize(_items, JsonOptions);
    }

    /// <summary>Write to a temp file, keep the previous file as .bak, then move into place.</summary>
    private void Save()
    {
        var json = JsonSerializer.Serialize(_items, JsonOptions);
        File.WriteAllText(_tempPath, json);
        if (File.Exists(_path)) File.Copy(_path, _backupPath, overwrite: true);
        File.Move(_tempPath, _path, overwrite: true);
    }

    /// <summary>Caller must hold the lock — collision checking reads the live list.</summary>
    private string NewId()
    {
        while (true)
        {
            var bytes = RandomNumberGenerator.GetBytes(IdLength);
            var chars = new char[IdLength];
            for (var i = 0; i < IdLength; i++) chars[i] = IdAlphabet[bytes[i] & 31];
            var id = new string(chars);
            if (_items.All(b => b.Id != id)) return id;
        }
    }
}
