using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Jellyfin.Plugin.JellySeeNotifications.Core;

public static class Identity
{
    // SystemManager.GetPublicSystemInfo.Id = IServerApplicationHost.SystemId. Do not parse as Guid.
    public static string ServerKey(string server)
    {
        // Swift CharacterSet.whitespaces is Unicode space separators plus TAB, excluding newlines.
        int start = 0, end = server.Length;
        static bool Space(char c) => c == '\t' || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.SpaceSeparator;
        while (start < end && Space(server[start])) start++;
        while (end > start && Space(server[end - 1])) end--;
        return "s1-" + Hash(server[start..end].ToLowerInvariant());
    }
    public static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string UserHash(string serverKey, Guid user) => Hash("jellysee-user-v1:" + serverKey + ":" + user.ToString("N"))[..16];
    public static string EventId() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
    public static bool IsId(string? value) => value is { Length: 32 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

// Persistence records are intentionally separate from the HTTP DTO. The serializer cannot leak metadata.
public sealed record Notice
{
    public string EventId { get; init; } = Identity.EventId();
    public string Op { get; init; } = "add";
    public string? Id { get; init; }
    public string? Type { get; init; }
    public string? Series { get; init; }
    public string[] Folders { get; init; } = [];
    public int Count { get; init; }
    public int Life { get; init; }
    public long QueuedAt { get; init; }
    public long At { get; init; }
    [System.Text.Json.Serialization.JsonConverter(typeof(AudienceConverter))]
    public string[] Audience { get; init; } = [];
    public string Stage { get; init; } = "pendingAdd";
    public string SetupHash { get; init; } = "";
    public string? ProviderDigest { get; init; }
}

public sealed record Batch(byte[] Body, Notice[] Events);

public static class Wire
{
    public const int MaxBody = 48 * 1024;
    public const int MaxUsers = 2000;
    public const int MaxEvents = 50;
    public static readonly string[] Types = ["Movie", "Series", "Episode", "MusicVideo", "Book", "AudioBook"];

    public static byte[] Bitmap(IReadOnlyList<string> users, IEnumerable<string> audience)
    {
        var allowed = audience.ToHashSet(StringComparer.Ordinal);
        byte[] bits = new byte[(users.Count + 7) / 8];
        for (int i = 0; i < users.Count; i++) if (allowed.Contains(users[i])) bits[i >> 3] |= (byte)(1 << (i & 7));
        return bits;
    }

    public static byte[] Encode(string server, IReadOnlyList<string> users, IReadOnlyList<Notice> events)
    {
        if (users.Count > MaxUsers || users.Distinct().Count() != users.Count || users.Any(u => u.Length != 16 || !u.All(Uri.IsHexDigit)))
            throw new InvalidDataException("Invalid users table");
        if (events.Count > MaxEvents) throw new InvalidDataException("Event cap");
        using var stream = new MemoryStream();
        using (var w = new Utf8JsonWriter(stream))
        {
            w.WriteStartObject(); w.WriteNumber("v", 2); w.WriteString("server", server);
            w.WriteBase64String("users", users.SelectMany(Convert.FromHexString).ToArray());
            w.WriteStartArray("events");
            foreach (var e in events)
            {
                Validate(e);
                w.WriteStartObject(); w.WriteString("e", e.EventId); w.WriteString("op", e.Op);
                w.WriteNumber("life", e.Life); w.WriteNumber("at", e.At);
                if (e.Op != "rmlibrary") w.WriteBase64String("a", Bitmap(users, e.Audience));
                if (e.Op is "add" or "rm") { w.WriteString("id", e.Id); w.WriteString("t", e.Type); }
                if (e.Op == "add" && e.Series is not null) w.WriteString("s", e.Series);
                if (e.Op != "add")
                {
                    w.WriteStartArray("f"); foreach (var f in e.Folders) w.WriteStringValue(f); w.WriteEndArray();
                }
                if (e.Op == "rmbulk") w.WriteNumber("count", e.Count);
                w.WriteEndObject();
            }
            w.WriteEndArray(); w.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static void Validate(Notice e)
    {
        if (!Identity.IsId(e.EventId) || e.Life < 0 || e.At < 0 || e.QueuedAt < 0 || e.Folders.Length > 4 || e.Folders.Any(f => !Identity.IsId(f)))
            throw new InvalidDataException("Invalid event");
        if (e.Op is "add" or "rm")
        {
            if (!Identity.IsId(e.Id) || !Types.Contains(e.Type)) throw new InvalidDataException("Invalid title");
        }
        else if (e.Op is not ("rmbulk" or "rmlibrary")) throw new InvalidDataException("Invalid op");
        if (e.Op is "rmbulk" or "rmlibrary" && e.Folders.Length != 1) throw new InvalidDataException("Invalid folder");
        if (e.Op == "rmbulk" && e.Count < 1) throw new InvalidDataException("Invalid count");
        if (e.Series is not null && !Identity.IsId(e.Series)) throw new InvalidDataException("Invalid series");
        if (e.Audience.Length > MaxUsers || e.Audience.Any(u => u.Length != 16 || u.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))))
            throw new InvalidDataException("Invalid audience");
    }

    public static IEnumerable<Batch> Split(string server, IReadOnlyList<string> users, IEnumerable<Notice> events)
    {
        List<Notice> group = [];
        foreach (var e in events)
        {
            var candidate = group.Append(e).ToArray();
            if (candidate.Length > MaxEvents || Encode(server, users, candidate).Length > MaxBody)
            {
                if (group.Count == 0) throw new InvalidDataException("Single event too large");
                yield return new Batch(Encode(server, users, group), group.ToArray()); group.Clear();
            }
            group.Add(e);
            if (Encode(server, users, group).Length > MaxBody) throw new InvalidDataException("Single event too large");
        }
        if (group.Count != 0) yield return new Batch(Encode(server, users, group), group.ToArray());
    }
}

public static class Responses
{
    public const int MaxBytes = 16 * 1024;
    public static async Task<byte[]> ReadBounded(Stream input, CancellationToken ct)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[2048];
        while (true)
        {
            int n = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaxBytes + 1 - (int)output.Length)), ct);
            if (n == 0) return output.ToArray();
            output.Write(buffer, 0, n);
            if (output.Length > MaxBytes) throw new InvalidDataException("Response cap");
        }
    }

    public static HashSet<string> Accepted(byte[] bytes, IEnumerable<string> requested)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException("Response cap");
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid response");
        var props = root.EnumerateObject().ToArray();
        if (props.Length != 1 || props[0].Name != "accepted" || props[0].Value.ValueKind != JsonValueKind.Array || props[0].Value.GetArrayLength() > 50)
            throw new InvalidDataException("Invalid acknowledgement");
        var allowed = requested.ToHashSet(StringComparer.Ordinal);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in props[0].Value.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || !Identity.IsId(value.GetString())) throw new InvalidDataException("Invalid acknowledgement id");
            string id = value.GetString()!;
            if (allowed.Contains(id)) result.Add(id);
        }
        return result;
    }

    public static TimeSpan RetryDelay(int attempt, TimeSpan? retryAfter, double jitter)
    {
        var backoff = TimeSpan.FromSeconds(Math.Min(3600, Math.Pow(2, Math.Min(attempt, 12))) * (0.5 + Math.Clamp(jitter, 0, 1)));
        return retryAfter is { } hint && hint > backoff ? hint : backoff;
    }
    public static bool Retryable(HttpStatusCode status) => (int)status == 429 || (int)status >= 500;
}

