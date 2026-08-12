# AgentHog Install Referrer (Android)

Optional companion to [`com.brightmotion.agenthog`](../com.brightmotion.agenthog) that reads
the **Play Install Referrer** on first launch, giving Android games automatic install
attribution — including Meta (Facebook/Instagram) app-install campaigns, whose encrypted
referrer payload the AgentHog **server** decrypts with the key you paste into project
settings once. The core package stays pure C# with zero native dependencies; this package
exists so only games that want attribution carry the Android Gradle dependency.

Games essentially never deep link, so on Android the install referrer is usually the *only*
attribution signal a game has.

## Install

Both packages, in `Packages/manifest.json` (pin the same tag):

```json
"com.brightmotion.agenthog": "https://github.com/AnniesAI/agenthog-unity.git?path=com.brightmotion.agenthog#v0.2.0",
"com.brightmotion.agenthog.installreferrer": "https://github.com/AnniesAI/agenthog-unity.git?path=com.brightmotion.agenthog.installreferrer#v0.2.0"
```

### The Gradle dependency

The reader talks to the Play Store through Google's
`com.android.installreferrer:installreferrer:2.2` AIDL client (a plain Maven artifact — this
package bundles no `.aar` of its own). One of:

- **External Dependency Manager for Unity (EDM4U)** — already handled: this package ships an
  `Editor/AgentHogInstallReferrerDependencies.xml` that EDM4U picks up and resolves.
- **Manual** — enable *Player Settings → Publishing Settings → Custom Main Gradle Template*
  and add to the `dependencies` block of `Assets/Plugins/Android/mainTemplate.gradle`:

  ```
  implementation 'com.android.installreferrer:installreferrer:2.2'
  ```

## Use

```csharp
using Brightmotion.AgentHog;
using Brightmotion.AgentHog.InstallReferrer;

var config = new AgentHogConfig
{
    Host = "https://your-agenthog-host.example",
    ProjectKey = "ah_xxxxxxxx",
};
PlayInstallReferrer.Attach(config);   // before Init
AgentHog.Init(config);

AgentHog.OnAttribution(a =>           // optional: the server-computed result, cached forever
    Debug.Log($"install source: {a.Source}"));
```

That's all. On the install session's first launch the SDK reads the referrer once (holding
the first batch for at most 1.5 s — the read is local IPC and typically resolves in
milliseconds), sends the raw string to your AgentHog server for classification/decryption,
and feeds any plaintext `utm_*` params into the session's landing URL. Editor, iOS, and
standalone builds resolve "no referrer" and nothing is sent. See the
[core package README](../com.brightmotion.agenthog/README.md#install-attribution-android)
for the full attribution API and semantics.

For Meta campaigns, paste the game's **Install Referrer Decryption Key** (Meta App
Dashboard → Settings → Basic → Android) into the AgentHog project settings. Installs that
arrive before the key is configured are stored raw and backfilled server-side; the SDK
re-asks on later launches until the result resolves.

## Privacy

Same posture as the core SDK: the referrer path needs **no device identifiers** — no
GAID/IDFA, no App Tracking Transparency prompt. The referrer string is delivered by the
Play Store itself, and Meta's decryption key never touches the SDK or this repo.

## Manual test checklist (device)

JNI against the Play Store can only be verified on a real Android device with the Play
Store present ([internal testing](https://play.google.com/console) track, or any
Play-delivered build):

1. Click a Play Store link carrying a referrer before installing, e.g.
   `https://play.google.com/store/apps/details?id=<package>&referrer=utm_source%3Dtest%26utm_campaign%3Dchecklist`.
2. Install from that store page, launch with `Debug = true`, and watch logcat for
   `install referrer read` followed by a `send` — the batch must carry `context.install`.
3. Verify in AgentHog: the session's source shows `utm_source=test`, and
   `AgentHog.GetAttribution()` returns `play_referrer` with those UTMs.
4. Relaunch: no second read (`agh_ref` flag), attribution replays from cache.
5. Sideload the same build (adb install): first launch reads the stock
   `utm_source=google-play&utm_medium=organic` referrer or none; session classifies
   `organic`/`none`, nothing crashes.
6. Uninstall/reinstall with airplane mode on during first launch, then go online and
   relaunch: the install batch delivers on the retry and only then sets the once-per-install
   flag.
7. IL2CPP release build (minify on): repeat step 2 — the `[Preserve]`d listener must survive
   code stripping.
