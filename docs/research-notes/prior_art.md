# Prior art: Jellyfin / Plex / Emby to Discord Rich Presence (as of Oct 2026)

Repo metadata (language, license, archived flag, last push, stars) comes from the GitHub REST API (`gh api repos/<owner>/<repo>`), queried 2026-10-18.

## 1. Jellyfin projects: architecture, maintenance, licenses (and the "jellycord" name)

### Takeaway
The best-known Jellyfin RPC client, Radiicall's jellyfin-rpc (Rust, GPL-3.0), is now **archived**. The most active replacement is kennethsible/jellyfin-rpc (Python, MIT, GUI and CLI, TMDB and Cover Art Archive fallbacks). Nearly every project is a **desktop-side client**: it polls `/Sessions` with an API key and talks to the local Discord client over IPC. In 2025-2026 a few *server plugins* appeared. They use three approaches: (a) a plugin endpoint polled by a desktop CLI, (b) a plugin that opens Discord IPC directly, which only works when Discord runs on the server host, and (c) a plugin that skips IPC and uses Discord's **headless-sessions (Social SDK) HTTP API** with per-user OAuth. The name **"jellycord" is heavily used** on GitHub, including one repo that is a Jellyfin Discord rich presence app.

### Cited Findings
**Radiicall / JustRadical jellyfin-rpc (Rust)**
- The repo moved from Radiicall/jellyfin-rpc to justradical/jellyfin-rpc. It is Rust, GPL-3.0, archived=true, last push 2026-09-25, 541 stars — [GitHub API / repo](https://github.com/Radiicall/jellyfin-rpc)
- The README banner says it "has been archived, its no longer receiving updates" — [repo](https://github.com/JustRadical/jellyfin-rpc)
- Last releases: 1.3.5 (2026-04-15), 1.3.4 (2025-11-11), 1.3.3 (2025-04-27) — [releases via GitHub API](https://github.com/JustRadical/jellyfin-rpc/releases)
- "Jellyfin-RPC uses the API to check what you're currently watching". It "can be ran from a server or your computer" and requires that Discord is open and logged in — [CLI README](https://raw.githubusercontent.com/Radiicall/jellyfin-rpc/main/jellyfin-rpc-cli/README.md)
- Playback state: it polls `GET {url}/Sessions`, filters by a priority list of usernames, takes the first session with `NowPlayingItem` and `PlayState`, and skips theme songs (`extra_type == "ThemeSong"`) — [jellyfin-rpc/src/lib.rs](https://github.com/JustRadical/jellyfin-rpc/blob/main/jellyfin-rpc/src/lib.rs)
- The code is split into a library crate (`jellyfin-rpc`) and a CLI crate (`jellyfin-rpc-cli`). Images live in `src/external/{imgur,litterbox,image_utils}.rs` — [repo tree](https://github.com/JustRadical/jellyfin-rpc)
- Config keys: `jellyfin.url` (scheme required, "without it the script will crash"), `jellyfin.api_key` (a user token works too), `images.enable_images`, `images.imgur_images`, `imgur.client_id`, `discord.buttons` (with `"dynamic"` values), and `blacklist.media_types` / `blacklist.libraries` — [wiki Setup](https://github.com/JustRadical/jellyfin-rpc/wiki/Setup). The source also has `images.litterbox_images`, `discord.show_paused`, and per-media `status_display_type` — [config.rs](https://github.com/JustRadical/jellyfin-rpc/blob/main/jellyfin-rpc-cli/src/config.rs)

**kennethsible/jellyfin-rpc (Python)**
- Python, MIT, not archived, pushed 2026-10-17, 103 stars. It is the most actively maintained Jellyfin RPC client found — [GitHub API](https://github.com/kennethsible/jellyfin-rpc)
- Ships a desktop GUI (PyInstaller builds for Windows, macOS, Linux, plus a Homebrew cask) and a headless CLI configured by an INI file. Auth is via Quick Connect (it generates a user access token) or an API key — [README](https://github.com/kennethsible/jellyfin-rpc)
- `POLLING_RATE` (default 5): "Interval in seconds to poll Jellyfin sessions, or the minimum delay between WebSocket events (if your Jellyfin server and reverse proxy support WebSockets)". So it can use Jellyfin's WebSocket with a polling fallback — [README](https://github.com/kennethsible/jellyfin-rpc)
- Other options: `SEEK_THRESHOLD` (10 s jump resyncs the timer), `SESSION_TIMEOUT` (2 s before clearing, smooths item transitions), `STALE_GRACE_PERIOD` (5 s), `MEDIA_TYPES` priority list, and library allowlist/denylist by `topParentId` — [README](https://github.com/kennethsible/jellyfin-rpc)

**Other client-side Jellyfin projects**
- MBCord (Emby/Jellyfin, Electron/JS, MIT): SandwichFox/MBCord is archived (last push 2023-10). Its successor oonqt/MBCord says "MBCord project has been abandoned" — [oonqt/MBCord](https://github.com/oonqt/MBCord); [SandwichFox/MBCord](https://github.com/SandwichFox/MBCord)
- exy (xyxxyxxy, TypeScript, Unlicense, pushed 2026-05): an Emby/Jellyfin companion app. It logs in with your regular user ("no Emby/Jellyfin API key required"), shows pause state, time remaining and current chapter, and notes "Discord must run on the same machine" — [exy README](https://github.com/xyxxyxxy/exy)
- cmerk2021/discord-jellyfin-rpc: Go daemon, GPL-3.0, pushed 2026-10. Its PR says it uses only the current authorization header and routes, so it works on Jellyfin 12 — [PR #1](https://github.com/cmerk2021/discord-jellyfin-rpc/pull/1)
- adwerygaming/jellyfin-discord-presence (TypeScript, no license); Ray-kong/jellyfin-discord-rich-presence (Python/pypresence, no license, 5 s updates, optional OMDb key for movie posters) — [adwerygaming](https://github.com/adwerygaming/jellyfin-discord-presence); [Ray-kong](https://github.com/Ray-kong/jellyfin-discord-rich-presence)
- Vencord userplugins also exist. macluxHD/JellyfinRichPresence is a "Vencord plugin to show your jellyfin activity in discord" — [GitHub search](https://github.com/macluxHD/JellyfinRichPresence)

**Jellyfin server plugins (server is not the user's machine)**
- **crobibero/jellyfin-plugin-discord** (C#, MIT) is archived and last pushed 2020-09. It is an old abandoned attempt — [GitHub API](https://github.com/crobibero/jellyfin-plugin-discord)
- **Phew/jellyfin-rpc** has a .NET 8 server plugin (Jellyfin 10.10+) that exposes `GET /Plugins/DiscordRpc/Presence/Me` and `/Ping`, plus a Python CLI on the PC that **polls** that endpoint over HTTP (with an `api_key` query param) and pushes to local Discord IPC. The plugin returns presence JSON (`active, details, state, timestamps, is_paused, public_cover_url`). It is scoped by `username` so other users' sessions are not exposed. When paused, the CLI backs off to 30-45 s polling and clears presence after 3 min paused. It has no WebSocket push. No license, 0 stars, last push 2026-01 — [Phew/jellyfin-rpc](https://github.com/Phew/jellyfin-rpc)
- **Pankyop/rich-presence-for-jellyfin-in-discord-Plugin** (C#, MIT, pushed 2026-10-10): the plugin hooks `ISessionManager` events (PlaybackStart/Progress/Stopped) with a polling fallback. It writes **directly to the Discord IPC pipe** (`\\.\pipe\discord-ipc-0` or `/run/user/<uid>/discord-ipc-0`), so Discord must run "on same host" as the Jellyfin server. That only suits single-user setups where Jellyfin runs on the desktop — [README](https://github.com/Pankyop/rich-presence-for-jellyfin-in-discord-Plugin)
- **mrzeappleGit/jellyfin-plugin-discord-presence** (C#, MIT, pushed 2026-08-04, 0 stars) is the most novel. It "does not need the Discord desktop app running anywhere". It watches sessions in-process and calls `POST https://discord.com/api/v10/users/@me/headless-sessions` with a per-user OAuth token that has the self-serve `sdk.social_layer_presence` scope. Sessions refresh every 14 min (they expire at 20), update on play/pause/seek/item change, and clear on stop. Users link through a self-service page (`/DiscordPresence/LinkPage`) or a device code at `discord.com/activate`. Tokens are stored server-side in a 0600 credentials file — [README](https://github.com/mrzeappleGit/jellyfin-plugin-discord-presence); [DiscordApi.cs](https://github.com/mrzeappleGit/jellyfin-plugin-discord-presence/blob/main/Jellyfin.Plugin.DiscordPresence/DiscordApi.cs)
  - Caveats it documents: "Discord offers third parties no presence-only scope". `sdk.social_layer_presence` "also grants reading and writing the user's Discord friends list". The Social SDK is framed around games, so "if Discord ever gates it differently, statuses may stop". The session token "rotates on every response" — [README](https://github.com/mrzeappleGit/jellyfin-plugin-discord-presence); [PresenceService.cs](https://github.com/mrzeappleGit/jellyfin-plugin-discord-presence/blob/main/Jellyfin.Plugin.DiscordPresence/PresenceService.cs)

**"jellycord" name collisions (GitHub search)**
- **yhqe/jellycord**: "discord rich presence for jellyfin" (JavaScript, `@xhayper/discord-rpc`, package name `jellycord`, created 2025-10-25, last push 2026-05-03, 0 stars, no README). This is a direct same-purpose collision — [repo](https://github.com/yhqe/jellycord)
- SiddheshDongare/Jellycord: Discord bot for JFA-GO invites (GPL-3.0, 13 stars), also on Docker Hub as sidikulous/jellycord — [repo](https://github.com/SiddheshDongare/Jellycord); [Docker Hub](https://hub.docker.com/r/sidikulous/jellycord)
- Vernoxvernax/JellyCord: Rust bot announcing new Jellyfin/Emby library items (MIT, pushed 2026-09) — [repo](https://github.com/Vernoxvernax/JellyCord)
- Also shayyz-code/jellycord (unrelated social app), PenguCCN/Jellycord (user management bot), BrennanAyers/jellycord (voice-channel playback bot), dunsparth/Jellycord (server info bot), guyadambrown/JellyCord — [GitHub search "jellycord"](https://github.com/search?q=jellycord&type=repositories)

### Inferences
- Nobody has an established, popular server-plugin approach. The plugins found are all small (0 stars) and from 2025-2026. A plugin plus companion app is open territory, but the "server is not the user's machine" problem has been solved in only three ways (polled endpoint, same-host IPC, headless-sessions API).
- The headless-sessions route removes the companion app entirely, but it needs broad OAuth scopes and depends on an API Discord may restrict.
- "Jellycord" is a crowded name with at least one identical-purpose repo (small and dormant). Use a distinguishing suffix or a different name.

### Gaps
- No project was found that pushes presence from the server to a desktop companion over a **WebSocket** (Phew polls over HTTP; kennethsible uses Jellyfin's own WebSocket, not a custom server-to-companion channel).
- I couldn't verify Reddit r/jellyfin discussion threads (search returned no Reddit results).
- "JellyRPC" and "Jellyfin-RPC on Windows" did not surface as distinct maintained projects beyond those listed.

## 2. Plex and Emby equivalents

### Takeaway
phin05's Discord Rich Presence for Plex (DRPP) is the reference Plex implementation. It was rewritten in **Go** (AGPL-3.0, v3.4.0 May 2026), listens on Plex's **WebSocket notifications** endpoint, uploads posters to an external host (Litterbox by default), and offers a web UI and Docker image. Ombrelin's PlexRichPresence repo no longer resolves on GitHub. "Plexcord" is mostly unrelated or dormant projects. Emby is covered by exy (and the dead MBCord) plus small 2026 projects.

### Cited Findings
- DRPP: Go, AGPL-3.0, not archived, pushed 2026-05-12, 438 stars. Latest v3.4.0 (2026-05-12), previous v3.3.1 (2026-04-23) — [GitHub API](https://github.com/phin05/discord-rich-presence-plex)
- DRPP features: poster artwork, progress, buttons, external links, and per-media-type template strings. It has a web UI with interactive Plex auth and live logs, and runs on Windows, Linux (Docker) and macOS — [README](https://github.com/phin05/discord-rich-presence-plex)
- DRPP state source: it connects to the Plex server WebSocket at `/:/websockets/notifications` (gorilla/websocket, with ping keepalive) and filters by `ListenForUser` — [server/plex/client.go](https://github.com/phin05/discord-rich-presence-plex/blob/master/server/plex/client.go); [server/plex/service.go](https://github.com/phin05/discord-rich-presence-plex/blob/master/server/plex/service.go)
- DRPP Docker: Discord must run on the same machine. You mount Discord's runtime directory to `/run/app`, set `DRPP_UID`/`DRPP_GID`, and bind `127.0.0.1:8040`. On Windows/macOS Docker, Discord itself must run in a Linux container (e.g. kasmweb/discord) — [README](https://github.com/phin05/discord-rich-presence-plex)
- Ombrelin/PlexRichPresence and Ombrelin/plex-rich-presence return 404 (deleted, renamed or private). dhrinkino/PlexRichPresence (C#, MIT) is archived since 2021 — [GitHub API](https://github.com/Ombrelin/plex-rich-presence); [dhrinkino](https://github.com/dhrinkino/PlexRichPresence)
- "Plexcord" repos: MutanPlex/Plexcord is a Discord client mod (GPL-3.0), not Plex RPC. htbrown/plexcord (music RPC) was last pushed 2021. tmapes/plexcord is archived. Wifsimster/PlexCord (Go, MIT, 2026) bridges Plexamp to Discord — [GitHub search](https://github.com/search?q=plexcord&type=repositories)
- feliciacos/plexRichPresence is a Vencord userplugin showing Plex playback "including dynamic poster" (2026) — [repo](https://github.com/feliciacos/plexRichPresence)
- Multi-server 2026 projects: rowkavdev/nowplaying (AGPL-3.0, Plex/Jellyfin/Navidrome/Emby) and Kjerne/MediaRPC (MIT, Emby and browser streaming) — [nowplaying](https://github.com/rowkavdev/nowplaying); [MediaRPC](https://github.com/Kjerne/MediaRPC)

### Inferences
- DRPP's architecture (WebSocket subscription to server notifications, image-host abstraction with several providers, templated fields, web UI) is the most mature design to learn from. Because it is AGPL-3.0, copying its code forces AGPL on derivatives. A Jellyfin server plugin also links against GPL Jellyfin assemblies anyway.

### Gaps
- I could not find Ombrelin's PlexRichPresence's current location or history.

## 3. Image strategies and gotchas

### Takeaway
There are four strategies in use: (1) put the **public Jellyfin URL** `/Items/{id}/Images/Primary` directly in `large_image`, (2) **upload to a temporary or anonymous host** (Litterbox 72 h, Imgur, ImgBB, self-hosted Copyparty) and cache the URL per item, (3) **public metadata providers** (TMDB posters via provider IDs, Cover Art Archive or MusicBrainz for music, OMDb, AniList, TVMaze, Wikipedia), and (4) static fallback images or uploaded Discord app assets. Discord accepts arbitrary external HTTPS URLs in `large_image` and proxies them through its media proxy (`mp:`). The URL must be publicly reachable by Discord, so LAN-only servers break strategy 1.

### Cited Findings
- Discord docs: "To use an external image via media proxy, specify the URL as the field's value when sending". "You will only receive the `mp:` prefix via the gateway". The media proxy format is `mp:{image_id}` mapping to `https://media.discordapp.net/{image_id}`. External URL assets "support any publicly accessible image URL, including GIF, animated WebP, and AVIF". Uploaded app assets support PNG, JPEG and WebP — [Discord gateway docs, Activity Asset Image](https://docs.discord.com/developers/events/gateway-events#activity-object-activity-assets)
- Social SDK docs: the large image field accepts 1-300 characters, and the large image click URL (LargeUrl) accepts 1-256 characters (as summarized from search results) — [Discord Social SDK ActivityAssets](https://discord.com/developers/docs/social-sdk/classdiscordpp_1_1ActivityAssets.html)
- **Jellyfin's image endpoint is anonymous**: `GET/HEAD Items/{itemId}/Images/{imageType}` (`GetItemImage`) has no `[Authorize]` attribute, unlike neighbouring endpoints such as `GET Items/{itemId}/Images`. So direct poster URLs need **no API key in the URL** — [jellyfin ImageController.cs](https://github.com/jellyfin/jellyfin/blob/master/Jellyfin.Api/Controllers/ImageController.cs)
- Radiicall jellyfin-rpc direct mode builds `{url}/Items/{id}/Images/Primary` without any token. For music it tries the track id, then the album/session item id, and it detects "does not have an image of type Primary" to fall back. The default image is an imgur-hosted logo, and Live TV uses a fixed imgur image — [lib.rs](https://github.com/JustRadical/jellyfin-rpc/blob/main/jellyfin-rpc/src/lib.rs)
- Radiicall FAQ: "Your server needs to be port forwarded for images to work without using the imgur api" — [FAQ](https://github.com/JustRadical/jellyfin-rpc/wiki/Frequently-Asked-Questions)
- Radiicall Litterbox upload: it POSTs multipart data to `https://litterbox.catbox.moe/resources/internals/api.php` with `time=72h`. It caches item id to URL with a timestamp in a `urls.json` file and re-uploads when the entry is 72 h or older. Imgur uploads go to `https://api.imgur.com/3/image` with `Client-ID`. Images can be squared with blur padding (`make_square_with_blur`) before upload — [litterbox.rs](https://github.com/JustRadical/jellyfin-rpc/blob/main/jellyfin-rpc/src/external/litterbox.rs); [imgur.rs](https://github.com/JustRadical/jellyfin-rpc/blob/main/jellyfin-rpc/src/external/imgur.rs)
- DRPP "downloads poster images from your Plex server and uploads them to an external image host to enable Discord to fetch and serve those images". Providers are Litterbox (default), ImgBB, Imgur, and self-hosted Copyparty. The Litterbox expiry hours are configurable (`time: "%dh"`) — [README](https://github.com/phin05/discord-rich-presence-plex); [litterbox.go](https://github.com/phin05/discord-rich-presence-plex/blob/master/server/images/litterbox/litterbox.go)
- kennethsible: "posters and album covers won't be retrievable from your Jellyfin server with a local URL (requires HTTPS and must be publicly accessible by Discord)". It falls back to TMDB (user must supply a TMDB API key) and the Cover Art Archive (no key). Options include `ALWAYS_USE_TMDB`, `POSTER_LANGUAGES`, `TEXTLESS_POSTERS`, `SEASON_OVER_SERIES`, `SEARCH_MUSICBRAINZ`, and `RELEASE_OVER_GROUP` — [README](https://github.com/kennethsible/jellyfin-rpc)
- Phew plugin returns Jellyfin's own URLs (`/Items/{Id}/Images/Primary` with resize params; episodes prefer the series poster). It requires them to be reachable "over HTTPS without auth or redirects, returning HTTP 200". The fallback is a 512x512 uploaded Discord app asset. It has an `include_token_in_image_url` setting — [Phew/jellyfin-rpc](https://github.com/Phew/jellyfin-rpc)
- mrzeappleGit plugin: `large_image = {PublicUrl}/Items/{id:N}/Images/Primary?fillHeight=512&quality=90`, only when a public URL is configured. "Discord's image proxy must be able to reach it from the internet" — [PresenceService.cs](https://github.com/mrzeappleGit/jellyfin-plugin-discord-presence/blob/main/Jellyfin.Plugin.DiscordPresence/PresenceService.cs); [README](https://github.com/mrzeappleGit/jellyfin-plugin-discord-presence)
- Pankyop plugin `ArtworkResolver`: if `PublicServerUrl` is set and public, it uses `/Items/{SeriesId or Id}/Images/Primary?fillHeight=300&fillWidth=300&quality=90`. Otherwise it resolves posters from external metadata providers (AniList, TVMaze, Wikipedia) with a cache and a 3 s timeout. It falls back to the Jellyfin icon from `raw.githubusercontent.com/jellyfin/jellyfin-ux`, and uses twemoji play/pause icons via jsDelivr — [ArtworkResolver.cs](https://github.com/Pankyop/rich-presence-for-jellyfin-in-discord-Plugin/blob/main/Jellyfin.Plugin.DiscordRichPresence/Discord/ArtworkResolver.cs)
- Ray-kong uses the OMDb API (optional key) for movie posters — [repo](https://github.com/Ray-kong/jellyfin-discord-rich-presence)

### Inferences
- **API key leakage**: Jellyfin's `GetItemImage` is anonymous, so there is no reason to append `api_key=` to poster URLs. Doing so (as Phew's `include_token_in_image_url` option hints at) would expose a token to everyone who views the profile and to Discord's proxy. Direct URLs still disclose the server's public hostname and item GUIDs to anyone who sees the presence.
- **Private or LAN servers** cannot use direct URLs. Robust tools layer a fallback chain: public Jellyfin URL, then provider-ID lookups (TMDB/TVDB/MusicBrainz IDs, which Jellyfin stores in `ProviderIds`), then upload to a temp host, then a static logo.
- **Caching**: temporary hosts need per-item URL caches with expiry (Radiicall's 72 h `urls.json`). Discord's media proxy also caches by URL, so adding a cache-busting param (e.g. image tag) on artwork change is advisable. Uploading user posters to third-party hosts is a privacy trade-off, and Imgur's anonymous API has historically been unreliable, which is why DRPP moved its default to Litterbox.
- Resize server-side (`fillHeight`/`fillWidth`/`quality`) to keep proxy fetches fast and square-ish. Radiicall pads to a square with blur because Discord crops the large image to a square.

### Gaps
- I found no authoritative Discord statement on media proxy cache duration or timeouts, or on whether redirects or self-signed certs are rejected (Phew's README claims no redirects is required, which is unverified against Discord docs).
- I couldn't verify the Imgur anonymous upload reliability issue from a primary source within budget.

## 4. Activity type, status_display_type, progress, buttons, pause and clear behaviour

### Takeaway
All modern projects use activity type **3 (Watching)** for video and **2 (Listening)** for music, set `status_display_type` so the member list shows the title rather than the app name, use start/end timestamps for Discord's progress bar, and generate up to two buttons from provider IDs (IMDb, TMDB, Trakt, Letterboxd, TVDB). Pause handling varies. Some drop the timestamps and add a small "paused" icon; others clear the presence (immediately or after a timeout). Most clear on stop, often with a short debounce.

### Cited Findings
- Discord activity types: 0 Playing, 1 Streaming, 2 "Listening to {name}", 3 "Watching {name}", 4 Custom, 5 Competing. Status display types: 0 name, 1 state, 2 details ("control which field appears in the user's status text in the member list"). Buttons: max 2, label 1-32 characters, URL 1-512 characters, and "Bots cannot access a user's activity button URLs" — [Discord docs](https://docs.discord.com/developers/events/gateway-events#activity-object-activity-assets)
- Radiicall: Music and AudioBook use `ActivityType::Listening`, books get no type, everything else uses `Watching`. It sets `status_display_type` per media type. When playing it sets start/end timestamps. When paused with `show_paused`, it shows no timestamps plus a small "Paused" image; otherwise paused returns empty (no update). It pads `state`/`details` shorter than 3 chars with invisible characters because "discord requir[es] a minimum length of 3 chars", and truncates at 128. Dynamic buttons come from Jellyfin `ExternalUrls`, skipping localhost URLs, max 2. It calls `clear_activity()` when nothing is playing — [lib.rs](https://github.com/JustRadical/jellyfin-rpc/blob/main/jellyfin-rpc/src/lib.rs); [main.rs](https://github.com/JustRadical/jellyfin-rpc/blob/main/jellyfin-rpc-cli/src/main.rs)
- DRPP: `ActivityTypeListening` for music, otherwise `ActivityTypeWatching`. `status_display_type` is chosen between Details and State. It has a stop/idle timer for clearing, including a configurable pause timeout (-1 disables). Template links: `https://www.imdb.com/title/{id}`, `https://www.themoviedb.org/{movie|tv|episode}/{id}`, `https://trakt.tv/search/tmdb/{id}?id_type=...`, `https://letterboxd.com/tmdb/{id}` (movies), and `https://www.thetvdb.com/dereferrer/...` — [mediator/service.go](https://github.com/phin05/discord-rich-presence-plex/blob/master/server/mediator/service.go); [mediator/template.go](https://github.com/phin05/discord-rich-presence-plex/blob/master/server/mediator/template.go); [discord/models.go](https://github.com/phin05/discord-rich-presence-plex/blob/master/server/discord/models.go)
- kennethsible: `SHOW_WHEN_PAUSED` "Shows paused activities with a small icon ... instead of a progress bar. Pausing clears the activity when disabled". `SHOW_SERVER_NAME` replaces the activity name "Jellyfin" with the server name. `IMDB_EXTERNAL_LINKS` prioritizes IMDb links. `SEEK_THRESHOLD` resyncs the elapsed timer on seeks — [README](https://github.com/kennethsible/jellyfin-rpc)
- mrzeappleGit: type 3 WATCHING by default and 2 LISTENING for Audio. When paused it appends "(paused)" to state and omits timestamps. Position tracking is disabled while paused so the seek detector doesn't misfire ("every pause looks like a seek"). It clears on playback stop. Sessions are sorted to prefer non-paused sessions — [PresenceService.cs](https://github.com/mrzeappleGit/jellyfin-plugin-discord-presence/blob/main/Jellyfin.Plugin.DiscordPresence/PresenceService.cs)
- Phew CLI: progress bar while playing, slower polling when paused, presence cleared after 3 min paused. "Discord hides [buttons] on your own profile" — [Phew/jellyfin-rpc](https://github.com/Phew/jellyfin-rpc)
- exy: "Buttons are only visible to other users in Discord". Users must enable "Display current activity as a status message" under Activity Privacy — [exy README](https://github.com/xyxxyxxy/exy)
- kennethsible requires Discord's Activity Sharing settings to be enabled — [README](https://github.com/kennethsible/jellyfin-rpc)

### Inferences
- Recommended defaults from prior art: Watching/Listening types; `status_display_type` = details (title); timestamps only while playing, with a resync on seek above ~10 s; on pause, either keep presence with a paused small-image and no timer, or clear after a configurable timeout; debounce clears (~2-5 s) to avoid flicker between episodes; build buttons from provider IDs and skip localhost URLs.
- Users commonly report "buttons don't work", but buttons are simply invisible on your own profile. Document this up front.

### Gaps
- I didn't check whether Discord now renders a native progress bar for Watching-type activities differently from the elapsed/remaining timer (the docs fetched don't describe rendering).

## 5. Licenses and reuse

### Takeaway
Permissive (MIT) sources: kennethsible/jellyfin-rpc, mrzeappleGit and Pankyop server plugins, MBCord, yhqe/jellycord. Copyleft: Radiicall jellyfin-rpc (GPL-3.0) and DRPP (AGPL-3.0). Unlicensed (no reuse rights): Phew, adwerygaming, Ray-kong. exy is Unlicense (public domain).

### Cited Findings
- License SPDX IDs from the GitHub API: justradical/jellyfin-rpc GPL-3.0; kennethsible/jellyfin-rpc MIT; phin05/discord-rich-presence-plex AGPL-3.0; mrzeappleGit/jellyfin-plugin-discord-presence MIT; Pankyop/... MIT; SandwichFox and oonqt MBCord MIT; xyxxyxxy/exy Unlicense; cmerk2021/discord-jellyfin-rpc GPL-3.0; Phew/jellyfin-rpc none; adwerygaming none; Ray-kong none; crobibero/jellyfin-plugin-discord MIT (archived) — [GitHub API per repo, e.g.](https://github.com/kennethsible/jellyfin-rpc)

### Inferences
- Jellyfin server plugins link against GPL-licensed Jellyfin assemblies, and the official Jellyfin plugin template/ecosystem is commonly GPL. A server-plugin project could therefore reuse GPL-3.0 code (Radiicall) more naturally than a standalone MIT app could. MIT code (kennethsible's TMDB/CAA artwork logic, mrzeapple's headless-sessions client) is reusable either way with attribution.

### Gaps
- I didn't verify whether Jellyfin requires plugins in its official repository to be GPL (outside scope and call budget).
