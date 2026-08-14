namespace Bookies.Models;

/// <summary>Persisted credentials. Lives in config.json and is the authority at runtime.</summary>
public sealed class AppConfig
{
    public string Username { get; set; } = "admin";
    public string PasswordHash { get; set; } = "";
    public string ApiToken { get; set; } = "";
    public DateTimeOffset Created { get; set; }
    public DateTimeOffset? Updated { get; set; }
}
