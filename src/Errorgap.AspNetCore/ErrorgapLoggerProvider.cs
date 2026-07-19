using System;
using Errorgap;
using Microsoft.Extensions.Logging;

namespace Errorgap.AspNetCore;

public sealed class ErrorgapLoggerProvider : ILoggerProvider
{
    private readonly ErrorgapClient _client;
    private readonly ErrorgapConfiguration _configuration;
    private readonly LogLevel _minimum;

    public ErrorgapLoggerProvider(ErrorgapClient client, ErrorgapConfiguration configuration)
    {
        _client = client;
        _configuration = configuration;
        _minimum = Enum.TryParse<LogLevel>(configuration.MinimumLogLevel, true, out var parsed)
            ? parsed
            : LogLevel.Warning;
    }

    public ILogger CreateLogger(string categoryName) => new ErrorgapLogger(this, categoryName);
    public void Dispose() { }

    private sealed class ErrorgapLogger : ILogger
    {
        private readonly ErrorgapLoggerProvider _provider;
        private readonly string _category;

        public ErrorgapLogger(ErrorgapLoggerProvider provider, string category)
        {
            _provider = provider;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
            => _provider._configuration.LogsEnabled
               && logLevel != LogLevel.None
               && logLevel >= _provider._minimum;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var message = formatter(state, exception);
            if (string.IsNullOrWhiteSpace(message)) return;
            _provider._client.NotifyLog(message, logLevel.ToString(), _category);
        }
    }
}
