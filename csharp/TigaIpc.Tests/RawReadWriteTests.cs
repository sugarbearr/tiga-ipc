using Xunit;

namespace TigaIpc.Tests;

public class RawReadWriteTests
{
    [Fact]
    public void WriteRaw_ReadRaw_RoundTrips()
    {
        var name = "raw_roundtrip_" + Guid.NewGuid().ToString("N");
        var options = new TigaIpcOptions
        {
            ChannelName = name,
            MaxFileSize = 64 * 1024,
        };

        var payload = new byte[] { 1, 2, 3, 4, 5 };

        using var file = new WaitFreeMemoryMappedFile(name, MappingType.Memory, options);
        file.WriteRaw(payload);

        using var lease = file.ReadRaw(true);
        using var stream = new MemoryStream();
        lease.Stream.CopyTo(stream);

        Assert.Equal(payload, stream.ToArray());
    }

    [Fact]
    public void Write_Read_WithSerializerAndValidator()
    {
        var name = "typed_roundtrip_" + Guid.NewGuid().ToString("N");
        var options = new TigaIpcOptions
        {
            ChannelName = name,
            MaxFileSize = 64 * 1024,
        };

        WaitFreeSerializer<string> serializer = value => new ReadOnlyMemory<byte>(System.Text.Encoding.UTF8.GetBytes(value));
        WaitFreeDeserializer<string> deserializer = data => System.Text.Encoding.UTF8.GetString(data);
        WaitFreeValidator validator = data => data.Length > 0;

        using var file = new WaitFreeMemoryMappedFile(name, MappingType.Memory, options);
        file.Write("hello", serializer, validator);

        var result = file.Read(deserializer, validator);
        Assert.Equal("hello", result);
    }
}
