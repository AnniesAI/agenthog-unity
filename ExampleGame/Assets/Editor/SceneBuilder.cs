using System.IO;
using Brightmotion.AgentHog;
using UnityEditor;
using UnityEditor.Events;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

/// <summary>
/// Deterministically (re)generates the example's three scenes, build settings, and the blank
/// AgentHogSettings asset. Editor menu: AgentHog → Rebuild Example Scenes, or headless:
///   Unity -batchmode -projectPath ExampleGame -executeMethod SceneBuilder.BuildAll -quit
/// Scenes are build output, but committed so the example opens ready-to-play.
/// </summary>
public static class SceneBuilder
{
    const string ScenesDir = "Assets/Scenes";

    [MenuItem("AgentHog/Rebuild Example Scenes")]
    public static void BuildAll()
    {
        Directory.CreateDirectory(ScenesDir);
        CreateSettingsAssets();
        BuildMainMenu();
        BuildGame();
        BuildResults();
        EditorBuildSettings.scenes = new[]
        {
            new EditorBuildSettingsScene(ScenesDir + "/MainMenu.unity", true),
            new EditorBuildSettingsScene(ScenesDir + "/Game.unity", true),
            new EditorBuildSettingsScene(ScenesDir + "/Results.unity", true),
        };
        AssetDatabase.SaveAssets();
        Debug.Log("[SceneBuilder] example scenes rebuilt");
    }

    // ---- scenes ----

    static void BuildMainMenu()
    {
        var scene = NewScene();
        var canvas = CreateCanvas();

        CreateText(canvas.transform, "Title", "AgentHog Example", 64, new Vector2(0, 760), new Vector2(900, 120));
        var status = CreateText(canvas.transform, "StatusText", "…", 30, new Vector2(0, -820), new Vector2(1000, 80));

        var play = CreateButton(canvas.transform, "PlayButton", "Play", new Vector2(0, 250), new Vector2(520, 140));
        var settings = CreateButton(canvas.transform, "SettingsButton", "Settings", new Vector2(0, 60), new Vector2(520, 140));
        var login = CreateButton(canvas.transform, "LoginButton", "Log in", new Vector2(-140, -130), new Vector2(240, 110));
        var logout = CreateButton(canvas.transform, "LogoutButton", "Log out", new Vector2(140, -130), new Vector2(240, 110));
        // deliberately label-less: click autocapture falls back to the GameObject name
        var referrer = CreateButton(canvas.transform, "SimulateInstallReferrer", null, new Vector2(0, -320), new Vector2(520, 90));

        var panel = new GameObject("SettingsPanel", typeof(Image));
        panel.transform.SetParent(canvas.transform, false);
        var panelRect = (RectTransform)panel.transform;
        panelRect.sizeDelta = new Vector2(760, 420);
        panelRect.anchoredPosition = new Vector2(0, 480);
        panel.GetComponent<Image>().color = new Color(0.12f, 0.12f, 0.16f, 0.95f);
        CreateText(panel.transform, "PanelText", "Settings panel (demo)\nopening this emits\npageview: /main-menu/settings", 34, Vector2.zero, new Vector2(700, 380));
        panel.SetActive(false);

        var controllerGo = new GameObject("MainMenuController");
        var controller = controllerGo.AddComponent<MainMenuController>();
        controller.settingsPanel = panel;
        controller.statusText = status;

        Wire(play, controller.OnPlay);
        Wire(settings, controller.OnToggleSettings);
        Wire(login, controller.OnLogin);
        Wire(logout, controller.OnLogout);
        Wire(referrer, controller.OnSimulateInstallReferrer);

        Save(scene, "MainMenu");
    }

    static void BuildGame()
    {
        var scene = NewScene();
        var canvas = CreateCanvas();

        var score = CreateText(canvas.transform, "ScoreText", "hits 0/5", 40, new Vector2(-320, 880), new Vector2(360, 80));
        var timer = CreateText(canvas.transform, "TimerText", "15.0s", 40, new Vector2(320, 880), new Vector2(360, 80));
        var target = CreateButton(canvas.transform, "Target", "TAP!", Vector2.zero, new Vector2(220, 220));

        var controllerGo = new GameObject("GameController");
        var controller = controllerGo.AddComponent<GameController>();
        controller.target = (RectTransform)target.transform;
        controller.scoreText = score;
        controller.timerText = timer;
        Wire(target, controller.OnTargetHit);

        Save(scene, "Game");
    }

