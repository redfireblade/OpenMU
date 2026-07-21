// <copyright file="SimpleLogger.cs" company="MUnique">
// Licensed under the MIT License. See LICENSE file in the project root for full license information.
// </copyright>

namespace MUnique.OpenMU.BotClient;

using System;
using Microsoft.Extensions.Logging;

/// <summary>
/// 简易日志工厂。
/// </summary>
internal sealed class SimpleLoggerFactory : ILoggerFactory
{
    private readonly SimpleLogger _logger = new();

    /// <inheritdoc />
    public void AddProvider(ILoggerProvider provider)
    {
    }

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) => this._logger;

    /// <inheritdoc />
    public void Dispose()
    {
    }
}

/// <summary>
/// 简易日志 — 输出到控制台。
/// </summary>
internal sealed class SimpleLogger : ILogger
{
    private static readonly object Lock = new();

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (!this.IsEnabled(logLevel))
        {
            return;
        }

        var prefix = logLevel switch
        {
            LogLevel.Error => "!",
            LogLevel.Warning => "?",
            LogLevel.Information => " ",
            LogLevel.Debug => ".",
            _ => " ",
        };

        lock (Lock)
        {
            Console.WriteLine($"{prefix} [{DateTime.Now:HH:mm:ss}] {formatter(state, exception)}");
        }
    }

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Trace;

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => null;
}
