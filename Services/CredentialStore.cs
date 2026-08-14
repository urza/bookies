using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Bookies.Models;
using Bookies.Options;
using Microsoft.Extensions.Options;

namespace Bookies.Services;

/// <summary>
/// Owns config.json. Once that file exists it is the only authority for credentials — the
/// BOOKIES__USERNAME / PASSWORD / APITOKEN variables seed it on first run and are ignored
/// afterwards, so a password changed in the UI is never reverted by a stale environment variable.
/// </summary>
public sealed class CredentialStore
{
    private const int Iterations = 600_000;
    private const string HashScheme = "pbkdf2-sha256";
    private const string TokenDerivationPrefix = "bookies-api-token:";

    // No 0/O/1/l/I: a generated password gets read off a terminal and typed into a phone.
    private const string PasswordAlphabet = "abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string _path;
    private readonly string _tempPath;
    private readonly ILogger<CredentialStore> _log;
    private readonly Lock _gate = new();
    private AppConfig _config = new();

    public CredentialStore(IOptions<AppOptions> options, ILogger<CredentialStore> log)
    {
        _log = log;
        var settings = options.Value;
        Directory.CreateDirectory(settings.DataDir);
        _path = Path.Combine(settings.DataDir, "config.json");
        _tempPath = _path + ".tmp";

        if (File.Exists(_path)) LoadExisting();
        else Bootstrap(settings);
    }

    private void LoadExisting()
    {
        var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_path), JsonOptions);
        if (config is null || string.IsNullOrEmpty(config.PasswordHash) || string.IsNullOrEmpty(config.ApiToken))
        {
            throw new InvalidOperationException(
                $"{_path} is incomplete. Delete it and restart to generate new credentials — " +
                "bookmarks.json is a separate file and will not be touched.");
        }

        _config = config;
        _log.LogInformation("Loaded credentials for '{Username}' from {Path}.", _config.Username, _path);
    }

    private void Bootstrap(AppOptions settings)
    {
        var wasGenerated = string.IsNullOrWhiteSpace(settings.Password);
        var password = wasGenerated ? GeneratePassword() : settings.Password!.Trim();
        var username = string.IsNullOrWhiteSpace(settings.Username) ? "admin" : settings.Username.Trim();
        var token = string.IsNullOrWhiteSpace(settings.ApiToken) ? DeriveToken(password) : settings.ApiToken.Trim();

        _config = new AppConfig
        {
            Username = username,
            PasswordHash = HashPassword(password),
            ApiToken = token,
            Created = DateTimeOffset.UtcNow,
        };
        Save();

        _log.LogWarning("First run — wrote {Path}", _path);
        _log.LogWarning("  username  : {Username}", username);
        if (wasGenerated) _log.LogWarning("  password  : {Password}   <- generated, shown only once", password);
        _log.LogWarning("  api token : {Token}", token);
        _log.LogWarning("Change the password and rotate the token any time from the Settings page.");
    }

    public string Username
    {
        get { lock (_gate) return _config.Username; }
    }

    public string ApiToken
    {
        get { lock (_gate) return _config.ApiToken; }
    }

    public bool VerifyLogin(string? username, string? password)
    {
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password)) return false;

        string expectedUser, hash;
        lock (_gate)
        {
            expectedUser = _config.Username;
            hash = _config.PasswordHash;
        }

        // Both sides evaluated before combining, so a wrong username doesn't return faster
        // than a wrong password.
        var userMatches = FixedTimeEquals(username, expectedUser);
        var passwordMatches = VerifyHash(password, hash);
        return userMatches && passwordMatches;
    }

    public bool VerifyToken(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        lock (_gate) return FixedTimeEquals(token, _config.ApiToken);
    }

    public void ChangePassword(string newPassword)
    {
        var hash = HashPassword(newPassword);
        lock (_gate)
        {
            _config.PasswordHash = hash;
            _config.Updated = DateTimeOffset.UtcNow;
            Save();
        }
        _log.LogInformation("Password changed.");
    }

    /// <summary>
    /// Replaces the token with 32 fresh random bytes — no relation to the password, unlike the
    /// value seeded on first run. Every bookmarklet and Shortcut must be updated afterwards.
    /// </summary>
    public string RegenerateApiToken()
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        lock (_gate)
        {
            _config.ApiToken = token;
            _config.Updated = DateTimeOffset.UtcNow;
            Save();
        }
        _log.LogWarning("API token regenerated — existing bookmarklets and shortcuts will stop working.");
        return token;
    }

    private void Save()
    {
        File.WriteAllText(_tempPath, JsonSerializer.Serialize(_config, JsonOptions));
        RestrictPermissions(_tempPath);
        File.Move(_tempPath, _path, overwrite: true);
    }

    /// <summary>Best effort 0600. Some mounts (Windows drives under WSL) don't support it.</summary>
    private void RestrictPermissions(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            _log.LogDebug(ex, "Could not set 0600 on {Path}; the filesystem may not support it.", path);
        }
    }

    private static string GeneratePassword() => RandomNumberGenerator.GetString(PasswordAlphabet, 16);

    private static string DeriveToken(string password) =>
        Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(TokenDerivationPrefix + password)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    public static string HashPassword(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);
        return $"{HashScheme}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    private bool VerifyHash(string password, string stored)
    {
        var parts = stored.Split('$');
        if (parts.Length != 4 || parts[0] != HashScheme || !int.TryParse(parts[1], out var iterations))
        {
            _log.LogError("Password hash in {Path} is malformed; nobody can log in. Delete the file and restart.", _path);
            return false;
        }

        byte[] salt, expected;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            expected = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException)
        {
            _log.LogError("Password hash in {Path} is not valid base64. Delete the file and restart.", _path);
            return false;
        }

        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }
}
