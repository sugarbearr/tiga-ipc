namespace TigaIpc.IO;

public sealed class SynchronizationMetrics
{
    public SynchronizationMetrics(long lockTimeouts, long lockAbandoned, long readerGraceResets)
    {
        LockTimeouts = lockTimeouts;
        LockAbandoned = lockAbandoned;
        ReaderGraceResets = readerGraceResets;
    }

    public long LockTimeouts { get; }

    public long LockAbandoned { get; }

    public long ReaderGraceResets { get; }
}

public interface ISynchronizationMetricsProvider
{
    SynchronizationMetrics GetSynchronizationMetrics();
}
