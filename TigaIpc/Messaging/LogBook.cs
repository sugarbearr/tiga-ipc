using System.Collections.Immutable;
using System.Diagnostics;
using MessagePack;

namespace TigaIpc.Messaging;

[MessagePackObject]
public readonly record struct LogBook(
    [property: Key(0)] long LastId,
    [property: Key(1)] ImmutableList<LogEntry> Entries
)
{
    public long CalculateLogSize(int start)
    {
        var size = (long)sizeof(long);
        for (var i = start; i < Entries.Count; i++)
        {
            size += LogEntry.Overhead + Entries[i].Message.Length + Entries[i].MediaType?.Length ?? 0;
        }

        return size;
    }

    public int CountEntriesToTrim(TimeProvider timeProvider, TimeSpan minMessageAge)
    {
#if NET7_0_OR_GREATER
            ArgumentNullException.ThrowIfNull(timeProvider);
#else
        if (timeProvider is null)
        {
            throw new ArgumentNullException(nameof(timeProvider));
        }
#endif

        if (Entries.Count == 0)
        {
            return 0;
        }

        var cutoffPoint = timeProvider.GetTimestamp() -
                          minMessageAge.Ticks / TimeSpan.TicksPerSecond * Stopwatch.Frequency;

        var i = 0;
        for (; i < Entries.Count; i++)
        {
            if (Entries[i].Timestamp >= cutoffPoint)
            {
                break;
            }
        }

        return i;
    }
}