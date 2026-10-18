# Jellycord

A Jellyfin plugin that shows what you're watching as your **Discord status**: "Watching Breaking Bad · S5E14 · Ozymandias", with the poster and a live progress bar. It works for every user on the server who links their Discord, on any device (TV, phone, browser). Nobody needs the Discord desktop app running.

> **Status: early (v0.1, Jellyfin 12.x).** Built against Jellyfin 12.0 / .NET 10, and loads and tracks playback on 12.2. The Discord side uses an undocumented endpoint, so please report what works and what doesn't.

## Why not a bot token?

A Discord bot can only set **its own** status, and even then only plain text, no poster or progress bar. Discord gives third-party apps no supported way to set a user's status with a bot token, and using your own user token is self-botting, which can get your account banned.

Jellycord uses Discord's **Social SDK presence** instead. Each server admin creates their own (free, self-serve) Discord application, and each user links their Discord account once through OAuth. The server then sets their status via Discord's `headless-sessions` API. See [docs/research.md](docs/research.md) for the full write-up.

## Setup

### 1. Create a Discord application (admin, once)

1. Go to <https://discord.com/developers/applications> → **New Application**. Any name works; the status shows the media title, not the app name.
2. **OAuth2** → turn on **Public Client** → Save. (If you'd rather keep it off, copy the **Client Secret** for step 3.)
3. **Discord Social SDK → Getting Started**: fill in the short form. It's self-serve, with no review.
4. Copy the **Application ID** from *General Information*.

### 2. Install the plugin

Dashboard → **Plugins → Repositories → +** and add:

```
https://raw.githubusercontent.com/hecker-01/jellycord/main/manifest.json
```

Then **Catalog → Jellycord → Install**, and restart Jellyfin.

Manual install: download the zip from [Releases](https://github.com/hecker-01/jellycord/releases), unzip it into `<jellyfin data>/plugins/Jellycord/`, and restart.

### 3. Configure

Dashboard → Plugins → **Jellycord**:

- **Application ID**: from step 1.
- **Public server URL** (optional): `https://jellyfin.example.com`. Discord must be able to reach it for posters to show. Image URLs never include an API key.
- **Fallback image** (optional): an https image URL, or the name of an art asset uploaded to your Discord app, shown when there's no poster.
- Choose what to show (movies / episodes / music), whether to keep the status while paused, and the clear delay.

### 4. Link users

Each user opens **`https://<your-jellyfin>/Jellycord/Link`** while signed in to Jellyfin web, clicks **Link Discord**, and approves the code at `discord.com/activate`. The link page and the admin page both show the URL. Admins can see and unlink accounts on the settings page.

## What it shows

| Playing | Discord shows |
|---|---|
| Episode | **Watching** *Series* · `S1E2 · Episode title` · series poster · progress bar |
| Movie | **Watching** *Title* · year · poster · progress bar |
| Music | **Listening to** *Track* · `by Artist · Album` · album art |
| Paused | Same, with "Paused" and no progress bar (or cleared, if configured) |

Theme songs, trailers and other extras are ignored. If a user plays on several devices at once, the one actually playing wins.

## Privacy and caveats

- **The Discord permission is broader than presence.** Discord has no presence-only scope; `sdk.social_layer_presence` also grants access to the user's friends list. Jellycord only sets the status. The link page tells users this, and they can revoke access any time under *Discord → Settings → Authorized Apps*.
- Tokens are stored in `<jellyfin data>/plugins/configurations/Jellyfin.Plugin.Jellycord.credentials.json` (owner-only on Linux), outside the plugin configuration, so they're never sent to a browser.
- `headless-sessions` is not in Discord's public docs. Discord could change or gate it. Jellycord delivers presence through a swappable "sink", so a desktop-companion (local RPC) fallback can be added without a rewrite.
- Only approve link codes you generated yourself.

## Development

Requires the .NET 10 SDK.

```bash
dotnet test
```

```bash
dotnet publish src/Jellyfin.Plugin.Jellycord -c Release -o out
```

To check the Discord API against your own app before installing the plugin, run the spike:

```bash
dotnet run --project spike/HeadlessSpike -- --client-id <your application id>
```

It walks through linking, sets a test "Watching Big Buck Bunny" status, tries a paused update, then deletes the session.

### Layout

```
src/Jellyfin.Plugin.Jellycord/
  Playback/   PlaybackTracker (session events → state, 1s decision loop), PresenceDecision (debounce/seek/heartbeat rules)
  Presence/   PresenceMapper (item → activity), IPresenceSink
  Discord/    DiscordClient (OAuth device flow + headless-sessions), HeadlessSessionSink, CredentialStore
  Api/        JellycordController (link page + endpoints)
  Web/        configPage.html (admin), linkPage.html (users)
spike/HeadlessSpike/   standalone API check
docs/                  research report and notes
```

### Releasing

Push a tag like `v0.1.0`. The release workflow builds the zip, creates a GitHub release, and adds the version to `manifest.json`.

## Roadmap

- Poster lookups for LAN-only servers (TMDB, Cover Art Archive, opt-in temporary image host)
- Desktop companion sink (local Discord RPC) as a fallback
- IMDb / TMDB buttons, custom text templates, library allow/deny lists
- Jellyfin 10.11 build

## Credits

The headless-sessions flow follows [mrzeappleGit/jellyfin-plugin-discord-presence](https://github.com/mrzeappleGit/jellyfin-plugin-discord-presence) (MIT), which first documented the endpoint's behaviour.

## License

MIT