public sealed record SendResult(bool Success, HashSet<string> Accepted, string ErrorCode, TimeSpan? RetryAfter = null, bool Retryable = true);

public sealed class RelayClient : IDisposable
{
    public const string Origin = "https://jellysee-relay.harshlabsllc.workers.dev";
    private readonly HttpClient client;
    public RelayClient() : this(new HttpClientHandler { AllowAutoRedirect = false }) { }
    public RelayClient(HttpMessageHandler handler) => client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
    public static Uri Destination(string origin, string serverKey, string route)
    {
        if (origin != Origin || !serverKey.StartsWith("s1-", StringComparison.Ordinal) || serverKey.Length != 67 || !serverKey[3..].All(Uri.IsHexDigit) || route is not ("events" or "heartbeat"))
            throw new InvalidDataException("Relay origin is not allowlisted");
        return new Uri(origin + "/v1/jellyfin/" + serverKey + "/" + route);
    }
    public async Task<SendResult> Post(string origin, string key, string secret, string route, byte[] body, IEnumerable<string> ids, CancellationToken ct)
    {
        if (secret.Length is 0 or > 4096 || secret.Any(c => char.IsControl(c))) return new(false, [], "invalidConfiguration", null, false);
        using var request = new HttpRequestMessage(HttpMethod.Post, Destination(origin, key, route));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            TimeSpan? retry = response.Headers.RetryAfter?.Delta;
            if (response.Headers.RetryAfter?.Date is { } date) retry = date - DateTimeOffset.UtcNow;
            if (response.StatusCode != HttpStatusCode.OK)
                return new(false, [], response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? "unauthorized" : "relayUnreachable", retry, Responses.Retryable(response.StatusCode));
            if (response.Content.Headers.ContentLength > Responses.MaxBytes) throw new InvalidDataException("Response cap");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            byte[] bytes = await Responses.ReadBounded(stream, timeout.Token);
            return new(true, route == "events" ? Responses.Accepted(bytes, ids) : [], "none");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException or OperationCanceledException && !ct.IsCancellationRequested)
        { return new(false, [], "relayUnreachable"); }
    }
    public void Dispose() => client.Dispose();
}
