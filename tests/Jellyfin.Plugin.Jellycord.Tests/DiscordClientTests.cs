using System.Net;
using System.Text.Json.Nodes;
using Jellyfin.Plugin.Jellycord.Discord;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Jellycord.Tests;

public sealed class DiscordClientTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("jellycord-tests-").FullName;
    private readonly FakeHandler _http = new();

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private CredentialStore NewStore() => new(CredentialStoreTests.PathsProxy.For(_dir), NullLogger<CredentialStore>.Instance);

    [Theory]
    [InlineData("""{"error":{"code":1}}""")]
    [InlineData("""["error"]""")]
    [InlineData("42")]
    public void Error_ToleratesUnexpectedShapes(string json)
        => Assert.Null(new DiscordResponse(400, JsonNode.Parse(json)).Error);

    [Fact]
    public void Readers_ReturnNullForWrongTypes()
    {
        var body = JsonNode.Parse("""{"token":5,"retry_after":"soon","name":"x"}""");
        Assert.Null(body.Str("token"));
        Assert.Null(body.Num("retry_after"));
        Assert.Equal("x", body.Str("name"));
        Assert.Null(body.Str("missing"));
    }

    [Fact]
    public async Task Refresh_StoresRotatedTokens()
    {
        var store = NewStore();
        var account = new LinkedAccount { UserId = Guid.NewGuid(), AccessToken = "a1", RefreshToken = "r1" };
        store.AddOrReplace(account);
        _http.Respond(HttpStatusCode.OK, """{"access_token":"a2","refresh_token":"r2","expires_in":604800}""");

        await new DiscordClient(_http, store).RefreshAsync(account, force: true, CancellationToken.None);

        Assert.Equal("r2", NewStore().Find(account.UserId)!.RefreshToken);
    }

    [Fact]
    public async Task Refresh_WithAnotherApplicationsTokens_NeedsRelinkWithoutCallingDiscord()
    {
        var account = new LinkedAccount { UserId = Guid.NewGuid(), RefreshToken = "r1", ClientId = "old-app" };

        await Assert.ThrowsAsync<InvalidGrantException>(
            () => new DiscordClient(_http, NewStore()).RefreshAsync(account, force: true, CancellationToken.None));
        Assert.Equal(0, _http.Calls);
    }

    [Fact]
    public async Task NetworkFailure_IsStatusZeroWithoutAnErrorCode()
    {
        _http.Throw(new HttpRequestException("no route to discord.com (10.0.0.1)"));

        var res = await new DiscordClient(_http, NewStore()).DeviceTokenAsync("code", CancellationToken.None);

        Assert.Equal(0, res.Status);
        Assert.Null(res.Error); // the raw message must not read as an OAuth error the browser would see
    }

    [Fact]
    public async Task Sink_RunsOneOperationPerUserAtATime()
    {
        var sink = new HeadlessSessionSink(new DiscordClient(_http, NewStore()), NewStore(), NullLogger<HeadlessSessionSink>.Instance);
        var user = Guid.NewGuid();
        var running = 0;
        var maxRunning = 0;

        async Task<bool> Work()
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref maxRunning, now);
            await Task.Delay(20);
            Interlocked.Decrement(ref running);
            return true;
        }

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => sink.RunExclusiveAsync(user, Work, CancellationToken.None)));
        Assert.Equal(1, maxRunning);

        // different users don't wait for each other
        maxRunning = 0;
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => sink.RunExclusiveAsync(Guid.NewGuid(), Work, CancellationToken.None)));
        Assert.True(maxRunning > 1);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = target) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }

    /// <summary>An IHttpClientFactory whose clients answer from a canned response.</summary>
    private sealed class FakeHandler : HttpMessageHandler, IHttpClientFactory
    {
        private Func<HttpResponseMessage> _next = () => new HttpResponseMessage(HttpStatusCode.NotFound);

        public int Calls { get; private set; }

        public void Respond(HttpStatusCode status, string json)
            => _next = () => new HttpResponseMessage(status) { Content = new StringContent(json) };

        public void Throw(Exception ex) => _next = () => throw ex;

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(_next());
        }
    }
}
