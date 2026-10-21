# Changelog

## Unreleased

- **Changed: bump `Quonfig.Sdk` from `1.8.1` to `1.9.0` and map its init-failure
  modes onto provider status (qfg-9p4i.12).** Spec:
  `project/plans/2026-10-05-init-failure-design.md` B.5. **UPGRADE: when
  delivery is unreachable at startup, `InitializeAsync` now retries for
  `InitTimeout` (10 s by default) before it fails, instead of failing within
  milliseconds.** A healthy start is unchanged.
  - **New options `QuonfigProviderOptions.OnInitFailure` and `InitTimeout`.**
    They pass through to the wrapped client. `null` (the default) keeps the SDK
    defaults (`Fail`, 10 s). Setting them through `ConfigureClient` still works.
  - **`Fail` (default): the provider keeps the client.** `InitializeAsync`
    still throws (now `QuonfigInitException`) and the status is `ERROR`. Before,
    the provider dropped the client, so it stayed in `ERROR` forever. Now the
    kept client keeps retrying, and when delivery comes back the provider emits
    `PROVIDER_READY` and the status becomes `READY`. `GetClient()` returns that
    client after a failed init.
  - **`Continue` reports `ERROR`, not `READY`.** `InitializeAsync` returns, then
    the provider emits a non-fatal `PROVIDER_ERROR`, since every evaluation is a
    code default. It emits `PROVIDER_READY` when the client heals.
  - **`Wait`:** `InitializeAsync` blocks until the first config, so the status is
    `NOT_READY` until then, then `READY`.
  - **Not-ready evaluations return `PROVIDER_NOT_READY`.** While the client has
    no config, every evaluation returns your default with error type
    `PROVIDER_NOT_READY` and reason `ERROR`.

## 1.5.1 - 2026-10-20

- Bump the `Quonfig.Sdk` dependency from `1.8.0` to `1.8.1`. `Quonfig.Sdk`
  1.7.0 and 1.8.0 required `System.Collections.Immutable >= 10.0.12` (an
  unintended Dependabot bump), so this provider 1.4.0 and 1.5.0 forced that
  package onto every consumer too. 1.8.1 lowers the requirement back to
  `8.0.0`. No change to this provider's API or behavior.

## 1.5.0 - 2026-10-20

- Bump the `Quonfig.Sdk` dependency from `1.7.0` to `1.8.0`. No change to this
  provider's own public API, but `Quonfig.Sdk` 1.8.0 changes connection timing
  that you can see through this provider, so this is a minor release:
  - **UPGRADE: the stream counts as connected at its first frame, not at the
    200 response (qfg-goi1.10.41, qfg-goi1.10.9).** The SDK's connection state
    (`GetClient().ConnectionState` and `OnConnectionStateChange`), the
    reconnect backoff and the fallback poller now treat a stream as connected
    only once it delivers a config envelope or a keepalive. Against Quonfig's
    servers the two arrive together, so you should see no difference. Behind a
    proxy that sends 200 headers early, `Connected` is reported up to one
    keepalive interval (30 s) later. A stream that closes before any frame is
    now a failed attempt: the reconnect delay grows and the fallback poller
    takes over after its usual 120 s threshold, so `ProviderConfigurationChanged`
    events keep flowing from polled config instead of the provider sitting on
    stale config behind a reconnect loop.
  - **Fixed: the stream reconnect backoff caps at 30 s, jitter included
    (qfg-n3nr).** It capped at 60 s with +/-20% jitter, so after a long stream
    outage the next reconnect could land up to 72 s after the server came back.
  - **Fixed: `GetClient().ConnectionState` no longer reads `Connected` right
    after initialization, before any stream exists.** In HTTP mode with a
    stream configured it now reads `Initializing` until the stream delivers its
    first frame. Datadir and datafile modes still read `Connected` once config
    is installed. `ProviderReady` timing is unchanged: it still fires when the
    initial config is installed.

## 1.4.0 - 2026-10-16

- Bump the `Quonfig.Sdk` dependency from `1.6.0` to `1.7.0`. No change to this
  provider's own public API, but `Quonfig.Sdk` 1.7.0 changes behavior that
  OpenFeature users see through this provider, so this is a minor release:
  - **UPGRADE: rule operators follow the canonical cross-SDK semantics
    (qfg-goi1.5.5).** Pass dates in the evaluation context as epoch
    milliseconds (a number or an all-digit string) or ISO 8601 / RFC3339 with
    `Z` or a numeric offset. Three rules change who matches: a `bool` context
    value now reads `"true"` / `"false"` for string operators such as
    `PROP_IS_ONE_OF` (a `["True"]` workaround list stops matching; make it
    lowercase), `PROP_SEMVER_*` accepts abbreviated versions such as `"4.1"`,
    and `PROP_BEFORE` / `PROP_AFTER` no longer parse date-only, offset-less or
    free-form date strings, so those criteria stop matching. Whole-number
    `double` / `decimal` values read as plain digits (`1.0` -> `"1"`).
  - **UPGRADE: a rejected SDK key slows down instead of polling at the normal
    cadence (qfg-goi1.10.28).** After a fetch cycle where the primary answered
    401, 403, 404 or 410 and no leg returned 200/304, the SDK stops SSE and the
    60 s poller and sends one probe every 150-300 s until the key works again.
    It logs one `Error` on entry (status and host, never the key) instead of
    failing silently.
  - **Fixed: an SSE peer that accepts the connection but never sends response
    headers no longer wedges streaming forever (qfg-goi1.2.37).** Each connect
    now waits at most 90 s for headers, then reconnects with backoff, so
    `ProviderConfigurationChanged` events resume after a hung proxy or load
    balancer.
  - Docs: the SDK README describes the default `quonfig-user.email` dev-context
    injection and how to turn it off (qfg-goi1.2.41).

