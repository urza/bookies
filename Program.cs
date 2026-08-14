using System.Net.Http.Headers;
using System.Threading.RateLimiting;
using Bookies;
using Bookies.Options;
using Bookies.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.Section));
builder.Services.Configure<AiOptions>(builder.Configuration.GetSection(AiOptions.Section));

var appOptions = builder.Configuration.GetSection(AppOptions.Section).Get<AppOptions>() ?? new AppOptions();
var aiOptions = builder.Configuration.GetSection(AiOptions.Section).Get<AiOptions>() ?? new AiOptions();
Directory.CreateDirectory(appOptions.DataDir);

// Cookie encryption keys have to outlive the container, or every restart logs you out.
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(appOptions.DataDir, "keys")))
    .SetApplicationName("bookies");

builder.Services.AddSingleton<BookmarkStore>();
builder.Services.AddSingleton<CredentialStore>();
builder.Services.AddSingleton<MetadataFetcher>();
builder.Services.AddSingleton<AiTagger>();
builder.Services.AddSingleton<BookmarkService>();
builder.Services.AddSingleton<ApiTokenFilter>();

builder.Services.AddHttpClient(MetadataFetcher.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(5);
    // Plenty of sites serve nothing useful to an unrecognised agent.
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36");
    client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml");
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
{
    AllowAutoRedirect = true,
    MaxAutomaticRedirections = 5,
});

builder.Services.AddHttpClient(AiTagger.HttpClientName, client =>
{
    // AiTagger applies the real deadline per call; this is only a backstop.
    client.Timeout = TimeSpan.FromMinutes(5);
    if (!string.IsNullOrWhiteSpace(aiOptions.ApiKey))
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", aiOptions.ApiKey);
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = ".bookies.auth";
        options.Cookie.HttpOnly = true;
        // Lax rather than Strict: the bookmarklet is a top-level GET navigation from another
        // site and has to arrive already logged in.
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/login";
        options.LogoutPath = "/logout";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            Window = TimeSpan.FromMinutes(1),
            PermitLimit = 20,
            QueueLimit = 0,
        }));
});

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AllowAnonymousToPage("/Index");
    options.Conventions.AllowAnonymousToPage("/Login");
    options.Conventions.AllowAnonymousToPage("/Logout");
    options.Conventions.AllowAnonymousToPage("/Error");
    options.Conventions.AllowAnonymousToPage("/Bookmark");
    options.Conventions.AddPageApplicationModelConvention("/Login",
        model => model.EndpointMetadata.Add(new EnableRateLimitingAttribute("login")));
}).AddMvcOptions(options =>
{
    // Title, description and tags are all genuinely optional. Without this, every non-nullable
    // string on a form model picks up an implicit [Required].
    options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true;
});

if (appOptions.TrustProxyHeaders)
{
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    });
}

var app = builder.Build();

// No HTTPS redirect or HSTS on purpose: this is expected to run over plain HTTP on a LAN, or
// behind a reverse proxy that terminates TLS itself.
if (appOptions.TrustProxyHeaders) app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment()) app.UseExceptionHandler("/Error");

app.UseStaticFiles();
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapRazorPages();
app.MapApi();
app.MapGet("/healthz", () => Results.Text("ok")).AllowAnonymous();

// Build both stores now so a corrupt data file or first-run credentials surface at startup
// rather than on whichever request happens to touch them first.
app.Services.GetRequiredService<CredentialStore>();
app.Services.GetRequiredService<BookmarkStore>();

app.Run();
