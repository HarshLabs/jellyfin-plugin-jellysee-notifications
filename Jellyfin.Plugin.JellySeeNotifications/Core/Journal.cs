using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellySeeNotifications.Core;

// Single writer, append + fsync before in-memory mutation. Snapshot rename precedes journal truncation.
public sealed class Journal : IDisposable
{
    public const int MaxRecord = 128 * 1024;
    public const long MaxFile = 192L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { MaxDepth = 12, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly StateDirectory files;
    private FileStream? writer;
    private long operations;
    public State State { get; private set; } = new();
    public bool RecoveredCorruption { get; private set; }
    public long DiskBytes
    {
        get
        {
            try { return files.Length("journal.ndjson") + files.Length("snapshot.bin"); }
            catch (IOException ex) { throw new StatePersistenceException(ex); }
        }
    }
    private sealed record Entry(long Seq, Change[] Changes);
    private sealed record Envelope(string Payload, string Sha256);
    private sealed record Header(long Seq, int Format);

    public Journal(string directory)
    {
        files = new StateDirectory(directory);
        try { Load(); writer!.Seek(0, SeekOrigin.End); }
        catch { writer?.Dispose(); files.Dispose(); throw; }
    }
    private static byte[] Encode<T>(T value)
    {
        string payload = JsonSerializer.Serialize(value, Json);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new Envelope(payload, Identity.Hash(payload)), Json);
        if (bytes.Length > MaxRecord) throw new InvalidDataException("Record cap");
        return bytes;
    }
    private static T Decode<T>(byte[] bytes)
    {
        var env = JsonSerializer.Deserialize<Envelope>(bytes, Json) ?? throw new InvalidDataException();
        if (env.Payload is null || env.Sha256 != Identity.Hash(env.Payload)) throw new InvalidDataException("Checksum");
        return JsonSerializer.Deserialize<T>(env.Payload, Json) ?? throw new InvalidDataException();
    }
    // ReadLine would allocate without a bound before detecting hostile input.
    private static byte[]? Line(FileStream input)
    {
        using var line = new MemoryStream();
        while (true)
        {
            int b = input.ReadByte();
            if (b < 0) { if (line.Length != 0) throw new InvalidDataException("Truncated record"); return null; }
            if (b == '\n') return line.ToArray();
            if (line.Length >= MaxRecord) throw new InvalidDataException("Record cap");
            line.WriteByte((byte)b);
        }
    }
    private static byte[] ReadExact(FileStream input, int size)
    {
        byte[] bytes = new byte[size];
        try { input.ReadExactly(bytes); } catch (EndOfStreamException) { throw new InvalidDataException("Truncated snapshot"); }
        return bytes;
    }
    private static void WriteLine(FileStream output, byte[] bytes) { output.Write(bytes); output.WriteByte((byte)'\n'); }
    private void Load()
    {
        using var snapshot = files.Read("snapshot.bin");
        if (snapshot is not null)
        {
            try
            {
                var input = snapshot;
                if (input.Length > MaxFile) throw new InvalidDataException("Snapshot cap");
                var header = Decode<Header>(Line(input) ?? throw new InvalidDataException());
                if (header.Format != 3 || header.Seq < 0) throw new InvalidDataException();
                State restored = new(); long count = 0;
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                while (true)
                {
                    int kind = input.ReadByte();
                    if (kind == 4)
                    {
                        byte[] footer = ReadExact(input, 40);
                        if (BinaryPrimitives.ReadInt64LittleEndian(footer.AsSpan(0, 8)) != count || !CryptographicOperations.FixedTimeEquals(footer.AsSpan(8), digest.GetHashAndReset()) || input.ReadByte() != -1)
                            throw new InvalidDataException("Snapshot footer");
                        break;
                    }
                    if (kind is < 0 or > 3) throw new InvalidDataException("Incomplete snapshot");
                    digest.AppendData([(byte)kind]);
                    byte[] payload;
                    Change change;
                    if (kind == 0)
                    {
                        byte[] length = ReadExact(input, 4); digest.AppendData(length);
                        int size = BinaryPrimitives.ReadInt32LittleEndian(length);
                        if (size is < 1 or > MaxRecord) throw new InvalidDataException("Snapshot record cap");
                        payload = ReadExact(input, size);
                        var entry = Decode<Entry>(payload);
                        if (entry.Seq != header.Seq || entry.Changes.Length != 1) throw new InvalidDataException();
                        change = entry.Changes[0];
                    }
                    else if (kind == 1) { payload = ReadExact(input, CompactCodec.TitleBytes); change = new() { Kind = "title", Title = CompactCodec.Title(payload) }; }
                    else if (kind == 2) { payload = ReadExact(input, CompactCodec.TombstoneBytes); change = new() { Kind = "remove", Tombstone = CompactCodec.Tombstone(payload) }; }
                    else { payload = ReadExact(input, 20); change = new() { Kind = "life", Lifecycle = CompactCodec.Lifecycle(payload) }; }
                    restored.Apply(change); digest.AppendData(payload); count++;
                }
                restored.Seq = header.Seq; State = restored;
            }
            catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or NullReferenceException or OverflowException)
            {
                // Copy through a new verified inode; never open/truncate an existing
                // quarantine destination, including an attacker-installed link.
                using var quarantine = files.Rewrite("snapshot.bin.corrupt", snapshot, Math.Min(snapshot.Length, MaxFile));
                files.Remove("snapshot.bin");
                RecoveredCorruption = true;
            }
        }
        using var log = files.Read("journal.ndjson");
        if (log is null) { writer = files.Rewrite("journal.ndjson", null, 0); return; }
        long good = 0;
        try
        {
            byte[]? line;
            while ((line = Line(log)) is not null)
            {
                if (log.Position > MaxFile) throw new InvalidDataException("Journal cap");
                var entry = Decode<Entry>(line);
                if (entry.Seq < 1 || entry.Changes is null || entry.Changes.Length is 0 or > 256) throw new InvalidDataException("Invalid transaction");
                if (entry.Seq > State.Seq)
                {
                    if (entry.Seq != State.Seq + 1 || entry.Changes.Length > 256) throw new InvalidDataException("Sequence gap");
                    // Validate all changes against a bounded copy of the affected state before committing.
                    State.Transaction(entry.Changes);
                    State.Seq = entry.Seq;
                }
                good = log.Position;
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException or NullReferenceException or OverflowException)
        { RecoveredCorruption = true; }
        // Recovery and future appends use a fresh exclusive inode. Existing hard
        // links cannot turn a replay-tail repair into an outside-file truncation.
        writer = files.Rewrite("journal.ndjson", log, good);
    }
    public void Commit(params Change[] changes)
    {
        try { CommitCore(changes); }
        catch (Exception ex) when (ex is IOException and not StatePersistenceException or InvalidDataException or UnauthorizedAccessException) { throw new StatePersistenceException(ex); }
    }
    private void CommitCore(Change[] changes)
    {
        if (changes.Length is 0 or > 256) throw new InvalidDataException("Transaction cap");
        State.Transaction(changes, dryRun: true);
        long seq = checked(State.Seq + 1);
        byte[] bytes = Encode(new Entry(seq, changes));
        if (writer!.Length + bytes.Length + 1 > MaxFile / 2) Compact();
        files.Verify("journal.ndjson", writer!);
        WriteLine(writer!, bytes); writer!.Flush(true);
        State.Transaction(changes);
        State.Seq = seq; operations++;
    }
    public void MaybeCompact() { if (operations >= 4096) Compact(); }
    public void Compact()
    {
        try { CompactCore(); }
        catch (Exception ex) when (ex is IOException and not StatePersistenceException or InvalidDataException or UnauthorizedAccessException) { throw new StatePersistenceException(ex); }
    }
    private void CompactCore()
    {
        State.ValidateInvariants();
        var temp = files.Create("snapshot.tmp");
        using (var output = temp.Stream)
        {
            WriteLine(output, Encode(new Header(State.Seq, 3)));
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long count = 0;
            foreach (var c in State.Snapshot())
            {
                byte kind = c.Kind == "title" ? (byte)1 : c.Kind == "remove" ? (byte)2 : c.Kind == "life" ? (byte)3 : (byte)0;
                byte[] bytes = kind == 1 ? CompactCodec.Encode(c.Title!) : kind == 2 ? CompactCodec.Encode(c.Tombstone!) : kind == 3 ? CompactCodec.Encode(c.Lifecycle!) : Encode(new Entry(State.Seq, [c]));
                output.WriteByte(kind); digest.AppendData([kind]);
                if (kind == 0)
                {
                    byte[] length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
                    output.Write(length); digest.AppendData(length);
                }
                output.Write(bytes); digest.AppendData(bytes); count++;
                if (output.Length > MaxFile - 41) throw new InvalidDataException("Snapshot cap");
            }
            output.WriteByte(4);
            byte[] footer = new byte[40]; BinaryPrimitives.WriteInt64LittleEndian(footer.AsSpan(0, 8), count);
            digest.GetHashAndReset().CopyTo(footer, 8); output.Write(footer);
            files.Replace(temp.Name, output, "snapshot.bin");
        }
        var replacement = files.Rewrite("journal.ndjson", null, 0);
        writer!.Dispose(); writer = replacement; operations = 0;
    }
    public void Dispose() { writer?.Dispose(); files.Dispose(); }
}

public sealed class StatePersistenceException(Exception inner) : IOException("Plugin state could not be written", inner);
