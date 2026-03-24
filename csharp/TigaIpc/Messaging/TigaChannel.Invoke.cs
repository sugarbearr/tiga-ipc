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

        // parse the response
        var responseData = response.ToObjectFromMessagePack<ResponseMessage>();
        if (responseData == null)
        {
            throw new InvalidOperationException(
                $"Invalid response received for method '{method}', response data is null");
        }

        return responseData.Data;
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
    private async Task<BinaryData> InternalInvokeAsync(string method, object? data, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
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

        // create task completion source and timeout handler
        var tcs = new TaskCompletionSource<BinaryData>();
        var actualTimeout = timeout ?? _options.Value.InvokeTimeout;

        // create cancellation token source for timeout and user cancellation
        using var timeoutCts = new CancellationTokenSource();
        timeoutCts.CancelAfter(actualTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        // set timeout
        var timeoutTask = Task.Delay(Timeout.InfiniteTimeSpan, linkedCts.Token);

        // create response handler
        EventHandler<MessageResponseEventArgs>? handler = null;
        handler = (sender, e) =>
        {
            try
            {
                // try to parse message as response message
                if (!e.Message.TryToObject<ResponseMessage>(out var response))
                {
                    Println(
                        $"Received message is not a valid response format for request ID {requestId}");
                    return; // not the expected response format, ignore
                }

                // check if response ID matches
                if (response?.Id != requestId)
                {
                    Println(
                        $"Received response ID {response?.Id} does not match request ID {requestId}");
                    return; // not the expected response ID, ignore
                }

                // unsubscribe event and set result
                MessageResponse -= handler;
                tcs.TrySetResult(e.Message);

                // cancel timeout task
                try
                {
                    // Check if the CancellationTokenSource is not disposed before calling Cancel
                    if (timeoutCts is { IsCancellationRequested: false, Token.CanBeCanceled: true })
                    {
                        timeoutCts.Cancel();
                    }
                }
                catch (Exception ex)
                {
                    PrintFailed(ex, "Error canceling timeout");
                }

                Println($"Successfully received response for method {method} with request ID {requestId}");
            }
            catch (Exception ex)
            {
                // handle exception when processing response
                MessageResponse -= handler;
                tcs.TrySetException(ex);
                PrintFailed(
                    ex,
                    $"[InvokeAsync] Error processing response for method {method} with request ID {requestId}");
            }
        };

        // register timeout handler
        var timeoutRegistration = timeoutCts.Token.Register(() =>
        {
            if (handler != null)
            {
                MessageResponse -= handler;
                var timeoutException = new TimeoutException(
                    $"Waiting for response timeout for method '{method}', waited {actualTimeout.TotalSeconds} seconds");
                tcs.TrySetException(timeoutException);
                PrintWarn(
                    $"Request timed out for method {method} with request ID {requestId} after {actualTimeout.TotalSeconds} seconds");
            }
        });

        // subscribe to response event
        MessageResponse += handler;

        try
        {
            // create request message
            var requestMessage = new InvokeMessage
            {
                Id = requestId,
                Protocol = EventProtocol.Invoke,
                Method = method,
                Data = data,
            };

            // use compression to reduce data transfer, according to configuration decide whether to compress
            var requestBinary = BinaryDataExtensions.FromObjectAsMessagePack(
                requestMessage,
                compress: _options.Value.EnableCompression,
                compressionThreshold: _options.Value.CompressionThreshold);

            // publish request directly, avoid using normal publish queue
            await PublishInvokeMessageAsync(requestBinary, linkedCts.Token).ConfigureAwait(false);
            Println($"Successfully published invoke request for method {method} with request ID {requestId}");

            // wait for response or timeout
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);

            if (completedTask == timeoutTask && !tcs.Task.IsCompleted)
            {
                // timeout occurred
                if (cancellationToken.IsCancellationRequested)
                {
                    // operation was canceled
                    throw new OperationCanceledException("Operation was canceled", cancellationToken);
                }
                else
                {
                    // timeout
                    throw new TimeoutException(
                        $"Waiting for response timeout for method '{method}', waited {actualTimeout.TotalSeconds} seconds");
                }
            }

            // return result
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not TimeoutException && ex is not OperationCanceledException)
        {
            // clear event handler
            if (handler != null)
            {
                MessageResponse -= handler;
            }

            PrintFailed(ex, $"[InvokeAsync] Error invoking method {method} with request ID {requestId}");

            // wrap exception to provide more context
            throw new InvalidOperationException($"Error invoking method '{method}': {ex.Message}", ex);
        }
        finally
        {
            // dispose resources
            timeoutRegistration.Dispose();

            try
            {
                timeoutCts.Cancel();
            }
            catch
            {
                /* ignore cancel exception */
            }
        }
    }
}
