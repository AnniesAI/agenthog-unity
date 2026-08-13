# Changelog

## [0.2.0] — 2026-08-12

Automatic install attribution (Android).

- `AgentHogConfig.InstallReferrer` provider hook: the raw Play Install Referrer (plus
  ReferrerDetails timestamps) is read once per install, gated ahead of the install session's
  first flush (1.5 s valve), and ships as `context.install` for server-side classification
  and Meta-envelope decryption. Plaintext referrer `utm_*` params feed the landing URL at
  lower precedence than explicit `SetLandingParams` keys; Meta's encrypted `utm_content`
  stays off the landing URL. The once-per-install flag is written only after a 2xx confirmed
  delivery, and fails closed on storage errors.
- `AgentHog.OnAttribution(...)` + `AgentHog.GetAttribution()`: the server-computed
  attribution result, cached durably, replayed on later launches, delivered on the main
  thread, surviving `Reset()`. A `pending` result (decryption key not yet configured)
  re-asks on later launches until it resolves.
- New optional companion package `com.brightmotion.agenthog.installreferrer` carries the
  Play installreferrer Gradle dependency and the JNI reader; the core package keeps zero
  native dependencies. Installing it is the whole integration: it registers itself as
  `AgentHog.DefaultInstallReferrer` at load, covering the no-code settings-asset flow too.
- Attribution is strictly install-session-scoped: a crash before the first flush carries
  the read referrer with the persisted snapshot (delivered later under the install
  session's original ids), and a read resolving after the session rotated is discarded
  rather than stamped onto a later session.
- `OnAttribution` is safe before `Init` (early registrations queue), and callbacks are
  released as soon as no result can ever arrive (iOS/editor, organic installs).
- Transport callbacks now surface the response body (any 2xx is success, as before).

## [0.1.0] — 2026-08-11

Initial release.

- Sessions with 30-minute idle semantics matching the AgentHog web tracker (restart-survival,
  background rotation, carry-over of unsent batches under their original ids).
- Automatic scene-view `pageview` events + manual `Screen()` for in-scene UI states.
- Per-screen time via `leave` events with `duration_s`; backgrounded time excluded.
- uGUI click autocapture (label from child text → GameObject name; drag-vs-tap at 8dp).
- Behavior telemetry (mouseMoved / anyScroll / firstInteractionMs) for honest bot scoring.
- `Capture` / `Identify` / `Tag` / `Register` / `SetLandingParams` / `Flush` / `Reset`.
- Config via code (`AgentHog.Init`) or `AgentHogSettings` asset with `.local` override.
- Zero dependencies; Unity 2021.3+; legacy and new Input System supported.
