using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenFeature;
using OpenFeature.Model;
using Quonfig.Sdk;
using OFConst = OpenFeature.Constant;
using QuonfigClient = Quonfig.Sdk.Quonfig;

namespace Quonfig.OpenFeature.ServerProvider;

/// <summary>
/// OpenFeature <see cref="FeatureProvider"/> for Quonfig — wraps the <c>Quonfig.Sdk</c> native client.
/// Construct with a <see cref="QuonfigProviderOptions"/>, register via
/// <c>await Api.Instance.SetProviderAsync(provider)</c>, then resolve flags through an
/// OpenFeature client. Mirrors the openfeature-go reference provider.
/// </summary>
/// <remarks>
/// Emits <c>PROVIDER_READY</c> after a successful <see cref="InitializeAsync"/>, <c>PROVIDER_ERROR</c>
/// if initialization fails, and <c>PROVIDER_CONFIGURATION_CHANGED</c> on every post-ready config
/// refresh (SSE / fallback / datadir watcher). <c>PROVIDER_STALE</c> is deferred to a follow-up.
/// </remarks>
public sealed class QuonfigProvider : FeatureProvider
{
    private const string ProviderName = "quonfig";

    private readonly QuonfigProviderOptions _options;
    private readonly string _targetingKeyMapping;
    private IQuonfig? _client;
    private int _ready;

    /// <summary>Creates a provider that builds and owns its own Quonfig client from <paramref name="options"/>.</summary>
    public QuonfigProvider(QuonfigProviderOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _targetingKeyMapping = string.IsNullOrEmpty(options.TargetingKeyMapping)
            ? "user.id"
            : options.TargetingKeyMapping;
    }

