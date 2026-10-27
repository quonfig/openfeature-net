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
/// if initialization fails (or, non-fatal, when <see cref="OnInitFailure.Continue"/> started without
/// config), <c>PROVIDER_READY</c> again when such a client heals, and <c>PROVIDER_CONFIGURATION_CHANGED</c>
/// on every post-ready config refresh (SSE / fallback / datadir watcher). The server SDK keeps no config
/// cache, so the provider never reports <c>PROVIDER_STALE</c>.
/// </remarks>
public sealed class QuonfigProvider : FeatureProvider
{
    private const string ProviderName = "quonfig";

    /// <summary>Delay before the non-fatal PROVIDER_ERROR under OnInitFailure.Continue (see <see cref="WatchForHeal"/>).</summary>
    private static readonly TimeSpan ContinueErrorDelay = TimeSpan.FromMilliseconds(100);

    private readonly QuonfigProviderOptions _options;
    private readonly string _targetingKeyMapping;
    private readonly CancellationTokenSource _shutdown = new();
    private IQuonfig? _client;
    private int _ready;
    private int _subscribed;
    private int _watching;

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
    /// log levels, raw key access). <c>null</c> until <see cref="InitializeAsync"/> has run. After a failed
    /// initialization it is the kept client, still retrying in the background.
    /// </summary>
    public IQuonfig? GetClient() => _client;

    /// <inheritdoc/>
    public override async Task InitializeAsync(EvaluationContext context, CancellationToken cancellationToken = default)
    {
        var client = _client;
        try
        {
            if (client is null)
            {
                client = new QuonfigClient(BuildQuonfigOptions());
                // Keep the client even if InitAsync throws: a client that survives a caught
                // OnInitFailure.Fail keeps retrying and heals (init-failure design B.5).
                _client = client;
            }

            // Subscribe before InitAsync so an HTTP-mode initial install is observed; the _ready
            // guard suppresses that spurious pre-ready config-change (matches openfeature-go).
            if (Interlocked.Exchange(ref _subscribed, 1) == 0)
            {
                client.OnConfigChange += HandleConfigChange;
            }

            await client.InitAsync(cancellationToken).ConfigureAwait(false);
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
            if (client is not null)
            {
                WatchForHeal(client, deferredError: null);
            }

            throw;
        }

        if (!IsClientReady(client))
        {
            // OnInitFailure.Continue with no config: InitAsync returned but every evaluation is a code
            // default. Report ERROR (non-fatal) rather than READY, then READY once the client heals.
            WatchForHeal(client, deferredError: "Quonfig initialization continued without config; serving code defaults until delivery is reachable");
            return;
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
        _shutdown.Cancel();
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
        if (client is null || !IsClientReady(client))
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
        if (client is null || !IsClientReady(client))
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
        if (client is null || !IsClientReady(client))
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
        if (client is null || !IsClientReady(client))
        {
            return Task.FromResult(NotReady(flagKey, defaultValue));
        }

        var details = client.GetDoubleDetails(flagKey, MapContext(context), defaultValue);
        var value = HasError(details) || details.Value is null ? defaultValue : details.Value.Value;
        return Task.FromResult(Build(flagKey, value, details));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Resolves a <c>json</c> config (as a parsed value) first, then a <c>string_list</c> (as a list of
    /// strings). The string-list read runs only when the json read reports a type mismatch: since
    /// <c>Quonfig.Sdk</c> 2.0 every type-mismatch read logs a warning once per key, so probing a type
    /// the config cannot have would log a spurious warning for a valid read. Json goes first because it
    /// is the common structure type; a <c>string_list</c> read through here still logs one warning per
    /// key (the SDK has no public way to learn a config's type without a typed read).
    /// </remarks>
    public override Task<ResolutionDetails<Value>> ResolveStructureValueAsync(
        string flagKey, Value defaultValue, EvaluationContext? context = null, CancellationToken cancellationToken = default)
    {
        var client = _client;
        if (client is null || !IsClientReady(client))
        {
            return Task.FromResult(NotReady(flagKey, defaultValue));
        }

        var ctx = MapContext(context);

        var jsonDetails = client.GetJsonDetails(flagKey, ctx);
        if (jsonDetails.ErrorCode != ErrorCode.TypeMismatch)
        {
            // Resolved, no rule matched, or an error that a second typed read would only repeat
            // (flag not found, unset ENV_VAR, decryption failure).
            var value = HasError(jsonDetails) || jsonDetails.Value is null ? defaultValue : Mappings.ToValue(jsonDetails.Value);
            return Task.FromResult(Build(flagKey, value, jsonDetails));
        }

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

        return Task.FromResult(Build(flagKey, defaultValue, listDetails));
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

    /// <summary>
    /// True once the client holds a config from Quonfig. Only the concrete SDK client exposes readiness
    /// (it is not on <see cref="IQuonfig"/>), so any other implementation counts as ready.
    /// </summary>
    private static bool IsClientReady(IQuonfig client) => client is not QuonfigClient q || q.IsReady;

    /// <summary>
    /// Background watch for a client that is not ready after <see cref="InitializeAsync"/>: optionally emits
    /// a non-fatal <c>PROVIDER_ERROR</c>, then emits <c>PROVIDER_READY</c> when the client heals.
    /// </summary>
    private void WatchForHeal(IQuonfig client, string? deferredError)
    {
        // One watch per provider: a repeated InitializeAsync joins the client's retry and must not emit twice.
        if (client is not QuonfigClient q || Interlocked.Exchange(ref _watching, 1) == 1)
        {
            return;
        }

        var token = _shutdown.Token;
        _ = Task.Run(
            async () =>
            {
                try
                {
                    if (deferredError is not null)
                    {
                        // The OF SDK sets READY right after InitializeAsync returns. Give that a moment
                        // so this ERROR lands after it instead of being overwritten.
                        await Task.Delay(ContinueErrorDelay, token).ConfigureAwait(false);
                        if (!q.IsReady)
                        {
                            await EmitAsync(new ProviderEventPayload
                            {
                                ProviderName = ProviderName,
                                Type = OFConst.ProviderEventTypes.ProviderError,
                                ErrorType = OFConst.ErrorType.ProviderNotReady,
                                Message = deferredError,
                            }).ConfigureAwait(false);
                        }
                    }

                    await q.WaitForReadyAsync(token).ConfigureAwait(false);
                    Interlocked.Exchange(ref _ready, 1);
                    await EmitAsync(new ProviderEventPayload
                    {
                        ProviderName = ProviderName,
                        Type = OFConst.ProviderEventTypes.ProviderReady,
                    }).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Provider shut down before the client healed.
                }
            },
            CancellationToken.None);
    }

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

        if (_options.OnInitFailure is { } onInitFailure)
        {
            quonfigOptions.OnInitFailure = onInitFailure;
        }

        if (_options.InitTimeout is { } initTimeout)
        {
            quonfigOptions.InitTimeout = initTimeout;
        }

        _options.ConfigureClient?.Invoke(quonfigOptions);
        return quonfigOptions;
    }
}
