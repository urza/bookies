namespace Bookies.Options;

/// <summary>Bound from BOOKIES__* environment variables.</summary>
public sealed class AppOptions
{
    public const string Section = "Bookies";

    /// <summary>Seed only — used when config.json does not exist yet, ignored afterwards.</summary>
    public string? Username { get; set; }

    /// <summary>Seed only. When unset a random password is generated and logged once.</summary>
    public string? Password { get; set; }

    /// <summary>Seed only. When unset the token is derived from the seed password.</summary>
    public string? ApiToken { get; set; }

    public string DataDir { get; set; } = "/data";
    public string Title { get; set; } = "Bookies";
    public int PageSize { get; set; } = 50;

    /// <summary>
    /// Honour X-Forwarded-For / X-Forwarded-Proto. Only enable behind a reverse proxy you control:
    /// it means any caller can claim any client IP, which the login rate limiter partitions on.
    /// </summary>
    public bool TrustProxyHeaders { get; set; }
}
