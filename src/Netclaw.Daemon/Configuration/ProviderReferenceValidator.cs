// -----------------------------------------------------------------------
// <copyright file="ProviderReferenceValidator.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Microsoft.Extensions.Options;
using Netclaw.Configuration;
using Netclaw.Providers;

namespace Netclaw.Daemon.Configuration;

/// <summary>
/// Cross-references the bound <see cref="ModelSelection"/> against the
/// configured providers and the registered <see cref="ILlmProviderPlugin"/>
/// type keys. Catches "Models.Main.Provider names a key that isn't in
/// Providers" at startup as a structured options-validation failure
/// instead of letting it surface later as an unhandled exception from
/// <see cref="ProviderPluginFactory.Create"/>.
/// </summary>
/// <remarks>
/// Zero configured providers is a legal state — the daemon boots so that
/// the operator can complete first-run setup (pairing, doctor, wizard)
/// before configuring an LLM. Cross-reference checks only run when at
/// least one provider is present; otherwise the <see cref="ModelSelection"/>
/// values are treated as documentation rather than as live references.
/// </remarks>
public sealed class ProviderReferenceValidator : IValidateOptions<ModelSelection>
{
    private readonly IReadOnlyDictionary<string, ProviderEntry> _providers;
    private readonly HashSet<string> _knownProviderTypes;

    public ProviderReferenceValidator(
        IReadOnlyDictionary<string, ProviderEntry> providers,
        IEnumerable<ILlmProviderPlugin> plugins)
    {
        _providers = providers;
        _knownProviderTypes = new HashSet<string>(
            plugins.Select(p => p.TypeKey),
            StringComparer.OrdinalIgnoreCase);
    }

    public ValidateOptionsResult Validate(string? name, ModelSelection options)
    {
        var errors = new List<string>();

        // First: every configured provider's Type must be a known plugin key.
        // This applies regardless of provider count — a misspelled Type would
        // crash ProviderPluginFactory at session start with the same shape of
        // error the operator hit at boot.
        foreach (var (key, entry) in _providers)
        {
            if (string.IsNullOrWhiteSpace(entry.Type))
            {
                errors.Add($"Providers:{key}:Type is empty. Set it to one of: "
                    + string.Join(", ", KnownTypesOrdered()));
                continue;
            }

            if (!_knownProviderTypes.Contains(entry.Type))
            {
                errors.Add($"Providers:{key}:Type '{entry.Type}' is not a known provider type. "
                    + $"Known types: {string.Join(", ", KnownTypesOrdered())}");
            }
        }

        // Cross-reference Models.* against configured providers, but only when
        // at least one provider exists. Zero providers means "operator hasn't
        // set up an LLM yet" and the daemon should boot anyway.
        if (_providers.Count > 0)
        {
            ValidateRole(nameof(options.Main), options.Main, required: true, errors);

            if (options.Fallback is not null)
                ValidateRole(nameof(options.Fallback), options.Fallback, required: false, errors);

            if (options.Compaction is not null)
                ValidateRole(nameof(options.Compaction), options.Compaction, required: false, errors);
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }

    private void ValidateRole(string role, ModelReference model, bool required, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(model.Provider))
        {
            if (required)
                errors.Add($"Models:{role}:Provider is empty. Set it to one of the configured providers: "
                    + string.Join(", ", ConfiguredOrdered()));
            return;
        }

        if (!_providers.ContainsKey(model.Provider))
        {
            errors.Add($"Models:{role}:Provider '{model.Provider}' is not defined under Providers. "
                + $"Configured providers: {string.Join(", ", ConfiguredOrdered())}");
        }
    }

    private IEnumerable<string> KnownTypesOrdered() =>
        _knownProviderTypes.OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

    private IEnumerable<string> ConfiguredOrdered() =>
        _providers.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
}
