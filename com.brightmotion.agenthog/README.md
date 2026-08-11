# AgentHog Analytics for Unity

Drop-in analytics for Unity games: **sessions, scene views, click autocapture, custom
events, and identity** — posted straight to your [AgentHog](https://github.com/AnniesAI)
project over the standard ingest contract. Pure C#, zero dependencies, no native plugins,
IL2CPP-safe.

- **Unity**: 2021.3 LTS or newer (developed against Unity 6 LTS)
- **Platforms**: iOS and Android are the primary targets; standalone and the editor work
  out of the box. WebGL is expected to work but is not yet a tested target.

## Install

Add to `Packages/manifest.json` (or Package Manager → *Add package from git URL*):

```json
"com.brightmotion.agenthog": "https://github.com/AnniesAI/agenthog-unity.git?path=com.brightmotion.agenthog#v0.1.0"
```

Pin a tag. `#main` floats; game builds shouldn't.

## Quick start

**Option A — settings asset (no code).** Create *Assets → Create → AgentHog → Settings*,
save it as `Assets/Resources/AgentHogSettings.asset`, fill in `host` + `projectKey`. The SDK
initializes itself on startup. Committing a blank asset and keeping your real key in an
uncommitted `Assets/Resources/AgentHogSettingsLocal.asset` (which takes precedence) keeps
keys out of public repos.

**Option B — code.**

```csharp
using Brightmotion.AgentHog;

AgentHog.Init(new AgentHogConfig
{
    Host = "https://your-agenthog-host.example",
    ProjectKey = "ah_xxxxxxxx",
});
```

With no key (or `Enabled = false`) every call is an inert no-op — call sites never need
`if` guards, so leaving analytics off in dev builds is just an empty settings asset.

## What gets tracked automatically

| Signal | How |
|---|---|
| Sessions | 30-min idle timeout, survives app restarts within the window, rotates in background |
| Scene views | `SceneManager.sceneLoaded` → `pageview: /scene-name` |
| Screen time | every scene/screen change and app-background emits `leave: /path` with `duration_s` — backgrounded time never counts |
| UI clicks | taps on uGUI `Selectable`s / `IPointerClickHandler`s → `click: <label>` (label = child text, else GameObject name). Drags are excluded |
| Device context | platform, app version, OS, device model, engine version — registered on every event |
| Offline / crash | unsent events persist and ship on next launch under their original session |

World-space gameplay objects and UI Toolkit are **not** autocaptured — instrument gameplay
with `Capture`, which is the norm for game analytics.

## API

```csharp
AgentHog.Capture("level_complete", new() { ["level"] = 12, ["stars"] = 3 });
AgentHog.Screen("/settings/audio");            // manual screens for in-scene UI states
AgentHog.Identify(traits: new() { ["user_id"] = playerId });  // stitch identity (no email needed)
AgentHog.Tag("ab_test", "variant_b");          // set one trait + emit "tag: ab_test"
AgentHog.Register(new() { ["build_channel"] = "beta" });      // merged into every event
AgentHog.SetLandingParams(new() { ["utm_source"] = "playstore" }); // install attribution — call before first flush
AgentHog.Flush();                              // force-send now
AgentHog.Reset();                              // sign-out: device becomes a new anonymous person
AgentHog.AnonId; AgentHog.SessionId; AgentHog.Enabled;
```

All calls are safe from any thread (cross-thread calls are marshalled to the main thread)
and safe before `Init` (no-ops).

### Config reference

| Field | Default | Notes |
|---|---|---|
| `Host` | — | your AgentHog host, no trailing slash |
| `ProjectKey` | — | `ah_…`; ships in the binary like every analytics SDK key |
| `AppName` / `AppVersion` | product name / `Application.version` | used in the User-Agent + registered props |
| `Enabled` | `true` | `false` → inert no-op SDK |
| `FlushIntervalSeconds` | `10` | foreground send cadence |
| `MaxQueue` | `20` | queue length that forces a send (hard cap 500) |
| `IdleMinutes` | `30` | session idle timeout — must match the server |
| `AutoTrackScenes` | `true` | scene loads → pageviews |
| `AutoCaptureUiClicks` | `true` | uGUI tap autocapture |
| `Debug` | `false` | log sends/drops via `Debug.Log` |

## Event naming (the AgentHog contract)

Scene/screen views go over the wire as `pageview: <path>`, so goals, funnels, and
entry/exit/bounce treat game traffic exactly like web traffic. Custom event names are sent
verbatim — pick stable snake_case names (`level_start`, `iap_purchase`) and keep variable
data in props.

## Privacy

- Identity is a **random UUID** stored in `PlayerPrefs`. No IDFA/GAID, no device
  fingerprinting → no App Tracking Transparency prompt is required for this SDK.
- Click autocapture records UI label text (button captions), never user input.
- `Reset()` severs the device from the previous identity on sign-out.
- Requests carry a `<YourGame>/<version> AgentHogUnity/<sdk> (<platform> <os>)` user agent.

## Verifying the integration

Run the game (editor Play mode counts) with a real key, then watch your AgentHog
dashboard's live view — you should see a session with `pageview: /<your-first-scene>`,
click events as you tap UI, and `human` classification after the sweep. The
[ExampleGame](../ExampleGame) in this repo is a working reference.
