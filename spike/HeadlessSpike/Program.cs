// Phase 0 spike: prove Discord's headless-sessions endpoint can set YOUR presence
// from a plain .NET process, with no Discord client involved.
//
//   dotnet run --project spike/HeadlessSpike -- --client-id <application id> [--client-secret <secret>] [--image <https url>]
//
// Walks through: device-code OAuth -> GET /users/@me -> create session -> update
// session (rotated token) -> optional status_display_type probe -> delete session.
// Nothing is written to disk.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

const string Api = "https://discord.com/api/v10";
const string Scope = "sdk.social_layer_presence";

var opts = ParseArgs(args);
if (!opts.TryGetValue("client-id", out var clientId))
{
    Console.WriteLine("usage: HeadlessSpike --client-id <application id> [--client-secret <secret>] [--image <https url>]");
    return 1;
}

opts.TryGetValue("client-secret", out var clientSecret);
var image = opts.GetValueOrDefault("image", "https://upload.wikimedia.org/wikipedia/commons/c/c5/Big_buck_bunny_poster_big.jpg");

using var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("jellycord-spike/0.1");

// 1. device authorization
var (st, auth) = await PostForm($"{Api}/oauth2/device/authorize", Form(("scope", Scope)));
Step("device/authorize", st, auth);
if (st != 200 || auth is null)
{
    Console.WriteLine("Hint: enable 'Public Client' on the app's OAuth2 tab, or pass --client-secret.");
    return 2;
}

var deviceCode = auth["device_code"]!.GetValue<string>();
var interval = auth["interval"]?.GetValue<double>() ?? 5;
Console.WriteLine();
Console.WriteLine($"  Open {auth["verification_uri_complete"] ?? auth["verification_uri"]}");
Console.WriteLine($"  and approve code: {auth["user_code"]}");
Console.WriteLine();

// 2. poll for the token
string accessToken;
while (true)
{
    await Task.Delay(TimeSpan.FromSeconds(interval));
    var (ts, tok) = await PostForm(
        $"{Api}/oauth2/token",
        Form(("grant_type", "urn:ietf:params:oauth:grant-type:device_code"), ("device_code", deviceCode)));
    var err = tok?["error"]?.GetValue<string>();
    if (ts == 200 && tok?["access_token"] is not null)
    {
        accessToken = tok["access_token"]!.GetValue<string>();
        Console.WriteLine($"[ok] token granted, scope='{tok["scope"]}', expires_in={tok["expires_in"]}");
        break;
    }

    if (err == "slow_down")
    {
        interval += 5;
    }
    else if (err != "authorization_pending")
    {
        Step("oauth2/token", ts, tok);
        return 3;
    }
}

// 3. who am I
var (ms, me) = await Send(HttpMethod.Get, $"{Api}/users/@me", null, accessToken);
Console.WriteLine($"[{ms}] linked as {me?["global_name"] ?? me?["username"]}");

// 4. create a session
var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
var activity = new JsonObject
{
    ["type"] = 3,
    ["name"] = "Big Buck Bunny",
    ["application_id"] = clientId,
    ["platform"] = "desktop",
    ["details"] = "Big Buck Bunny",
    ["state"] = "2008 · jellycord spike",
    ["timestamps"] = new JsonObject
    {
        ["start"] = (now - 60_000).ToString(),
        ["end"] = (now + 540_000).ToString(),
    },
    ["assets"] = new JsonObject { ["large_image"] = image, ["large_text"] = "Big Buck Bunny" },
};

var token = await PostSession(activity, null, "create");
if (token is null)
{
    return 4;
}

Pause("Check your Discord profile from another device: expect 'Watching Big Buck Bunny' with poster + progress bar.");

// 5. update (paused look, no timestamps) using the rotated token
var paused = (JsonObject)activity.DeepClone();
paused.Remove("timestamps");
paused["state"] = "2008 · (paused)";
token = await PostSession(paused, token, "update/paused") ?? token;
Pause("Expect the progress bar gone and '(paused)' in the state line.");

