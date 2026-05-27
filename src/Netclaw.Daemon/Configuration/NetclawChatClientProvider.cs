// -----------------------------------------------------------------------
// <copyright file="NetclawChatClientProvider.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Configuration;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Resolves <see cref="IChatClient"/> instances by <see cref="ModelRole"/>
/// using a <see cref="ProviderPluginFactory"/> and <see cref="ModelSelection"/>.
/// Clients are constructed lazily on the first <see cref="GetClient"/> call
/// per role and reused thereafter, so a misconfigured Fallback or Compaction
/// role does not sink daemon startup — only sessions that actually request
/// the broken role surface the error.
/// </summary>
public sealed class NetclawChatClientProvider : IChatClientProvider
{
    private readonly Lazy<IChatClient> _main;
    private readonly Lazy<IChatClient>? _fallback;
    private readonly Lazy<IChatClient>? _compaction;

    public NetclawChatClientProvider(ProviderPluginFactory factory, ModelSelection models)
    {
        _main = new Lazy<IChatClient>(
            () => factory.Create(models.Main),
            LazyThreadSafetyMode.ExecutionAndPublication);

        _fallback = models.Fallback is { } fallback
            ? new Lazy<IChatClient>(
                () => factory.Create(fallback),
                LazyThreadSafetyMode.ExecutionAndPublication)
            : null;

        _compaction = models.Compaction is { } compaction
            ? new Lazy<IChatClient>(
                () => factory.Create(compaction),
                LazyThreadSafetyMode.ExecutionAndPublication)
            : null;
    }

    public IChatClient GetClient(ModelRole role) => role switch
    {
        ModelRole.Fallback => (_fallback ?? _main).Value,
        ModelRole.Compaction => (_compaction ?? _main).Value,
        _ => _main.Value
    };
}
