using System.Collections.Immutable;
using System.Runtime.ExceptionServices;
using System.Threading;
using MessagePack;
using Microsoft.Extensions.Options;
using TigaIpc.IO;
using TigaIpc.Messaging;
using Xunit;

namespace TigaIpc.Tests;

public class LogBookSchemaTests
{
    [Fact(Timeout = 15000)]
    public async Task LogBookSchemaVersion_EnvelopeRoundTrip()
    {
        var name = "schema_v2_" + Guid.NewGuid().ToString("N");
        var options = new TigaIpcOptions
        {
            ChannelName = name,
            LogBookSchemaVersion = 2,
            AllowLegacyLogBook = true,
            WaitTimeout = TimeSpan.FromSeconds(2),
            InvokeTimeout = TimeSpan.FromSeconds(5),
            MinMessageAge = TimeSpan.FromMilliseconds(100),
        };

        var optionsWrapper = new OptionsWrapper<TigaIpcOptions>(options);
        await using var publisher = new TigaChannel(name, MappingType.Memory, optionsWrapper);
        await using var subscriber = new TigaChannel(name, MappingType.Memory, optionsWrapper);

        var received = 0;
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscriber.MessageReceived += (_, _) =>
        {
            if (Interlocked.Increment(ref received) == 1)
            {
                tcs.TrySetResult(true);
            }
        };

        await publisher.PublishAsync("schema-v2");
        var completed = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(tcs.Task, completed);
    }

    [Fact]
    public void LogBookSchemaVersion_StrictRejectsLegacy()
    {
        var name = "schema_strict_" + Guid.NewGuid().ToString("N");
        var ipcDirectory = Path.Combine(Path.GetTempPath(), "tiga-ipc-schema-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(ipcDirectory);

        try
        {
            var legacyOptions = new TigaIpcOptions
            {
                ChannelName = name,
                LogBookSchemaVersion = 1,
                IpcDirectory = ipcDirectory,
            };

            using (var legacyFile = new WaitFreeMemoryMappedFile(name, MappingType.File, legacyOptions))
            {
                var logBook = new LogBook(1, []);
                using var stream = new MemoryStream();
                MessagePackSerializer.Serialize(stream, logBook);
                stream.Seek(0, SeekOrigin.Begin);
                legacyFile.Write(stream);
            }

            var strictOptions = new TigaIpcOptions
            {
                ChannelName = name,
                LogBookSchemaVersion = 2,
                AllowLegacyLogBook = false,
                IpcDirectory = ipcDirectory,
            };

            Assert.Throws<InvalidOperationException>(() =>
            {
                using var channel =
                    new TigaChannel(name, MappingType.File, new OptionsWrapper<TigaIpcOptions>(strictOptions));
            });
        }
        finally
        {
            TryDeleteDirectory(ipcDirectory);
        }
    }

    [Fact]
    public void LogBookSchemaVersion_LegacyPayloadOpensWithoutFirstChanceMessagePackException()
    {
        var name = "schema_legacy_probe_" + Guid.NewGuid().ToString("N");
        var ipcDirectory = Path.Combine(Path.GetTempPath(), "tiga-ipc-schema-" + Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(ipcDirectory);

        try
        {
            var legacyOptions = new TigaIpcOptions
            {
                ChannelName = name,
                LogBookSchemaVersion = 1,
                IpcDirectory = ipcDirectory,
            };

            using (var legacyFile = new WaitFreeMemoryMappedFile(name, MappingType.File, legacyOptions))
            {
                var logBook = new LogBook(
                    14,
                    ImmutableList.Create(
                        new LogEntry(13, Guid.NewGuid(), 1, new byte[] { 0x01 }, "application/octet-stream"),
                        new LogEntry(14, Guid.NewGuid(), 2, new byte[] { 0x02 }, "application/octet-stream")
                    )
                );

                using var stream = new MemoryStream();
                MessagePackSerializer.Serialize(stream, logBook);
                stream.Seek(0, SeekOrigin.Begin);
                legacyFile.Write(stream);
            }

            var consumerOptions = new TigaIpcOptions
            {
                ChannelName = name,
                LogBookSchemaVersion = 1,
                AllowLegacyLogBook = true,
                IpcDirectory = ipcDirectory,
            };

            var firstChanceMessagePackExceptions = 0;
            void OnFirstChanceException(object? _, FirstChanceExceptionEventArgs args)
            {
                if (args.Exception is MessagePackSerializationException)
                {
                    Interlocked.Increment(ref firstChanceMessagePackExceptions);
                }
            }

            AppDomain.CurrentDomain.FirstChanceException += OnFirstChanceException;
            try
            {
                using var channel =
                    new TigaChannel(name, MappingType.File, new OptionsWrapper<TigaIpcOptions>(consumerOptions));
            }
            finally
            {
                AppDomain.CurrentDomain.FirstChanceException -= OnFirstChanceException;
            }

            Assert.Equal(0, firstChanceMessagePackExceptions);
        }
        finally
        {
            TryDeleteDirectory(ipcDirectory);
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (true)
        {
            try
            {
                if (!Directory.Exists(path))
                {
                    return;
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(50);
            }
            catch
            {
                return;
            }
        }
    }
}
