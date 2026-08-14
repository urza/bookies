using Bookies.Options;
using Bookies.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

namespace Bookies.Pages;

public sealed class SettingsModel(
    CredentialStore credentials,
    BookmarkStore store,
    BookmarkService service,
    IOptions<AppOptions> options) : PageModel
{
    [BindProperty] public string CurrentPassword { get; set; } = "";
    [BindProperty] public string NewPassword { get; set; } = "";
    [BindProperty] public string ConfirmPassword { get; set; } = "";

    [TempData] public string? Message { get; set; }
    [TempData] public string? Error { get; set; }

    public string Username => credentials.Username;
    public string ApiToken => credentials.ApiToken;

    /// <summary>Label the bookmarklet gets once it's sitting on the toolbar.</summary>
    public string SiteTitle => options.Value.Title;
    public int BookmarkCount => store.Count;
    public bool TaggingEnabled => service.TaggingEnabled;

    public string BaseUrl => $"{Request.Scheme}://{Request.Host}";
    public string ShortcutUrl => $"{BaseUrl}/api/add?token={ApiToken}&url=";
    public string ExportUrl => $"{BaseUrl}/api/export?token={ApiToken}";

    /// <summary>
    /// Selected text on the page becomes the description, the way Shaarli's bookmarklet does it.
    /// </summary>
    public string Bookmarklet =>
        // popup=true, not popup=1 — the model binder only accepts real boolean literals.
        "javascript:(function(){window.open('" + BaseUrl + "/add?popup=true&url='" +
        "+encodeURIComponent(location.href)+'&title='+encodeURIComponent(document.title)" +
        "+'&description='+encodeURIComponent(String(window.getSelection()||''))," +
        "'_blank','width=720,height=680');})();";

    public void OnGet()
    {
    }

    public IActionResult OnPostPassword()
    {
        if (!credentials.VerifyLogin(credentials.Username, CurrentPassword))
            Error = "Current password is wrong.";
        else if (NewPassword.Length < 8)
            Error = "New password must be at least 8 characters.";
        else if (NewPassword != ConfirmPassword)
            Error = "The two new passwords don't match.";
        else
        {
            credentials.ChangePassword(NewPassword);
            Message = "Password changed. The API token was not affected.";
        }

        return RedirectToPage();
    }

    public IActionResult OnPostToken()
    {
        credentials.RegenerateApiToken();
        Message = "New API token generated. Update your bookmarklet and iOS Shortcut — the old token no longer works.";
        return RedirectToPage();
    }
}