    static void BuildResults()
    {
        var scene = NewScene();
        var canvas = CreateCanvas();

        var result = CreateText(canvas.transform, "ResultText", "…", 64, new Vector2(0, 400), new Vector2(900, 240));
        var retry = CreateButton(canvas.transform, "RetryButton", "Retry", new Vector2(0, 0), new Vector2(520, 140));
        var menu = CreateButton(canvas.transform, "MenuButton", "Main Menu", new Vector2(0, -190), new Vector2(520, 140));

        var controllerGo = new GameObject("ResultsController");
        var controller = controllerGo.AddComponent<ResultsController>();
        controller.resultText = result;
        Wire(retry, controller.OnRetry);
        Wire(menu, controller.OnMenu);

        Save(scene, "Results");
    }

    // ---- settings assets ----

    static void CreateSettingsAssets()
    {
        Directory.CreateDirectory("Assets/Resources");
        if (AssetDatabase.LoadAssetAtPath<AgentHogSettings>("Assets/Resources/AgentHogSettings.asset") == null)
        {
            // committed blank → SDK inert; devs put real host/key in AgentHogSettings.local.asset
            // (gitignored, see repo README) which auto-init prefers
            var blank = ScriptableObject.CreateInstance<AgentHogSettings>();
            blank.debugLog = true;
            AssetDatabase.CreateAsset(blank, "Assets/Resources/AgentHogSettings.asset");
        }
    }

    // ---- uGUI helpers ----

    static Scene NewScene() => EditorSceneManager.NewScene(NewSceneSetup.DefaultGameObjects, NewSceneMode.Single);

    static void Save(Scene scene, string name)
    {
        if (!EditorSceneManager.SaveScene(scene, $"{ScenesDir}/{name}.unity"))
            throw new IOException($"failed to save scene {name}");
    }

    static Canvas CreateCanvas()
    {
        var canvasGo = new GameObject("Canvas", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        var canvas = canvasGo.GetComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGo.GetComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1080, 1920);
        scaler.matchWidthOrHeight = 0.5f;
        new GameObject("EventSystem", typeof(EventSystem), typeof(StandaloneInputModule));
        return canvas;
    }

    static Button CreateButton(Transform parent, string name, string label, Vector2 pos, Vector2 size)
    {
        var go = new GameObject(name, typeof(Image), typeof(Button));
        go.transform.SetParent(parent, false);
        var rect = (RectTransform)go.transform;
        rect.sizeDelta = size;
        rect.anchoredPosition = pos;
        var image = go.GetComponent<Image>();
        image.sprite = AssetDatabase.GetBuiltinExtraResource<Sprite>("UI/Skin/UISprite.psd");
        image.type = Image.Type.Sliced;
        image.color = new Color(0.20f, 0.45f, 0.85f);
        if (label != null)
        {
            var text = CreateText(go.transform, "Text", label, Mathf.RoundToInt(size.y * 0.4f), Vector2.zero, size);
            text.color = Color.white;
        }
        return go.GetComponent<Button>();
    }

    static Text CreateText(Transform parent, string name, string content, int fontSize, Vector2 pos, Vector2 size)
    {
        var go = new GameObject(name, typeof(Text));
        go.transform.SetParent(parent, false);
        var rect = (RectTransform)go.transform;
        rect.sizeDelta = size;
        rect.anchoredPosition = pos;
        var text = go.GetComponent<Text>();
        text.text = content;
        text.font = BuiltinFont();
        text.fontSize = fontSize;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = new Color(0.9f, 0.9f, 0.95f);
        return text;
    }

    static Font BuiltinFont()
    {
        // 2022.2+ renamed the built-in font; support both so the example opens anywhere
        foreach (string name in new[] { "LegacyRuntime.ttf", "Arial.ttf" })
        {
            try
            {
                var font = Resources.GetBuiltinResource<Font>(name);
                if (font != null) return font;
            }
            catch
            {
                // older/newer editor without this builtin — try the next name
            }
        }
        return null;
    }

    static void Wire(Button button, UnityAction handler)
        => UnityEventTools.AddPersistentListener(button.onClick, handler);
}
