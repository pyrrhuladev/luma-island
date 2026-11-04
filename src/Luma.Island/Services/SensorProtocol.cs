using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Luma.Island.Services;

public sealed record SensorRequest(int Version, bool Cpu, bool Memory);
public sealed record SensorResponse(int Version, DateTimeOffset SampledAtUtc, double? CpuCelsius,
    double? MemoryCelsius, string CpuStatus, string MemoryStatus);

/// <summary>Read-only local sensor protocol. One UTF-8 JSON object followed by LF per request/response.</summary>
public static class SensorProtocol
{
    public const int Version = 1, MaximumFrameBytes = 4096;
    public const string PipeName = "LumaIsland.SensorHost.v1", ServiceName = "LumaIslandSensors";
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4
    };

    public static SensorResponse Empty(bool cpu, bool memory, string status) => new(Version,
        DateTimeOffset.UtcNow, null, null, cpu ? status : "disabled", memory ? status : "disabled");
    public static bool IsValidCelsius(double? value, bool memory = false) => value.HasValue &&
        double.IsFinite(value.Value) && value.Value >= -40 && value.Value <= (memory ? 125 : 150);

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        if (json.Length == 0 || json.Length > MaximumFrameBytes) throw new InvalidDataException("Sensor frame size.");
        byte[] frame = new byte[json.Length + 1];
        json.CopyTo(frame, 0); frame[^1] = 10;
        await stream.WriteAsync(frame, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token)
    {
        // The protocol permits exactly one outstanding frame, never pipelined commands.
        byte[] frame = new byte[MaximumFrameBytes];
        byte[] chunk = new byte[512];
        int length = 0;
        while (true)
        {
            int count = await stream.ReadAsync(chunk, token).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException();
            int end = Array.IndexOf(chunk, (byte)10, 0, count);
            int copy = end < 0 ? count : end;
            if (length + copy > frame.Length || end >= 0 && end != count - 1)
                throw new InvalidDataException("Sensor frame size or pipelining.");
            chunk.AsSpan(0, copy).CopyTo(frame.AsSpan(length));
            length += copy;
            if (end >= 0) break;
        }
        if (length == 0) throw new InvalidDataException("Empty sensor frame.");
        return JsonSerializer.Deserialize<T>(frame.AsSpan(0, length), JsonOptions)
            ?? throw new InvalidDataException("Null sensor frame.");
    }
}
