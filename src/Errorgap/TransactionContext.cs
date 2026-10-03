using System;
using System.Threading;

namespace Errorgap;

/// <summary>
/// The APM transaction the current async flow is running in, so errors
/// reported during it carry its id as <c>context.transaction_id</c> and
/// errorgap links each error to the request or job that raised it.
/// </summary>
/// <example>
/// <code>
/// var transaction = new ApmTransaction { Method = "GET", Path = "/orders/{id}" };
/// using (TransactionContext.Enter(transaction.Id))
/// {
///     await HandleAsync(request); // errors reported here carry transaction.Id
/// }
/// client.NotifyTransaction(transaction);
/// </code>
/// </example>
/// <remarks>
/// Backed by <see cref="AsyncLocal{T}"/>: the id flows across awaits within the
/// request and never leaks into a concurrent one.
/// </remarks>
public static class TransactionContext
{
    private static readonly AsyncLocal<string?> CurrentId = new();

    /// <summary>The id of the transaction running now, or null.</summary>
    public static string? Current => CurrentId.Value;

    /// <summary>Make <paramref name="id"/> current until the returned scope is disposed.</summary>
    public static IDisposable Enter(string id)
    {
        var previous = CurrentId.Value;
        CurrentId.Value = id;
        return new Scope(previous);
    }

    private sealed class Scope : IDisposable
    {
        private readonly string? _previous;
        private bool _disposed;

        public Scope(string? previous) => _previous = previous;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            CurrentId.Value = _previous;
        }
    }
}
