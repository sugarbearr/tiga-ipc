using TigaIpc.IO;

namespace TigaIpc.Messaging;

public partial interface ITigaChannel : IDisposable, IAsyncDisposable
{
    /// <summary>
    /// when a new message is received
    /// </summary>
    event EventHandler<MessageReceivedEventArgs>? MessageReceived;

    /// <summary>
    /// the number of messages published
    /// </summary>
    long MessagesPublished { get; }

    /// <summary>
    /// the number of messages received
    /// </summary>
    long MessagesReceived { get; }

    /// <summary>
    /// the name of the channel
    /// </summary>
    string? Name { get; }

    /// <summary>
    /// get synchronization metrics from the underlying memory mapped file (if available)
    /// </summary>
    SynchronizationMetrics? GetSynchronizationMetrics();
}

public partial interface ITigaChannel
{
    /// <summary>
    /// reset the message send and receive counters
    /// </summary>
    void ResetMetrics();

    /// <summary>
    /// subscribe to messages asynchronously
    /// </summary>
    /// <param name="cancellationToken">cancellation token</param>
    /// <returns>async enumerable</returns>
    IAsyncEnumerable<BinaryData> SubscribeAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Respond to a message
    /// </summary>
    /// <param name="id">message ID</param>
    /// <param name="method">method name</param>
    /// <param name="data">data</param>
    /// <param name="cancellationToken">cancellation token</param>
    Task ResponseAsync(
        string id,
        string method,
        string data,
        CancellationToken cancellationToken = default
    );
}

public partial interface ITigaChannel
{
    /// <summary>
    /// invoke a method on the channel with no parameters
    /// </summary>
    /// <param name="method">method name</param>
    /// <param name="timeout">timeout</param>
    /// <param name="cancellationToken">cancellation token</param>
    Task InvokeAsync(
        string method,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// invoke a method on the channel with no parameters and get a typed result
    /// </summary>
    /// <typeparam name="T">return type</typeparam>
    /// <param name="method">method name</param>
    /// <param name="timeout">timeout</param>
    /// <param name="cancellationToken">cancellation token</param>
    /// <returns>result of type T</returns>
    Task<T?> InvokeAsync<T>(
        string method,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default
    );
}

public partial interface ITigaChannel
{
    /// <summary>
    /// Publish a message to the channel
    /// </summary>
    /// <param name="message">message</param>
    /// <param name="cancellationToken">cancellation token</param>
    Task PublishAsync(BinaryData message, CancellationToken cancellationToken = default);

    /// <summary>
    /// publish multiple messages to the channel
    /// </summary>
    /// <param name="messages">message list</param>
    /// <param name="cancellationToken">cancellation token</param>
    /// <returns>task</returns>
    Task PublishAsync(
        IReadOnlyList<BinaryData> messages,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// publish a message and wait for a response
    /// </summary>
    /// <param name="message">message to publish</param>
    /// <param name="messageId">message ID, for associating requests and responses</param>
    /// <param name="timeout">timeout</param>
    /// <param name="cancellationToken">cancellation token</param>
    /// <returns>response message</returns>
    Task<ResponseMessage> PublishAsync(
        MessageBase message,
        string messageId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    /// publish an asynchronous event and wait for a return value
    /// </summary>
    /// <typeparam name="T">return type</typeparam>
    /// <param name="method">method name</param>
    /// <param name="data">data</param>
    /// <param name="timeout">timeout</param>
    /// <param name="cancellationToken">cancellation token</param>
    /// <returns>return value</returns>
    Task<T?> PublishAsync<T>(
        string method,
        string data,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default
    );
}

public partial interface ITigaChannel
{
    /// <summary>
    /// register a synchronous method
    /// </summary>
    /// <param name="method">method name</param>
    /// <param name="func">method</param>
    void Register(string method, Func<object?, string> func);

    /// <summary>
    /// register an asynchronous method with no parameters
    /// </summary>
    /// <param name="method">method name</param>
    /// <param name="func">method</param>
    void RegisterAsync(string method, Func<Task> func);

    /// <summary>
    /// register an asynchronous method with no parameters
    /// </summary>
    /// <param name="method">method name</param>
    /// <param name="func">method</param>
    void RegisterAsync(string method, Func<Task<string>> func);

    /// <summary>
    /// register an asynchronous method
    /// </summary>
    /// <param name="method">method name</param>
    /// <param name="func">method</param>
    void RegisterAsync(string method, Func<object?, Task<string>> func);

    /// <summary>
    /// register an asynchronous method with cancellation support
    /// </summary>
    /// <param name="method">method name</param>
    /// <param name="func">method</param>
    void RegisterAsync(string method, Func<object?, CancellationToken, Task<string>> func);

    /// <summary>
    /// register an asynchronous method with cancellation support
    /// </summary>
    /// <param name="method">method name</param>
    /// <param name="func">method</param>
    void RegisterAsync<TIn>(string method, Func<TIn?, CancellationToken, Task<string>> func);

    /// <summary>
    /// register an asynchronous method with cancellation support
    /// </summary>
    /// <param name="method">method name</param>
    /// <param name="func">method</param>
    void RegisterAsync<TOut>(string method, Func<object?, CancellationToken, Task<TOut>> func);

    /// <summary>
    /// register an asynchronous method with cancellation support
    /// </summary>
    /// <param name="method">method name</param>
    /// <param name="func">method</param>
    void RegisterAsync<TIn, TOut>(string method, Func<TIn?, CancellationToken, Task<TOut>> func);

    /// <summary>
    /// register an asynchronous method with no parameters and a generic return type
    /// </summary>
    /// <typeparam name="T">return type</typeparam>
    /// <param name="method">method name</param>
    /// <param name="func">method</param>
    void RegisterAsync<T>(string method, Func<Task<T>> func);

    /// <summary>
    /// register an asynchronous method with a generic return type
    /// </summary>
    /// <typeparam name="T">return type</typeparam>
    /// <param name="method">method name</param>
    /// <param name="func">method</param>
    void RegisterAsync<T>(string method, Func<object?, Task<T>> func);
}
