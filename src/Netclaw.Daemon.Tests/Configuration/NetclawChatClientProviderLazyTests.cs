// -----------------------------------------------------------------------
// <copyright file="NetclawChatClientProviderLazyTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Providers;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

/// <summary>
/// Verifies that <see cref="NetclawChatClientProvider"/> defers
/// <see cref="ProviderPluginFactory.Create"/> until a client for the
/// affected role is actually requested. The crash that motivated this
/// behavior was a daemon-wide DI activation failure when Models.Main
/// referenced a provider that was no longer present in Providers; with
/// lazy construction, that misconfig now surfaces only when sessions
/// actually try to use the broken role.
/// </summary>
public sealed class NetclawChatClientProviderLazyTests
{
    [Fact]
    public void Construction_DoesNotInvokeFactory()
    {
        var calls = new List<string>();
        var factory = BuildFactory(calls);
        var models = new ModelSelection
        {
            Main = new ModelReference { Provider = "good", ModelId = "m" },
            Fallback = new ModelReference { Provider = "good", ModelId = "f" },
            Compaction = new ModelReference { Provider = "good", ModelId = "c" }
        };

        _ = new NetclawChatClientProvider(factory, models);

        Assert.Empty(calls);
    }

    [Fact]
    public void GetClient_Main_BuildsOnlyMain()
    {
        var calls = new List<string>();
        var factory = BuildFactory(calls);
        var models = new ModelSelection
        {
            Main = new ModelReference { Provider = "good", ModelId = "m" },
            Fallback = new ModelReference { Provider = "good", ModelId = "f" },
            Compaction = new ModelReference { Provider = "good", ModelId = "c" }
        };
        var provider = new NetclawChatClientProvider(factory, models);

        _ = provider.GetClient(ModelRole.Main);

        Assert.Equal(["m"], calls);
    }

    [Fact]
    public void BrokenFallback_DoesNotAffectMain()
    {
        var calls = new List<string>();
        var factory = BuildFactory(calls, throwForModelIds: ["fallback-model"]);
        var models = new ModelSelection
        {
            Main = new ModelReference { Provider = "good", ModelId = "main-model" },
            Fallback = new ModelReference { Provider = "ghost", ModelId = "fallback-model" }
        };
        var provider = new NetclawChatClientProvider(factory, models);

        // Constructing the provider must not throw, and Main must resolve fine
        // even though Fallback is broken.
        var main = provider.GetClient(ModelRole.Main);
        Assert.NotNull(main);

        Assert.Throws<InvalidOperationException>(() => provider.GetClient(ModelRole.Fallback));
    }

    [Fact]
    public void BrokenMain_DoesNotCrashConstructor()
    {
        var calls = new List<string>();
        var factory = BuildFactory(calls, throwForModelIds: ["main-model"]);
        var models = new ModelSelection
        {
            Main = new ModelReference { Provider = "ghost", ModelId = "main-model" }
        };

        // Whole point of this change: a broken Main no longer takes down the
        // daemon at construction. The failure surfaces only when something
        // actually asks for a Main client.
        var provider = new NetclawChatClientProvider(factory, models);

        Assert.Throws<InvalidOperationException>(() => provider.GetClient(ModelRole.Main));
    }

    [Fact]
    public void GetClient_ReusesMaterializedClient()
    {
        var calls = new List<string>();
        var factory = BuildFactory(calls);
        var models = new ModelSelection
        {
            Main = new ModelReference { Provider = "good", ModelId = "m" }
        };
        var provider = new NetclawChatClientProvider(factory, models);

        var first = provider.GetClient(ModelRole.Main);
        var second = provider.GetClient(ModelRole.Main);

        Assert.Same(first, second);
        Assert.Equal(["m"], calls);
    }

    private static ProviderPluginFactory BuildFactory(
        List<string> recordedModelIds,
        IReadOnlyCollection<string>? throwForModelIds = null)
    {
        var recordingPlugin = new RecordingPlugin(recordedModelIds, throwForModelIds ?? []);
        var providers = new Dictionary<string, ProviderEntry>
        {
            ["good"] = new() { Type = "recording" },
            // "ghost" intentionally absent — exercising the dangling-reference
            // path drives the broken-main/broken-fallback assertions above.
        };
        return new ProviderPluginFactory(providers, [recordingPlugin]);
    }

    private sealed class RecordingPlugin(
        List<string> recordedModelIds,
        IReadOnlyCollection<string> throwForModelIds) : ILlmProviderPlugin
    {
        public string TypeKey => "recording";
        public string DisplayName => "Recording";
        public string DefaultEndpoint => "https://example.invalid";
        public string ModelListingPath => "/v1/models";
        public IProviderAuth Auth { get; } = new EndpointOnlyAuth();

        public IChatClient CreateChatClient(ProviderEntry entry, ModelReference model)
        {
            recordedModelIds.Add(model.ModelId);
            if (throwForModelIds.Contains(model.ModelId))
                throw new InvalidOperationException(
                    $"Simulated provider failure for '{model.ModelId}'.");
            return new NullChatClient();
        }

        public Task<ProviderProbeResult> ProbeAsync(ProviderEntry entry, CancellationToken ct = default)
            => Task.FromResult(new ProviderProbeResult(true, null, []));
    }

    private sealed class NullChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
