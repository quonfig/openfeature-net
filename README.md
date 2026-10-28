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
`FlagNotFound` → `FLAG_NOT_FOUND`, `TypeMismatch` → `TYPE_MISMATCH` (or
`PARSE_ERROR` for a malformed value, see [Typed reads](#typed-reads)),
`General` → `GENERAL`. A missing flag surfaces with reason `DEFAULT` (matching the
other Quonfig OpenFeature providers).

## Typed reads

Each resolver reads only its own config type (the cross-SDK typed-getter
contract in `Quonfig.Sdk` 2.0). A value of another type returns your default
with reason `ERROR` and error type `TYPE_MISMATCH`, and `Quonfig.Sdk` logs one
warning per key (naming the key, never the value).

| Resolver | Reads |
|---|---|
| `GetBooleanValueAsync` | `bool` |
| `GetStringValueAsync` | `string`, `log_level` |
| `GetIntegerValueAsync` | `int`, and a `double` that is whole and within +/-(2^53-1) |
| `GetDoubleValueAsync` | `double`, `int` |
| `GetObjectValueAsync` | `json`, `string_list` (as a list of strings) |

Strings are never coerced: a stored `"true"` is not a boolean and `"3"` is not
a number. A fractional double read as an integer returns your default; it is
never truncated.

A value that has the requested type but does not parse returns your default
with reason `ERROR` and error type `PARSE_ERROR`: a stored int `"twelve"` or
double `"abc"`, or an int or double ENV_VAR set to `"twelve"`. That is a data
problem; reading a config with the resolver for another type is
`TYPE_MISMATCH`. An unset ENV_VAR or a decryption failure is `GENERAL`.
`PARSE_ERROR` relies on the client's default `OnNoDefault.Throw` policy; under
`Warn` or `Ignore` a malformed value reports `TYPE_MISMATCH`.

`duration` configs have no OpenFeature read path: every resolver returns your
default with `TYPE_MISMATCH`, except that `GetStringValueAsync` on a malformed
stored duration (for example `"30s"`) reports `PARSE_ERROR`. Read durations
natively (see below).

> Note: a flag whose only rule is an unconditional match (`ALWAYS_TRUE`) resolves
> with reason `STATIC` (the canonical reason semantics `Quonfig.Sdk` adopted in
> 0.0.2, consistent with the other Quonfig SDKs). The provider passes the SDK's
> reason through verbatim.

## Provider events

The provider emits:

- `PROVIDER_READY` after a successful `InitializeAsync`, and later when a client
  that started without config heals
- `PROVIDER_ERROR` if initialization fails, or (non-fatal) when it continued
  without config
- `PROVIDER_CONFIGURATION_CHANGED` on every post-ready config refresh (SSE, fallback
  poll, or datadir watcher)

`Quonfig.Sdk` keeps no config cache on servers, so the provider never reports
`STALE`.

## Init failure

When Quonfig delivery is unreachable at startup, the wrapped client retries for
`InitTimeout` (10 s by default) and then applies `OnInitFailure`. Set both on the
provider options:

```csharp
var provider = new QuonfigProvider(new QuonfigProviderOptions
{
    SdkKey = "qf_sk_production_...",
    OnInitFailure = OnInitFailure.Fail, // the default; or Wait / Continue
    InitTimeout = TimeSpan.FromSeconds(10),
});
```

| `OnInitFailure` | `InitializeAsync` | Provider status | When delivery comes back |
|---|---|---|---|
| `Fail` (default) | Throws `QuonfigInitException` | `ERROR` | `PROVIDER_READY`, status `READY` |
| `Wait` | Blocks until the first config | `NOT_READY` (evaluations short-circuit to the default with `PROVIDER_NOT_READY`) | `InitializeAsync` returns, status `READY` |
| `Continue` | Returns, then emits a non-fatal `PROVIDER_ERROR` | `ERROR` | `PROVIDER_READY`, status `READY` |

In every mode the provider keeps the client alive, so it keeps retrying and heals.
Until it does, evaluations return your default with error type
`PROVIDER_NOT_READY` and reason `ERROR`. Under `Wait`, `SetProviderAsync` holds
until config arrives (or its `CancellationToken` fires).

## Native SDK escape hatch

For features not covered by OpenFeature (parsed `TimeSpan` durations, long /
bytes values, log levels, raw key access), reach the underlying client:

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
| `duration`    | `GetStringValueAsync`   | ISO 8601 string (`"PT1M30S"`)  |
| `log_level`   | N/A                     | Native SDK only                |

`GetObjectValueAsync` resolves `string_list` first, then falls back to a parsed
`json` value.
