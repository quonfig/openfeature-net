# Quonfig.OpenFeature.ServerProvider

OpenFeature provider for [Quonfig](https://quonfig.com) — wraps the
[`Quonfig.Sdk`](https://www.nuget.org/packages/Quonfig.Sdk) .NET SDK.

## Installation

```bash
dotnet add package Quonfig.OpenFeature.ServerProvider
dotnet add package OpenFeature
```

## Usage

```csharp
using OpenFeature;
using OpenFeature.Model;
using Quonfig.OpenFeature.ServerProvider;

var provider = new QuonfigProvider(new QuonfigProviderOptions
{
    SdkKey = "qf_sk_production_...",
    TargetingKeyMapping = "user.id", // default
});

// Register and wait for initialization
await Api.Instance.SetProviderAsync(provider);

var client = Api.Instance.GetClient();

// Boolean flag
bool enabled = await client.GetBooleanValueAsync("my-flag", false);

// String flag with context
var ctx = EvaluationContext.Builder()
    .SetTargetingKey("user-123")
    .Set("user.email", new Value("alice@co.com"))
    .Set("org.tier", new Value("enterprise"))
    .Build();
string plan = await client.GetStringValueAsync("billing.plan", "free", ctx);
```

## Context mapping

OpenFeature uses a flat key-value evaluation context. Quonfig uses a nested
namespace model. The provider maps between them using dot-notation (split on the
first dot only):

| OpenFeature key      | Quonfig namespace | Quonfig property             |
|----------------------|-------------------|------------------------------|
| `"user.email"`       | `"user"`          | `"email"`                    |
| `"org.tier"`         | `"org"`           | `"tier"`                     |
| `"country"` (no dot) | `""` (default)    | `"country"`                  |
| `"user.ip.address"`  | `"user"`          | `"ip.address"` (first dot)   |
| `targetingKey`       | `"user"`          | `"id"` (via `TargetingKeyMapping`) |

To use a different targeting key property:

```csharp
var provider = new QuonfigProvider(new QuonfigProviderOptions
{
    SdkKey = "qf_sk_...",
    TargetingKeyMapping = "org.id", // targetingKey -> org namespace, id property
});
```

## Local / offline mode

Use `Datadir` instead of `SdkKey` to load config from a local workspace directory:

```csharp
var provider = new QuonfigProvider(new QuonfigProviderOptions
{
    Datadir = "/path/to/workspace",
    Environment = "Production",
});
```

## Reason and error mapping

The provider forwards Quonfig's evaluation `Reason`, `Variant`, `ErrorCode`, and
flag metadata onto OpenFeature's `ResolutionDetails`. The reason mapping is 1:1:

| Quonfig reason     | OpenFeature reason |
|--------------------|--------------------|
| `Static`           | `STATIC`           |
| `TargetingMatch`   | `TARGETING_MATCH`  |
| `Split`            | `SPLIT`            |
| `Default`          | `DEFAULT`          |
| (anything else)    | `UNKNOWN`          |

Errors never throw — they return the caller's default with an `ErrorType` set:
`FlagNotFound` → `FLAG_NOT_FOUND`, `TypeMismatch` → `TYPE_MISMATCH`,
`General` → `GENERAL`. A missing flag surfaces with reason `DEFAULT` (matching the
other Quonfig OpenFeature providers).

> Note: a flag whose only rule is an unconditional match (`ALWAYS_TRUE`) resolves
> with reason `TARGETING_MATCH` in `Quonfig.Sdk` (consistent with the Java SDK).
> The provider passes the SDK's reason through verbatim.

## Provider events

The provider emits:

- `PROVIDER_READY` after a successful `InitializeAsync`
- `PROVIDER_ERROR` if initialization fails
- `PROVIDER_CONFIGURATION_CHANGED` on every post-ready config refresh (SSE, fallback
  poll, or datadir watcher)

`PROVIDER_STALE` is deferred to a follow-up release.

## Native SDK escape hatch

For features not covered by OpenFeature (duration / long / bytes values, log
levels, raw key access), reach the underlying client:

```csharp
var native = provider.GetClient();
TimeSpan? ttl = native?.GetDuration("cache.ttl");
```

## Type mapping

| Quonfig type  | OpenFeature method      | Notes                          |
|---------------|-------------------------|--------------------------------|
| `bool`        | `GetBooleanValueAsync`  | Direct                         |
| `string`      | `GetStringValueAsync`   | Direct                         |
| `int`         | `GetIntegerValueAsync`  | Returns `int`                  |
| `double`      | `GetDoubleValueAsync`   | Returns `double`               |
| `string_list` | `GetObjectValueAsync`   | Returns a `Value` list         |
| `json`        | `GetObjectValueAsync`   | Returns a parsed `Value`       |
| `long`        | N/A                     | Use the native client          |
| `duration`    | N/A                     | Use the native client          |
| `log_level`   | N/A                     | Native SDK only                |

`GetObjectValueAsync` resolves `string_list` first, then falls back to a parsed
`json` value.
