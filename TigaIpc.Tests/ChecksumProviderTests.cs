using System.Threading;
using TigaIpc.IO;
using Xunit;

namespace TigaIpc.Tests;

public class ChecksumProviderTests
{
    [Fact]
    public void CustomChecksumProvider_IsUsedForWriteAndRead()
    {
        var name = "checksum_provider_" + Guid.NewGuid().ToString("N");
        var calls = 0;

        ChecksumProvider provider = data =>
        {
            Interlocked.Increment(ref calls);
            return 1;
        };

        var options = new TigaIpcOptions
        {
            Name = name,
            MaxFileSize = 64 * 1024,
            ChecksumProvider = provider,
            VerifyChecksumOnRead = true,
        };

        using var file = new WaitFreeMemoryMappedFile(name, MappingType.Memory, options);
        using var stream = new MemoryStream(new byte[128], writable: false);
        file.Write(stream);

        using var lease = file.ReadRaw(true);
        Assert.True(calls >= 2);
        Assert.True(lease.Size > 0);
    }
}
