using Newtonsoft.Json;
using TigaIpc.IO;

namespace TigaIpc.Messaging;

/// <summary>
/// Message bus class
/// </summary>
public partial class TigaMessageBus
{
    /// <summary>
    /// Publish information to the message bus in the background task as soon as possible
    /// </summary>
    /// <param name="message">Message to publish</param>
    /// <param name="cancellationToken">Cancel token</param>
    /// <returns>Async task</returns>
    public Task PublishAsync(string message, CancellationToken cancellationToken = default)
    {
#if NET7_0_OR_GREATER
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(message);
            ArgumentOutOfRangeException.ThrowIfZero(message.Length);
#else
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TigaMessageBus));
        }

        if (message is null)
        {
            throw new ArgumentNullException(nameof(message), "Message can not be empty");
        }

        if (message.Length == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(message), "Message can not be empty");
        }
#endif
        var messager = new PublisherMessage
            { Id = Guid.NewGuid().ToString(), Protocol = EventProtocol.Publisher, Data = message };
        var binary = BinaryDataExtensions.FromObjectAsMessagePack(messager,
            compress: _options.Value.EnableCompression, compressionThreshold: _options.Value.CompressionThreshold);
        return PublishAsync(binary, cancellationToken);
    }

    /// <summary>
    /// Publish information to the message bus in the background task as soon as possible
    /// </summary>
    /// <param name="message"></param>
    public Task PublishAsync(BinaryData message, CancellationToken cancellationToken = default)
    {
#if NET7_0_OR_GREATER
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(message);
            ArgumentOutOfRangeException.ThrowIfZero(message.Length);
#else
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TigaMessageBus));
        }

        if (message is null)
        {
            throw new ArgumentNullException(nameof(message), "Message can not be empty");
        }

        if (message.Length == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(message), "Message can not be empty");
        }
#endif

        return PublishAsync(new[] { message }, cancellationToken);
    }

    public async Task PublishAsync(IReadOnlyList<BinaryData> messages,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TigaMessageBus));
        }

        await _publishMessageSemaphore.WaitAsync(cancellationToken);
        try
        {
            if (messages is null)
            {
                throw new ArgumentNullException(nameof(messages), "Message list can not be null");
            }

            if (messages.Count == 0)
            {
                return;
            }

            await Task.Run(
                async () =>
                {
                    try
                    {
                        var timeoutTask = Task.Delay(_options?.Value?.WaitTimeout ?? TimeSpan.FromSeconds(5),
                            cancellationToken);
                        var completedTask = await Task.WhenAny(_receiverTaskCompletionSource.Task, timeoutTask)
                            .ConfigureAwait(false);
                        if (completedTask == timeoutTask)
                        {
                            PrintWarn("Timed out waiting for receiver task to be ready");
                            return;
                        }

                        if (_logger is not null)
                        {
                            var totalSize = messages.Sum(m => m.Length);
                            var messageCount = messages.Count;
                            Println($"Publishing {messageCount} messages with total size {totalSize} bytes");

                            foreach (var message in messages)
                            {
                                LogPublishingMessage(_logger, message.Length, message.MediaType);
                            }
                        }
                        var publishQueue = new Queue<BinaryData>(messages);
                        var initialCount = publishQueue.Count;
                        var retryCount = 0;
                        var maxRetries = _options?.Value?.MaxPublishRetries ?? 3;

                        while (publishQueue.Count > 0 && !cancellationToken.IsCancellationRequested)
                        {
                            try
                            {
                                _writeFile.ReadWrite((readStream, writeStream) =>
                                {
                                    var publishCount = PublishMessages(readStream, writeStream, publishQueue,
                                        TimeSpan.FromMilliseconds(100));
                                    if (publishCount > 0)
                                    {
                                        Interlocked.Add(ref _messagesPublished, publishCount);
                                        retryCount = 0;
                                    }
                                    else if (publishQueue.Count > 0)
                                    {
                                        retryCount++;
                                    }
                                }, cancellationToken);

                                if (publishQueue.Count > 0)
                                {
                                    var delayMs = Math.Min(50 * Math.Pow(2, retryCount), 1000);
                                    await Task.Delay((int)delayMs, cancellationToken).ConfigureAwait(false);

                                    if (retryCount >= maxRetries)
                                    {
                                        PrintWarn(
                                            $"Failed to publish all messages after {retryCount} retries. {publishQueue.Count} messages remaining.");
                                        break;
                                    }
                                }
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                break;
                            }
                            catch (Exception ex) when (!_disposed)
                            {
                                PrintFailed(ex,
                                    $"Error publishing messages, {publishQueue.Count} messages remaining");
                                retryCount++;

                                if (retryCount >= maxRetries)
                                {
                                    PrintWarn($"Aborting publish operation after {retryCount} retries");
                                    break;
                                }

                                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
                            }
                        }

                        var publishedCount = initialCount - publishQueue.Count;
                        if (publishedCount < initialCount)
                        {
                            PrintWarn($"Published {publishedCount} of {initialCount} messages");
                        }
                        else
                        {
                            Println($"Successfully published all {initialCount} messages");
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                    }
                    catch (Exception ex) when (!_disposed)
                    {
                        PrintFailed(ex, "Unexpected error in PublishAsync");
                        throw;
                    }
                }, cancellationToken);
        }
        catch (Exception e)
        {
            PrintFailed(e, "Error in PublishAsync");
            throw;
        }
        finally
        {
            _publishMessageSemaphore.Release();
        }
    }

    /// <summary>
    /// publish a message and wait for a response
    /// </summary>
    /// <param name="message">message to publish</param>
    /// <param name="messageId">message ID, for associating requests and responses</param>
    /// <param name="timeout">timeout</param>
    /// <param name="cancellationToken">cancellation token</param>
    /// <returns>response message</returns>
    /// <exception cref="TimeoutException">timeout waiting for response</exception>
    /// <exception cref="OperationCanceledException">operation was canceled</exception>
    /// <exception cref="ObjectDisposedException">object was disposed</exception>
    public async Task<ResponseMessage> PublishAsync(MessageBase message, string messageId,
        TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
#if NET7_0_OR_GREATER
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(message);
            ArgumentNullException.ThrowIfNull(messageId);
#else
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TigaMessageBus));
        }

        if (message == null)
        {
            throw new ArgumentNullException(nameof(message));
        }

        if (string.IsNullOrEmpty(messageId))
        {
            throw new ArgumentNullException(nameof(messageId));
        }