// 6. probe: does headless accept status_display_type (2 = show details in member list)?
var probe = (JsonObject)activity.DeepClone();
probe["name"] = "Jellyfin";
probe["status_display_type"] = 2;
token = await PostSession(probe, token, "probe/status_display_type") ?? token;
Pause("If accepted: does the member list show 'Big Buck Bunny' rather than 'Jellyfin'?");

// 7. delete
var (ds, dbody) = await PostJson($"{Api}/users/@me/headless-sessions/delete", new JsonObject { ["token"] = token }, accessToken);
Step("delete", ds, dbody);
Console.WriteLine("Done. Revoke the test grant any time under Discord > Settings > Authorized Apps.");
return 0;

async Task<string?> PostSession(JsonObject act, string? sessionToken, string label)
{
    for (var attempt = 0; attempt < 5; attempt++)
    {
        var payload = new JsonObject { ["activities"] = new JsonArray(act.DeepClone()) };
        if (sessionToken is not null)
        {
            payload["token"] = sessionToken;
        }

        var (s, body) = await PostJson($"{Api}/users/@me/headless-sessions", payload, accessToken);
        Step(label, s, body);
        if (s is 200 or 201)
        {
            return body?["token"]?.GetValue<string>();
        }

        if (s is 202 or 429)
        {
            var retry = body?["retry_after"]?.GetValue<double>() ?? 4;
            var seconds = s == 202 ? retry / 1000 : retry; // 202 reports ms, 429 seconds
            Console.WriteLine($"     retrying in {seconds:0.#}s");
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(seconds, 1)));
            continue;
        }

        return null;
    }

    return null;
}

Dictionary<string, string> Form(params (string Key, string Value)[] fields)
{
    var form = new Dictionary<string, string> { ["client_id"] = clientId };
    if (!string.IsNullOrEmpty(clientSecret))
    {
        form["client_secret"] = clientSecret;
    }

    foreach (var (k, v) in fields)
    {
        form[k] = v;
    }

    return form;
}

Task<(int, JsonNode?)> PostForm(string url, Dictionary<string, string> form)
    => Send(HttpMethod.Post, url, new FormUrlEncodedContent(form), null);

Task<(int, JsonNode?)> PostJson(string url, JsonObject body, string bearer)
    => Send(HttpMethod.Post, url, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), bearer);

async Task<(int, JsonNode?)> Send(HttpMethod method, string url, HttpContent? content, string? bearer)
{
    using var req = new HttpRequestMessage(method, url) { Content = content };
    if (bearer is not null)
    {
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
    }

    using var res = await http.SendAsync(req);
    var text = await res.Content.ReadAsStringAsync();
    JsonNode? node = null;
    if (!string.IsNullOrWhiteSpace(text))
    {
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (System.Text.Json.JsonException)
        {
            node = new JsonObject { ["raw"] = text[..Math.Min(300, text.Length)] };
        }
    }

    return ((int)res.StatusCode, node);
}

static void Step(string label, int status, JsonNode? body)
{
    // never echo tokens to the console
    var shown = body?.DeepClone();
    if (shown is JsonObject o)
    {
        foreach (var key in new[] { "token", "access_token", "refresh_token", "device_code" })
        {
            if (o.ContainsKey(key))
            {
                o[key] = "<redacted>";
            }
        }
    }

    Console.WriteLine($"[{status}] {label}: {shown?.ToJsonString() ?? "(empty)"}");
}

void Pause(string msg)
{
    Console.WriteLine();
    Console.WriteLine($">> {msg}");
    if (Console.IsInputRedirected)
    {
        // unattended (e.g. run from a script): hold each state long enough to look at it
        var hold = int.Parse(opts.GetValueOrDefault("hold", "60"));
        Console.WriteLine($"   Holding for {hold}s...");
        Thread.Sleep(TimeSpan.FromSeconds(hold));
        return;
    }

    Console.WriteLine("   Press Enter to continue...");
    Console.ReadLine();
}

static Dictionary<string, string> ParseArgs(string[] argv)
{
    var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    for (var i = 0; i + 1 < argv.Length; i += 2)
    {
        d[argv[i].TrimStart('-')] = argv[i + 1];
    }

    return d;
}
