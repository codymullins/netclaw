// -----------------------------------------------------------------------
// <copyright file="ProviderReferenceValidatorTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.AI;
using Netclaw.Configuration;
using Netclaw.Daemon.Configuration;
using Netclaw.Providers;
using Xunit;

namespace Netclaw.Daemon.Tests.Configuration;

public sealed class ProviderReferenceValidatorTests
{
    private static readonly ILlmProviderPlugin[] Plugins =
    [
        new FakePlugin("ollama"),
        new FakePlugin("openai-compatible"),
        new FakePlugin("github-copilot"),
    ];

    [Fact]
    public void ZeroProviders_Passes_EvenWhenModelReferencesUnknownProvider()
    {
        var validator = new ProviderReferenceValidator(
            new Dictionary<string, ProviderEntry>(),
            Plugins);

        var result = validator.Validate(null, new ModelSelection
        {
            Main = new ModelReference { Provider = "local-ollama" }
        });

        Assert.False(result.Failed);
    }

    [Fact]
    public void DanglingMainProvider_Fails_WithConfiguredNamesListed()
    {
        var providers = new Dictionary<string, ProviderEntry>
        {
            ["my-github-copilot"] = new() { Type = "github-copilot" }
        };
        var validator = new ProviderReferenceValidator(providers, Plugins);

        var result = validator.Validate(null, new ModelSelection
        {
            Main = new ModelReference { Provider = "local-ollama" }
        });

        Assert.True(result.Failed);
        Assert.Contains("Models:Main:Provider 'local-ollama'", result.FailureMessage);
        Assert.Contains("my-github-copilot", result.FailureMessage);
    }

    [Fact]
    public void DanglingFallbackProvider_Fails()
    {
        var providers = new Dictionary<string, ProviderEntry>
        {
            ["main"] = new() { Type = "ollama" }
        };
        var validator = new ProviderReferenceValidator(providers, Plugins);

        var result = validator.Validate(null, new ModelSelection
        {
            Main = new ModelReference { Provider = "main" },
            Fallback = new ModelReference { Provider = "ghost" }
        });

        Assert.True(result.Failed);
        Assert.Contains("Models:Fallback:Provider 'ghost'", result.FailureMessage);
    }

    [Fact]
    public void UnknownProviderType_Fails_RegardlessOfModelReferences()
    {
        var providers = new Dictionary<string, ProviderEntry>
        {
            ["my-thing"] = new() { Type = "not-a-real-type" }
        };
        var validator = new ProviderReferenceValidator(providers, Plugins);

        var result = validator.Validate(null, new ModelSelection
        {
            Main = new ModelReference { Provider = "my-thing" }
        });

        Assert.True(result.Failed);
        Assert.Contains("Providers:my-thing:Type 'not-a-real-type'", result.FailureMessage);
        Assert.Contains("ollama", result.FailureMessage);
    }

    [Fact]
    public void ProviderTypeIsCaseInsensitive()
    {
        var providers = new Dictionary<string, ProviderEntry>
        {
            ["one"] = new() { Type = "OLLAMA" }
        };
        var validator = new ProviderReferenceValidator(providers, Plugins);

        var result = validator.Validate(null, new ModelSelection
        {
            Main = new ModelReference { Provider = "one" }
        });

        Assert.False(result.Failed);
    }

    [Fact]
    public void EmptyMainProvider_Fails_WhenProvidersExist()
    {
        var providers = new Dictionary<string, ProviderEntry>
        {
            ["main"] = new() { Type = "ollama" }
        };
        var validator = new ProviderReferenceValidator(providers, Plugins);

        var result = validator.Validate(null, new ModelSelection
        {
            Main = new ModelReference { Provider = "" }
        });

        Assert.True(result.Failed);
        Assert.Contains("Models:Main:Provider is empty", result.FailureMessage);
    }

    [Fact]
    public void MultipleErrors_AllReported()
    {
        var providers = new Dictionary<string, ProviderEntry>
        {
            ["bad-type"] = new() { Type = "unknown" }
        };
        var validator = new ProviderReferenceValidator(providers, Plugins);

        var result = validator.Validate(null, new ModelSelection
        {
            Main = new ModelReference { Provider = "missing-main" },
            Compaction = new ModelReference { Provider = "missing-compaction" }
        });

        Assert.True(result.Failed);
        Assert.Contains("bad-type", result.FailureMessage);
        Assert.Contains("missing-main", result.FailureMessage);
        Assert.Contains("missing-compaction", result.FailureMessage);
    }

    private sealed class FakePlugin(string typeKey) : ILlmProviderPlugin
    {
        public string TypeKey { get; } = typeKey;
        public string DisplayName => TypeKey;
        public string DefaultEndpoint => "https://example.invalid";
        public string ModelListingPath => "/v1/models";
        public IProviderAuth Auth { get; } = new EndpointOnlyAuth();
        public IChatClient CreateChatClient(ProviderEntry entry, ModelReference model)
            => throw new NotSupportedException();
        public Task<ProviderProbeResult> ProbeAsync(ProviderEntry entry, CancellationToken ct = default)
            => Task.FromResult(new ProviderProbeResult(true, null, []));
    }
}
