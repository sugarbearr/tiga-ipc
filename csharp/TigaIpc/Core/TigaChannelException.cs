namespace TigaIpc.Core;

public class TigaChannelException : Exception
{
    public string MethodName { get; }

    public string? RequestId { get; }

    public TigaChannelException(string message, string methodName, string? requestId = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        MethodName = methodName;
        RequestId = requestId;
    }
}
