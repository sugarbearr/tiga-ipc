using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace TigaIpc.Messaging;

/// <summary>
/// channel class
/// </summary>
public partial class TigaChannel
{
    /// <summary>
    /// Subscribe to messages using an async enumerable.
    /// </summary>
    /// <param name="cancellationToken"></param>
    public async IAsyncEnumerable<BinaryData> SubscribeAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
#if NET7_0_OR_GREATER
            ObjectDisposedException.ThrowIf(disposed, this);
#else
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TigaChannel));
        }
#endif

        var (id, receiverChannel) = CreateRegisteredReceiverChannel();

        try
        {
            await foreach (var entry in StreamEntriesAsync(receiverChannel.Reader, cancellationToken)
                               .ConfigureAwait(false))
            {
                yield return BinaryData.FromBytes(entry.Message, entry.MediaType);
            }
        }
        finally
        {
            CleanupReceiverChannel(id, receiverChannel);
        }
    }
}
