# Changelog

## [0.4.0] — 2026-08-26

Attribution attach — `AgentHog.SetAttribution(AhAttribution)`, the Unity half of agent-hog's
`context.attribution` wire field (agent-hog `docs/ATTRIBUTION_ATTACH_PLAN.md`): hand the SDK
a third-party attribution verdict (an MMP result, e.g. Singular's device attribution
callback) and it stamps the session's `utm_*` columns server-side under the wire-format
precedence (deep-link > attach > install-referrer).

- Callable at any time — even before `Init` (early attaches queue and deliver at Init,
  like `OnAttribution`): rides the first batch when called pre-flush, otherwise marks
  context pending so the next flush on the normal cadence carries it — no forced flush.
  A verdict arriving after an idle gap rotates the session first and stamps the fresh one.
- Delivered once per distinct payload, confirmed end-to-end: the payload persists across
  crashes and offline launches until a carrying batch gets a 2xx; repeating a delivered
  payload is a no-op, a different payload (re-engagement) is a new delivery. The SDK keeps
  a bounded set of delivered hashes, so an MMP replaying its cached install verdict on
  later launches stays suppressed even after re-engagement verdicts rotate through.
- `Reset()` clears the pending payload and the delivered marker. `SetLandingParams` is
  unchanged and stays the deep-link-params hook.
- Golden fixture regenerated to carry `context.attribution` (values mirror agent-hog's
  repo-local `attribution-golden-batch.json` for the cross-repo contract check).

## [0.3.1] — 2026-08-24

- Fix: `AgentHog.SdkVersion` had been stuck at `0.2.0` since the 0.3.0 release, so the
  User-Agent misreported the SDK version; it now matches the package version.
- Docs: "In-game economy" README section — the `currency`/`amount`/`balance` event
  convention and `ah economy map`, per hog.brightmotion.io/docs/economy. ExampleGame now
  grants an aggregated `level_reward` on wins as a working reference. No SDK code changes —
  economy events are plain `Capture` calls.

## [0.3.0] — 2026-08-17

Feature flags & experiments (agent-hog `docs/EXPERIMENTS_PLAN.md`; bucketing spec + canonical
vectors in agent-hog `CONTRACTS.md` — `FlagsTests` pins them against the web reference).

- `AgentHog.Flag(key)` → assigned variant (or null = your code default), `FlagOn(key)` for
  boolean flags. Deterministic FNV-1a bucketing per player, evaluated locally — no per-read
  network round trip, same variant across sessions and offline play.
- Ruleset from `GET /sdk/flags`, loaded lazily (first `Flag()`/`FlagsReady()` call) and cached
  in PlayerPrefs for flicker-free relaunches; refreshed when an ingest response's
  `x-agh-flags-rev` header moves. Games that never use flags generate zero flag traffic.
- Automatic exposure: first read per flag per session emits one `$exposure` event and stamps
  `$ff/<key>` onto every subsequent event — `ah funnel <name> --by flag:<key>` just works.
- `AgentHog.FlagsReady(cb)` to gate the first read; `OverrideFlag(key, variant)` for QA
  (persisted, wins even before the ruleset loads, never emits exposure data).
- Internal: `ITransport` grew `Fetch` and the `Send` callback now carries the flags revision
  alongside the response body.

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
