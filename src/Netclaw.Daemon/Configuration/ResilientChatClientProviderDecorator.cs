// -----------------------------------------------------------------------
// <copyright file="ResilientChatClientProviderDecorator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Wraps an <see cref="IChatClientProvider"/> and decorates each client with
/// logging and retry. For the <see cref="ModelRole.Main"/> role, additionally
/// wraps in <see cref="FailoverChatClient"/> if a distinct fallback is configured,
/// or <see cref="AlertingChatClientDecorator"/> for single-provider setups.
/// </summary>
/// <remarks>
/// Decoration is deferred to first <see cref="GetClient"/> call per role.
/// A broken provider config no longer crashes the daemon at startup — the
/// daemon stays up so first-run setup, doctor, and non-LLM surfaces keep
/// working, and only sessions that actually request the broken role error
/// out.
/// </remarks>
public sealed class ResilientChatClientProviderDecorator : IChatClientProvider
{
    private readonly Lazy<IChatClient> _main;
    private readonly Lazy<IChatClient> _compaction;

    public ResilientChatClientProviderDecorator(
        IChatClientProvider inner,
        RetryPolicy retryPolicy,
        ModelSelection models,
        ILoggerFactory loggerFactory,
        IOperationalNotificationSink notificationSink,
        TimeProvider? timeProvider = null)
    {
        var tp = timeProvider ?? TimeProvider.System;
        var retryLogger = loggerFactory.CreateLogger<RetryingChatClient>();
        var loggingLogger = loggerFactory.CreateLogger<LoggingChatClient>();
        var failoverLogger = loggerFactory.CreateLogger<FailoverChatClient>();

        _main = new Lazy<IChatClient>(
            () => BuildMain(inner, retryPolicy, models, notificationSink, tp,
                retryLogger, loggingLogger, failoverLogger),
            LazyThreadSafetyMode.ExecutionAndPublication);

        _compaction = new Lazy<IChatClient>(
            () => BuildCompaction(inner, retryPolicy, tp, retryLogger, loggingLogger),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IChatClient GetClient(ModelRole role) => role switch
    {
        ModelRole.Compaction => _compaction.Value,
        _ => _main.Value
    };

    private static IChatClient BuildMain(
        IChatClientProvider inner,
        RetryPolicy retryPolicy,
        ModelSelection models,
        IOperationalNotificationSink notificationSink,
        TimeProvider tp,
        ILogger retryLogger,
        ILogger loggingLogger,
        ILogger<FailoverChatClient> failoverLogger)
    {
        var rawMain = inner.GetClient(ModelRole.Main);
        var decoratedMain = Decorate(rawMain, retryPolicy, retryLogger, loggingLogger, tp);

        if (models.Fallback is not null)
        {
            var rawFallback = inner.GetClient(ModelRole.Fallback);
            if (!ReferenceEquals(rawFallback, rawMain))
            {
                var decoratedFallback = Decorate(rawFallback, retryPolicy, retryLogger, loggingLogger, tp);
                return new FailoverChatClient(
                    decoratedMain, decoratedFallback, failoverLogger, notificationSink, tp);
            }
        }

        return new AlertingChatClientDecorator(decoratedMain, notificationSink, tp);
    }

    private IChatClient BuildCompaction(
        IChatClientProvider inner,
        RetryPolicy retryPolicy,
        TimeProvider tp,
        ILogger retryLogger,
        ILogger loggingLogger)
    {
        // If the inner provider returns the same instance for Compaction and
        // Main (no distinct Compaction model configured), reuse the decorated
        // Main chain so logging/retry/alerting wrappers aren't duplicated.
        var rawMain = inner.GetClient(ModelRole.Main);
        var rawCompaction = inner.GetClient(ModelRole.Compaction);
        return ReferenceEquals(rawCompaction, rawMain)
            ? _main.Value
            : Decorate(rawCompaction, retryPolicy, retryLogger, loggingLogger, tp);
    }

    private static IChatClient Decorate(
        IChatClient raw,
        RetryPolicy policy,
        ILogger retryLogger,
        ILogger loggingLogger,
        TimeProvider tp)
    {
        // Inner → Retry → Logging (logging is outermost so it captures retry time)
        var retrying = new RetryingChatClient(raw, policy, retryLogger, tp);
        return new LoggingChatClient(retrying, loggingLogger, tp);
    }
}
