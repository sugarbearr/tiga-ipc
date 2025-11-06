using MessagePack;

/// <summary>
/// Event protocol
/// </summary>
public enum EventProtocol
{
    /// <summary>
    /// Publisher
    /// </summary>
    Publisher,

    /// <summary>
    /// Invoke
    /// </summary>
    Invoke,

    /// <summary>
    /// Response
    /// </summary>
    Response,
}

/// <summary>
/// Message class
/// </summary>
[MessagePackObject]
public class MessageBase
{
    /// <summary>
    /// Message ID
    /// </summary>
    [property: Key(0)]
    public string Id { get; set; }

    /// <summary>   
    /// Message type
    /// </summary>
    [property: Key(1)]
    public EventProtocol Protocol { get; set; }
}

/// <summary>
/// Request message
/// </summary>
[MessagePackObject]
public class InvokeMessage : MessageBase
{
    /// <summary>   
    /// Message method
    /// </summary>
    [property: Key(2)]
    public string Method { get; set; }

    /// <summary>
    /// Message data
    /// </summary>
    [property: Key(3)]
    public object? Data { get; set; }
}

/// <summary>
/// Response code
/// </summary>
public enum ResponseCode
{
    Failed = -1,
    Successful = 0,
}

/// <summary>
/// Response message
/// </summary>
[MessagePackObject]
public class ResponseMessage : MessageBase
{
    /// <summary>
    /// Message data
    /// </summary>
    [property: Key(2)]
    public string Data { get; set; }

    /// <summary>
    /// Response code
    /// </summary>
    [property: Key(3)]
    public ResponseCode Code { get; set; }
}

/// <summary>
/// Broadcast message
/// </summary>
[MessagePackObject]
public class PublisherMessage : MessageBase
{
    /// <summary>
    /// Message data
    /// </summary>
    [property: Key(2)]
    public string Data { get; set; }
}