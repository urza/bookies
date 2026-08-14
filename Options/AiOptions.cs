namespace Bookies.Options;

/// <summary>Bound from AI__* environment variables. Any OpenAI-compatible server works.</summary>
public sealed class AiOptions
{
    public const string Section = "AI";

    public bool Enabled { get; set; }

    /// <summary>Base URL including the version segment, e.g. http://192.168.1.50:11434/v1</summary>
    public string? BaseUrl { get; set; }

    public string? Model { get; set; }
    public string? ApiKey { get; set; }
    public int MaxTags { get; set; } = 5;
    public int TimeoutSeconds { get; set; } = 20;

    public bool IsUsable => Enabled && !string.IsNullOrWhiteSpace(BaseUrl) && !string.IsNullOrWhiteSpace(Model);
}
