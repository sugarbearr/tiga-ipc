namespace TigaIpc.Messaging;

/// <summary>
/// channel class
/// </summary>
public partial class TigaChannel
{
    /// <summary>
    /// Responding to requests with a specific ID and response code
    /// </summary>
    /// <param name="id">Request ID</param>
    /// <param name="method">Method name</param>
    /// <param name="data">Response data</param>
    /// <param name="cancellationToken">Cancel Token</param>
    /// <returns>Async task</returns>
    public async Task ResponseAsync(string id, string method, string data,
        CancellationToken cancellationToken = default)
    {
#if NET7_0_OR_GREATER
            ObjectDisposedException.ThrowIf(_disposed, this);
#else
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TigaChannel));
        }
#endif

        try
        {
            var responseMessage = new InvokeMessage { Id = id, Method = method, Data = data };

            // Use compression to reduce the amount of data transferred, with or without compression depending on the configuration
            var responseBinary = IO.BinaryDataExtensions.FromObjectAsMessagePack(
                responseMessage,
                compress: _options.Value.EnableCompression,
                compressionThreshold: _options.Value.CompressionThreshold);
            await PublishAsync(responseBinary, cancellationToken);
        }
        catch (Exception ex)
        {
            if (_logger is not null)
            {
                PrintFailed(ex, "Error responding to request {RequestId} with method {Method}", id, method);
            }

            throw;
        }
    }
}
