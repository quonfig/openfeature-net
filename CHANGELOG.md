# Changelog

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
