# Jellyfin Server Plugin Architecture (C#/.NET): playback events, config page, custom repository (current as of Oct 2026)

> **Big headline for the report writer:** The ask covered "10.10 and 10.11, and anything newer." Something newer has shipped. Jellyfin dropped the "10." prefix, and **Jellyfin 12.0 was released 08 Sep 2026** (12.1 on 15 Sep, 12.2 on 05 Oct 2026). 12.x targets **.NET 10**, and every third-party plugin has to be retargeted and rebuilt for it. A new plugin started in Oct 2026 should target 12.x first and add a 10.11 build only if needed. — [Jellyfin releases](https://github.com/jellyfin/jellyfin/releases); [v12.0 release notes](https://github.com/jellyfin/jellyfin/releases/tag/v12.0)

## Target framework, NuGet package versions, and targetAbi per server version

### Takeaway
The rule is one server line, one .NET version: 10.9/10.10 use net8.0, 10.11 uses net9.0, and 12.x uses net10.0. Reference `Jellyfin.Controller` and `Jellyfin.Model` at the server version you target, with `ExcludeAssets=runtime`. Set targetAbi to the **minimum** server version (e.g. `10.11.0.0`, `12.0.0.0`). The catalog offers each server the newest build whose targetAbi it satisfies.

### Cited Findings
- The current template csproj (master) uses `<TargetFramework>net9.0</TargetFramework>`, `Jellyfin.Controller` **10.11.5** and `Jellyfin.Model` **10.11.5**, both with `<ExcludeAssets>runtime</ExcludeAssets>`. It also sets `Nullable=enable`, `TreatWarningsAsErrors`, StyleCop/Serilog analyzers, and `<EmbeddedResource Include="Configuration\configPage.html" />`. — [template csproj](https://raw.githubusercontent.com/jellyfin/jellyfin-plugin-template/master/Jellyfin.Plugin.Template/Jellyfin.Plugin.Template.csproj)
- Template csproj history: "Upgrade template to 10.9.x and .NET 8" (2024-10-05), "Update dependency Jellyfin.Controller to 10.9.11", then "Upgrade to Jellyfin 10.11.x (#86)". The template never had a dedicated 10.10 commit, and **it had not been updated for 12.x** when fetched. — [GitHub commits API for csproj](https://api.github.com/repos/jellyfin/jellyfin-plugin-template/commits?path=Jellyfin.Plugin.Template/Jellyfin.Plugin.Template.csproj&per_page=20)
- Template `build.yaml`: guid `eb5d7894-8eef-4b36-aa6f-5d124e828ce1`, version `1.0.0.0`, **targetAbi `10.11.0.0`**, framework `net9.0`, category General, owner jellyfin, artifacts `Jellyfin.Plugin.Template.dll`, plus overview, description and changelog fields. — [template build.yaml](https://raw.githubusercontent.com/jellyfin/jellyfin-plugin-template/master/build.yaml)
- 10.11.0 changelog includes "Update projects to .NET 9" (PR #13023) and the library.db to jellyfin.db EF Core refactor (PR #12798). — [v10.11.0 release](https://github.com/jellyfin/jellyfin/releases/tag/v10.11.0)
- 12.0: "The server now targets .NET 10. Plugins have to be retargeted and rebuilt." Users are told to remove non-built-in repository plugins before migrating. — [v12.0 release](https://github.com/jellyfin/jellyfin/releases/tag/v12.0)
- NuGet `Jellyfin.Controller` versions: 12.2.0 (2026-10-05, targets net10.0), 12.1.0 (2026-09-15), 12.0.0 (2026-09-08). The 10.11 line ends at 10.11.11 (2026-06-06), after 10.11.10 (05-24), 10.11.9 (05-21), 10.11.8 (04-05) and 10.11.7 (03-31). The page did not load the 10.10.x entries. — [NuGet Jellyfin.Controller](https://www.nuget.org/packages/Jellyfin.Controller/)
- Template README: "Ensure the package reference version matches the install version of jellyfin server". A mismatch makes the plugin show as **NotSupported**. — [jellyfin-plugin-template README](https://github.com/jellyfin/jellyfin-plugin-template)
- Community reports say targetAbi works as a *minimum* server version. A 10.10.7 user cannot see a plugin version with targetAbi 10.11.0.0 in the catalog. — [jellyfin-plugin-tvheadend issue #106](https://github.com/jellyfin/jellyfin-plugin-tvheadend/issues/106)
- ABI drift within one minor line: plugins built against the 10.11.0 SDK threw `MissingMethodException` on 10.11.9/10.11.10 because a host interface member's return type changed (user list, session properties). Jellyscribe responded by setting targetAbi to 10.11.9 for its v1.13.0. — [Jellyscribe v1.13.0 notes](https://newreleases.io/project/github/builtbyproxy/Jellyscribe/release/v1.13.0); [Jellyscribe v1.13.3](https://newreleases.io/project/github/builtbyproxy/Jellyscribe/release/v1.13.3)
- Dual-targeting example: Posterizarr 3.3.2 ships a .NET 9 build (targetAbi 10.11.0.0) and a .NET 10 build (targetAbi 12.0.0.0, "-jf12" zip) in one manifest. — [posterizarr 3.3.2](https://newreleases.io/project/github/fscorrupt/posterizarr/release/3.3.2)
- Meilisearch plugin 1.12.1.1 moved to net10.0 and changed its Jellyfin dependency from a pinned 12.0.0-rc2 to `12.0.*`. — [meilisearch 1.12.1.1](https://newreleases.io/project/github/arnesacnussem/jellyfin-plugin-meilisearch/release/1.12.1.1)
- Third-party plugins stop working on 12 until they are rebuilt for .NET 10 (Notebookcheck, German). — [Notebookcheck](https://www.notebookcheck.com/Grosses-Jellyfin-Update-ueberarbeitet-Buch-Support-und-Playlists-erfordert-aber-angepasste-Plugins.1393186.0.html)

### Inferences
- Practical matrix:
  | Server | TFM | NuGet Jellyfin.Controller/Model | targetAbi |
  |---|---|---|---|
  | 10.9.x / 10.10.x | net8.0 | 10.9.x / 10.10.x | 10.9.0.0 / 10.10.0.0 |
  | 10.11.x | net9.0 | 10.11.x (10.11.11 latest; mind the 10.11.9 ABI break) | 10.11.0.0 (or 10.11.9.0 if you touch the changed members) |
  | 12.x | net10.0 | 12.0.0 to 12.2.0 | 12.0.0.0 |
- The net8.0 for 10.10 entry is inferred: the template went to net8 at 10.9, and 10.11 is where .NET 9 arrived. No fetched page states it directly.
- To cover several server lines, either multi-target with conditional PackageReferences (`net9.0;net10.0`) or keep a branch per line. Publish a separate zip per line with its own targetAbi in the same manifest, as Posterizarr does.
- A small playback-events plugin only touches ISessionManager, ILibraryManager, IUserManager and BaseItem. In 12.0 the relevant break is `IUserManager.Users` becoming `GetUsers()`. Mostly-shared source with `#if` is feasible.

### Gaps
- No exact 10.10.x NuGet version list was retrieved (NuGet "load more" failed). The latest 10.10 is believed to be 10.10.7, based on the 12.0 upgrade notes requiring "10.10.7+", not on NuGet.
- No official Jellyfin documentation page defines targetAbi semantics. The evidence is community reports plus observed catalog behaviour.

## Project template structure (Plugin.cs, PluginConfiguration, configPage.html, build.yaml, meta.json)

### Takeaway
Minimal layout: a `Plugin : BasePlugin<PluginConfiguration>, IHasWebPages` with a fixed GUID and a static `Instance`, a `Configuration/PluginConfiguration.cs : BasePluginConfiguration`, and `Configuration/configPage.html` compiled as an EmbeddedResource. jprm/CI read `build.yaml`. The server reads `meta.json` from the installed plugin folder (the repo tooling generates it at install time).

### Cited Findings
- Template `Plugin.cs`: a constructor `(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer) : base(...)` that sets `Instance = this`, overrides `Name` and `Id => Guid.Parse("eb5d7894-...")`, and `GetPages()` returns a `PluginPageInfo { Name = Name, EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html" }`. Namespaces used: `MediaBrowser.Common.Configuration`, `MediaBrowser.Common.Plugins`, `MediaBrowser.Model.Plugins`, `MediaBrowser.Model.Serialization`. — [template Plugin.cs](https://raw.githubusercontent.com/jellyfin/jellyfin-plugin-template/master/Jellyfin.Plugin.Template/Plugin.cs)
- README steps: `dotnet new classlib -f net9.0`, add the Jellyfin.Model/Jellyfin.Controller packages, make PluginConfiguration inherit `BasePluginConfiguration`, set a GUID (`New-Guid` / `uuidgen`), and copy the DLL (and PDB for debugging) into a subfolder of the plugins directory. — [template README](https://github.com/jellyfin/jellyfin-plugin-template)
- README: "your main plugin class (IBasePlugin) cannot also be a IHostedService". Other extension interfaces: IScheduledTask (appears in the dashboard's scheduled tasks), IItemResolver, IIntroProvider, and ControllerBase for REST endpoints. Core services such as ILibraryManager, IUserManager and ITaskManager are constructor-injected. — [template README](https://github.com/jellyfin/jellyfin-plugin-template)
- 12.0: `IHasEmbeddedImage` is only for plugins compiled into the server. External plugins should keep declaring their image with `imagePath` in `meta.json`. — [v12.0 release](https://github.com/jellyfin/jellyfin/releases/tag/v12.0)
- 12.0: the installer now rejects unsafe plugin package names. — [v12.0 release](https://github.com/jellyfin/jellyfin/releases/tag/v12.0)

### Inferences
- Recommended layout for the Discord-presence plugin:
  ```
  Jellyfin.Plugin.Jellycord/
    Plugin.cs                         // BasePlugin<PluginConfiguration>, IHasWebPages
    PluginServiceRegistrator.cs       // IPluginServiceRegistrator
    PlaybackListener.cs               // IHostedService that subscribes to ISessionManager events
    Api/JellycordController.cs        // ControllerBase
    Configuration/PluginConfiguration.cs
    Configuration/configPage.html     // EmbeddedResource
    Jellyfin.Plugin.Jellycord.csproj
  build.yaml                          // jprm metadata: guid, targetAbi, framework, artifacts
  ```
- Keep `Name` and `Id` in Plugin.cs identical to `build.yaml`'s name and guid, and to `pluginUniqueId` in the config page JS.

### Gaps
- The exact `meta.json` schema was not fetched. From memory it holds guid, name, overview, description, owner, category, version, changelog, targetAbi, timestamp, status, autoUpdate and imagePath. Verify against a real installed plugin folder before relying on it.

## Replacement for IServerEntryPoint (removed in 10.9): IPluginServiceRegistrator + IHostedService

### Takeaway
Implement `IPluginServiceRegistrator.RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)` (namespace `MediaBrowser.Controller.Plugins`) and call `serviceCollection.AddHostedService<YourListener>()`. The listener implements `Microsoft.Extensions.Hosting.IHostedService` (StartAsync/StopAsync) and subscribes or unsubscribes to ISessionManager events. There is also an `IEventConsumer<TEventArgs>` pattern.

### Cited Findings
- Exact signature: `void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost);` in `namespace MediaBrowser.Controller.Plugins;`. — [IPluginServiceRegistrator.cs](https://raw.githubusercontent.com/jellyfin/jellyfin/master/MediaBrowser.Controller/Plugins/IPluginServiceRegistrator.cs)
- README: Jellyfin discovers IPluginServiceRegistrator at startup. IHostedService runs background code from startup, stays in memory, and must be registered through an IPluginServiceRegistrator. — [template README](https://github.com/jellyfin/jellyfin-plugin-template)
- The official Webhook plugin registers `serviceCollection.AddHostedService<WebhookServerEntryPoint>()` and three more hosted entry points (item added, item deleted, user data saved). It wires playback through the event-consumer pattern, e.g. `serviceCollection.AddScoped<IEventConsumer<PlaybackStartEventArgs>, PlaybackStartNotifier>();`. — [Webhook PluginServiceRegistrator.cs](https://github.com/jellyfin/jellyfin-plugin-webhook/blob/master/Jellyfin.Plugin.Webhook/PluginServiceRegistrator.cs)
- Webhook's `PlaybackProgressNotifier : IEventConsumer<PlaybackProgressEventArgs>` has `public async Task OnEvent(PlaybackProgressEventArgs eventArgs)`. It skips the event if `Users.Count == 0`, `Item is null` or `Item.IsThemeMedia`, then iterates users. The file has no throttling. — [Webhook PlaybackProgressNotifier.cs](https://github.com/jellyfin/jellyfin-plugin-webhook/blob/master/Jellyfin.Plugin.Webhook/Notifiers/PlaybackProgressNotifier.cs)
- SessionManager publishes both ways: `await _eventManager.PublishAsync(eventArgs)` (feeds IEventConsumer<T>) and then `PlaybackProgress?.Invoke(this, eventArgs)` (C# event). — [SessionManager.cs](https://raw.githubusercontent.com/jellyfin/jellyfin/master/Emby.Server.Implementations/Session/SessionManager.cs)

### Inferences
- Two equally valid patterns:
  1. **IHostedService + C# events** (one long-lived singleton, easy to keep per-session state such as last-sent presence):
     ```csharp
     public class PluginServiceRegistrator : IPluginServiceRegistrator
     {
         public void RegisterServices(IServiceCollection services, IServerApplicationHost host)
         {
             services.AddSingleton<PresenceStore>();
             services.AddHostedService<PlaybackListener>();
         }
     }
     public sealed class PlaybackListener(ISessionManager sessions, PresenceStore store, ILogger<PlaybackListener> log) : IHostedService
     {
         public Task StartAsync(CancellationToken ct)
         {
             sessions.PlaybackStart += OnStart; sessions.PlaybackProgress += OnProgress;
             sessions.PlaybackStopped += OnStopped; sessions.SessionEnded += OnSessionEnded;
             return Task.CompletedTask;
         }
         public Task StopAsync(CancellationToken ct) { /* unsubscribe with -= */ return Task.CompletedTask; }
         // handlers ...
     }
     ```
  2. **IEventConsumer<PlaybackStartEventArgs / PlaybackProgressEventArgs / PlaybackStopEventArgs>** registered as scoped (the webhook style). These are stateless per call, so shared state needs a separate singleton.
- In 10.9+ the C# events are `event EventHandler<T>`, and SessionManager raises some of them through `QueueEventIfNotNull`. Handlers must be fast and must not throw. Do network I/O asynchronously, e.g. through a Channel.

### Gaps
- The `IEventConsumer<T>` interface file was not fetched. Its namespace is believed to be `Jellyfin.Data.Events`/`MediaBrowser.Controller.Events` and should be verified against the targeted package.

## ISessionManager playback events, event-args fields, pause state, and progress frequency

### Takeaway
ISessionManager exposes `PlaybackStart`, `PlaybackProgress` (both `EventHandler<PlaybackProgressEventArgs>`), `PlaybackStopped` (`EventHandler<PlaybackStopEventArgs>`), and `SessionStarted`, `SessionEnded`, `SessionActivity`, `SessionControllerConnected` and `CapabilitiesChanged` (`EventHandler<SessionEventArgs>`). Pause state is `eventArgs.IsPaused`, also mirrored in `Session.PlayState.IsPaused`. PlaybackProgress fires on **every** client report with no server-side throttling, so it depends on the client. The server also generates "automated" progress ticks (`IsAutomated == true`).

### Cited Findings
- Events (namespace `MediaBrowser.Controller.Session`): `PlaybackStart`, `PlaybackProgress` (PlaybackProgressEventArgs), `PlaybackStopped` (PlaybackStopEventArgs), and `SessionStarted`, `SessionEnded`, `SessionActivity`, `SessionControllerConnected`, `CapabilitiesChanged` (SessionEventArgs). — [ISessionManager.cs](https://raw.githubusercontent.com/jellyfin/jellyfin/master/MediaBrowser.Controller/Session/ISessionManager.cs)
- `PlaybackProgressEventArgs` (namespace `MediaBrowser.Controller.Library`) properties: `List<User> Users`, `long? PlaybackPositionTicks`, `BaseItem Item`, `BaseItemDto MediaInfo`, `string MediaSourceId`, `bool IsPaused`, `bool IsAutomated`, `string DeviceId`, `string DeviceName`, `string ClientName`, `string PlaySessionId`, `SessionInfo Session`. `User` comes from `Jellyfin.Database.Implementations.Entities` (the 10.11+ namespace). — [PlaybackProgressEventArgs.cs](https://raw.githubusercontent.com/jellyfin/jellyfin/master/MediaBrowser.Controller/Library/PlaybackProgressEventArgs.cs)
- No throttling: OnPlaybackProgress publishes and invokes `PlaybackProgress` on every call. User data is saved only when `!isAutomated`. After a real client report, the server calls `session.StartAutomaticProgress(info)`, which produces automated progress events. — [SessionManager.cs](https://raw.githubusercontent.com/jellyfin/jellyfin/master/Emby.Server.Implementations/Session/SessionManager.cs)
- Pause handling in `UpdateNowPlayingItem`: `session.PlayState.IsPaused = info.IsPaused; session.PlayState.PositionTicks = info.PositionTicks;`. `LastPausedDate` is set on the first pause report and cleared on unpause. — [SessionManager.cs](https://raw.githubusercontent.com/jellyfin/jellyfin/master/Emby.Server.Implementations/Session/SessionManager.cs)
- Timers:
  - The idle-playback check runs every 5 minutes and calls `OnPlaybackStopped` for sessions whose `LastPlaybackCheckIn` is more than 5 minutes old. A dead client can therefore leave "now playing" stale for up to about 5 to 10 minutes.
  - An inactive-session timer (1-minute period, only when `InactiveSessionThreshold > 0`) sends Stop to sessions paused longer than the threshold.
  - `SessionActivity` fires at most every 10 seconds per session.
  — [SessionManager.cs](https://raw.githubusercontent.com/jellyfin/jellyfin/master/Emby.Server.Implementations/Session/SessionManager.cs)
- PlaybackStopped: `PlayedToCompletion` comes from `UpdatePlayState`. It is `false` if playback failed, `true` if no PositionTicks were reported, and it reflects the last user in the loop. The event is raised with `QueueEventIfNotNull`. PlaybackStart is raised only when the client reports a start, because the server does not detect playback itself. — [SessionManager.cs](https://raw.githubusercontent.com/jellyfin/jellyfin/master/Emby.Server.Implementations/Session/SessionManager.cs)
- 10.11 changelog: "Deprecate OnPlaybackXXX API operations in favor of ReportPlaybackXXX" (PR #13993). This changes the client-facing REST endpoints, not the C# events. — [v10.11.0 release](https://github.com/jellyfin/jellyfin/releases/tag/v10.11.0)

### Inferences
- For Discord presence:
  - Treat `PlaybackStart` as "set presence".
  - Treat `PlaybackProgress` as "update if IsPaused changed, or if the position jumped (seek) by more than a few seconds versus elapsed wall time".
  - Treat `PlaybackStopped` and `SessionEnded` as "clear".
  - Ignore `IsAutomated` ticks for pause/seek detection, or at least don't let them override the last real client state.
  - Debounce outgoing updates yourself, because Discord rate-limits activity updates (roughly 1 per 15 s historically). Progress can arrive every second, or roughly every 10 s, depending on the client.
- Read pause state from `e.IsPaused` (the client's last report) or `e.Session.PlayState.IsPaused`, which the server keeps updated. Both come from the same report, so a client that never sends a progress report on pause will look unpaused. Web and most official clients do report pause.
- Key per-session state by `Session.Id` or `PlaySessionId`. `Users` usually has one entry, but can hold several in SyncPlay or shared sessions.

### Gaps
- No official statement of the client progress-report interval was found. jellyfin-web is commonly cited at about 10 s, but that is unverified here.
- `SessionInfo.StartAutomaticProgress` was not fetched, so the automated tick interval is unknown.

## Item metadata, image URLs, and ProviderIds for external posters

### Takeaway
`e.Item` is a `BaseItem`. Cast it to `Episode` (SeriesName, ParentIndexNumber = season, IndexNumber = episode), `Movie`, or `Audio` (Album, Artists, AlbumArtists). `ProductionYear`, `RunTimeTicks` and `ProviderIds` live on BaseItem. A server image is at `/Items/{id}/Images/Primary`, but Discord can only fetch a publicly reachable URL. For private servers, build a TMDB poster URL from `ProviderIds["Tmdb"]` (this needs a TMDB API lookup for the poster path) or upload/proxy the image.

### Cited Findings
- The webhook plugin passes `eventArgs.Item` to `AddBaseItemData(...)` and branches on `eventArgs.Item.GetType()`, which shows the type-cast approach for item subtypes. — [Webhook PlaybackProgressNotifier.cs](https://github.com/jellyfin/jellyfin-plugin-webhook/blob/master/Jellyfin.Plugin.Webhook/Notifiers/PlaybackProgressNotifier.cs)
- `PlaybackProgressEventArgs` carries both `BaseItem Item` and `BaseItemDto MediaInfo`. — [PlaybackProgressEventArgs.cs](https://raw.githubusercontent.com/jellyfin/jellyfin/master/MediaBrowser.Controller/Library/PlaybackProgressEventArgs.cs)
- 10.11 changelog: "Prefer ApiKey over api_key in generated URL's" (PR #13342). This matters if image URLs carry auth tokens. — [v10.11.0 release](https://github.com/jellyfin/jellyfin/releases/tag/v10.11.0)

### Inferences
- From prior knowledge of the Jellyfin codebase (not re-verified in this session):
  - `MediaBrowser.Controller.Entities.TV.Episode` has `SeriesName`, `SeasonName`, `ParentIndexNumber`, `IndexNumber`, `SeriesId`.
  - `MediaBrowser.Controller.Entities.Movies.Movie`.
  - `MediaBrowser.Controller.Entities.Audio.Audio` has `Album`, `Artists` (IReadOnlyList<string>), `AlbumArtists`.
  - `BaseItem.ProviderIds` is a `Dictionary<string,string>` that can be read with `item.GetProviderId(MetadataProvider.Tmdb)` / `MetadataProvider.Imdb`.
  - An episode's `ProviderIds` are episode-level, so for a show poster use the series' IDs (`episode.Series?.ProviderIds`).
  - The image endpoint is `GET /Items/{itemId}/Images/Primary` (plus optional `?maxWidth=&tag=`). It is anonymous-readable by default, but useless to Discord if the server isn't public.
- TMDB poster URLs have the form `https://image.tmdb.org/t/p/w500{poster_path}`. The poster_path is not in Jellyfin's ProviderIds. It requires a TMDB API call (`/movie/{id}` or `/tv/{id}`) or reading the remote image URL that Jellyfin's TMDB provider stored. Alternatively, Discord's RPC asset fields accept arbitrary https URLs, so a desktop companion could upload the image to a public host.
- 12.0 added nothing that changes these entity basics. The 12.0 notes list moved repository members (`IItemRepository` split into `IItemPersistenceService`/`IItemCountService`/`INextUpService`), which only matters if the plugin queries the library directly. — [v12.0 release](https://github.com/jellyfin/jellyfin/releases/tag/v12.0)

### Gaps
- The Episode, Audio and BaseItem source files and the ImageController route were not fetched in this session. Verify the property names against the targeted NuGet version.

## Per-user configuration and config-page JavaScript

### Takeaway
Plugin configuration is a single XML-serialized `BasePluginConfiguration` object, server-wide. Do per-user settings by storing a list keyed by Jellyfin user ID (Guid) inside it. The admin config page loads and saves it with `ApiClient.getPluginConfiguration(pluginId)` / `ApiClient.updatePluginConfiguration(pluginId, config)`, then calls `Dashboard.processPluginConfigurationUpdateResult(result)`.

### Cited Findings
- Template configPage.html:
  - The page div has `data-role="page"`, `class="page type-interior pluginConfigurationPage"` and `data-require="emby-input,emby-button,emby-select,emby-checkbox"`.
  - On `pageshow` it calls `Dashboard.showLoadingMsg()`, then `ApiClient.getPluginConfiguration(pluginUniqueId)`, then fills the form.
  - On submit it re-fetches the config, copies the field values, calls `ApiClient.updatePluginConfiguration(pluginUniqueId, config)`, then `Dashboard.processPluginConfigurationUpdateResult(result)`, and finally `preventDefault()`.
  — [template configPage.html](https://raw.githubusercontent.com/jellyfin/jellyfin-plugin-template/master/Jellyfin.Plugin.Template/Configuration/configPage.html)
- The template registers the page through `IHasWebPages.GetPages()` with an EmbeddedResourcePath of `{Namespace}.Configuration.configPage.html`. — [template Plugin.cs](https://raw.githubusercontent.com/jellyfin/jellyfin-plugin-template/master/Jellyfin.Plugin.Template/Plugin.cs)
- 12.0: `IUserManager.Users`/`UsersIds` became the methods `GetUsers()`/`GetUsersIds()`, and `GetFirstUser` was added. — [v12.0 release](https://github.com/jellyfin/jellyfin/releases/tag/v12.0)

### Inferences
- Model:
  ```csharp
  public class PluginConfiguration : BasePluginConfiguration
  {
      public List<UserPresenceSettings> Users { get; set; } = new();   // XmlSerializer needs public get/set + parameterless ctor
  }
  public class UserPresenceSettings { public Guid UserId { get; set; } public bool Enabled { get; set; } public string? DiscordToken { get; set; } /* ... */ }
  ```
- In the page JS, list users with `ApiClient.getUsers()` (admin context), render a row per user, and serialize into `config.Users`. On the server, read `Plugin.Instance!.Configuration` and match `e.Users[i].Id`.
- Read `Plugin.Instance.Configuration` fresh on each event, or subscribe to `Plugin.ConfigurationChanged`, so changes take effect without a restart.
- The config page is admin-only. If ordinary users should edit their own mapping, expose a REST endpoint with a non-elevated policy, or add a user-facing page (newer servers support `EnableInMainMenu`/`MenuSection` on PluginPageInfo, unverified).

### Gaps
- No official doc on user-scoped (non-admin) plugin pages was found.

## Custom REST endpoints (ControllerBase), authorization policies, and push to a companion app

### Takeaway
Add an `[ApiController]` class deriving from `ControllerBase` anywhere in the plugin assembly. Jellyfin's MVC discovers it automatically. Secure it with `[Authorize]` (any authenticated user/API key) or `[Authorize(Policy = Policies.RequiresElevation)]` (admins). Policy constants live in `MediaBrowser.Common.Api.Policies`.

### Cited Findings
- Playback Reporting's controller uses `[ApiController]`, `[Authorize(Policy = Policies.RequiresElevation)]`, `[Route("user_usage_stats")]` and `[Produces(MediaTypeNames.Application.Json)]`. An example action: `[HttpGet("user_activity")] public ActionResult GetUserReport([FromQuery] int days, ...)`. — [PlaybackReportingActivityController.cs](https://github.com/jellyfin/jellyfin-plugin-playbackreporting/blob/master/Jellyfin.Plugin.PlaybackReporting/Api/PlaybackReportingActivityController.cs)
- Policy names in `MediaBrowser.Common.Api.Policies`: FirstTimeSetupOrElevated, RequiresElevation, LocalAccessOnly, IgnoreParentalControl, Download, FirstTimeSetupOrDefault, LocalAccessOrRequiresElevation, AnonymousLanAccessPolicy, FirstTimeSetupOrIgnoreParentalControl, SyncPlay*, CollectionManagement, LiveTvAccess, LiveTvManagement, SubtitleManagement, LyricManagement. — [Policies.cs](https://raw.githubusercontent.com/jellyfin/jellyfin/master/MediaBrowser.Common/Api/Policies.cs)
- Template README: ControllerBase lets you define custom REST endpoints like a standard ASP.NET Web API controller. — [template README](https://github.com/jellyfin/jellyfin-plugin-template)
- 12.0 API policy: endpoints absent from the OpenAPI spec, or marked obsolete, may be removed in any major release. Legacy `/emby/*` and `/mediabrowser/*` prefixes were removed. — [v12.0 release](https://github.com/jellyfin/jellyfin/releases/tag/v12.0); [releases page](https://github.com/jellyfin/jellyfin/releases)

### Inferences
- A companion endpoint sketch:
  ```csharp
  [ApiController, Authorize, Route("Jellycord")]
  public class JellycordController(PresenceStore store) : ControllerBase
  {
      [HttpGet("NowPlaying")]
      public ActionResult<PresenceDto?> Get() => store.Get(User.GetUserId());   // ClaimsPrincipal helper in Jellyfin.Api.Extensions (verify)
      [HttpGet("Stream")] public async Task Stream(CancellationToken ct) { Response.ContentType = "text/event-stream"; /* write "data: ...\n\n" + FlushAsync */ }
  }
  ```
- The companion authenticates with a user access token or API key, sent in the `Authorization: MediaBrowser Token="..."` header (or `X-Emby-Token`), never in a URL query.
- SSE through a plain controller action works with ASP.NET Core. Long-polling is the simplest fallback. A desktop companion could also skip the plugin entirely: use the server's own WebSocket (`/socket`) with `SessionsStart` messages, or poll `/Sessions`. The plugin is mainly worth having to add per-user opt-in, artwork resolution and debouncing.

### Gaps
- The exact helper for extracting the user ID from claims in plugin controllers (`ClaimsPrincipalExtensions.GetUserId`) was not verified for 10.11/12.
- No first-hand source was found confirming SSE behaviour through Jellyfin's middleware (e.g. response buffering or compression).

## Publishing: manifest.json format, hosting, and GitHub Actions

### Takeaway
A repository is a single `manifest.json`: an array of plugins, each `{guid, name, description, overview, owner, category, imageUrl?, versions:[{version, changelog, targetAbi, sourceUrl, checksum (MD5 of the zip), timestamp}]}`. Host it anywhere over HTTPS (GitHub Pages or a raw file). The zips can live on GitHub Releases. Generate it with `jprm` (`pip install jprm`; `jprm repo init`, `jprm plugin build`, `jprm repo add`) or the reusable workflows in `jellyfin/jellyfin-meta-plugins`.

### Cited Findings
- Official manifest example (Bookshelf): top-level keys `guid`, `name`, `description`, `overview`, `owner`, `category`, `versions` (some entries also have `imageUrl`). A version object:
  ```json
  {"version":"13.0.0.0","changelog":"- Jellyfin 10.11 support (#130) @crobibero","targetAbi":"10.11.0.0",
   "sourceUrl":"https://repo.jellyfin.org/files/plugin/bookshelf/bookshelf_13.0.0.0.zip",
   "checksum":"6e29bb9bdc18e571c41964c6fdb07a49","timestamp":"2025-10-20T01:25:49Z"}
  ```
  The checksum is 32 hex characters, consistent with MD5. — [official manifest (mirror)](https://ams1.mirror.jellyfin.org/files/plugin/manifest.json)
- The official stable repo URL is `https://repo.jellyfin.org/files/plugin/manifest.json`. Users add third-party repositories by their manifest.json URL under Dashboard > Plugins > Repositories. Manual install means dropping the plugin into a subfolder of the plugins directory and restarting. — [Jellyfin plugin docs](https://jellyfin.org/docs/general/server/plugins/)
- jprm installs with `pip install --user jprm`, and `jprm repo init /path/to/repo` creates a repository. (Its README doesn't document the other commands; see Metadata.md and build_plugin.sh in the repo.) — [jellyfin-plugin-repository-manager](https://github.com/oddstr13/jellyfin-plugin-repository-manager)
- Template workflows: `build.yaml` uses `jellyfin/jellyfin-meta-plugins/.github/workflows/build.yaml@master`. `publish.yaml` triggers `on: release: types: [released]` (and workflow_dispatch) and uses `jellyfin/jellyfin-meta-plugins/.github/workflows/publish.yaml@master` with `version: ${{ github.event.release.tag_name }}` and `is-unstable: ${{ github.event.release.prerelease }}`. Other workflows: changelog, command-dispatch, command-rebase, scan-codeql, sync-labels, test. — [template build workflow](https://raw.githubusercontent.com/jellyfin/jellyfin-plugin-template/master/.github/workflows/build.yaml); [template publish workflow](https://raw.githubusercontent.com/jellyfin/jellyfin-plugin-template/master/.github/workflows/publish.yaml); [workflows dir](https://github.com/jellyfin/jellyfin-plugin-template/tree/master/.github/workflows)
- 12.0 tells users who were on the unstable repo to switch back to the stable manifest URL. The RC notes advised reinstalling plugins from the unstable repo, which shows the server supports multiple repos side by side. — [v12.0 release](https://github.com/jellyfin/jellyfin/releases/tag/v12.0); [releases page](https://github.com/jellyfin/jellyfin/releases)

### Inferences
- The meta-plugins **publish** workflow uploads to Jellyfin's own repo server with Jellyfin secrets. A third-party plugin should reuse only the build workflow, or its own `dotnet publish` plus jprm, and write its own release job:
  1. On tag: `dotnet publish -c Release`, then `jprm plugin build . --dotnet-framework net10.0` (or zip the DLL plus meta.json).
  2. Compute the MD5 (`md5sum`) and upload the zip to the GitHub Release.
  3. Run `jprm repo add --url <release-download-base> ./repo <zip>` (or a small script) to prepend a `versions[]` entry with targetAbi, sourceUrl, checksum and an ISO-8601 UTC timestamp.
  4. Commit `manifest.json` to a `gh-pages` branch. Users add `https://<user>.github.io/<repo>/manifest.json`.
- For dual support, add two version entries per release (e.g. `1.2.0.0` with targetAbi `10.11.0.0` and a net9 zip, and `1.2.0.0`/`1.2.0.1` with targetAbi `12.0.0.0` and a net10 zip). Use distinct version numbers so the server can tell them apart. Posterizarr does a variant of this. — [posterizarr 3.3.2](https://newreleases.io/project/github/fscorrupt/posterizarr/release/3.3.2)
- Use four-part versions (`1.0.0.0`) to match Jellyfin's convention in build.yaml and the manifest.

### Gaps
- The exact jprm CLI flags (`plugin build`, `repo add` arguments) and the meta-plugins publish workflow internals were not fetched. Check `jprm --help` and the jellyfin-meta-plugins repo before writing CI.
- No source confirmed whether the server verifies the MD5 checksum case-sensitively or what happens on a mismatch. It is assumed the install is rejected.
