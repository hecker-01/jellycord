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

    /// <summary>Unix ms. Start and end together give Discord's progress bar; start alone an elapsed timer.</summary>
    public long? StartMs { get; init; }

    public long? EndMs { get; init; }

    public string? LargeImage { get; init; }

    public string? LargeText { get; init; }

    /// <summary>Badge in the corner of the large image (only shown when there is a large image).</summary>
    public string? SmallImage { get; init; }

    public string? SmallText { get; init; }

    /// <summary>Changes when anything other than the timestamps changes.</summary>
    public string ContentKey => $"{Type}|{Name}|{Details}|{State}|{LargeImage}|{SmallImage}|{StartMs is null}";

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

        if (StartMs is not null)
        {
            var timestamps = new JsonObject { ["start"] = StartMs.Value.ToString(CultureInfo.InvariantCulture) };
            if (EndMs is not null)
            {
                timestamps["end"] = EndMs.Value.ToString(CultureInfo.InvariantCulture);
            }

            o["timestamps"] = timestamps;
        }

        if (LargeImage is not null)
        {
            var assets = new JsonObject { ["large_image"] = LargeImage };
            if (LargeText is not null)
            {
                assets["large_text"] = LargeText;
            }

            if (SmallImage is not null)
            {
                assets["small_image"] = SmallImage;
                if (SmallText is not null)
                {
                    assets["small_text"] = SmallText;
                }
            }

            o["assets"] = assets;
        }

        return o;
    }
}
