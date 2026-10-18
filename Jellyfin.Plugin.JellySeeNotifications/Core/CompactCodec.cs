using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.JellySeeNotifications.Core;

// Packed audience hashes keep a worst-case 5,000-event / 2,000-user outbox bounded on disk.
public sealed class AudienceConverter : JsonConverter<string[]>
{
    public override string[] Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String || reader.ValueSpan.Length > 21336) throw new JsonException("Audience cap");
        byte[] bytes = reader.GetBytesFromBase64();
        if (bytes.Length > 16000 || bytes.Length % 8 != 0) throw new JsonException("Invalid audience");
        string[] result = new string[bytes.Length / 8];
        for (int i = 0; i < result.Length; i++) result[i] = Convert.ToHexStringLower(bytes.AsSpan(i * 8, 8));
        return result;
    }
    public override void Write(Utf8JsonWriter writer, string[] value, JsonSerializerOptions options) => writer.WriteBase64StringValue(value.SelectMany(Convert.FromHexString).ToArray());
}

public static class CompactCodec
{
    public const int TitleBytes = 103;
    public const int TombstoneBytes = 36;
    public static byte[] Encode(Title title)
    {
        byte[] bytes = new byte[TitleBytes];
        Convert.FromHexString(title.Id).CopyTo(bytes, 0);
        bytes[16] = checked((byte)Array.IndexOf(Wire.Types, title.Type)); bytes[17] = (byte)title.Folders.Length;
        for (int i = 0; i < title.Folders.Length; i++) Convert.FromHexString(title.Folders[i]).CopyTo(bytes, 18 + i * 16);
        if (title.ProviderDigest is not null) { bytes[82] = 1; Convert.FromHexString(title.ProviderDigest).CopyTo(bytes, 83); }
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(91, 8), title.Seq);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(99, 4), title.Life);
        return bytes;
    }
    public static Title Title(byte[] bytes)
    {
        if (bytes.Length != TitleBytes || bytes[16] >= Wire.Types.Length || bytes[17] > 4 || bytes[82] > 1) throw new InvalidDataException("Invalid compact title");
        return new(Convert.ToHexStringLower(bytes.AsSpan(0, 16)), Wire.Types[bytes[16]],
            Enumerable.Range(0, bytes[17]).Select(i => Convert.ToHexStringLower(bytes.AsSpan(18 + i * 16, 16))).ToArray(),
            bytes[82] == 0 ? null : Convert.ToHexStringLower(bytes.AsSpan(83, 8)),
            BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(91, 8)), BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(99, 4)));
    }
    public static byte[] Encode(Tombstone tombstone)
    {
        byte[] bytes = new byte[TombstoneBytes]; Convert.FromHexString(tombstone.Id).CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16, 8), tombstone.Seq);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(24, 8), tombstone.At);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(32, 4), tombstone.Life); return bytes;
    }
    public static Tombstone Tombstone(byte[] bytes) => bytes.Length != TombstoneBytes ? throw new InvalidDataException() : new(
        Convert.ToHexStringLower(bytes.AsSpan(0, 16)), BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16, 8)),
        BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(24, 8)), BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(32, 4)));    public static byte[] Encode(Lifecycle lifecycle)
    {
        byte[] bytes = new byte[20]; Convert.FromHexString(lifecycle.Id).CopyTo(bytes, 0);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), lifecycle.Life); return bytes;
    }
    public static Lifecycle Lifecycle(byte[] bytes) => bytes.Length != 20 ? throw new InvalidDataException() : new(
        Convert.ToHexStringLower(bytes.AsSpan(0, 16)), BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16)));

}
