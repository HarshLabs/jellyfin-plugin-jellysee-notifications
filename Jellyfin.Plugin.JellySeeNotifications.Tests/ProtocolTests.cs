using System.Net;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.JellySeeNotifications.Core;
using Xunit;

namespace Jellyfin.Plugin.JellySeeNotifications.Tests;

public sealed class ProtocolTests
{
    internal static string Id(int i) => i.ToString("x32");
    internal static Notice Event(int i = 1) => new() { EventId = Id(i), Id = Id(i + 100), Type = "Movie", QueuedAt = 100, At = 200, Stage = "outbox" };
    [Fact] public void SharedHashVectorsPreserveServerPunctuation()
    {
        Assert.Equal("s1-ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Identity.ServerKey(" ABC "));
        Assert.Equal("baa90b183b045770", Identity.UserHash(Identity.ServerKey("ABC"), Guid.ParseExact(Id(1), "N")));
        Assert.NotEqual(Identity.ServerKey("a-b"), Identity.ServerKey("ab"));
        Assert.NotEqual(Identity.ServerKey("abc\n"), Identity.ServerKey("abc"));
        Assert.Equal(Identity.ServerKey("abc"), Identity.ServerKey("\t\u00a0ABC\u3000"));
    }
    [Fact] public void BitmapIsLsbFirstAndRemapsReorderedTables()
    {
        string[] users = Enumerable.Range(0, 10).Select(i => i.ToString("x16")).ToArray();
        Assert.Equal(new byte[] { 0x81, 0x02 }, Wire.Bitmap(users, [users[0], users[7], users[9]]));
        Assert.Equal(new byte[] { 0x05, 0x02 }, Wire.Bitmap(users.Reverse().ToArray(), [users[0], users[7], users[9]]));
    }
    [Fact] public void TwoThousandUsersAndFullAudienceSplitByEncodedBytes()
    {
        var users = Enumerable.Range(0, 2000).Select(i => i.ToString("x16")).ToArray();
        var events = Enumerable.Range(1, 140).Select(i => Event(i) with { Audience = users }).ToArray();
        var batches = Wire.Split("server", users, events).ToArray();
        Assert.Equal(140, batches.Sum(b => b.Events.Length));
        Assert.All(batches, b => { Assert.InRange(b.Body.Length, 1, Wire.MaxBody); Assert.InRange(b.Events.Length, 1, 50); });
        Assert.Contains(batches, b => b.Events.Length < 50);
        Assert.InRange(Wire.Encode("server", users, [events[0]]).Length, 1, Wire.MaxBody);
        Assert.Throws<InvalidDataException>(() => Wire.Encode("server", users.Append("ffffffffffffffff").ToArray(), [events[0]]));
    }
    [Fact] public void AllOperationsHaveExactFieldAllowlist()
    {
        var sample = Event();
        var operations = new[] { sample, sample with { Op = "rm", Folders = [Id(20)] }, sample with { Op = "rmbulk", Folders = [Id(20)], Count = 250 }, sample with { Op = "rmlibrary", Folders = [Id(20)] } };
        foreach (var notice in operations)
        {
            using var json = JsonDocument.Parse(Wire.Encode("server", [], [notice]));
            Assert.Equal(new[] { "events", "server", "users", "v" }, json.RootElement.EnumerateObject().Select(p => p.Name).Order());
            var item = json.RootElement.GetProperty("events")[0];
            var expected = notice.Op switch
            {
                "add" => new[] { "a", "at", "e", "id", "life", "op", "t" },
                "rm" => ["a", "at", "e", "f", "id", "life", "op", "t"],
                "rmbulk" => ["a", "at", "count", "e", "f", "life", "op"],
                _ => ["at", "e", "f", "life", "op"]
            };
            Assert.Equal(expected, item.EnumerateObject().Select(p => p.Name).Order());
        }
    }
    [Fact] public void AcknowledgementsAreStrictAndIntersectRequest()
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { accepted = new[] { Id(1), Id(2) } }));
        Assert.Equal(new[] { Id(1) }, Responses.Accepted(bytes, [Id(1)]));
        foreach (string bad in new[] { "null", "[]", "{}", "{\"accepted\":null}", "{\"accepted\":[12]}", "{\"accepted\":[\"../bad\"]}", "{\"accepted\":[],\"redirect\":\"evil\"}", "{\"accepted\":[],\"accepted\":[]}", "{\"accepted\":[[[[[[]]]]]]}" })
            Assert.ThrowsAny<Exception>(() => Responses.Accepted(Encoding.UTF8.GetBytes(bad), [Id(1)]));
        Assert.Throws<InvalidDataException>(() => Responses.Accepted(new byte[16385], []));
    }
    [Fact] public async Task ReadsAreBoundedIncludingUnknownContentLength()
    {
        Assert.Equal(16384, (await Responses.ReadBounded(new MemoryStream(new byte[16384]), default)).Length);
        await Assert.ThrowsAsync<InvalidDataException>(() => Responses.ReadBounded(new MemoryStream(new byte[16385]), default));
    }
    [Fact] public void RetryAfterIsALowerBound()
    {
        Assert.Equal(TimeSpan.FromMinutes(10), Responses.RetryDelay(1, TimeSpan.FromMinutes(10), 0));
        Assert.True(Responses.Retryable(HttpStatusCode.TooManyRequests)); Assert.True(Responses.Retryable(HttpStatusCode.ServiceUnavailable));
        Assert.False(Responses.Retryable(HttpStatusCode.Unauthorized));
    }
    [Theory]
    [InlineData("http://jellysee-relay.harshlabsllc.workers.dev")]
    [InlineData("https://evil.test")]
    [InlineData("https://jellysee-relay.harshlabsllc.workers.dev.evil.test")]
    [InlineData("https://jellysee-relay.harshlabsllc.workers.dev/@evil")]
    [InlineData("https://jellysee-relay.harshlabsllc.workers.dev:444")]
    public void OriginIsExactAllowlist(string origin) => Assert.Throws<InvalidDataException>(() => RelayClient.Destination(origin, Identity.ServerKey("test"), "events"));
    [Fact] public async Task FakeTransportChecksHeadersAndNeverFollowsRedirects()
    {
        var handler = new Handler(async request =>
        {
            Assert.Equal(RelayClient.Origin, request.RequestUri!.GetLeftPart(UriPartial.Authority));
            Assert.Equal("Bearer test-secret", request.Headers.Authorization!.ToString());
            Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
            Assert.NotEmpty(await request.Content.ReadAsByteArrayAsync());
            return new HttpResponseMessage(HttpStatusCode.TemporaryRedirect) { Headers = { Location = new Uri("https://evil.test") } };
        });
        using var client = new RelayClient(handler);
        var result = await client.Post(RelayClient.Origin, Identity.ServerKey("test"), "test-secret", "events", Wire.Encode("test", [], [Event()]), [Id(1)], default);
        Assert.False(result.Success); Assert.False(result.Retryable); Assert.Equal(1, handler.Calls);
    }
    [Fact] public async Task HugeOrWrongSuccessNeverAcknowledges()
    {
        foreach (var body in new[] { new string('x', 17000), "{\"accepted\":[\"wrong\"]}", "{\"accepted\":[],\"extra\":1}" })
        {
            using var client = new RelayClient(new Handler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) })));
            var result = await client.Post(RelayClient.Origin, Identity.ServerKey("server"), "test-secret", "events", [], [Id(1)], default);
            Assert.False(result.Success); Assert.Empty(result.Accepted);
        }
    }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) { Calls++; return send(request); }
    }
}
