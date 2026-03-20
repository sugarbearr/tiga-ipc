namespace TigaIpc.Core;

public class MessageBusOptions
{
    public TimeSpan DefaultTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public int MaxRetries { get; set; } = 3;
    public bool EnableCompression { get; set; } = true;
    public int CompressionThreshold { get; set; } = 1024;
    public bool EnableMetrics { get; set; } = true;
}
