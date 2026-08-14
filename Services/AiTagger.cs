using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Bookies.Models;
using Bookies.Options;
using Microsoft.Extensions.Options;

namespace Bookies.Services;

/// <summary>
/// Asks a local model to suggest tags over the OpenAI-compatible chat completions API, so Ollama,
/// LM Studio, llama.cpp, vLLM and LiteLLM all work through the same code path.
/// Tagging is never allowed to fail or delay a save: on any error this returns no tags.
/// </summary>
public sealed partial class AiTagger(
    IHttpClientFactory factory,
    IOptions<AiOptions> options,
    ILogger<AiTagger> log)
{
    public const string HttpClientName = "ai";
    private const int MaxTagLength = 32;

    public bool Enabled => options.Value.IsUsable;

    public async Task<List<string>> SuggestTagsAsync(
        string url, string title, string description, IReadOnlyList<string> vocabulary, CancellationToken ct)
    {
        var settings = options.Value;
        if (!settings.IsUsable) return [];

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));

            var request = new
            {
                model = settings.Model,
                temperature = 0.2,
                max_tokens = 300,
                stream = false,
                messages = new[]
                {
                    new { role = "system", content = SystemPrompt(settings.MaxTags) },
                    new { role = "user", content = UserPrompt(url, title, description, vocabulary) },
                },
            };

            var client = factory.CreateClient(HttpClientName);
            var endpoint = $"{settings.BaseUrl!.TrimEnd('/')}/chat/completions";

            using var response = await client.PostAsJsonAsync(endpoint, request, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(timeout.Token);
                log.LogWarning("Tag suggestion failed: {Status} {Body}", (int)response.StatusCode, Truncate(body, 300));
                return [];
            }

            var payload = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
            var content = payload
                .GetProperty("choices")[0]
                .GetProperty("message")
                .GetProperty("content")
                .GetString();

            var tags = ParseTags(content, settings.MaxTags);
            log.LogInformation("Suggested {Count} tags for {Url}.", tags.Count, url);
            return tags;
        }
        catch (Exception ex)
        {
            log.LogWarning("Tag suggestion failed for {Url}: {Message}", url, ex.Message);
            return [];
        }
    }

    private static string SystemPrompt(int maxTags) =>
        $"You tag bookmarks for a personal link archive. Reply with ONLY a comma-separated list of " +
        $"1 to {maxTags} short lowercase tags. No explanation, no markdown, no numbering, no quotes, " +
        "no other text.";

    private static string UserPrompt(string url, string title, string description, IReadOnlyList<string> vocabulary)
    {
        var prompt = $"URL: {url}\nTitle: {title}\nDescription: {Truncate(description, 1000)}";

        // Reusing tags the archive already has matters more than inventing better new ones.
        if (vocabulary.Count > 0)
            prompt += $"\n\nPrefer these existing tags where they fit: {string.Join(", ", vocabulary)}";

        return prompt + "\n\nTags:";
    }

    /// <summary>
    /// Models ignore output instructions in creative ways — reasoning models emit think blocks,
    /// chat models wrap lists in code fences or prefix them with "Tags:". Salvage what we can.
    /// </summary>
    internal static List<string> ParseTags(string? content, int maxTags)
    {
        if (string.IsNullOrWhiteSpace(content)) return [];

        var text = ThinkBlockRegex().Replace(content, "").Replace("`", "").Trim();

        // Reasoning models sometimes leave an unterminated think block; keep only what follows.
        var lastOpen = text.LastIndexOf("<think>", StringComparison.OrdinalIgnoreCase);
        if (lastOpen >= 0) text = text[(lastOpen + "<think>".Length)..];

        text = LeadingLabelRegex().Replace(text.Trim(), "").Trim();
        if (text.Length == 0) return [];

        List<string> candidates;
        if (text.StartsWith('['))
        {
            try
            {
                candidates = JsonSerializer.Deserialize<List<string>>(text) ?? [];
            }
            catch (JsonException)
            {
                candidates = [.. text.Trim('[', ']').Split(',')];
            }
        }
        else
        {
            // If it rambled over several lines, the tags are on the last line that looks like a list.
            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var best = lines.LastOrDefault(l => l.Contains(',')) ?? lines.LastOrDefault() ?? "";
            candidates = [.. best.Split(',')];
        }

        return Bookmark
            .NormalizeTags(candidates.Select(c => c.Trim().Trim('"', '\'', '#', '-', '*', '.')))
            .Where(t => t.Length <= MaxTagLength)
            .Take(maxTags)
            .ToList();
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max];

    [GeneratedRegex("<think>.*?</think>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ThinkBlockRegex();

    [GeneratedRegex(@"^\s*tags\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingLabelRegex();
}
