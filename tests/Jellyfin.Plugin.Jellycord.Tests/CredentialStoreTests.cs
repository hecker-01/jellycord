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
