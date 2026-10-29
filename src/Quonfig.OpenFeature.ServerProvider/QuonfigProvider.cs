using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using OpenFeature;
using OpenFeature.Model;
using Quonfig.Sdk;
using Quonfig.Sdk.Exceptions;
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

    /// <summary>Wire names of the stored value types, as <see cref="EvaluationDetails{T}.ValueType"/> reports them.</summary>
    private const string DurationValueType = "duration";
    private const string StringValueType = "string";
    private const string LogLevelValueType = "log_level";
    private const string JsonValueType = "json";
    private const string StringListValueType = "string_list";

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
    /// The underlying Quonfig client for native-only features (parsed <see cref="TimeSpan"/> durations,
    /// long / bytes values, log levels, raw key access). A duration's ISO 8601 text is also readable
    /// through <see cref="ResolveStringValueAsync"/>. <c>null</c> until <see cref="InitializeAsync"/> has run. After a failed
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

        var ctx = MapContext(context);
        if (client is IQuonfigDetailsReader reader)
        {
            var raw = reader.EvaluateDetails(flagKey, ctx);
            if (raw.ValueType == DurationValueType)
            {
                return Task.FromResult(DurationAsString(client, flagKey, defaultValue, ctx, raw));
            }

            // A missing key, or a string or log_level config (both read as strings): the untyped read
            // is the whole answer, so the config is evaluated (and counted in telemetry) once.
            if (raw.ValueType is null
                || ((raw.ValueType == StringValueType || raw.ValueType == LogLevelValueType)
                    && (HasError(raw) || raw.Value is null or string)))
            {
                var resolved = HasError(raw) || raw.Value is not string s ? defaultValue : s;
                return Task.FromResult(Build(flagKey, resolved, raw));
            }

            // Any other type: the typed read below reports the type mismatch. It returns before the
            // SDK records an evaluation, so the untyped read above stays the only one counted.
        }

        var details = client.GetStringDetails(flagKey, ctx, defaultValue);
        var value = HasError(details) || details.Value is null ? defaultValue : details.Value;
        // Without IQuonfigDetailsReader (a custom IQuonfig) a duration config is TYPE_MISMATCH, unless
        // the stored duration is malformed: then it is PARSE_ERROR (qfg-9xsl).
        return Task.FromResult(Build(flagKey, value, details, IsMalformed(details, () => client.GetDuration(flagKey, ctx))));
    }

    /// <summary>
    /// A string read of a <c>duration</c> config returns its ISO 8601 text exactly as stored, with the
    /// evaluation's reason, variant and metadata (integration-test-data openfeature.yaml Case 2,
    /// qfg-177f), matching openfeature-go's <c>stringOf</c>. A stored value that is not a valid
    /// duration (for example <c>"30s"</c>) or a malformed ENV_VAR value is PARSE_ERROR.
    /// </summary>
    private static ResolutionDetails<string> DurationAsString(
        IQuonfig client, string flagKey, string defaultValue, ContextSet? ctx, EvaluationDetails<object?> raw)
    {
        if (HasError(raw))
        {
            // An ENV_VAR value that does not parse comes back as TypeMismatch; the plain duration
            // getter confirms it is a parse failure (see IsMalformed). An unset ENV_VAR stays GENERAL.
            return Build(flagKey, defaultValue, raw, IsMalformed(raw, () => client.GetDuration(flagKey, ctx)));
        }

        if (raw.Value is not string text)
        {
            // No rule matched: the default with the SDK's reason.
            return Build(flagKey, defaultValue, raw);
        }

        if (IsoDurationText.IsValid(text))
        {
            return Build(flagKey, text, raw);
        }

        // EvaluateDetails returns a malformed stored duration as stored, with a success reason.
        return new ResolutionDetails<string>(
            flagKey,
            defaultValue,
            OFConst.ErrorType.ParseError,
            OFConst.Reason.Error,
            raw.Variant,
            $"config \"{flagKey}\" has a malformed duration value",
            Mappings.ToFlagMetadata(raw.Metadata));
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

        var ctx = MapContext(context);
        var details = client.GetIntDetails(flagKey, ctx, defaultValue);
        var value = HasError(details) || details.Value is null ? defaultValue : details.Value.Value;
        return Task.FromResult(Build(flagKey, value, details, IsMalformed(details, () => client.GetInt(flagKey, ctx))));
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

        var ctx = MapContext(context);
        var details = client.GetDoubleDetails(flagKey, ctx, defaultValue);
        var value = HasError(details) || details.Value is null ? defaultValue : details.Value.Value;
        return Task.FromResult(Build(flagKey, value, details, IsMalformed(details, () => client.GetDouble(flagKey, ctx))));
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Resolves a <c>json</c> config (as a parsed value) or a <c>string_list</c> (as a list of strings).
    /// With the SDK client this is one untyped <see cref="IQuonfigDetailsReader.EvaluateDetails"/> read,
    /// which names the config's stored type, so a valid read of either type is evaluated once and logs
    /// no type-mismatch warning (qfg-tzby). For any other type, and for a custom <see cref="IQuonfig"/>
    /// without <see cref="IQuonfigDetailsReader"/>, the provider reads json first, then string_list only
    /// when the json read reports a type mismatch: since <c>Quonfig.Sdk</c> 2.0 every type-mismatch read
    /// logs a warning once per key.
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
        if (client is IQuonfigDetailsReader reader)
        {
            var raw = reader.EvaluateDetails(flagKey, ctx);

            // A missing key, or a json or string_list config: the untyped read is the whole answer
            // (json as the tree GetJson returns, string_list as a list of strings).
            if (raw.ValueType is null || raw.ValueType == JsonValueType || raw.ValueType == StringListValueType)
            {
                var resolved = HasError(raw) || raw.Value is null ? defaultValue : Mappings.ToValue(raw.Value);
                return Task.FromResult(Build(flagKey, resolved, raw));
            }

            // Any other type: the typed reads below report the type mismatch. They return before the
            // SDK records an evaluation, so the untyped read above stays the only one counted.
        }

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

    /// <summary>
    /// True when a typed read that reported <see cref="ErrorCode.TypeMismatch"/> failed because the value
    /// has the type <paramref name="plainRead"/> reads but does not parse (a stored int <c>"twelve"</c>, an
    /// ENV_VAR int set to <c>"twelve"</c>, a stored duration <c>"30s"</c>), so the provider reports
    /// PARSE_ERROR instead of TYPE_MISMATCH (qfg-9xsl).
    /// </summary>
    /// <remarks>
    /// <c>Quonfig.Sdk</c> 2.0 reports both failures as <see cref="ErrorCode.TypeMismatch"/> on its
    /// <c>Get*Details</c> getters. Its plain typed getters, called with no default under
    /// <see cref="OnNoDefault.Throw"/> (the default policy), raise a distinct exception for each:
    /// <see cref="QuonfigCoercionException"/> for an unparseable value and
    /// <see cref="QuonfigTypeMismatchException"/> for a value of another type. This re-read, made only on
    /// the type-mismatch path, uses that typed signal rather than the error-message text. It logs nothing
    /// new: the SDK warns once per key and the details read already did. Under any other
    /// <see cref="OnNoDefault"/> policy the plain getter does not throw and the result stays TYPE_MISMATCH.
    /// </remarks>
    private static bool IsMalformed<T>(EvaluationDetails<T> details, Action plainRead)
    {
        if (details.ErrorCode != ErrorCode.TypeMismatch)
        {
            return false;
        }

        try
        {
            plainRead();
        }
        catch (QuonfigCoercionException)
        {
            return true;
        }
        catch (QuonfigException)
        {
            // A value of another type (or the config changed between the two reads).
        }

        return false;
    }

    private static ResolutionDetails<T> Build<T, TQuonfig>(string flagKey, T value, EvaluationDetails<TQuonfig> details, bool malformed = false) =>
        new(
            flagKey,
            value,
            malformed ? OFConst.ErrorType.ParseError : Mappings.ErrorTypeFor(details.ErrorCode),
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
