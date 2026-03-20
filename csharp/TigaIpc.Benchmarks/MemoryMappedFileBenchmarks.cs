using BenchmarkDotNet.Attributes;
using TigaIpc.IO;

namespace TigaIpc.Benchmarks;

[MemoryDiagnoser]
public class MemoryMappedFileBenchmarks
{
    private WaitFreeMemoryMappedFile? _waitFree;
    private MemoryStream? _payloadStream;
    private byte[]? _payload;

    [Params(256, 4096, 65536)]
    public int PayloadSize { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var name = "bench_" + Guid.NewGuid().ToString("N");
        var options = new TigaIpcOptions
        {
            ChannelName = name,
            MaxFileSize = 1024 * 1024,
            WaitTimeout = TimeSpan.FromSeconds(2),
        };

        _waitFree = new WaitFreeMemoryMappedFile(name + "_wf", MappingType.Memory, options);
        _payload = new byte[PayloadSize];
        _payloadStream = new MemoryStream(_payload, writable: false);

        Seed();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _payloadStream?.Dispose();
        _waitFree?.Dispose();
    }

    [Benchmark]
    public void WaitFree_Write()
    {
        _payloadStream!.Position = 0;
        _waitFree!.Write(_payloadStream);
    }

    [Benchmark]
    public long WaitFree_Read()
    {
        return _waitFree!.Read(static stream => stream.Length);
    }

    [Benchmark]
    public long WaitFree_ReadLease()
    {
        using var lease = _waitFree!.ReadLease(false);
        return lease.Size;
    }

    private void Seed()
    {
        using var seedStream = new MemoryStream(_payload ?? Array.Empty<byte>(), writable: false);
        _waitFree!.Write(seedStream);
    }
}
