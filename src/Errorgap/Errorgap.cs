using System;
using System.Threading;
using System.Threading.Tasks;

namespace Errorgap;

public static class ErrorgapSdk
{
    private static ErrorgapClient? _client;
    private static UnhandledExceptionEventHandler? _appDomainHandler;
    private static EventHandler<UnobservedTaskExceptionEventArgs>? _taskHandler;
    private static readonly object _gate = new();

    public static ErrorgapClient? Client => _client;
    public static ErrorgapConfiguration? Configuration => _client?.Config;

    public static void Init(ErrorgapConfiguration config, bool captureGlobals = true)
    {
        config.Validate();
        lock (_gate)
        {
            var previous = _client;
            _client = new ErrorgapClient(config);
            previous?.DisposeAsync().AsTask().ContinueWith(_ => { }, TaskScheduler.Default);

            if (captureGlobals) InstallGlobals();
            else UninstallGlobals();
        }
    }

    public static DeliveryResult Notify(Exception exception, NoticeOptions? options = null)
    {
        var client = _client;
        return client is null
            ? new DeliveryResult(null, null, new InvalidOperationException("Errorgap not initialized"), false)
            : client.Notify(exception, options);
    }

    public static Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var client = _client;
        return client is null ? Task.CompletedTask : client.FlushAsync(cancellationToken);
    }

    public static async Task ShutdownAsync()
    {
        ErrorgapClient? client;
        lock (_gate)
        {
            client = _client;
            _client = null;
            UninstallGlobals();
        }
        if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
    }

    private static void InstallGlobals()
    {
        if (_appDomainHandler is not null) return;

        _appDomainHandler = (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
            {
                Notify(ex, new NoticeOptions
                {
                    Context = new System.Collections.Generic.Dictionary<string, object?>
                    {
                        ["source"] = "AppDomain.UnhandledException",
                        ["is_terminating"] = args.IsTerminating,
                    },
                });
            }
        };
        AppDomain.CurrentDomain.UnhandledException += _appDomainHandler;

        _taskHandler = (_, args) =>
        {
            Notify(args.Exception, new NoticeOptions
            {
                Context = new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["source"] = "TaskScheduler.UnobservedTaskException",
                },
            });
            args.SetObserved();
        };
        TaskScheduler.UnobservedTaskException += _taskHandler;
    }

    private static void UninstallGlobals()
    {
        if (_appDomainHandler is not null)
        {
            AppDomain.CurrentDomain.UnhandledException -= _appDomainHandler;
            _appDomainHandler = null;
        }
        if (_taskHandler is not null)
        {
            TaskScheduler.UnobservedTaskException -= _taskHandler;
            _taskHandler = null;
        }
    }
}
