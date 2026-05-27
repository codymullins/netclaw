// -----------------------------------------------------------------------
// <copyright file="DaemonProviderServiceExtensions.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Netclaw.Configuration;
using Netclaw.Providers;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Daemon-level provider wiring: plugin factory, retry, resilient decorator.
/// Chains on top of <see cref="LlmProviderServiceExtensions.AddLlmProviders"/>.
/// </summary>
public static class DaemonProviderServiceExtensions
{
    /// <summary>
    /// Registers provider plugins (via Netclaw.Providers) plus daemon-specific
    /// factory, retry, and resilient chat client provider.
    /// </summary>
    public static IServiceCollection AddDaemonLlmProviders(
        this IServiceCollection services,
        Dictionary<string, ProviderEntry> providers,
        ModelSelection models)
    {
        // Register plugins and OAuth from Netclaw.Providers
        services.AddLlmProviders();

        // Expose the resolved provider map so other services (status, doctor,
        // diagnostics) can introspect it without re-parsing the config section.
        services.AddSingleton(providers);

        // Cross-reference Models.* against the configured providers at host
        // start, converting "provider name not in dict" / "unknown provider
        // Type" misconfigs into a structured OptionsValidationException
        // instead of a deep DI activation crash later. See
        // ProviderReferenceValidator for the rules.
        services.AddSingleton<IValidateOptions<ModelSelection>>(sp =>
            new ProviderReferenceValidator(providers, sp.GetServices<ILlmProviderPlugin>()));

        // Register the plugin factory and chat client provider
        services.AddSingleton(sp =>
            new ProviderPluginFactory(providers, sp.GetServices<ILlmProviderPlugin>()));

        // Retry policy (TODO: make configurable via netclaw.json Resilience section)
        services.AddSingleton(new RetryPolicy());

        // Raw provider → Resilient decorator (Logging → Retry → Failover → Alerting)
        services.AddSingleton<IChatClientProvider>(sp =>
        {
            var raw = new NetclawChatClientProvider(
                sp.GetRequiredService<ProviderPluginFactory>(), models);
            return new ResilientChatClientProviderDecorator(
                raw,
                sp.GetRequiredService<RetryPolicy>(),
                models,
                sp.GetRequiredService<ILoggerFactory>(),
                sp.GetRequiredService<IOperationalNotificationSink>(),
                sp.GetService<TimeProvider>());
        });

        return services;
    }
}