    /// <summary>Test seam: wraps an already-constructed client. The provider does not rebuild it on init.</summary>
    internal QuonfigProvider(IQuonfig client, string targetingKeyMapping = "user.id")
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _options = new QuonfigProviderOptions();
        _targetingKeyMapping = string.IsNullOrEmpty(targetingKeyMapping) ? "user.id" : targetingKeyMapping;
    }

    /// <inheritdoc/>
    public override Metadata GetMetadata() => new(ProviderName);

    /// <summary>
    /// The underlying Quonfig client for native-only features (duration / long / bytes values,
    /// log levels, raw key access). <c>null</c> until <see cref="InitializeAsync"/> has run.
    /// </summary>
    public IQuonfig? GetClient() => _client;

    /// <inheritdoc/>
    public override async Task InitializeAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        if (_client is null)
        {
            var quonfigOptions = BuildQuonfigOptions();
            try
            {
                var client = new QuonfigClient(quonfigOptions);
                // Subscribe before InitAsync so an HTTP-mode initial install is observed; the _ready
                // guard suppresses that spurious pre-ready config-change (matches openfeature-go).
                client.OnConfigChange += HandleConfigChange;
                await client.InitAsync(cancellationToken).ConfigureAwait(false);
                _client = client;
            }
            catch (Exception ex)
            {
                await EmitAsync(new ProviderEventPayload
                {
                    ProviderName = ProviderName,
                    Type = OFConst.ProviderEventTypes.ProviderError,
                    ErrorType = OFConst.ErrorType.General,
                    Message = ex.Message,
                }).ConfigureAwait(false);
                throw;
            }
        }
        else
        {
            _client.OnConfigChange += HandleConfigChange;
        }

        Interlocked.Exchange(ref _ready, 1);
        await EmitAsync(new ProviderEventPayload
        {
            ProviderName = ProviderName,
            Type = OFConst.ProviderEventTypes.ProviderReady,
        }).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public override async Task ShutdownAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Exchange(ref _ready, 0);
        var client = _client;
        if (client is not null)
        {
            await client.CloseAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public override Task<ResolutionDetails<bool>> ResolveBooleanValueAsync(
        string flagKey, bool defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
    {
        var client = _client;
        if (client is null)
        {
            return Task.FromResult(NotReady(flagKey, defaultValue));
        }

        var details = client.GetBoolDetails(flagKey, MapContext(context), defaultValue);
        var value = HasError(details) || details.Value is null ? defaultValue : details.Value.Value;
        return Task.FromResult(Build(flagKey, value, details));
    }

    /// <inheritdoc/>
    public override Task<ResolutionDetails<string>> ResolveStringValueAsync(
        string flagKey, string defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
    {
        var client = _client;
        if (client is null)
        {
            return Task.FromResult(NotReady(flagKey, defaultValue));
        }

        var details = client.GetStringDetails(flagKey, MapContext(context), defaultValue);
        var value = HasError(details) || details.Value is null ? defaultValue : details.Value;
        return Task.FromResult(Build(flagKey, value, details));
    }

    /// <inheritdoc/>
    public override Task<ResolutionDetails<int>> ResolveIntegerValueAsync(
        string flagKey, int defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
    {
        var client = _client;
        if (client is null)
        {
            return Task.FromResult(NotReady(flagKey, defaultValue));
        }

        var details = client.GetIntDetails(flagKey, MapContext(context), defaultValue);
        var value = HasError(details) || details.Value is null ? defaultValue : details.Value.Value;
        return Task.FromResult(Build(flagKey, value, details));
    }

    /// <inheritdoc/>
    public override Task<ResolutionDetails<double>> ResolveDoubleValueAsync(
        string flagKey, double defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
    {
        var client = _client;
        if (client is null)
        {
            return Task.FromResult(NotReady(flagKey, defaultValue));
        }

        var details = client.GetDoubleDetails(flagKey, MapContext(context), defaultValue);
        var value = HasError(details) || details.Value is null ? defaultValue : details.Value.Value;
        return Task.FromResult(Build(flagKey, value, details));
    }

    /// <inheritdoc/>
    /// <remarks>Resolves <c>string_list</c> first (as a list of strings), then falls back to a parsed <c>json</c> value.</remarks>
    public override Task<ResolutionDetails<Value>> ResolveStructureValueAsync(
        string flagKey, Value defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
    {
        var client = _client;
        if (client is null)
        {
            return Task.FromResult(NotReady(flagKey, defaultValue));
        }

        var ctx = MapContext(context);

        var listDetails = client.GetStringListDetails(flagKey, ctx);
        if (!HasError(listDetails) && listDetails.Value is not null)
        {
            var items = new List<Value>();
            foreach (var s in listDetails.Value)
            {
                items.Add(new Value(s));
            }

            return Task.FromResult(Build(flagKey, new Value(items), listDetails));
        }

        var jsonDetails = client.GetJsonDetails(flagKey, ctx);
        if (!HasError(jsonDetails) && jsonDetails.Value is not null)
        {
            return Task.FromResult(Build(flagKey, Mappings.ToValue(jsonDetails.Value), jsonDetails));
        }

        return Task.FromResult(Build(flagKey, defaultValue, jsonDetails));
    }

    private ContextSet? MapContext(EvaluationContext? context) =>
        ContextMapper.MapContext(context, _targetingKeyMapping);

    private static bool HasError<T>(EvaluationDetails<T> details) => details.ErrorCode is not null;

    private static ResolutionDetails<T> Build<T, TQuonfig>(string flagKey, T value, EvaluationDetails<TQuonfig> details) =>
        new(
            flagKey,
            value,
            Mappings.ErrorTypeFor(details.ErrorCode),
            Mappings.ReasonFor(details.Reason, details.ErrorCode),
            details.Variant,
            details.ErrorMessage,
            Mappings.ToFlagMetadata(details.Metadata));

    private static ResolutionDetails<T> NotReady<T>(string flagKey, T defaultValue) =>
        new(
            flagKey,
            defaultValue,
            OFConst.ErrorType.ProviderNotReady,
            OFConst.Reason.Error,
            "default",
            "provider not initialized");

    private void HandleConfigChange()
    {
        if (Volatile.Read(ref _ready) == 0)
        {
            return;
        }

        // Drop the event rather than block if the bounded channel is full (matches openfeature-go).
        EventChannel.Writer.TryWrite(new ProviderEventPayload
        {
            ProviderName = ProviderName,
            Type = OFConst.ProviderEventTypes.ProviderConfigurationChanged,
            FlagsChanged = new List<string>(),
        });
    }

    private async Task EmitAsync(ProviderEventPayload payload) =>
        await EventChannel.Writer.WriteAsync(payload).ConfigureAwait(false);

    private QuonfigOptions BuildQuonfigOptions()
    {
        var quonfigOptions = new QuonfigOptions();
        if (!string.IsNullOrEmpty(_options.SdkKey))
        {
            quonfigOptions.SdkKey = _options.SdkKey;
        }

        if (!string.IsNullOrEmpty(_options.Datadir))
        {
            quonfigOptions.Datadir = _options.Datadir;
        }

        if (!string.IsNullOrEmpty(_options.Environment))
        {
            quonfigOptions.Environment = _options.Environment;
        }

        _options.ConfigureClient?.Invoke(quonfigOptions);
        return quonfigOptions;
    }
}
