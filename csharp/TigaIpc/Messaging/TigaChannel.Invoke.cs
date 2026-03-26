using Newtonsoft.Json;
using TigaIpc.IO;

namespace TigaIpc.Messaging;

/// <summary>
/// Channel class
/// </summary>
public partial class TigaChannel
{
    /// <inheritdoc/>
    public async Task<string> InvokeAsync(string method, object? data, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
#if NET7_0_OR_GREATER
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(method);
            ArgumentNullException.ThrowIfNull(data);
#else
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TigaChannel));
        }

        if (method == null)
        {
            throw new ArgumentNullException(nameof(method));
        }

#endif

        var response = await InternalInvokeAsync(method, data, timeout, cancellationToken);
        return response.Data;
    }

    /// <inheritdoc/>
    public async Task<T?> InvokeAsync<T>(string method, object? data, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
#if NET7_0_OR_GREATER
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(method);
            ArgumentNullException.ThrowIfNull(data);
#else
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TigaChannel));
        }

        if (method == null)
        {
            throw new ArgumentNullException(nameof(method));
        }

#endif

        var response = await InvokeAsync(method, data, timeout, cancellationToken);

        try
        {
            // try to deserialize the response
            return JsonConvert.DeserializeObject<T>(response, new JsonSerializerSettings
            {
                // add error handling settings
                Error = (sender, args) =>
                {
                    Println($"Error deserializing response: {args.ErrorContext.Error.Message}");
                    args.ErrorContext.Handled = true;
                },

                // allow handling special floating point values
                FloatParseHandling = FloatParseHandling.Double,

                // add type converters to handle special cases
                Converters = new List<JsonConverter>
                {
                    new Newtonsoft.Json.Converters.StringEnumConverter(),
                },
            });
        }
        catch (JsonException ex)
        {
            // record detailed error information
            PrintFailed(ex, "JSON deserialization error for method {Method}: Response was: {Response}", method, response);

            // throw a more meaningful exception
            throw new InvalidOperationException(
                $"Failed to deserialize response from method '{method}'. Response: {response}", ex);
        }
    }

    /// <summary>
    /// Invoke a method on the channel with no parameters
    /// </summary>
    /// <param name="method">method name</param>
    /// <param name="timeout">timeout</param>
    /// <param name="cancellationToken">cancellation token</param>
    /// <returns>task</returns>
    public async Task InvokeAsync(string method, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        await InvokeAsync(method, string.Empty, timeout, cancellationToken);
    }

    /// <summary>
    /// Invoke a method on the channel with no parameters and get a typed result
    /// </summary>
    /// <typeparam name="T">return type</typeparam>
    /// <param name="method">method name</param>
    /// <param name="timeout">timeout</param>
    /// <param name="cancellationToken">cancellation token</param>
    /// <returns>result of type T</returns>
    public async Task<T?> InvokeAsync<T>(string method, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        return await InvokeAsync<T>(method, null, timeout, cancellationToken);
    }

    /// <summary>
    /// internal invoke method
    /// </summary>
    /// <param name="method">method name</param>
    /// <param name="data">data</param>
    /// <param name="timeout">timeout</param>
    /// <param name="cancellationToken">cancellation token</param>
    private async Task<ResponseMessage> InternalInvokeAsync(string method, object? data, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
#if NET7_0_OR_GREATER
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(method);
            ArgumentNullException.ThrowIfNull(data);
#else
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TigaChannel));
        }

        if (method == null)
        {
            throw new ArgumentNullException(nameof(method));
        }
#endif

        // generate unique request ID
        var requestId = Guid.NewGuid().ToString();
        Println($"Starting invoke for method {method} with request ID {requestId}");
        var actualTimeout = timeout ?? _options.Value.InvokeTimeout;
        var responseTaskSource = new TaskCompletionSource<ResponseMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeoutCts = new CancellationTokenSource(actualTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, linkedCts.Token);

        try
        {
            var requestMessage = new InvokeMessage
            {
                Id = requestId,
                Protocol = EventProtocol.Invoke,
                Method = method,
                Data = data,
            };

            if (!_pendingResponses.TryAdd(requestId, responseTaskSource))
            {
                throw new InvalidOperationException(
                    $"A pending response with ID '{requestId}' is already registered.");
            }

            var requestBinary = BinaryDataExtensions.FromObjectAsMessagePack(
                requestMessage,
                compress: _options.Value.EnableCompression,
                compressionThreshold: _options.Value.CompressionThreshold);

            await PublishInvokeMessageAsync(requestBinary, linkedCts.Token).ConfigureAwait(false);
            Println($"Successfully published invoke request for method {method} with request ID {requestId}");

            var completedTask = await Task.WhenAny(responseTaskSource.Task, timeoutTask).ConfigureAwait(false);
            if (completedTask != responseTaskSource.Task)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("Operation was canceled", cancellationToken);
                }

                throw new TimeoutException(
                    $"Waiting for response timeout for method '{method}', waited {actualTimeout.TotalSeconds} seconds");
            }

            var responseMessage = await responseTaskSource.Task
                .ConfigureAwait(false);

            if (responseMessage.Code != ResponseCode.Successful)
            {
                throw new InvalidOperationException(
                    $"Error response received for method '{method}': {responseMessage.Data}");
            }

            return responseMessage;
        }
        catch (Exception ex) when (ex is not TimeoutException && ex is not OperationCanceledException)
        {
            _pendingResponses.TryRemove(requestId, out _);
            PrintFailed(ex, $"[InvokeAsync] Error invoking method {method} with request ID {requestId}");

            throw new InvalidOperationException($"Error invoking method '{method}': {ex.Message}", ex);
        }
        finally
        {
            _pendingResponses.TryRemove(requestId, out _);

            try
            {
                timeoutCts.Cancel();
            }
            catch
            {
                // Ignore timeout CTS cancellation during cleanup.
            }
        }
    }
}