#endif
        // set message ID
        message.Id = messageId;

        // create TaskCompletionSource and timeout handling
        var tcs = new TaskCompletionSource<ResponseMessage>();
        var actualTimeout = timeout ?? _options.Value.InvokeTimeout;

        // create CancellationTokenSource for timeout and user cancellation
        using var timeoutCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        // set timeout
        var timeoutTask = Task.Delay(actualTimeout, linkedCts.Token);

        // register timeout handling
        var timeoutRegistration = timeoutCts.Token.Register(() =>
        {
            if (_pendingResponses.TryRemove(messageId, out _))
            {
                var timeoutException = new TimeoutException(
                    $"Waiting for response timeout for message ID '{messageId}', waited {actualTimeout.TotalSeconds} seconds");
                tcs.TrySetException(timeoutException);
                PrintWarn(
                    $"Request timed out for message ID {messageId} after {actualTimeout.TotalSeconds} seconds");
            }
        });

        try
        {
            // add TaskCompletionSource to dictionary
            _pendingResponses[messageId] = tcs;

            // serialize message
            var messageBinary = BinaryDataExtensions.FromObjectAsMessagePack(
                message,
                compress: _options.Value.EnableCompression,
                compressionThreshold: _options.Value.CompressionThreshold);

            // publish message
            await PublishAsync(messageBinary, linkedCts.Token).ConfigureAwait(false);
            Println($"Successfully published message with ID {messageId}");

            // wait for response or timeout
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask).ConfigureAwait(false);

            if (completedTask == timeoutTask && !tcs.Task.IsCompleted)
            {
                // timeout occurred
                if (timeoutTask.IsCanceled)
                {
                    // operation was canceled
                    throw new OperationCanceledException("Operation was canceled", cancellationToken);
                }
                else
                {
                    // timeout
                    throw new TimeoutException(
                        $"Waiting for response timeout for message ID '{messageId}', waited {actualTimeout.TotalSeconds} seconds");
                }
            }

            // return result
            return await tcs.Task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not TimeoutException && ex is not OperationCanceledException)
        {
            // clear TaskCompletionSource
            _pendingResponses.TryRemove(messageId, out _);

            PrintFailed(ex, $"[PublishAndWaitResponseAsync] Error publishing message with ID {messageId}");
            // wrap exception to provide more context
            throw new InvalidOperationException($"Error publishing message with ID '{messageId}': {ex.Message}",
                ex);
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
                /* ignore cancellation exception */
            }
        }
    }

    /// <summary>
    /// send an asynchronous event and wait for a return value
    /// </summary>
    /// <typeparam name="T">return type</typeparam>
    /// <param name="method">method name</param>
    /// <param name="data">data</param>
    /// <param name="timeout">timeout</param>
    /// <param name="cancellationToken">cancellation token</param>
    /// <returns>return value</returns>
    public async Task<T?> PublishAsync<T>(string method, string data, TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
#if NET7_0_OR_GREATER
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(method);
            ArgumentNullException.ThrowIfNull(data);
#else
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(TigaMessageBus));
        }

        if (method == null)
        {
            throw new ArgumentNullException(nameof(method));
        }

        if (data == null)
        {
            throw new ArgumentNullException(nameof(data));
        }
#endif

        // generate a unique request ID
        var requestId = Guid.NewGuid().ToString();
        Println($"Starting publish event for method {method} with request ID {requestId}");

        // create request message
        var requestMessage = new InvokeMessage
        {
            Id = requestId,
            Protocol = EventProtocol.Invoke,
            Method = method,
            Data = data,
        };

        // send message and wait for response
        var responseMessage =
            await PublishAsync(requestMessage, requestId, timeout, cancellationToken);

        // check response status
        if (responseMessage.Code != ResponseCode.Successful)
        {
            throw new InvalidOperationException(
                $"Error response received for method '{method}': {responseMessage.Data}");
        }

        // parse response data
        try
        {
            return JsonConvert.DeserializeObject<T>(responseMessage.Data, new JsonSerializerSettings
            {
                Error = (sender, args) =>
                {
                    Println($"Error deserializing response: {args.ErrorContext.Error.Message}");
                    args.ErrorContext.Handled = true;
                },
                FloatParseHandling = FloatParseHandling.Double,
                Converters = new List<JsonConverter>
                {
                    new Newtonsoft.Json.Converters.StringEnumConverter()
                }
            });
        }
        catch (JsonException ex)
        {
            PrintFailed(ex, "JSON deserialization error in PublishAsync<T> for method {Method}", method);
            return default;
        }
    }
}
