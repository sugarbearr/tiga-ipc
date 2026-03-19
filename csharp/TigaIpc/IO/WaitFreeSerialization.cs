namespace TigaIpc.IO;

public delegate ReadOnlyMemory<byte> WaitFreeSerializer<in T>(T value);

public delegate T WaitFreeDeserializer<out T>(ReadOnlySpan<byte> data);

public delegate bool WaitFreeValidator(ReadOnlySpan<byte> data);
