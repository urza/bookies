# Bookies

A small simple self-hosted bookmark manager in the spirit of [Shaarli](https://github.com/shaarli/Shaarli).
One user, one Docker container, plain JSON files on disk.

- Add, search, tag, edit and delete bookmarks from a plain server-rendered UI
- Per-bookmark public/private flag — logged out visitors see only the public ones
- A bookmarklet for the desktop and a one-action iOS Shortcut for the phone
- Fills in the title and description by fetching the page itself, so the phone only sends a URL
- Optional auto-tagging by a local LLM on your own network

<img width="832" height="347" alt="image" src="https://github.com/user-attachments/assets/e2f1440e-149c-4876-9346-e619d5f532a5" />


## Run it

```bash
docker compose up --build -d
docker compose logs bookies
```

Then open <http://localhost:8080>.

On the very first start it creates `/data/config.json` and prints the credentials **once**:

```
warn: Bookies.Services.CredentialStore[0]
      First run — wrote /data/config.json
        username  : admin
        password  : au774zQoU4vWadF6   <- generated, shown only once
        api token : A4ZSutrxSx7QhrlRdn1V9ZmRpJO6L_G8WXg7VFn8Gwg
```

Save those, then change the password from **Settings**. To pick your own credentials instead,
uncomment `BOOKIES__USERNAME` and `BOOKIES__PASSWORD` in `docker-compose.yml` *before* the first
start — they are seeds, read only when `config.json` doesn't exist yet, and ignored after that so
a password you change in the UI is never reverted by a stale environment variable.

Forgot the password? Delete `data/config.json` and restart. Bookmarks live in a separate file and
are not touched.

## Bookmarklet

Settings has the snippet with your host already filled in. Make a new browser bookmark and paste it
as the address:

```js
javascript:(function(){window.open('http://YOUR-HOST:8080/add?popup=true&url='+encodeURIComponent(location.href)+'&title='+encodeURIComponent(document.title)+'&description='+encodeURIComponent(String(window.getSelection()||'')),'_blank','width=720,height=680');})();
```

Clicking it on any page opens the add form, prefilled — including any text you have selected as the
description. It uses your normal login session, so no token ends up in the bookmarklet. Saving
closes the window. If the page is already bookmarked you get the edit form instead of a duplicate.

## iOS Shortcut

Uses the API token, since Safari on iOS won't be carrying your session.

**The instructions are on the Settings page**, with the six steps and a snippet carrying your host
and token already. Open it on the iPhone rather than following anything here — that page knows your
address, this file doesn't.

You build the shortcut yourself, one **Get Contents of URL** action. There is no install button
worth trusting: since iOS 15 a shortcut has to be **signed** before iOS will import it, signing
needs a Mac (`shortcuts sign`), and `--mode anyone` notarises through iCloud — so no server can
generate a prefilled shortcut per user, however it's hosted. Any tap-to-install link is necessarily
a shortcut someone else built, which you then hand your API token to. Settings offers one in a
details block for the impatient; the six taps are the honest path.

Once it's on the phone it lands in Safari's Share Sheet, sends only the page address, and gets the
title, description and tags filled in server-side. Sharing a page twice returns the bookmark you
already have rather than a duplicate.

Third-party Linkding shortcuts do work here, because `POST /api/bookmarks` speaks that dialect
deliberately. Verified against Bookies: Linkding's trailing slash (`/api/bookmarks/`),
`Authorization: Token`, and the `notes` / `tag_names` / `is_private` field names. The exception is
any shortcut that reads your tag list first to offer a picker — that's `GET /api/tags/`, which
Bookies doesn't serve and answers with 404, deliberately: the metadata fetch and AI tagging already
fill tags in, so the round trip buys nothing.

Your phone has to be able to reach the host — same Wi-Fi, or a VPN in. Plain `http://` to a LAN
address is fine here; the restriction that blocks that applies to apps, not to this action.

Keep `url` as the **last** parameter. Shortcuts inserts the shared link without encoding it, so a
link carrying its own `?v=…&list=…` would otherwise be cut short at the first `&` — the server
recovers the full URL by reading everything after `url=`.

If you would rather not put the token in a URL, use `Authorization: Token YOUR-TOKEN` as a header
instead, or POST to `/api/bookmarks` with **Request Body: Form** and a `url` field.

## AI auto-tagging

Optional and off by default. Point it at anything speaking the OpenAI chat completions API —
Ollama, LM Studio, llama.cpp, vLLM, LiteLLM:

```yaml
environment:
  AI__ENABLED: "true"
  AI__BASEURL: http://192.168.1.50:11434/v1
  AI__MODEL: qwen3:8b
```

The prompt includes the tags you already use, so it reuses your vocabulary instead of inventing a
parallel one. In the add form suggestions arrive in the background and are *appended* — anything
you typed wins. On the API path tagging is synchronous, and only runs when no tags were supplied.

Tagging never blocks or fails a save: if the model is slow, unreachable or replies with nonsense,
the bookmark is saved without tags and a warning goes to the log.

## API

Everything under `/api` authenticates with the token only, never the session cookie — which is why
a plain `GET` is allowed to create a bookmark. Pass it as `?token=…`,
`Authorization: Token …`, `Authorization: Bearer …`, or `X-Token: …`.

| Endpoint | Purpose |
|---|---|
| `GET /api/add?url=…&title=…&description=…&tags=…&private=…` | Create. Only `url` is required. |
| `POST /api/bookmarks` | Create from a JSON or form body. |
| `GET /api/bookmarks?q=…` | Search, including private bookmarks. |
| `GET /api/suggest-tags?url=…&title=…` | Tag suggestions without saving anything. |
| `GET /api/export` | The raw bookmarks file, for backups. |
| `GET /healthz` | No auth. Used by the container healthcheck. |

Creates are idempotent by URL — an existing bookmark comes back with `"created": false`.

`POST /api/bookmarks` reads the body whatever the content type claims, because iOS Shortcuts can
only attach a JSON body as a *file*. It also accepts Linkding's field names (`notes`, `tag_names`),
so a shortcut written for Linkding mostly works as-is.

```bash
curl "http://localhost:8080/api/add?token=$TOKEN&url=https://example.com"

curl -X POST http://localhost:8080/api/bookmarks \
  -H "Authorization: Token $TOKEN" -H "Content-Type: application/json" \
  -d '{"url":"https://example.com","tags":["reference","web"],"private":false}'
```

## Configuration

All environment variables. `__` is the section separator.

| Variable | Default | Notes |
|---|---|---|
| `BOOKIES__USERNAME` | `admin` | Seed only — first run, before `config.json` exists. |
| `BOOKIES__PASSWORD` | random, logged once | Seed only. |
| `BOOKIES__APITOKEN` | derived from the seed password | Seed only. Rotate from Settings. |
| `BOOKIES__DATADIR` | `/data` | Mount a volume here. |
| `BOOKIES__TITLE` | `Bookies` | Shown in the header. |
| `BOOKIES__PAGESIZE` | `50` | Bookmarks per page. |
| `BOOKIES__TRUSTPROXYHEADERS` | `false` | See below. |
| `AI__ENABLED` | `false` | |
| `AI__BASEURL` | — | e.g. `http://192.168.1.50:11434/v1` |
| `AI__MODEL` | — | e.g. `qwen3:8b` |
| `AI__APIKEY` | — | Sent as a bearer token when set. |
| `AI__MAXTAGS` | `5` | |
| `AI__TIMEOUTSECONDS` | `20` | |

## Data and backups

Everything is in the mounted `/data` directory:

| File | |
|---|---|
| `bookmarks.json` | Every bookmark. Readable and editable by hand. |
| `bookmarks.json.bak` | The previous save, kept automatically. |
| `config.json` | Username, password hash (PBKDF2-SHA256), API token. Written `0600`. |
| `keys/` | Cookie encryption keys. Deleting these logs you out. |

Saves are atomic — written to a temp file and moved into place — so an unlucky restart can't leave
a half-written file. Copy the directory to back it up, or pull `GET /api/export`.

The whole file is rewritten on every save and all bookmarks are held in memory. That's the right
trade at personal scale; if you ever import tens of thousands of links, `BookmarkStore` is the one
class that would need to change.

## Deploying somewhere else

Pushing to `main` builds the image and publishes it to GitHub Container Registry
(`.github/workflows/publish.yml`). Tags: `latest` on `main`, `v1.2.3` from a `v*` git tag, and the
short commit SHA on every build — so a bad deploy rolls back by pinning the previous SHA.

The deployment machine never needs the source. Copy `docker-compose.deploy.yml` there as
`docker-compose.yml`, replace `OWNER/REPO` with your GitHub owner and repository (lowercase), then:

```bash
docker compose pull && docker compose up -d
docker compose logs bookies      # the first start prints the password and API token, once
```

Updating later is the same `pull && up -d`.

Two things that bite on the first run:

- **The package is private by default.** Either make it public in the repository's package
  settings, or run `docker login ghcr.io` on the deployment machine with a personal access token
  scoped `read:packages`.
- **The container runs as uid 1000**, so the `./data` directory next to the compose file has to be
  writable by uid 1000 — otherwise it can't write `config.json` and won't start.

The image is `linux/amd64`. For an arm64 host (Raspberry Pi, most NAS boxes) add
`platforms: linux/amd64,linux/arm64` to the build step in the workflow, or the container exits with
an exec format error.

## Behind a reverse proxy

Bookies serves plain HTTP and does no HTTPS redirect, so it works on a LAN as-is and stays out of
the way of a proxy terminating TLS. If you put one in front, set
`BOOKIES__TRUSTPROXYHEADERS=true` so `X-Forwarded-For` and `X-Forwarded-Proto` are honoured. Only
turn that on when the proxy is the *only* way in — it makes the app believe whatever client IP it
is told, and the login rate limiter partitions on that.

The session cookie is marked Secure only on HTTPS requests, so it survives plain-HTTP LAN use.

## Development

```bash
dotnet run     # http://localhost:5000
dotnet build
```

Local settings go in `appsettings.Development.json` (gitignored); point `BOOKIES__DATADIR` at a
local folder rather than `/data`.

Design notes and conventions are in `CLAUDE.md`.



## License

YOU CAN USE THIS SOFTWARE "AS IS" (NO WARRANTY) IN ANY WAY YOU WANT, BUT BY DOING SO YOU ACKNOWLEDGE THAT:

Science is a force for human liberation and one of humanity's greatest inventions. Through open inquiry, evidence, and the willingness to correct our errors, we expand our understanding and our ability to improve the human condition.

Technology is the physical manifestation of our discoveries. By building better tools, we overcome limitations, reduce suffering, and create abundance.

Free markets enable cooperation on an extraordinary scale. Through competition, exchange, and entrepreneurship, they reward useful ideas, spread innovation, and help lift people out of poverty.
