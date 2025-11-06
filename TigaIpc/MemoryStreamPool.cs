using Microsoft.IO;

namespace TigaIpc;

internal static class MemoryStreamPool
{
    /// <summary>
    /// Gets memory stream manager    /// </summary>
    public static RecyclableMemoryStreamManager Manager { get; } = new();
}