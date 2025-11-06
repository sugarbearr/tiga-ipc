using Newtonsoft.Json;
using TigaIpc.IO;

namespace TigaIpc.Messaging;

/// <summary>
/// message bus class
/// </summary>
public partial class TigaMessageBus
{
    /// <inheritdoc/>
    public void Register(string method, Func<object?, string> func)
    {
        ValidateParameters(method, func);
        _eventAggregator.AddOrUpdate(method, func, (_, _) => func);
    }

    #region RegisterAsync Methods

    /// <summary>
    /// Register an asynchronous method handler with no parameters
    /// </summary>
    /// <param name="method">Method name</param>
    /// <param name="func">Asynchronous function to execute when the method is called</param>
    /// <exception cref="ArgumentNullException">Thrown when method or func is null</exception>
    public void RegisterAsync(string method, Func<Task> func)
    {
        ValidateParameters(method, func);

        // Store the function for later reference
        _asyncTaskEventAggregator[method] = func;

        // Register a handler that executes the async function
        Register(method, _ => ExecuteAsyncAction(() => func(), method));
    }

    /// <summary>
    /// Register an asynchronous method handler with no parameters
    /// </summary>
    /// <param name="method">Method name</param>
    /// <param name="func">Asynchronous function to execute when the method is called</param>
    /// <exception cref="ArgumentNullException">Thrown when method or func is null</exception>
    public void RegisterAsync(string method, Func<Task<string>> func)
    {
        ValidateParameters(method, func);

        // Create an adapter that ignores the input parameter
        Func<object?, Task<string>> adapter = _ => func();
        _asyncEventAggregator.AddOrUpdate(method, adapter, (_, _) => adapter);

        // Register a handler that executes the async function
        Register(method, _ => ExecuteAsync(func, method));
    }

    /// <summary>
    /// Register an asynchronous method handler
    /// </summary>
    /// <param name="method">Method name</param>
    /// <param name="func">Asynchronous handler</param>
    /// <exception cref="ArgumentNullException">Thrown when method or func is null</exception>
    public void RegisterAsync(string method, Func<object?, Task<string>> func)
    {
        ValidateParameters(method, func);

        // Store the function in the event aggregator
        _asyncEventAggregator.AddOrUpdate(method, func, (_, _) => func);

        // Register a handler that executes the async function with the provided data
        Register(method, data => ExecuteAsync(() => func(data), method));
    }

    /// <summary>
    /// Register an asynchronous method handler with cancellation support
    /// </summary>
    /// <param name="method">Method name</param>
    /// <param name="func">Asynchronous handler</param>
    public void RegisterAsync<TIn>(string method, Func<TIn?, CancellationToken, Task<string>> func)
    {
        ValidateParameters(method, func);

        // Create adapter for the event aggregator
        Func<object?, Task<string>> adapter = data => func(ConvertInput<TIn>(data), CancellationToken.None);
        _asyncEventAggregator.AddOrUpdate(method, adapter, (_, _) => adapter);

        // Register handler
        Register(method, data => ExecuteWithInputConversion(data, func, method));
    }

    /// <summary>
    /// Register an asynchronous method handler with cancellation support
    /// </summary>
    /// <param name="method">Method name</param>
    /// <param name="func">Asynchronous handler</param>
    public void RegisterAsync(string method, Func<object?, CancellationToken, Task<string>> func)
    {
        ValidateParameters(method, func);

        // Create an adapter that passes CancellationToken.None
        Func<object?, Task<string>> adapter = data => func(data, CancellationToken.None);
        _asyncEventAggregator.AddOrUpdate(method, adapter, (_, _) => adapter);

        // Register a handler that executes the async function with the provided data
        Register(method, data => ExecuteAsync(() => func(data, CancellationToken.None), method));
    }

    /// <summary>
    /// Register an asynchronous method handler with cancellation support
    /// </summary>
    /// <typeparam name="TIn">Input type</typeparam>
    /// <typeparam name="TOut">Return type</typeparam>
    /// <param name="method">Method name</param>
    /// <param name="func">Asynchronous handler</param>
    public void RegisterAsync<TIn, TOut>(string method, Func<TIn?, CancellationToken, Task<TOut>> func)
    {
        ValidateParameters(method, func);

        _asyncGenericEventAggregator.AddOrUpdate(method, func, (_, _) => func);
        Register(method, data => ExecuteGenericWithInputConversion(data, func, method));
    }

    /// <summary>
    /// Register an asynchronous method handler with no input parameters and cancellation support
    /// </summary>
    /// <typeparam name="TOut">Return type</typeparam>
    /// <param name="method">Method name</param>
    /// <param name="func">Asynchronous handler</param>
    public void RegisterAsync<TOut>(string method, Func<object?, CancellationToken, Task<TOut>> func)
    {
        ValidateParameters(method, func);

        _asyncGenericEventAggregator.AddOrUpdate(method, func, (_, _) => func);
        Register(method, data => ExecuteAsyncGeneric(() => func(data, CancellationToken.None), method));
    }

