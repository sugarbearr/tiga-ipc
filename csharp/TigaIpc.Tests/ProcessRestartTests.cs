using System.Diagnostics;
using Microsoft.Extensions.Options;
using TigaIpc.Messaging;
using Xunit;

namespace TigaIpc.Tests;

public class ProcessRestartTests
{
    [Fact(Timeout = 30000)]
    public async Task WriterProcess_Restart_BurstsAreReceived()
    {
        var name = "restart_" + Guid.NewGuid().ToString("N");
        var options = new TigaIpcOptions
        {
            Name = name,
            WaitTimeout = TimeSpan.FromSeconds(2),
            InvokeTimeout = TimeSpan.FromSeconds(5),
            MinMessageAge = TimeSpan.FromMilliseconds(100),
        };

        var expectedPerBurst = 25;
        var bursts = 3;
        var expectedTotal = expectedPerBurst * bursts;
        var received = 0;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var subscriber = new TigaMessageBus(name, MappingType.Memory, new OptionsWrapper<TigaIpcOptions>(options));
        subscriber.MessageReceived += (_, _) =>
        {
            if (Interlocked.Increment(ref received) == expectedTotal)
            {
                tcs.TrySetResult(true);
            }
        };

        for (var i = 0; i < bursts; i++)
        {
            using var process = StartTestHost("publish-burst", name, expectedPerBurst.ToString());
            WaitForChildReady(process);
            process.WaitForExit(5000);
        }

        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(tcs.Task, completed);
        Assert.Equal(expectedTotal, received);
    }

    private static Process StartTestHost(string command, string name, string? count)
    {
        var testHostPath = FindTestHostPath();
        var args = count == null ? $"{command} {name}" : $"{command} {name} {count}";
        var startInfo = new ProcessStartInfo("dotnet", $"\"{testHostPath}\" {args}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start test host");
    }

    private static void WaitForChildReady(Process process)
    {
        var line = process.StandardOutput.ReadLine();
        if (!string.Equals(line, "ready", StringComparison.OrdinalIgnoreCase))
        {
            var error = process.StandardError.ReadToEnd();
            throw new InvalidOperationException($"Test host not ready. Output: {line} Error: {error}");
        }
    }

    private static string FindTestHostPath()
    {
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 6 && baseDir != null; i++)
        {
            var candidateDebug = Path.Combine(baseDir.FullName, "TigaIpc.TestHost", "bin", "Debug", "net6.0", "TigaIpc.TestHost.dll");
            if (File.Exists(candidateDebug))
            {
                return candidateDebug;
            }

            var candidateRelease = Path.Combine(baseDir.FullName, "TigaIpc.TestHost", "bin", "Release", "net6.0", "TigaIpc.TestHost.dll");
            if (File.Exists(candidateRelease))
            {
                return candidateRelease;
            }

            baseDir = baseDir.Parent;
        }

        throw new FileNotFoundException("Could not locate TigaIpc.TestHost.dll");
    }
}
