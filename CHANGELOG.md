# Changelog

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
