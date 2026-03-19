using MessagePack;

namespace TigaIpc.Messaging;

[MessagePackObject]
public readonly record struct LogEntry(
    [property: Key(0)] long Id,
    [property: Key(1)] Guid Instance,
    [property: Key(2)] long Timestamp,
    [property: Key(3)] ReadOnlyMemory<byte> Message,
    [property: Key(4)] string? MediaType
)
{
    public static long Overhead { get; }

    static LogEntry()
    {
        using var memoryStream = MemoryStreamPool.Manager.GetStream(nameof(LogEntry));

        MessagePackSerializer.Serialize(
            (MemoryStream)memoryStream,
            new LogEntry
            { Id = long.MaxValue, Instance = Guid.Empty, Timestamp = TimeProvider.System.GetTimestamp() },
            MessagePackOptions.Instance
        );

        Overhead = memoryStream.Length;
    }
}