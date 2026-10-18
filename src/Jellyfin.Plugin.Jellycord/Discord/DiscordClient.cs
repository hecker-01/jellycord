using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using MediaBrowser.Common.Net;

namespace Jellyfin.Plugin.Jellycord.Discord;

/// <summary>The user's Discord grant is gone (revoked or expired); they need to link again.</summary>
public sealed class InvalidGrantException : Exception
{
}

/// <summary>Status code (0 = network failure) plus parsed JSON body.</summary>
public readonly record struct DiscordResponse(int Status, JsonNode? Body)
{
    public string? Error => Body?["error"]?.GetValue<string>();
}

/// <summary>
/// Discord OAuth (device flow) and the Social SDK headless-sessions endpoint.
/// headless-sessions is undocumented; its behaviour here follows
/// mrzeappleGit/jellyfin-plugin-discord-presence (MIT) and our own spike.
/// </summary>
public sealed class DiscordClient
{
    public const string Scope = "sdk.social_layer_presence";
    private const string ApiBase = "https://discord.com/api/v10";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly CredentialStore _store;

    public DiscordClient(IHttpClientFactory httpClientFactory, CredentialStore store)
    {
        _httpClientFactory = httpClientFactory;
        _store = store;
    }

    private static string ClientId => Plugin.Instance?.Configuration.ClientId.Trim() ?? string.Empty;

    public Task<DiscordResponse> DeviceAuthorizeAsync(CancellationToken ct)
        => PostFormAsync("/oauth2/device/authorize", ct, ("scope", Scope));

    public Task<DiscordResponse> DeviceTokenAsync(string deviceCode, CancellationToken ct)
        => PostFormAsync(
            "/oauth2/token",
            ct,
            ("grant_type", "urn:ietf:params:oauth:grant-type:device_code"),
            ("device_code", deviceCode));

    public Task<DiscordResponse> RevokeAsync(string token, string hint, CancellationToken ct)
        => PostFormAsync("/oauth2/token/revoke", ct, ("token", token), ("token_type_hint", hint));

    public Task<DiscordResponse> GetMeAsync(string accessToken, CancellationToken ct)
        => SendAsync(HttpMethod.Get, "/users/@me", null, accessToken, ct);

    /// <summary>Refreshes the account's tokens when close to expiry (or when forced).</summary>
    public async Task RefreshAsync(LinkedAccount account, bool force, CancellationToken ct)
    {
        const long margin = 6 * 3600;
        if (!force && DateTimeOffset.UtcNow.ToUnixTimeSeconds() < account.ExpiresAt - margin)
        {
            return;
        }

        var res = await PostFormAsync(
            "/oauth2/token", ct, ("grant_type", "refresh_token"), ("refresh_token", account.RefreshToken))
            .ConfigureAwait(false);
        if (res.Status == 400 && res.Error == "invalid_grant")
        {
            throw new InvalidGrantException();
        }

        if (res.Status != 200 || res.Body?["access_token"] is null)
        {
            throw new HttpRequestException($"Discord token refresh failed ({res.Status})");
        }

        ApplyTokens(account, res.Body);
        _store.Save(account);
    }

    public static void ApplyTokens(LinkedAccount account, JsonNode body)
    {
        account.AccessToken = body["access_token"]!.GetValue<string>();
        var rotated = body["refresh_token"]?.GetValue<string>();
        if (!string.IsNullOrEmpty(rotated))
        {
            account.RefreshToken = rotated;
        }

        account.ExpiresAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + (body["expires_in"]?.GetValue<long>() ?? 604800);
    }

    /// <summary>Creates (sessionToken null) or updates a headless session. The response carries a rotated token.</summary>
    public Task<DiscordResponse> PostHeadlessSessionAsync(
        string accessToken, JsonObject activity, string? sessionToken, CancellationToken ct)
    {
        var payload = new JsonObject { ["activities"] = new JsonArray(activity) };
        if (sessionToken is not null)
        {
            payload["token"] = sessionToken;
        }

        return PostJsonAsync("/users/@me/headless-sessions", payload, accessToken, ct);
    }

    public Task<DiscordResponse> DeleteHeadlessSessionAsync(string accessToken, string sessionToken, CancellationToken ct)
        => PostJsonAsync("/users/@me/headless-sessions/delete", new JsonObject { ["token"] = sessionToken }, accessToken, ct);

    private Task<DiscordResponse> PostFormAsync(string path, CancellationToken ct, params (string Key, string Value)[] fields)
    {
        var form = new Dictionary<string, string> { ["client_id"] = ClientId };
        var secret = _store.ClientSecret;
        if (!string.IsNullOrEmpty(secret))
        {
            form["client_secret"] = secret;
        }

        foreach (var (key, value) in fields)
        {
            form[key] = value;
        }

        return SendAsync(HttpMethod.Post, path, new FormUrlEncodedContent(form), null, ct);
    }

    private Task<DiscordResponse> PostJsonAsync(string path, JsonObject body, string bearer, CancellationToken ct)
        => SendAsync(HttpMethod.Post, path, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), bearer, ct);

    private async Task<DiscordResponse> SendAsync(
        HttpMethod method, string path, HttpContent? content, string? bearer, CancellationToken ct)
    {
        using var client = _httpClientFactory.CreateClient(NamedClient.Default);
        using var request = new HttpRequestMessage(method, ApiBase + path) { Content = content };
        request.Headers.UserAgent.ParseAdd("Jellycord (https://github.com/hecker-01/jellycord)");
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        try
        {
            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            JsonNode? body = null;
            if (!string.IsNullOrWhiteSpace(text))
            {
                try
                {
                    body = JsonNode.Parse(text);
                }
                catch (System.Text.Json.JsonException)
                {
                    body = new JsonObject { ["raw"] = text[..Math.Min(text.Length, 300)] };
                }
            }

            return new DiscordResponse((int)response.StatusCode, body);
        }
        catch (HttpRequestException ex)
        {
            return new DiscordResponse(0, new JsonObject { ["error"] = ex.Message });
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            return new DiscordResponse(0, new JsonObject { ["error"] = ex.Message });
        }
    }
}
