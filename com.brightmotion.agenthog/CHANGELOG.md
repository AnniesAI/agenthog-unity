# Changelog

## [0.2.0] — 2026-08-14

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
- Internal: `ITransport` grew `Fetch` and the `Send` callback now carries the flags revision.

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
