namespace TigaIpc.Core;

public class MessageBusException : Exception
{
    public string MethodName { get; }
    public string? RequestId { get; }

    public MessageBusException(string message, string methodName, string? requestId = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        MethodName = methodName;
        RequestId = requestId;
    }
}