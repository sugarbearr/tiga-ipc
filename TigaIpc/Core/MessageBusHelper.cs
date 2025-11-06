using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace TigaIpc.Core;

internal static class MessageBusHelper
{
    private static readonly JsonSerializerSettings SerializerSettings = new()
    {
        FloatParseHandling = FloatParseHandling.Double,
        Converters = new List<JsonConverter>
        {
            new StringEnumConverter()
        }
    };

    public static async Task<T> ExecuteWithRetryAsync<T>(
        Func<Task<T>> action,
        string methodName,
        string? requestId = null,
        int maxRetries = 3,
        ILogger? logger = null)
    {
        var retryCount = 0;
        while (true)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (retryCount < maxRetries && IsRetryableException(ex))
            {
                retryCount++;
                var delay = TimeSpan.FromSeconds(Math.Pow(2, retryCount)); // 指数退避
                logger?.LogWarning(ex,
                    "Retry {RetryCount} for method {Method} with requestId {RequestId}. Waiting {Delay} seconds",
                    retryCount, methodName, requestId, delay.TotalSeconds);
                await Task.Delay(delay).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger?.LogError(ex,
                    "Error executing method {Method} with requestId {RequestId}",
                    methodName, requestId);
                throw new MessageBusException(
                    $"Error executing method '{methodName}'",
                    methodName,
                    requestId,
                    ex);
            }
        }
    }

    public static T? DeserializeResponse<T>(string response, string methodName)
    {
        try
        {
            return JsonConvert.DeserializeObject<T>(response, SerializerSettings);
        }
        catch (JsonException ex)
        {
            throw new MessageBusException(
                $"Failed to deserialize response from method '{methodName}'. Response: {response}",
                methodName,
                null,
                ex);
        }
    }

    private static bool IsRetryableException(Exception ex)
    {
        return ex switch
        {
            TimeoutException => true,
            SocketException => true,
            HttpRequestException => true,
            _ => false
        };
    }
}