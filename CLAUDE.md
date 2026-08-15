# Bookies

A self-hosted, single-user bookmark manager in the spirit of [Shaarli](https://github.com/shaarli/Shaarli).
ASP.NET Core 10, one JSON file for storage, one Docker container.

The project is named **Bookies**: root namespace `Bookies`, assembly `Bookies`, Docker image and
compose service `bookies`.

**Simplicity is the primary design goal of this project.** When in doubt, choose the boring
option: fewer files, fewer dependencies, fewer abstractions. No repository interfaces with a
single implementation, no MediatR, no AutoMapper, no JS framework, no build step for the
frontend. If a feature can't be explained in two sentences, it probably doesn't belong.

## Stack

- **ASP.NET Core 10** — Razor Pages for the UI, minimal APIs for the machine endpoints.
- **Storage**: plain JSON files on disk — `bookmarks.json` plus a small `config.json` for
  credentials. No database, no ORM, no migrations.
- **Frontend**: server-rendered HTML + one hand-written `style.css`. A few dozen lines of
  vanilla JS at most (tag suggestion), and the app must remain fully usable without it.
- **Dependencies**: aim for zero NuGet packages beyond the framework.

## Features (v1 scope)

- Add / view / edit / delete bookmarks: `url`, `title`, `description`, `tags`, `private` flag.
- Full-text search across title/description/url, plus filter by tag. Paged list, newest first.
- Tag list / cloud for navigation.
- Per-bookmark public/private flag. Logged out visitors see only public bookmarks; logged in
  sees everything. There is no separate "public view" page — same page, filtered query.
- Bookmarklet target: a prefilled add form.
- Token API for the iOS Shortcut.
- Server-side metadata fetch: fills title/description from the page when not supplied.
- Optional AI auto-tagging against a local OpenAI-compatible endpoint.
- JSON export for backup.

Explicitly **out** of v1: multi-user, RSS/Atom, Netscape HTML import/export, link archiving,
thumbnails, markdown rendering, sub-second search indexes.

## Architecture

```
Bookies.csproj              single project, repo root
Program.cs                  composition root: config, DI, auth, pipeline
ApiEndpoints.cs             the /api group and its token filter
Models/Bookmark.cs          the record, plus url and tag normalisation helpers
Models/BookmarkInput.cs     shared binding target for the add and edit forms
Models/AppConfig.cs         username, password hash, api token — persisted
Options/AppOptions.cs       BOOKIES__* config
Options/AiOptions.cs        AI__* config
Services/BookmarkStore.cs   the whole data layer
Services/CredentialStore.cs config.json, password hashing, token rotation
Services/MetadataFetcher.cs
Services/AiTagger.cs
Services/BookmarkService.cs the shared create pipeline used by both API entry points
Pages/                      Index, Add, Edit, Bookmark (permalink), Login, Logout, Settings
Pages/Shared/               _Layout, _BookmarkItem, _BookmarkFields
wwwroot/style.css
wwwroot/favicon.svg        the icon; favicon.ico and apple-touch-icon.png are rasterised from it
Dockerfile / .dockerignore / docker-compose.yml
```

### Data model

```csharp
public sealed class Bookmark
{
    public required string Id { get; init; }          // 8-char base32, the permalink
    public required string Url { get; set; }
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public List<string> Tags { get; set; } = [];      // lowercase, trimmed, deduped, sorted
    public bool Private { get; set; } = true;         // private by default
    public DateTimeOffset Created { get; init; }
    public DateTimeOffset? Updated { get; set; }
}
```

`Id` is generated once and is the permalink — never reuse or renumber. Tags are normalised on
write (lowercase, trim, drop empties, dedupe) so comparison is always plain string equality.
`Bookmark` also owns `TryNormalizeUrl` and `ParseTags`, so every entry point — pages, API, AI
output — goes through the same validation rather than growing its own.

### BookmarkStore

The entire persistence layer, one class:

- Loads `{DataDir}/bookmarks.json` into a `List<Bookmark>` at startup. Missing file = empty list.
- All reads serve from that in-memory list. All mutations take a `lock` and then save.
- **Saves are atomic**: serialise to `bookmarks.json.tmp`, `File.Move(tmp, real, overwrite: true)`.
  Never write the live file in place — a crash mid-write must not be able to truncate the data.
- Keeps one rolling backup (`bookmarks.json.bak`) from the previous save.
- Search is a `LINQ Where` over the list with `StringComparison.OrdinalIgnoreCase`. That is fast
  enough for tens of thousands of entries; do not add an index.

Known trade-off, accepted deliberately: every save rewrites the whole file and the entire
dataset is resident in memory. Fine at personal scale. If this ever needs to hold a 50k-bookmark
import, the migration is to SQLite and `BookmarkStore` is the only class that should change —
so keep file I/O and JSON concerns from leaking outside it.

### Auth

Single user. Credentials live in `{DataDir}/config.json`, which is the **sole authority** at
runtime — env vars only seed it on first run (see Bootstrap). This is what makes changing the
password and rotating the token from the UI possible; an env var couldn't be updated by the app.

```json
{
  "username": "urza",
  "passwordHash": "pbkdf2-sha256$600000$<b64salt>$<b64hash>",
  "apiToken": "3Qk7...",
  "created": "2026-07-30T12:00:00Z",
  "updated": "2026-07-30T12:00:00Z"
}
```

`CredentialStore` owns this file. Same atomic write as `BookmarkStore` (tmp + move), and
`File.SetUnixFileMode(..., UserRead | UserWrite)` so it lands as `0600`.

**Password** → cookie session for the web UI.

- Hashing is PBKDF2-HMAC-SHA256 via `Rfc2898DeriveBytes.Pbkdf2` — in the framework, no NuGet.
  16-byte random salt, 32-byte output, 600,000 iterations. Store the whole thing as the single
  PHC-style string above so the parameters travel with the hash. Verify with
  `CryptographicOperations.FixedTimeEquals`.
- Cookie: sliding expiration ~30 days, `SameSite=Lax` (Lax is required — the bookmarklet is a
  top-level GET navigation and must carry the cookie), `CookieSecurePolicy.SameAsRequest` so
  plain HTTP on the LAN works.
- Fixed-window rate limiter on the login endpoint. A handful of attempts per minute.

**API token** → the `/api/*` endpoints. Accepted from any of: `Authorization: Token <t>`,
`Authorization: Bearer <t>`, `X-Token: <t>` header, or `?token=<t>` query string. The query
string form exists because iOS Shortcuts makes it trivial; the header forms exist because that's
what the Linkding-style shortcuts already send.

- Its initial value is derived from the initial password:
  `Base64Url(SHA256("bookmarks-api-token:" + password))`. From then on the stored value is what
  counts — changing the password does **not** change the token, and rotating the token does not
  touch the password. The two are independent once written.
- **Settings has a "generate new token" button**: 32 bytes from `RandomNumberGenerator`,
  base64url-encoded, written to `config.json`, no relation to the password. This invalidates the
  old token immediately, so the page must warn that every bookmarklet and Shortcut needs updating.
- Compare tokens with `FixedTimeEquals` too. Never log the token except at first-run bootstrap.

**Bootstrap** — on startup, if `config.json` is missing:

- `username` ← `BOOKIES__USERNAME`, default `admin`.
- `password` ← `BOOKIES__PASSWORD` if set; otherwise generate a random 16-character one.
- `apiToken` ← `BOOKIES__APITOKEN` if set; otherwise derive from the password as above.
- Write the file, then log the token, and the password too if it was generated — clearly framed as
  first-run credentials that appear exactly once. A `docker run` with no configuration at all must
  come up secure and usable.

If `config.json` exists, those env vars are **ignored**; the app never silently reverts a password
you changed in the UI. Lost password recovery is documented as: delete `config.json` and restart.
Bookmarks live in a separate file, so nothing is lost — this is why credentials aren't stored
alongside them.

Anything that mutates data requires auth. Unauthenticated `GET /` returns public bookmarks only.

### Endpoints

Web UI (cookie):

| Route | Purpose |
|---|---|
| `GET /` | list; `?q=`, `?tag=`, `?page=`, `?edit=` |
| `POST /?handler=Save&id=` | save an inline edit |
| `POST /?handler=Delete&id=` | delete, behind a JS confirm |
| `GET,POST /login`, `POST /logout` | session |
| `GET,POST /settings` | change password, show / regenerate API token, draggable bookmarklet + Shortcut URL with the token already filled in |
| `GET /add?url=&title=&description=&popup=` | prefilled add form — the bookmarklet target. Redirects to `/edit/{id}` if the URL is already saved. |
| `POST /add` | create |
| `GET,POST /edit/{id}` | the standalone edit page. **Only** reached from the bookmarklet popup, where there is no list to edit inside. |
| `?handler=SuggestTags` on `/`, `/add`, `/edit/{id}` | AI suggestions for the form's JS. Cookie auth + antiforgery header, so the token never reaches the browser. |
| `GET /b/{id}` | permalink; 404 (not 403) for private bookmarks when logged out |

### Editing happens in the list

`?edit={id}` swaps that one row for a form; every other row renders normally. There is no
client-side toggle and no fetch — the server just renders one item differently, so it works with
scripting off. Save posts to `?handler=Save`, then redirects back to the same `q`/`tag`/`page` with
a `#b-{id}` fragment so you land on the row you edited rather than the top of the list. The edit
link and the form action both carry the current view for the same reason.

`Pages/Edit.cshtml` survives only for the bookmarklet popup: that window has no list to edit
inside. Don't delete it, and don't route the list's edit link back to it.

### The bookmarklet popup

`popup=true` puts `/add` and `/edit/{id}` into a chrome-less mode: Add and Edit pass it to
`ViewData["Popup"]`, `_Layout` then drops the whole header — brand, search, navigation — and marks
`<body class="popup">` so the CSS can tighten the spacing. The page's own `<h1>` goes too; the window
title bar already says what this is. Nothing to navigate to justifies a header in a window whose
next action is closing itself.

The window can't be sized correctly from the outside: `window.open` asks for pixels, but the popup
inherits the opener's **zoom level**, so a form that fits at 100% is cut off at 150% and Save ends up
below the fold. `_Layout`'s popup script measures the overflow after load and grows the window to
match — a script-opened window may resize itself. It reads CSS pixels (`scrollHeight`,
`innerHeight`) but resizes in window units (`outerHeight`), which zoom doesn't scale, so it converts
with the `outerWidth / innerWidth` ratio and re-measures for a few frames, since each resize
reflows. Don't replace it with a fixed size in the bookmarklet — that's the thing that doesn't work.

The description textarea takes `autofocus` here and nowhere else — it's the field you almost always
came to change.

Delete is a red trash-icon "remove" button in the form's top-right corner (`.remove`), not a button
in the actions row — nothing destructive should sit next to Save where you're already aiming. It
stays **last inside the `<form>`** and is moved there by CSS: the first submit button in the DOM is
what Enter in a text field triggers, so putting it first would make Enter delete the bookmark. It
would also come first in the tab order. Don't tidy it into `.actions`.

API (token only — see below):

| Route | Purpose |
|---|---|
| `GET /api/add?url=&title=&description=&tags=&private=` | one-shot create. The iOS Shortcut path. |
| `POST /api/bookmarks` | create from a JSON or form body |
| `GET /api/bookmarks?q=` | search / existence check |
| `GET /api/suggest-tags?url=&title=&description=` | AI tags only, no save |
| `GET /api/export` | the raw JSON, for backup |
| `GET /healthz` | anonymous, for the Docker healthcheck |

API rules:

- **The API accepts the token and never the session cookie.** That is what makes a mutating `GET`
  safe: with no ambient credential, another site can't trigger `/api/add` through your browser.
  It also keeps the two surfaces separate — UI is cookie plus antiforgery, API is token.
- **Creates are idempotent by URL.** If the URL already exists, return the existing bookmark with
  `"created": false` instead of duplicating. Double-tapping the Shortcut is harmless.
- `POST /api/bookmarks` must **read and parse the raw request body regardless of `Content-Type`**.
  iOS Shortcuts can only attach a JSON body as a *file*, so the content type may be
  `application/octet-stream` or multipart. Do not rely on `[FromBody]` model binding here.
- Accept Linkding's field names as aliases (`notes`, `tag_names`, `is_private`) — shortcuts written
  for it then mostly work unchanged. They send `description` *and* `notes` with `description` often
  empty, so fall back from one to the other on **blank**, not merely on absent. A trailing slash
  (`/api/bookmarks/`, which is what Linkding documents) already matches; don't add a route for it.
- **There is no `/api/tags`**, so a Linkding shortcut offering a tag picker gets a 404 and won't
  work. Declined deliberately: the metadata fetch and AI tagging already fill everything in
  server-side, so the one-action shortcut needs no tag round trip. Point people at a create-only
  shortcut instead.
- `tags` accepts a comma- or space-separated string, or a JSON array. Normalise all three.
- **`url` must be the last query parameter on `GET /api/add`.** Shortcuts inserts the shared link
  unencoded, so one carrying its own `?v=…&list=…` gets split across query keys and the bound value
  is a silent truncation. `ResolveUrl` detects exactly that case — bound value is a strict prefix of
  the raw remainder, and the link already had a query — and takes the raw text instead. Don't
  "simplify" this away; losing half a URL is invisible until you click it months later.
- Return the created/found bookmark as JSON, plus a human-readable `message` ("Saved: …" /
  "Already saved: …") — the iOS Shortcut's optional notification shows exactly that field.
  Non-2xx responses get `{"error": "...", "message": "..."}`, same text in both, for the same
  reason.

### Metadata fetch

When a create arrives without a title, fetch the page and fill in what's missing:
`<title>`, then `og:description` / `meta[name=description]`. Guardrails, all of them required:
5 second timeout, ~512 KB read cap, follow redirects, HTTP/HTTPS schemes only, a normal-looking
`User-Agent`. Extraction by regex — do not add an HTML parser dependency for two tags. Any
failure is non-fatal: save the bookmark with whatever we have.

### AI auto-tagging

Optional, off unless `AI__ENABLED=true`. One `POST {AI__BASEURL}/chat/completions` call in the
OpenAI-compatible shape, so Ollama / LM Studio / llama.cpp / vLLM / LiteLLM all work unchanged.

- Prompt gets the url, title, description, and **the existing tag vocabulary from the store** —
  reusing your own tags matters more than inventing good new ones.
- Ask for a comma-separated list, lowercase, at most `AI__MAXTAGS` (default 5). Parse
  defensively: models will wrap it in prose, backticks, or JSON. Take what parses, ignore the rest.
- **Never let tagging block or fail a save.** Timeout `AI__TIMEOUTSECONDS` (default 20), and on
  any error log a warning and return no tags.
- Strip `<think>…</think>` before parsing, and tolerate a `Tags:` prefix, code fences and JSON
  arrays. Reasoning models will not follow "reply with only a list", and a suggestion feature that
  breaks on the model you happen to run is worse than none.
- Where it runs:
  - `/add` form: fired from the browser against the page's own `?handler=SuggestTags` after render,
    so the form is interactive immediately and still works with JS disabled.
  - `/api/add` and `POST /api/bookmarks`: synchronous, since no human is watching, and only when no
    tags were supplied.
  - Edit page: a "suggest tags" button, so anything can be re-tagged later.
- AI tags are *suggestions* in the UI and are appended to, never replace, tags the user supplied.

### Look

The dark theme is **Nord**; the light theme keeps the warm amber the icon is drawn in. Both accents
are warm yellows, which is what lets the fixed amber favicon sit in either header without looking
like it wandered in — a cool accent would strand it, and that is the thing to check before swapping
the palette again.

Nord's four surfaces are its Polar Night ramp used as drawn, except that `nord0` is the **card** and
`--bg` is `nord0` taken one step down, so cards lift off the ground instead of merging into it. Nord
is a dim theme rather than a black one, and that single step is what keeps a list of cards reading as
a list of cards. Two values are deliberately not literal Nord, both for contrast at text size:
`nord3`, the comment grey, is 1.6:1 on the card and unusable for the dates and hosts, so `--muted` is
`nord4` brought down to 5.5:1; and `nord11` is 3.0:1, fine as a fill but failing as the remove
button's own label, so `--danger` is that red lightened to 4.9:1. Don't "restore" either to the
canonical hex.

One accent, spent only on things you can act on — the list is a wall of titles, and a title you can
click is not news. Hence `--accent` on fills, `--link` for the same idea at text size (darker in the
light theme, where yellow on white is unreadable), and `--ink` for text on a filled accent or danger
control, which is dark in both themes because both fills are light. `_Layout` inlines the favicon's
geometry beside the site title, so the mark is present at the same size in both themes.

Depth is `--bg` under `--card` plus a hairline and one soft shadow — no heavy borders. `--line-strong`
is the hover state of a border, so cards and controls answer the pointer without moving. `:root` sets
`color-scheme` and `accent-color`, which is what makes the checkbox, the scrollbars and the search
field's own clear button render in the theme rather than defaulting to light. The description's left
rule is a `color-mix` of the accent against the card, so it needs a high enough percentage to stay
yellow — on Nord's lighter blue-grey card a weak mix goes olive.

Two small conventions worth keeping: tags are stored bare and the `#` is drawn by CSS
(`.tag::before`), so the marker never reaches the data; and `header .search input` is qualified past
`header` because the shared `input` rule sets the `background` shorthand and would otherwise wipe out
the search glyph on source order alone.

### Icon

A bookmark ribbon knocked out of a rounded amber tile: `#f0b429` behind `style.css`'s `--ink`.
The ink is not the page's off-white — off-white on yellow is 1.8:1 and the ribbon mushes into the
tile at 16px, where ink is 9.2:1. Amber carries a light or a dark browser theme on its own, so the
icon has no `prefers-color-scheme` swap, which is also what lets all three files stay identical.
`wwwroot/favicon.svg` is the source of truth; `favicon.ico` (16/32/48) and `apple-touch-icon.png`
(180, full bleed — iOS applies its own mask) are that same geometry rasterised, for browsers that
ignore SVG icons. `_Layout` declares all three rather than leaving `/favicon.ico` to chance.

Change the SVG and the raster files no longer match, so redraw them together. Two things in the
geometry are load-bearing at 16px, where the icon is actually seen: the ribbon sits on even
coordinates (10–22, 6–26 in a 32-unit box) so its edges land on whole pixels, and its notch is a
full 8 units deep. A shallower notch antialiases into a grey smudge and the icon reads as a plain
white rectangle.

## Configuration

All via environment variables, using ASP.NET's `__` section binding.

Credentials are **not** configuration after first run — see Auth. The three seed variables below
are read only when `config.json` doesn't exist yet, and ignored forever after.

| Variable | Default | Notes |
|---|---|---|
| `BOOKIES__USERNAME` | `admin` | Seed only. |
| `BOOKIES__PASSWORD` | random, logged once | Seed only. |
| `BOOKIES__APITOKEN` | derived from seed password | Seed only. |
| `BOOKIES__DATADIR` | `/data` | Mount a volume here. Holds `bookmarks.json`, `config.json`, `keys/`. |
| `BOOKIES__TITLE` | `Bookies` | Shown in the header. |
| `BOOKIES__PAGESIZE` | `50` | |
| `BOOKIES__TRUSTPROXYHEADERS` | `false` | Honour `X-Forwarded-*`. Only behind a proxy that is the sole way in — it trusts any caller's claimed IP, which the login limiter partitions on. |
| `AI__ENABLED` | `false` | |
| `AI__BASEURL` | — | e.g. `http://192.168.1.50:11434/v1` |
| `AI__MODEL` | — | e.g. `qwen3:8b` |
| `AI__APIKEY` | — | optional; sent as `Authorization: Bearer` when set |
| `AI__MAXTAGS` | `5` | |
| `AI__TIMEOUTSECONDS` | `20` | |

## Docker

Multi-stage build: `dotnet/sdk:10.0-alpine` to publish, `dotnet/aspnet:10.0-alpine` to run. Non-root
(uid 1000), `ASPNETCORE_URLS=http://+:8080`, volume at `/data`, healthcheck against `/healthz`.
`docker-compose.yml` is the documented way to run it.

Alpine ships without ICU, so the project sets `InvariantGlobalization`. Keep it that way, or the
container fails to start rather than degrading.

Data protection keys are persisted to `{DataDir}/keys` with a fixed application name. Without that
every container restart invalidates the session cookie and logs you out — easy to miss in dev,
where the app doesn't restart.

## Client snippets

Bookmarklet — one browser bookmark whose URL is this. Selected text on the page becomes the
description, matching Shaarli's behaviour:

```js
javascript:(function(){window.open('http://HOST:8080/add?popup=true&url='+encodeURIComponent(location.href)+'&title='+encodeURIComponent(document.title)+'&description='+encodeURIComponent(String(window.getSelection()||'')),'_blank','width=720,height=680');})();
```

`popup=true`, not `popup=1` — the model binder only accepts real boolean literals, and `1` binds
silently to `false`. The Settings page generates this snippet with the real host, so change it
there rather than here.

iOS Shortcut — Share Sheet input accepting **Safari web pages and URLs** (both — see below), then
two actions:

```
1. Get URLs from Input           (from Shortcut Input)
2. Get Contents of URL:  http://HOST:8080/api/add?token=YOUR_TOKEN&url=<URLs chip>
```

Both halves of that were each, at some point, the bug that broke the whole thing on a real
device — don't simplify either away:

- **"URLs" alone is not enough as the accepted type.** Safari shares a *Safari web page* item,
  not a bare URL, and the share sheet filters shortcuts by declared input type — a URL-only
  shortcut simply never shows up in Safari's share sheet. Tick both types.
- **`Get URLs from Input` is required, not belt-and-braces.** The raw `Shortcut Input` chip
  dropped into the URL text sometimes coerces the web-page item to its *title* instead of its
  address (observed live: the server got `url=Počasí` and answered "A valid http(s) url is
  required"), even though the same chip worked in an earlier test. Third-party shortcuts read the
  `Page URL` property explicitly for the same reason. `Get URLs from Input` extracts the address
  deterministically from either input form.

Title and tags come from the server-side metadata fetch and AI tagging, so the base shortcut needs
nothing else. Use the `Authorization: Token` header form instead if you'd rather keep the token
out of the URL. Settings also documents two optional add-ons:

- **A "Saved" banner** — `Get Dictionary Value` of `message` + `Show Notification`. This is what
  the `message` field on every `/api/add` / `POST /api/bookmarks` response exists for (errors and
  the 401 carry `message` too, so the same two actions surface failures). Don't remove the field.
- **A tags prompt** — `Ask for Input` dragged above the URL call, inserted as `&tags=<Provided
  Input>` *before* `&url=`, which must stay the last parameter (see `ResolveUrl`). An empty answer
  normalises to no tags, so AI tagging still runs — that's `BookmarkService`'s `tags.Count == 0`
  check doing the work.

A redacted screenshot of the finished base shortcut lives at `wwwroot/ios-shortcut.jpeg` and is
shown on Settings. It predates the `Get URLs from Input` step (the page's caption says so) — if
it's ever retaken, remember that `wwwroot` is served without authentication, so any replacement
screenshot must have the host and token painted over **before** it lands in the repo.

**The app cannot ship an installable shortcut, and this is settled — don't reopen it.** Apple's
`shortcuts://import-shortcut?url=…` scheme will import from any reachable URL, so self-hosting the
file is not the obstacle; signing is. Since iOS 15 a `.shortcut` must be signed before iOS will
import it, signing only happens via `shortcuts sign` on a **Mac** (or an iOS 12–14 device), and
`--mode anyone` notarises through iCloud. A shortcut prefilled with a user's host and token is a
different file per user, so it would need signing per request — impossible from ASP.NET on Linux.
Settings therefore teaches building it by hand, in seven steps.

**Don't link the "Add to Linkding" iCloud shortcut again** (Settings used to). Its decompiled
plist shows it never calls the Linkding API: it collects only a hostname (no token), builds
`https://<host>/bookmarks/new?...&auto_close` — Linkding's *web UI* form, a route Bookies doesn't
have — and opens it in a web view, with `https://` hardcoded. It cannot work against Bookies. Any
replacement candidate must be decompiled and checked first (iCloud serves the plist via
`icloud.com/shortcuts/api/records/<id>`), and one that posts to `/api/bookmarks/` with a
`Authorization: Token` header would work — that surface is kept Linkding-compatible on purpose.

## Status

All of v1 is built and verified end to end: storage, UI, auth, API, metadata fetch, AI tagging and
the container. There is no test project yet — verification so far has been manual, driving the real
server with curl. If a regression suite gets added, `BookmarkStore`, `Bookmark.TryNormalizeUrl`,
`Bookmark.ParseTags`, `AiTagger.ParseTags` and `ApiEndpoints.ResolveUrl` are the pieces with real
logic and no I/O, so they're the natural first targets.

Deferred deliberately: multi-user, RSS, Netscape HTML import/export, archiving, thumbnails.

## Conventions

- Nullable enabled, warnings as errors, file-scoped namespaces, `var` for locals.
- Minimal API endpoints live in `ApiEndpoints.cs` as one extension method; page handlers live with
  their page. Don't scatter routes into new files per endpoint.
- No `try/catch` that swallows silently. Network calls (metadata, AI) catch, log a warning, and
  degrade — everything else is allowed to throw.
- Validate the URL scheme on every input path. Only `http` and `https`; reject `javascript:`
  and friends. Razor auto-escapes output — never build HTML by string concatenation.
- Forms bind to `BookmarkInput` and render through the `_BookmarkFields` partial, which is included
  with `HtmlFieldPrefix = "Input"` so `asp-for` emits names that bind back. If a field stops
  binding, that prefix is the first thing to check.
- `SuppressImplicitRequiredAttributeForNonNullableReferenceTypes` is on: title, description and
  tags are genuinely optional, and without it every non-nullable string picks up an implicit
  `[Required]`.
- Prefer adding a few lines to an existing file over creating a new one.
- JS is progressive enhancement only. Every page must work with scripting off. Current scripts, all
  of which degrade to something usable:
  - **Search-as-you-type** (`_Layout`) — submits the search form after 3 characters and 500 ms,
    immediately when the box is emptied. Also listens for the `search` event, which is what the
    browser's own × clear button fires. Without JS the form still submits on Enter.
  - **Description auto-grow** (`_BookmarkFields`) — CSS `field-sizing: content` does this natively;
    the script is the fallback for browsers that don't support it yet. Don't drop either half.
  - **Focus on load** (`_Layout`) — one block decides who owns the caret: an `[autofocus]` field if
    the page has one, otherwise the search box when it holds a query. Keep that decision here and
    nowhere else. It was three files coordinating by DOM sniffing once, and every change to one of
    them broke the others. The markup's job is only to mark the target with `autofocus`, which is
    also what makes it work with JS off; the script exists because the attribute leaves the caret in
    front of the existing text and browsers abandon it if anything touches focus first.
  - **Copy buttons** (`Settings`) — one script adds a Copy button beside anything marked
    `data-copy`. The buttons are created in JS, never in the markup, so they can't render dead.
    `navigator.clipboard` is only defined in a secure context and this app is expected on plain
    HTTP over a LAN, so the `execCommand` selection route is the normal path, not the fallback —
    keep both, and keep the readonly textareas' tap-to-select as the no-JS answer.
  - **Tag picks and completion** (`_BookmarkFields`) — the form ships your existing tag vocabulary
    (most-used first, from `BookmarkStore.TagCounts`) as a JSON script block. One script turns the
    first dozen into clickable badges that toggle in and out of the field and light up when present,
    and completes the tag being typed — the text after the last comma — from the same list, with
    arrow keys, Enter, Escape and click. Enter with nothing highlighted still submits the form, so a
    brand new tag isn't harder to type than an existing one. Badges and menu are both built in JS,
    like the Copy buttons, so nothing renders dead; with scripting off the field is a plain text box.
  - **Popup fit** (`_Layout`) — see the bookmarklet popup section above.
  - **AI tag suggestion** and the **delete confirm**.
- The bookmarklet on the Settings page is a real `<a href="javascript:…">` so it can be dragged
  onto the toolbar. The copy-paste textarea stays as a fallback — dragging isn't possible on mobile.
