# Changelog

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