## 1.3.0 - 2026-10-09

- Bump the `Quonfig.Sdk` dependency from `1.5.0` to `1.6.0` to inherit the SDK
  quality program's Wave 0/1 fixes: bounded JSON telemetry counters, env-scoped
  `decryptWith` key evaluation, idle poller no longer busy-waits, copy-on-return
  getters and a regex match timeout (qfg-goi1.2.2, qfg-goi1.2.14, qfg-goi1.2.15,
  qfg-goi1.1.5, qfg-goi1.2.23, qfg-goi1.2.43). No change to this provider's own
  public API.

## 1.2.1 - 2026-10-02

- Fix: resolving a structure flag that does not exist now returns the default
  with `ErrorType.FlagNotFound` instead of throwing. `ResolveStructureValueAsync`
  calls `GetStringListDetails` / `GetJsonDetails` with no default, and in
  `Quonfig.Sdk` 1.2.0 those threw `QuonfigKeyNotFoundException` under the default
  `OnNoDefault.Throw`. Bumps the `Quonfig.Sdk` dependency from `1.2.0` to `1.5.0`,
  where `Get*Details` never throw (qfg-2agi.12). No change to this provider's own
  public API.

## 1.2.0 - 2026-07-08

- Bump the `Quonfig.Sdk` dependency from `1.1.0` to `1.2.0` to inherit its
  telemetry and configuration work: runtime telemetry is now actually emitted by
  the live client (previously sdk-net constructed no `TelemetryReporter`, so it
  sent zero runtime telemetry — qfg-gxm6), `QUONFIG_BACKEND_SDK_KEY` /
  `QUONFIG_ENVIRONMENT` env-var fallbacks (qfg-2qcq.1), `QUONFIG_DOMAIN`-derived
  API/stream/telemetry URLs with the SSE stream following `ApiUrls`
  (qfg-41nh.27), and the additive `failover` telemetry event (qfg-41nh.18). All
  additive and backward-compatible with no new dependencies. No change to this
  provider's own public API — the behavior rides in via the SDK bump. Coordinated
  1.2.0 version stamp across the Quonfig SDK family.

## 1.1.0 - 2026-07-01

- Bump the `Quonfig.Sdk` dependency from `1.0.0` to `1.1.0` to inherit the
  secondary-delivery failover work: request hedging across primary/secondary
  api-delivery, the reject-older generation guard, and the `gen<=0` carve-out
  that unblocks clients talking to pre-watermark servers (qfg-7h5d). No change
  to this provider's own public API — the failover behavior rides in via the
  SDK bump. Coordinated 1.1.0 version stamp across the Quonfig SDK family.

## 1.0.0 - 2026-06-06

- **Stable 1.0.0 release.** The Quonfig OpenFeature provider for .NET is now declared
  stable and references `Quonfig.Sdk` 1.0.0. No API or behavior changes from 0.0.3 —
  this is a coordinated 1.0.0 version stamp across the entire Quonfig SDK family.

## 0.0.3 - 2026-06-02

- Bump the `Quonfig.Sdk` dependency from `0.0.2` to `0.0.3` to inherit dev-context injection default-on (qfg-bw7g.9, via qfg-bw7g.7). No change to this provider's behavior — dev-context lives below the OpenFeature layer, so OpenFeature users now get `quonfig-user.email` injection by default in local dev (gated on the `qfg login` token file; inert in production).

## 0.0.2 - 2026-05-29

- Bump the bundled `Quonfig.Sdk` dependency from 0.0.1 to 0.0.2 (qfg-z3xp).
  The 0.0.1 SDK predated the delivery-mode per-environment override fix
  (sdk-net 0.0.2, qfg-64m9/qfg-pinh), so in SDK-key/HTTP mode the provider
  resolved the wire payload's base `default` block and silently ignored the
  singular per-`environment` override. 0.0.2 parses the singular `environment`
  block and evaluates against the authoritative `meta.environment`. No provider
  code change — the fix rides in via the SDK bump.

## 0.0.1 - 2026-05-28

- Initial release of the Quonfig OpenFeature provider for .NET (qfg-3e6d).
- `QuonfigProvider : FeatureProvider` wrapping `Quonfig.Sdk` 0.0.1, constructed from a
  single `QuonfigProviderOptions` (SDK-key or datadir mode).
- All five OpenFeature resolve methods (boolean, string, integer, double, structure).
  `string_list` and `json` resolve via the structure method; `long` / `duration` /
  `log_level` remain on the native client via `GetClient()`.
- Forwards Quonfig's `Reason`, `Variant`, `ErrorCode`, and flag metadata onto
  `ResolutionDetails`; errors return the caller default with an `ErrorType` rather than
  throwing. Reason mapping is identical to the openfeature-go / -node / -ruby / -python
  providers.
- Context mapping: OpenFeature flat keys map to Quonfig namespaces by splitting on the
  first dot; `targetingKey` maps via `TargetingKeyMapping` (default `user.id`).
- Emits `PROVIDER_READY`, `PROVIDER_ERROR`, and `PROVIDER_CONFIGURATION_CHANGED`
  (matching openfeature-go). `PROVIDER_STALE` deferred to a follow-up.
