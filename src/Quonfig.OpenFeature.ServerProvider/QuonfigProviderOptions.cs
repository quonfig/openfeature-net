using System;
using Quonfig.Sdk;

namespace Quonfig.OpenFeature.ServerProvider;

/// <summary>
/// Construction-time configuration for a <see cref="QuonfigProvider"/>. Mirrors the
/// <c>openfeature-go</c> provider's <c>Options</c>: pick exactly one of <see cref="SdkKey"/>
/// (HTTP+SSE mode) or <see cref="Datadir"/> (local/offline mode).
/// </summary>
public sealed class QuonfigProviderOptions
{
    /// <summary>Quonfig SDK key (e.g. <c>"qf_sk_production_..."</c>). Mutually exclusive with <see cref="Datadir"/>.</summary>
    public string? SdkKey { get; set; }

    /// <summary>Local Quonfig workspace directory for offline/test mode. Mutually exclusive with <see cref="SdkKey"/>.</summary>
    public string? Datadir { get; set; }

    /// <summary>Environment slug to evaluate against (e.g. <c>"production"</c>). Required in datadir mode.</summary>
    public string? Environment { get; set; }

    /// <summary>
    /// How the OpenFeature <c>targetingKey</c> maps to a Quonfig context property. Dot-notation:
    /// <c>"user.id"</c> means namespace <c>"user"</c>, property <c>"id"</c>. Defaults to <c>"user.id"</c>.
    /// </summary>
    public string TargetingKeyMapping { get; set; } = "user.id";

    /// <summary>
    /// Optional escape hatch to tweak the underlying <see cref="QuonfigOptions"/> before the client is
    /// constructed (e.g. custom <c>ApiUrls</c>, telemetry settings, or an <c>HttpMessageHandler</c> for
    /// tests). Mirrors openfeature-go's <c>AdditionalOptions</c>.
    /// </summary>
    public Action<QuonfigOptions>? ConfigureClient { get; set; }
}
