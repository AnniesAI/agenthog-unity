using System;
using System.Collections.Generic;
using System.Globalization;
using Brightmotion.AgentHog.Core;
using UnityEngine;

namespace Brightmotion.AgentHog.Unity
{
    /// <summary>Device/environment facts for session context (plan §4).</summary>
    internal sealed class UnityContextProvider : IContextProvider
    {
        readonly string appName;
        readonly string appVersion;

        public UnityContextProvider(string appName, string appVersion)
        {
            this.appName = appName;
            this.appVersion = appVersion;
        }

        // Application.absoluteURL: deep link on mobile (carries ?utm_... into parseLanding()
        // exactly like web), page URL on WebGL, "" otherwise.
        public string DeepLinkUrl => Application.absoluteURL ?? "";

        public string ScreenSize
        {
            get
            {
                var d = Display.main;
                return d != null ? d.systemWidth + "x" + d.systemHeight
                                 : UnityEngine.Screen.width + "x" + UnityEngine.Screen.height;
            }
        }

        public string ViewportSize => UnityEngine.Screen.width + "x" + UnityEngine.Screen.height;

        public string Timezone
        {
            get
            {
                // IANA on iOS/Android/macOS (Mono/IL2CPP), Windows id on Windows — sent as-is
                try { return TimeZoneInfo.Local.Id; }
                catch (Exception) { return ""; }
            }
        }

        public string Language => LanguageTag(Application.systemLanguage);

        public Dictionary<string, object> AutoRegistered => new Dictionary<string, object>
        {
            { "platform", PlatformName() },
            { "app_version", appVersion },
            { "os_version", SystemInfo.operatingSystem },
            { "device_model", SystemInfo.deviceModel },
            { "engine", "unity " + Application.unityVersion },
        };

        internal static string PlatformName()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.IPhonePlayer: return "ios";
                case RuntimePlatform.Android: return "android";
                case RuntimePlatform.WebGLPlayer: return "webgl";
                case RuntimePlatform.OSXPlayer:
                case RuntimePlatform.WindowsPlayer:
                case RuntimePlatform.LinuxPlayer: return "standalone";
                case RuntimePlatform.OSXEditor:
                case RuntimePlatform.WindowsEditor:
                case RuntimePlatform.LinuxEditor: return "editor";
                default: return Application.platform.ToString().ToLowerInvariant();
            }
        }

        internal static string LanguageTag(SystemLanguage lang)
        {
            switch (lang)
            {
                case SystemLanguage.Afrikaans: return "af";
                case SystemLanguage.Arabic: return "ar";
                case SystemLanguage.Basque: return "eu";
                case SystemLanguage.Belarusian: return "be";
                case SystemLanguage.Bulgarian: return "bg";
                case SystemLanguage.Catalan: return "ca";
                case SystemLanguage.Chinese: return "zh";
                case SystemLanguage.ChineseSimplified: return "zh-Hans";
                case SystemLanguage.ChineseTraditional: return "zh-Hant";
                case SystemLanguage.Czech: return "cs";
                case SystemLanguage.Danish: return "da";
                case SystemLanguage.Dutch: return "nl";
                case SystemLanguage.English: return "en";
                case SystemLanguage.Estonian: return "et";
                case SystemLanguage.Faroese: return "fo";
                case SystemLanguage.Finnish: return "fi";
                case SystemLanguage.French: return "fr";
                case SystemLanguage.German: return "de";
                case SystemLanguage.Greek: return "el";
                case SystemLanguage.Hebrew: return "he";
                case SystemLanguage.Hungarian: return "hu";
                case SystemLanguage.Icelandic: return "is";
                case SystemLanguage.Indonesian: return "id";
                case SystemLanguage.Italian: return "it";
                case SystemLanguage.Japanese: return "ja";
                case SystemLanguage.Korean: return "ko";
                case SystemLanguage.Latvian: return "lv";
                case SystemLanguage.Lithuanian: return "lt";
                case SystemLanguage.Norwegian: return "no";
                case SystemLanguage.Polish: return "pl";
                case SystemLanguage.Portuguese: return "pt";
                case SystemLanguage.Romanian: return "ro";
                case SystemLanguage.Russian: return "ru";
                case SystemLanguage.SerboCroatian: return "sr";
                case SystemLanguage.Slovak: return "sk";
                case SystemLanguage.Slovenian: return "sl";
                case SystemLanguage.Spanish: return "es";
                case SystemLanguage.Swedish: return "sv";
                case SystemLanguage.Thai: return "th";
                case SystemLanguage.Turkish: return "tr";
                case SystemLanguage.Ukrainian: return "uk";
                case SystemLanguage.Vietnamese: return "vi";
                default:
                    // fall back to the .NET culture when the enum has no clean mapping
                    try { return CultureInfo.CurrentCulture.Name; }
                    catch (Exception) { return "en"; }
            }
        }
    }
}
