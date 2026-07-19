using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.Reflection;

namespace Errorgap.AspNetCore;

internal sealed class ErrorgapDiagnostics :
    IObserver<DiagnosticListener>,
    IObserver<KeyValuePair<string, object?>>,
    IDisposable
{
    private readonly ErrorgapApm _apm;
    private readonly List<IDisposable> _subscriptions = new();
    private readonly IDisposable _allListeners;
    private readonly object _gate = new();

    public ErrorgapDiagnostics(ErrorgapApm apm)
    {
        _apm = apm;
        _allListeners = DiagnosticListener.AllListeners.Subscribe(this);
    }

    public void OnNext(DiagnosticListener listener)
    {
        if (listener.Name.Contains("EntityFrameworkCore", StringComparison.Ordinal)
            || listener.Name.Contains("SqlClient", StringComparison.Ordinal)
            || listener.Name.Equals("HttpHandlerDiagnosticListener", StringComparison.Ordinal))
        {
            lock (_gate) _subscriptions.Add(listener.Subscribe(this));
        }
    }

    public void OnNext(KeyValuePair<string, object?> diagnostic)
    {
        if (diagnostic.Key.EndsWith("CommandExecuted", StringComparison.Ordinal)
            || diagnostic.Key.EndsWith("CommandError", StringComparison.Ordinal))
        {
            var command = Property(diagnostic.Value, "Command") as DbCommand;
            var duration = Property(diagnostic.Value, "Duration") as TimeSpan?;
            if (command is not null && duration is not null)
            {
                _apm.RecordDatabase(command.CommandText, duration.Value.TotalMilliseconds);
            }
            return;
        }

        if (diagnostic.Key.Equals("System.Net.Http.HttpRequestOut.Stop", StringComparison.Ordinal))
        {
            _apm.RecordExternal(Activity.Current?.Duration.TotalMilliseconds ?? 0);
        }
    }

    public void OnCompleted() { }
    public void OnError(Exception error) { }

    public void Dispose()
    {
        _allListeners.Dispose();
        lock (_gate)
        {
            foreach (var subscription in _subscriptions) subscription.Dispose();
            _subscriptions.Clear();
        }
    }

    private static object? Property(object? instance, string name)
    {
        return instance?.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public)?.GetValue(instance);
    }
}
