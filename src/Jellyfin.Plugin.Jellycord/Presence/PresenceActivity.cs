using System.Globalization;
using System.Text.Json.Nodes;

namespace Jellyfin.Plugin.Jellycord.Presence;

/// <summary>A Discord activity, independent of how it gets delivered.</summary>
public sealed record PresenceActivity
{
    public const int Listening = 2;
    public const int Watching = 3;

    public required int Type { get; init; }

    /// <summary>Shown in the member list as "Watching {Name}".</summary>
    public required string Name { get; init; }

    public string? Details { get; init; }

    public string? State { get; init; }

    /// <summary>Unix ms. Start and end together give Discord's progress bar.</summary>
    public long? StartMs { get; init; }

    public long? EndMs { get; init; }

    public string? LargeImage { get; init; }

    public string? LargeText { get; init; }

    /// <summary>Changes when anything other than the timestamps changes.</summary>
    public string ContentKey => $"{Type}|{Name}|{Details}|{State}|{LargeImage}|{StartMs is null}";

    public JsonObject ToJson(string applicationId)
    {
        var o = new JsonObject
        {
            ["type"] = Type,
            ["name"] = Name,
            ["application_id"] = applicationId,
            ["platform"] = "desktop",
        };
        if (Details is not null)
        {
            o["details"] = Details;
        }

        if (State is not null)
        {
            o["state"] = State;
        }

        if (StartMs is not null && EndMs is not null)
        {
            o["timestamps"] = new JsonObject
            {
                ["start"] = StartMs.Value.ToString(CultureInfo.InvariantCulture),
                ["end"] = EndMs.Value.ToString(CultureInfo.InvariantCulture),
            };
        }

        if (LargeImage is not null)
        {
            var assets = new JsonObject { ["large_image"] = LargeImage };
            if (LargeText is not null)
            {
                assets["large_text"] = LargeText;
            }

            o["assets"] = assets;
        }

        return o;
    }
}
