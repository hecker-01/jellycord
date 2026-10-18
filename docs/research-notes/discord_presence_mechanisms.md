# Mechanisms for a server-side program to set a Discord user's Rich Presence (as of Oct 2026)

Note: Discord moved its developer docs from discord.com/developers/docs/* to docs.discord.com/developers/* (old URLs return 301). All doc citations below are the new URLs, fetched 2026-10-18.

## 1. Can a Discord bot token set a *user's* presence/activity? What can a bot do instead?

### Takeaway
No. Gateway opcode 3 (Update Presence) only changes the presence of the connection's own account. For a bot, that is the bot's own presence, and only `name`, `state`, `type` and `url` are honored, so a bot cannot show images, timestamps or buttons even on itself. A bot can show something like "Watching <title>" on its own profile, or post now-playing embeds/messages to a channel.

### Cited Findings
- Update Presence (op 3) fields are `since`, `activities`, `status`, `afk`. "Bot users are only able to set `name`, `state`, `type`, and `url`." — [Gateway Events docs](https://docs.discord.com/developers/events/gateway-events)
- Rate limit on presence updates: "Clients may only update their game status 5 times per 20 seconds." — [Gateway Events docs](https://docs.discord.com/developers/events/gateway-events)
- Activity type 3 (Watching) renders as "Watching {name}". Streaming (1) URLs only work for twitch.tv and youtube.com. — [Gateway Events docs](https://docs.discord.com/developers/events/gateway-events)
- The OAuth2 scope `activities.write` ("allows your app to update a user's activity") is marked "not currently available for apps". `activities.read` is also "not currently available for apps". — [OAuth2 docs](https://docs.discord.com/developers/topics/oauth2)
- Bots receiving other users' presences can see button labels but not URLs: "Bots cannot access a user's activity button URLs." — [Gateway Events docs](https://docs.discord.com/developers/events/gateway-events)
- Existing Jellyfin/Discord server-side integrations are notification or bot based (jellyfin-plugin-DiscordNotifier posts server events to a channel; "Jellybot" offers a /nowplaying command), not user Rich Presence. — [JellyWatch blog (secondary)](https://jellywatch.app/blog/jellyfin-discord-telegram-bot-now-playing-commands-2026)

### Inferences
- No REST endpoint or gateway opcode lets a bot write to another user's presence. A bot-based design can only (a) set the bot's own status, e.g. type 3 "Watching <title>", which suits a single-user server and loses images, progress bar and buttons, or (b) post or edit rich embeds (poster thumbnail, progress text, links) in a guild channel or DM.

### Gaps
- No doc gives a reason or roadmap for `activities.write` becoming available.

## 2. Local IPC/RPC (discord-ipc-N pipe, SET_ACTIVITY)

### Takeaway
SET_ACTIVITY over the local IPC pipe needs only an Application (client) ID. No token or OAuth is needed for presence, and it is the mechanism every existing Jellyfin RPC tool uses (jellyfin-rpc, pypresence, discord-rpc-csharp). The catch is that it has to run on the same machine (or Android device) as the user's logged-in Discord client. A headless Jellyfin server cannot reach a client on another PC this way. Watching (3), Listening (2) and Competing (5) have been accepted over RPC since July 25, 2024. `status_display_type` and the clickable `*_url` fields arrived in July/August 2025.

### Cited Findings
- IPC pipe paths: Windows `\\?\pipe\discord-ipc-{n}`. On Linux/macOS, Discord checks `XDG_RUNTIME_DIR`, `TMPDIR`, `TMP`, `TEMP`, then falls back to `/tmp`, with filename `discord-ipc-{n}`. The handshake is opcode 0 with `v` and `client_id`, and Discord replies with a FRAME containing `READY`. — [RPC docs](https://docs.discord.com/developers/topics/rpc)
- SET_ACTIVITY takes `pid` and an `activity` object. The documented example fields are `state`, `state_url`, `details`, `details_url`, `timestamps.start/end`, `assets.large_image/large_text/large_url/small_image/small_text/small_url`, `party`, `secrets` and `instance`. Allowed types are Playing 0, Listening 2, Watching 3 and Competing 5. — [RPC docs](https://docs.discord.com/developers/topics/rpc)
- Changelog, July 25, 2024: SET_ACTIVITY "has been updated to support 3 additional activity types: Listening (2), Watching (3), and Competing (5)." — [Change Log](https://docs.discord.com/developers/change-log)
- Changelog, July 17, 2025: "You can now choose which field (name, state, or details) is used in users' status text in the member list" (`status_display_type`). The same entry added clickable links for state/details text and large/small images. — [Change Log](https://docs.discord.com/developers/change-log)
- `status_display_type`: 0 = Name, 1 = State, 2 = Details. It applies to all activity types. — [Gateway Events docs](https://docs.discord.com/developers/events/gateway-events)
- Changelog, April 3, 2026: official documentation for "RPC over IPC" transport published. — [Change Log](https://docs.discord.com/developers/change-log)
- Unauthenticated Rich Presence via RPC: "The Discord desktop client must be running on the user's machine." It works on desktop and on Android from Social SDK 1.10+. iOS, console and web are not supported. — [Social SDK: Setting Rich Presence](https://docs.discord.com/developers/discord-social-sdk/development-guides/setting-rich-presence)
- Changelog, July 28, 2026 (Social SDK 1.10.18247): Android "Rich Presence via an unauthenticated RPC transport, matching desktop's existing capability." — [Change Log](https://docs.discord.com/developers/change-log)
- Restriction caveats: the RPC page says "We currently do not allow access to RPC for unapproved apps without being on the game's list of testers". The WebSocket RPC transport is deprecated and "only available for old participants of private beta". Scopes `rpc` and `rpc.activities.write` are "only available to approved partners". — [RPC docs](https://docs.discord.com/developers/topics/rpc); [OAuth2 docs](https://docs.discord.com/developers/topics/oauth2)
- Real-world practice: existing Jellyfin presence tools (e.g. a Node.js app needing "the Discord Desktop client", a Python app needing a "Discord application Client ID") run client-side and poll the Jellyfin API. jellyfin-rpc (Rust) is the most active, last pushed Mar 21, 2026, ~487 stars. — [adwerygaming/jellyfin-discord-presence](https://github.com/adwerygaming/jellyfin-discord-presence); [Ray-kong/jellyfin-discord-rich-presence](https://github.com/Ray-kong/jellyfin-discord-rich-presence); [gittrend jellyfin-rpc](https://gittrend.io/repo/JustRadical/jellyfin-rpc)

### Inferences
- The "approved apps only" restriction applies to the authenticated RPC commands (AUTHORIZE/AUTHENTICATE with the `rpc` scopes) and the WebSocket transport. SET_ACTIVITY over IPC with only a client_id is the "unauthenticated" path that the Social SDK docs describe as available to any app with a valid Application ID. The many working community tools (pypresence, arRPC, jellyfin-rpc) confirm this in practice.
- For a Jellyfin plugin, this means a companion app on each viewer's desktop, either polling Jellyfin or receiving pushes from the plugin over WebSocket/SSE and writing to the local pipe. The plugin on the server cannot write to the pipe directly unless the server and the Discord client share a machine.
- The 2026 Android RPC support works through apps using the Social SDK on the phone. It does not help a remote server.

### Gaps
- No doc states whether IPC SET_ACTIVITY is ever rate-limited or blocked for apps that haven't been verified. None was found, and community use suggests not.

## 3. OAuth2 `activities.write`, the Social SDK, and server-side/headless presence

### Takeaway
No documented HTTP endpoint sets a user's Rich Presence. `activities.write` is "not currently available for apps", and I found no trace of a "headless-sessions" endpoint. The one sanctioned way to set presence without the user's local client is the Discord Social SDK's authenticated mode. The user links their account via OAuth (`openid sdk.social_layer_presence`), and an SDK Client connected to Discord's gateway with that user's token sets presence for them. A community daemon (easy-presence) runs this headless on Linux, one SDK connection per linked user. Presence features need no approval application; only the communication features (chat/voice/lobbies) sit behind a "Comms Access" approval and rate limit. Caveats: the SDK is closed-source and native (C++), a .NET plugin would need to bundle or P/Invoke the native library, redistributing the SDK is not allowed, and it is designed and licensed for games.

### Cited Findings
- `activities.write` — "allows your app to update a user's activity - not currently available for apps" (not required for the GameSDK Activity Manager). `rpc.activities.write` is "only available to approved partners". — [OAuth2 docs](https://docs.discord.com/developers/topics/oauth2)
- Searches for "headless-sessions" turned up no official Discord API documentation (my search, Oct 2026). Treat any such endpoint as undocumented or nonexistent.
- The Social SDK "Use with Discord APIs" page advises using HTTP APIs from a game backend "when possible". Its only examples are OAuth token exchange, `/users/@me` and lobby creation. It names no rich-presence HTTP endpoint. — [Use with Discord APIs](https://docs.discord.com/developers/discord-social-sdk/how-to/use-with-discord-apis)
- Social SDK was introduced March 17, 2025. Release 1.1 (April 16, 2025) added "sending rich presence updates and invites without connecting to the Discord gateway on desktop". 1.2 (May 5, 2025) added custom buttons via `Activity::AddButton`. 1.5 (Aug 15, 2025) added Listening, Watching and Competing types plus clickable URL fields. — [Change Log](https://docs.discord.com/developers/change-log)
- Authenticated presence requires a connected Social SDK client and scopes `openid` and `sdk.social_layer_presence`. `Client::GetDefaultPresenceScopes` returns `openid sdk.social_layer_presence`. `sdk.social_layer` adds communication features, which "are currently available but have limited access". — [Setting Rich Presence](https://docs.discord.com/developers/discord-social-sdk/development-guides/setting-rich-presence); [Social SDK OAuth2 Scopes](https://docs.discord.com/developers/discord-social-sdk/core-concepts/oauth2-scopes)
- Getting access: create an application, then "Enable Discord Social SDK" in the Developer Portal. The SDK is free to download. Full access to communication features requires an approval submission ("Comms Access") with game-specific criteria (account linking, Rich Presence with Joins, documentation of your game). — [Discord blog](https://discord.com/blog/discords-powerful-cross-platform-chat-ready-for-your-game); [Communication Features docs](https://docs.discord.com/developers/discord-social-sdk/core-concepts/communication-features)
- easy-presence (community): a C++ daemon that "holds one Discord connection per linked user". The backend supplies each user's token in a `link` message. It runs headless (needs `libpulse0`, no display or audio server), uses about 4–5 MB per extra user, and you must "turn on Social SDK access" and download the SDK yourself because Discord "doesn't allow redistributing it". If the process dies uncleanly, "Discord can keep showing that user's presence for a while after." — [LunathDePixel/easy-presence](https://github.com/LunathDePixel/easy-presence)
- A Music Assistant maintainer discussion (Feb 4, 2026) says there is no public HTTP API for rich presence and that the server-side Social SDK is "theoretically possible". It flags the closed-source SDK, x86-64 Linux only with glibc/libasound/libpulse/libX11 dependencies, and suggests that each user would need their own developer application. It also speculates that PreMiD uses the Social SDK server-side. — [music-assistant discussion #2041](https://github.com/orgs/music-assistant/discussions/2041)
- The Social SDK Terms say Discord may "change, suspend, limit the availability of, or discontinue" the SDK and require that apps not serve users under 13 (quoted via search snippet; the terms page returned 403 to direct fetch). — [Discord Social SDK Terms](https://support-dev.discord.com/hc/en-us/articles/30225844245271-Discord-Social-SDK-Terms)

### Inferences
- Architecture: a Jellyfin plugin (or a sidecar the plugin talks to) can use one shared Discord application owned by the server admin, run an OAuth "Link Discord" flow per Jellyfin user (with a confidential client the token exchange happens server-side), store or refresh tokens, and keep a Social SDK Client per actively watching user to call `UpdateRichPresence`. This is the only route that works when viewers watch on TVs or phones with no Discord desktop client nearby.
- The "each user needs their own developer app" claim in the Music Assistant thread conflicts with easy-presence's one-app, many-users design. It probably reflects the self-hosted distribution problem: an open-source plugin cannot ship a shared app secret or redistribute the SDK, so each Jellyfin admin must create their own Discord app and download the SDK. Their end users need not.
- Platform risk: the native SDK binaries are per-OS/arch. Docker/ARM Jellyfin hosts (e.g. Raspberry Pi) may not be supported, so check current SDK platform downloads. Whether Discord would object to a non-game media app using the Social SDK is uncertain, since the terms text could not be read directly. Flag this as a licensing risk.

### Gaps
- I could not read the Social SDK Terms directly (403), so I can't confirm whether non-game use is allowed.
- No doc confirms how an SDK-gateway presence interacts with the user's own desktop client presence (multi-session merging), or whether a headless connection shows the user as "online".
- The current list of supported SDK platforms (Linux ARM64? Windows server?) was not verified.

## 4. Rich Presence images: external HTTPS URLs, mp:external, limits

### Takeaway
Yes. `large_image`/`small_image` can be any publicly accessible image URL instead of an uploaded asset key. Discord proxies it and reports it back as `mp:{image_id}` (commonly `mp:external/...`). External URLs also support GIF, animated WebP and AVIF. Uploaded assets are limited to PNG/JPEG/WebP (static), at most 300 per app, 1024x1024 recommended. The image field length is limited to 1–300 characters (SDK reference), which rules out long signed URLs.

### Cited Findings
- Image values are an application asset ID or a media proxy reference `mp:{image_id}`. To use an external image, send the URL as the value. Uploaded assets support PNG, JPEG and WebP (not animated). External URLs support "any publicly accessible image", including GIF, animated WebP and AVIF. — [Gateway Events docs](https://docs.discord.com/developers/events/gateway-events)
- External URLs are allowed "if they meet the required dimensions and size". Uploaded assets: up to 300 per app, 1024x1024 recommended, keys lowercased. — [Setting Rich Presence](https://docs.discord.com/developers/discord-social-sdk/development-guides/setting-rich-presence)
- ActivityAssets: image fields accept an uploaded asset key or an external image URL. Large image length is 1–300 characters. — [Social SDK ActivityAssets reference](https://discord.com/developers/docs/social-sdk/classdiscordpp_1_1ActivityAssets.html)
- History: about two years ago, a Discord-side maintainer told the Dolphin emulator team that external URLs were "not documented, but ... available for all applications & users". — [Dolphin issue #12775](https://bugs.dolphin-emu.org/issues/12775)
- Changelog, Oct 15, 2025 (Social SDK 1.6.12170): `inviteCoverImage` accepts a URL or asset key, and `activity.name` can override the displayed application name. — [Change Log](https://docs.discord.com/developers/change-log)

### Inferences
- Jellyfin poster URLs (e.g. `https://jellyfin.example/Items/{id}/Images/Primary`) only work if the server is reachable from the public internet without auth. Discord's proxy must fetch them. LAN-only servers need an image relay or a public metadata image (TMDB/IMDb poster URL). Avoid embedding Jellyfin API keys in the URL: the proxy fetches it, and the field is user-visible.

### Gaps
- No exact maximum file size or pixel dimensions for external images were found ("required dimensions and size" is not quantified in the fetched text). No dated changelog entry marks when external URLs became official.

## 5. Watching type, timestamps/progress bar, buttons

### Takeaway
Use type 3 (Watching) with both `timestamps.start` and `timestamps.end` to get a progress bar. Up to 2 buttons ({label 1–32 chars, url 1–512 chars}), visible only to other users. `details_url`/`state_url`/`large_url`/`small_url` make text and images clickable, and `status_display_type` controls which field appears in the member list.

### Cited Findings
- "For Listening and Watching activities, you can include both start and end timestamps to display a time bar." Timestamps are Unix milliseconds in the gateway activity object. — [Gateway Events docs](https://docs.discord.com/developers/events/gateway-events)
- In the Social SDK, timestamps are Unix seconds. Only `start` counts up, `end` counts down, and both set shows remaining time. — [Setting Rich Presence](https://docs.discord.com/developers/discord-social-sdk/development-guides/setting-rich-presence)
- At most 2 buttons, each `label` (1–32 chars) and `url` (1–512 chars). When received, `buttons` holds labels only. — [Gateway Events docs](https://docs.discord.com/developers/events/gateway-events)
- Buttons are "only visible to other users". — [Setting Rich Presence](https://docs.discord.com/developers/discord-social-sdk/development-guides/setting-rich-presence); [easy-presence README](https://github.com/LunathDePixel/easy-presence)
- `details_url`, `state_url`, `assets.large_url`, `assets.small_url` added (Embedded App SDK 2.1/2.2, Aug 11, 2025; RPC/gateway July 17, 2025). — [Change Log](https://docs.discord.com/developers/change-log)
- Third-party claim: with Watching, Discord renders season/episode formatting (e.g. "S2E5") when the large image text follows a certain pattern. Unverified against Discord docs. — [PreMiD PresenceData docs](https://docs.premid.app/v1/api/presence-data.html)

### Inferences
- On pause, clear the timestamps (or set state "Paused") and re-send on resume/seek, recomputing start/end from the playback position. Remember the gateway-level 5 updates per 20 s limit if updating via a gateway connection.

### Gaps
- The S/E formatting rule and the exact rendering of `status_display_type` with type 3 were not confirmed in first-party docs.

## 6. ToS: user tokens / selfbots (to rule out)

### Takeaway
Ruled out. Driving a user's account with their user token (selfbot), for example by sending op 3 with the user's token from the server, violates Discord policy and risks account termination for the end user.

### Cited Findings
- "Automating normal user accounts (generally called 'self-bots') outside of the OAuth2/bot API is forbidden, and can result in an account termination if found." — [Discord Support: self-bots](https://support.discord.com/hc/articles/115002192352)
- Selfbot libraries themselves warn that automating a user account "is against Discord's Terms of Service. This may result in termination." — [jalter (npm/GitHub)](https://github.com/sinkaroid/jalter)
- An official-marked forum comment calls selfbots "against the OAuth policy" with possible permanent account loss. This is secondhand. — [Discord community post](https://support.discord.com/hc/ko/community/posts/360042613891-I-can-t-find-anything-in-the-Terms-or-C-Guidelines-on-selfbots)

### Inferences
- The Social SDK OAuth route (section 3) is the legitimate counterpart. It also uses a per-user credential, but obtained through OAuth with documented scopes, so it falls under "OAuth2" rather than selfbotting.

### Gaps
- None material.

## Summary decision matrix (for the report writer)
| Mechanism | Sets user's presence? | Needs Discord client on same machine? | Credentials | Approval | Status 2026 |
|---|---|---|---|---|---|
| Bot op 3 | No (bot's own only; name/state/type/url) | No | Bot token | None | Works, limited |
| Bot channel embeds | No (messages) | No | Bot token | None | Works |
| Local IPC SET_ACTIVITY | Yes, full RP (Watching, images, timestamps, buttons) | Yes (desktop; Android via SDK 1.10+) | App ID only | None in practice | Works; most common |
| OAuth `activities.write` / REST | Would be | No | OAuth | "Not currently available for apps" | Not usable |
| Social SDK authenticated client (headless daemon) | Yes | No | Per-user OAuth token (`openid sdk.social_layer_presence`) | Enable SDK in portal; presence not gated | Works per community daemon; closed-source native lib, game-oriented terms, no redistribution |
| User token / selfbot | Yes | No | User token | Forbidden | ToS violation |
