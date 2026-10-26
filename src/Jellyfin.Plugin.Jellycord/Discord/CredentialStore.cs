using System.Text.Json;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Jellycord.Discord;

/// <summary>One Jellyfin user's Discord link.</summary>
public sealed class LinkedAccount
{
    public Guid UserId { get; set; }

    public string DiscordName { get; set; } = string.Empty;

    public string AccessToken { get; set; } = string.Empty;

    public string RefreshToken { get; set; } = string.Empty;

    /// <summary>Unix seconds.</summary>
    public long ExpiresAt { get; set; }
}

/// <summary>Per-user privacy choices. They only ever add to the admin's rules.</summary>
public sealed class UserSettings
{
    /// <summary>Never show what I'm playing, only "Watching {server}".</summary>
    public bool AlwaysPrivate { get; set; }

    public string[] PrivateLibraryIds { get; set; } = [];

    public string PrivateTags { get; set; } = string.Empty;
}

/// <summary>
/// Secrets kept out of the plugin XML config (which the dashboard can read back):
/// the Discord client secret and every user's OAuth tokens. Stored as a JSON file that is owner-only
/// (mode 0600) on Linux and macOS; on Windows it inherits the folder's permissions.
/// </summary>
public sealed class CredentialStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<CredentialStore> _logger;
    private readonly Lock _lock = new();
    private Data _data;

    /// <summary>
    /// Set when the file exists but couldn't be read. Saving would overwrite every link and the
    /// client secret, so writes are refused until the file is fixed and Jellyfin restarted.
    /// </summary>
    private bool _readFailed;

    public CredentialStore(IApplicationPaths paths, ILogger<CredentialStore> logger)
    {
        _logger = logger;
        _path = Path.Combine(paths.PluginConfigurationsPath, "Jellyfin.Plugin.Jellycord.credentials.json");
        _data = Load();
    }

    public string ClientSecret
    {
        get
        {
            lock (_lock)
            {
                return _data.ClientSecret;
            }
        }

        set
        {
            lock (_lock)
            {
                _data.ClientSecret = value;
                SaveLocked();
            }
        }
    }

    public LinkedAccount? Find(Guid userId)
    {
        lock (_lock)
        {
            return _data.Accounts.FirstOrDefault(a => a.UserId == userId);
        }
    }

    public LinkedAccount[] GetAccounts()
    {
        lock (_lock)
        {
            return [.. _data.Accounts];
        }
    }

    /// <summary>Stores the account, returning the one it replaced (if any).</summary>
    public LinkedAccount? AddOrReplace(LinkedAccount account)
    {
        lock (_lock)
        {
            var old = _data.Accounts.FirstOrDefault(a => a.UserId == account.UserId);
            if (old is not null)
            {
                _data.Accounts.Remove(old);
            }

            _data.Accounts.Add(account);
            SaveLocked();
            return old;
        }
    }

    public LinkedAccount? Remove(Guid userId)
    {
        lock (_lock)
        {
            var old = _data.Accounts.FirstOrDefault(a => a.UserId == userId);
            if (old is not null)
            {
                _data.Accounts.Remove(old);
                SaveLocked();
            }

            return old;
        }
    }

    public UserSettings GetSettings(Guid userId)
    {
        lock (_lock)
        {
            return _data.UserSettings.TryGetValue(userId, out var s) ? s : new UserSettings();
        }
    }

    public void SetSettings(Guid userId, UserSettings settings)
    {
        lock (_lock)
        {
            _data.UserSettings[userId] = settings;
            SaveLocked();
        }
    }

    /// <summary>Persists in-place token changes to an account that is still linked.</summary>
    public void Save(LinkedAccount account)
    {
        lock (_lock)
        {
            if (_data.Accounts.Contains(account))
            {
                SaveLocked();
            }
        }
    }

    private Data Load()
    {
        if (!File.Exists(_path))
        {
            return new Data();
        }

        try
        {
            return JsonSerializer.Deserialize<Data>(File.ReadAllText(_path)) ?? new Data();
        }
        catch (JsonException ex)
        {
            // unparseable: keep it aside for manual recovery rather than overwriting it on the next save
            var aside = $"{_path}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmss}";
            try
            {
                File.Move(_path, aside);
                _logger.LogError(ex, "Could not parse {Path}; moved it to {Aside} and starting with no linked accounts", _path, aside);
                return new Data();
            }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not parse {Path} or move it aside; not saving any changes to it", _path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not read {Path}; not saving any changes to it until it is readable and Jellyfin restarted", _path);
        }

        _readFailed = true;
        return new Data();
    }

    private void SaveLocked()
    {
        if (_readFailed)
        {
            throw new IOException($"Not overwriting {_path}: it could not be read at startup");
        }

        var tmp = _path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        // create the file owner-only from the start, so the secrets are never briefly world-readable
        // (the create mode only applies to new files, hence the delete)
        File.Delete(tmp);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using (var stream = new FileStream(tmp, options))
        {
            JsonSerializer.Serialize(stream, _data, JsonOptions);
            stream.Flush(flushToDisk: true);
        }

        File.Move(tmp, _path, overwrite: true);
    }

    private sealed class Data
    {
        public string ClientSecret { get; set; } = string.Empty;

        public List<LinkedAccount> Accounts { get; set; } = [];

        public Dictionary<Guid, UserSettings> UserSettings { get; set; } = [];
    }
}
