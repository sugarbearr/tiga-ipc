namespace TigaIpc.Messaging;

/// <summary>
/// Event arguments for message received event.
/// </summary>
public class MessageReceivedEventArgs(string message) : EventArgs
{
    /// <summary>
    /// Gets the message.
    /// </summary>
    public string Message { get; } = message;
}

/// <summary>
/// Event arguments for message response event.
/// </summary>
internal class MessageResponseEventArgs(BinaryData message) : EventArgs
{
    /// <summary>
    /// Gets the message.
    /// </summary>
    internal BinaryData Message { get; } = message;
}