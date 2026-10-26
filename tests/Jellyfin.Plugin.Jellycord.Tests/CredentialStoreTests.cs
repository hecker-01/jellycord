using System.Reflection;
using Jellyfin.Plugin.Jellycord.Discord;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellycord.Tests;

public sealed class CredentialStoreTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("jellycord-tests-").FullName;

    private string FilePath => Path.Combine(_dir, "Jellyfin.Plugin.Jellycord.credentials.json");

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private CredentialStore NewStore() => new(PathsProxy.For(_dir), NullLogger<CredentialStore>.Instance);

    [Fact]
    public void Save_WritesOwnerOnlyFile()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        NewStore().ClientSecret = "s3cret";

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Save_RoundTrips()
    {
        var store = NewStore();
        store.ClientSecret = "s3cret";
        store.AddOrReplace(new LinkedAccount { UserId = Guid.NewGuid(), RefreshToken = "r" });

        var reloaded = NewStore();
        Assert.Equal("s3cret", reloaded.ClientSecret);
        Assert.Single(reloaded.GetAccounts());
    }

    [Fact]
    public void CorruptFile_IsMovedAsideNotOverwritten()
    {
        File.WriteAllText(FilePath, "{ not json");

        var store = NewStore();
        store.ClientSecret = "new";

        var aside = Assert.Single(Directory.GetFiles(_dir, "*.corrupt-*"));
        Assert.Equal("{ not json", File.ReadAllText(aside));
        Assert.Equal("new", NewStore().ClientSecret);
    }

    [Fact]
    public void FailedSave_LeavesMemoryMatchingDisk()
    {
        var store = NewStore();
        var kept = new LinkedAccount { UserId = Guid.NewGuid(), RefreshToken = "kept" };
        store.AddOrReplace(kept);

        // a directory where the file should be makes every later save fail
        File.Delete(FilePath);
        Directory.CreateDirectory(FilePath);

        Assert.ThrowsAny<IOException>(() => store.AddOrReplace(new LinkedAccount { UserId = Guid.NewGuid() }));
        Assert.ThrowsAny<IOException>(() => store.Remove(kept.UserId));
        Assert.Equal([kept], store.GetAccounts());
    }

    [Fact]
    public void FailedSave_KeepsRefreshedTokensInMemory()
    {
        var store = NewStore();
        var account = new LinkedAccount { UserId = Guid.NewGuid(), RefreshToken = "old" };
        store.AddOrReplace(account);
        File.Delete(FilePath);
        Directory.CreateDirectory(FilePath);

        // Discord already invalidated "old"; losing "new" as well would kill the link
        store.UpdateTokens(account, new TokenSet("access", "new", 0), "app");

        Assert.Equal("new", store.Find(account.UserId)!.RefreshToken);
    }

    /// <summary>Just enough of IApplicationPaths for the store, without a mocking library.</summary>
    public class PathsProxy : DispatchProxy
    {
        private string _dir = string.Empty;

        public static IApplicationPaths For(string dir)
        {
            var paths = Create<IApplicationPaths, PathsProxy>();
            ((PathsProxy)(object)paths)._dir = dir;
            return paths;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => targetMethod?.Name == "get_" + nameof(IApplicationPaths.PluginConfigurationsPath)
                ? _dir
                : throw new NotSupportedException(targetMethod?.Name);
    }
}
