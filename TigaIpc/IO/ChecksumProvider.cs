namespace TigaIpc.IO;

public delegate ulong ChecksumProvider(ReadOnlySpan<byte> data);