    /// <summary>
    /// Register an asynchronous method handler with no parameters
    /// </summary>
    /// <typeparam name="T">Return type</typeparam>
    /// <param name="method">Method name</param>
    /// <param name="func">Asynchronous handler</param>
    public void RegisterAsync<T>(string method, Func<Task<T>> func)
    {
        ValidateParameters(method, func);

        _asyncGenericEventAggregator[method] = func;
        Register(method, _ => ExecuteAsyncGeneric(func, method));
    }

    /// <summary>
    /// Register a generic asynchronous method handler
    /// </summary>
    /// <typeparam name="T">Return type</typeparam>
    /// <param name="method">Method name</param>
    /// <param name="func">Asynchronous handler</param>
    public void RegisterAsync<T>(string method, Func<object?, Task<T>> func)
    {
        ValidateParameters(method, func);

        // Store the function in the event aggregator
        _asyncGenericEventAggregator.AddOrUpdate(method, func, (_, _) => func);

        // Register a handler that executes the async function with the provided data
        Register(method, data => ExecuteAsyncGeneric(() => func(data), method));
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Validates that method and func parameters are not null
    /// </summary>
    private static void ValidateParameters<T>(string method, T func) where T : class
    {
#if NET7_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(func);
#else
        if (method == null) throw new ArgumentNullException(nameof(method));
        if (func == null) throw new ArgumentNullException(nameof(func));
#endif
    }

    /// <summary>
    /// Converts input data to the specified type using MessagePack
    /// </summary>
    private static TIn? ConvertInput<TIn>(object? data)
    {
        if (data == null) return default;
        var binaryData = BinaryDataExtensions.FromObjectAsMessagePack(data);
        return binaryData.ToObjectFromMessagePack<TIn>();
    }

    /// <summary>
    /// Execute an asynchronous action and return an empty string on success
    /// </summary>
    /// <param name="action">The asynchronous action to execute</param>
    /// <param name="methodName">The name of the method being executed (for logging)</param>
    /// <returns>An empty string on successful execution</returns>
    private string ExecuteAsyncAction(Func<Task> action, string methodName)
    {
        try
        {
            JoinableTaskFactory.Run(action);
            return string.Empty;
        }
        catch (Exception ex)
        {
            PrintFailed(ex, "Error in void async handler for method {Method}", methodName);
            throw;
        }
    }

    /// <summary>
    /// Execute an asynchronous function and return its result
    /// </summary>
    /// <param name="func">The asynchronous function to execute</param>
    /// <param name="methodName">The name of the method being executed (for logging)</param>
    /// <returns>The result of the asynchronous function</returns>
    private string ExecuteAsync(Func<Task<string>> func, string methodName)
    {
        try
        {
            return JoinableTaskFactory.Run(func);
        }
        catch (Exception ex)
        {
            PrintFailed(ex, "Error in async handler for method {Method}", methodName);
            throw;
        }
    }

    /// <summary>
    /// Execute an asynchronous function with input conversion and return its result
    /// </summary>
    private string ExecuteWithInputConversion<TIn>(object? data, Func<TIn?, CancellationToken, Task<string>> func,
        string methodName)
    {
        try
        {
            var invokeData = ConvertInput<TIn>(data);
            return JoinableTaskFactory.Run(() => func(invokeData, CancellationToken.None));
        }
        catch (Exception ex)
        {
            PrintFailed(ex, "Error in cancellable async handler for method {Method}", methodName);
            throw;
        }
    }

    /// <summary>
    /// Execute an asynchronous generic function with input conversion and return its serialized result
    /// </summary>
    private string ExecuteGenericWithInputConversion<TIn, TOut>(object? data,
        Func<TIn?, CancellationToken, Task<TOut>> func, string methodName)
    {
        try
        {
            var invokeData = ConvertInput<TIn>(data);
            var result = JoinableTaskFactory.Run(() => func(invokeData, CancellationToken.None));
            return JsonConvert.SerializeObject(result);
        }
        catch (Exception ex)
        {
            PrintFailed(ex, "Error in cancellable async generic handler for method {Method}", methodName);
            throw;
        }
    }

    /// <summary>
    /// Execute an asynchronous function and serialize its result to JSON
    /// </summary>
    /// <typeparam name="T">The return type of the asynchronous function</typeparam>
    /// <param name="func">The asynchronous function to execute</param>
    /// <param name="methodName">The name of the method being executed (for logging)</param>
    /// <returns>The JSON serialized result of the asynchronous function</returns>
    private string ExecuteAsyncGeneric<T>(Func<Task<T>> func, string methodName)
    {
        try
        {
            var result = JoinableTaskFactory.Run(func);
            return JsonConvert.SerializeObject(result);
        }
        catch (Exception ex)
        {
            PrintFailed(ex, "Error in generic async handler for method {Method}", methodName);
            throw;
        }
    }

    #endregion
}