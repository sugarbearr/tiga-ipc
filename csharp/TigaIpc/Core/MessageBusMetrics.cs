using System.Collections.Concurrent;
using System.Diagnostics;

namespace TigaIpc.Core;

internal class MessageBusMetrics
{
    private readonly ConcurrentDictionary<string, Stopwatch> _methodTimers = new();
    private readonly ConcurrentDictionary<string, long> _messageCounters = new();

    public IDisposable MeasureMethod(string methodName)
    {
        var timer = new Stopwatch();
        timer.Start();
        _methodTimers.TryAdd(methodName, timer);

        return new DisposableAction(() =>
        {
            if (_methodTimers.TryRemove(methodName, out var sw))
            {
                sw.Stop();
                // 这里可以添加指标收集逻辑
            }
        });
    }

    public void IncrementMessageCount(string methodName)
    {
        _messageCounters.AddOrUpdate(methodName, 1, (_, count) => count + 1);
    }

    private class DisposableAction : IDisposable
    {
        private readonly Action _action;
        private bool _disposed;

        public DisposableAction(Action action)
        {
            _action = action;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _action();
            _disposed = true;
        }
    }
}