
// Editor-only window that checks GitHub releases for a newer version of
// com.midmanstudio.utilities and notifies the user.
//
// OPEN VIA: MidManStudio > Utilities > Check for Updates
//
// SETUP: Set GITHUB_USER, GITHUB_REPO, and PACKAGE_JSON_PATH below.
// The window also auto-checks once per day using EditorPrefs.

#if UNITY_EDITOR
using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace MidManStudio.Core.EditorTools
{
    public class MID_PackageUpdateChecker : EditorWindow
    {
        // ── Configuration — edit these ────────────────────────────────────────

        private const string GITHUB_USER      = "MidManStudio";
        private const string GITHUB_REPO      = "MidManStudio_Unity";
        private const string PACKAGE_JSON_PATH =
            "packages/com.midmanstudio.utilities/package.json";

        // GitHub API endpoint for latest release
        private const string API_URL =
            "https://api.github.com/repos/{0}/{1}/releases/latest";

        // EditorPrefs keys
        private const string PREF_LAST_CHECK  = "MidManStudio.UpdateChecker.LastCheck";
        private const string PREF_LAST_RESULT = "MidManStudio.UpdateChecker.LastResult";
        private const string PREF_SKIP_TAG    = "MidManStudio.UpdateChecker.SkipTag";

        // How many hours between automatic background checks
        private const double AUTO_CHECK_INTERVAL_HOURS = 24.0;

        // ── State ─────────────────────────────────────────────────────────────

        private enum CheckState { Idle, Checking, UpToDate, UpdateAvailable, Error }

        private CheckState  _state      = CheckState.Idle;
        private string      _localVer   = "unknown";
        private string      _remoteVer  = string.Empty;
        private string      _releaseUrl = string.Empty;
        private string      _releaseNotes = string.Empty;
        private string      _errorMsg   = string.Empty;
        private bool        _autoChecked;
        private Vector2     _notesScroll;

        // ── Colours ───────────────────────────────────────────────────────────

        private static readonly Color ColGreen  = new Color(0.28f, 0.95f, 0.45f, 1f);
        private static readonly Color ColOrange = new Color(1.00f, 0.70f, 0.25f, 1f);
        private static readonly Color ColRed    = new Color(1.00f, 0.40f, 0.40f, 1f);
        private static readonly Color ColDim    = new Color(0.55f, 0.55f, 0.55f, 1f);

        // ── Menu & lifecycle ──────────────────────────────────────────────────

        [MenuItem("MidManStudio/Utilities/Check for Updates", priority = 200)]
        public static void Open()
        {
            var w = GetWindow<MID_PackageUpdateChecker>("Update Checker");
            w.minSize = new Vector2(420, 340);
            w.Show();
        }

        /// <summary>
        /// Called once per Unity Editor session via InitializeOnLoad.
        /// Performs a silent background check if enough time has elapsed.
        /// </summary>
        public static void AutoCheck()
        {
            string lastCheckStr = EditorPrefs.GetString(PREF_LAST_CHECK, string.Empty);
            if (!string.IsNullOrEmpty(lastCheckStr))
            {
                if (DateTime.TryParse(lastCheckStr, out DateTime lastCheck))
                {
                    double hoursSince = (DateTime.UtcNow - lastCheck).TotalHours;
                    if (hoursSince < AUTO_CHECK_INTERVAL_HOURS)
                        return; // Too soon — skip
                }
            }

            // Silent check — only open the window if an update is found
            _ = SilentCheckAsync();
        }

        private void OnEnable()
        {
            _localVer = ReadLocalVersion();

            // Restore last cached result so the window shows something immediately
            string cached = EditorPrefs.GetString(PREF_LAST_RESULT, string.Empty);
            if (!string.IsNullOrEmpty(cached))
                ParseCachedResult(cached);

            // Auto-check once when window is first opened this session
            if (!_autoChecked)
            {
                _autoChecked = true;
                _ = RunCheckAsync(silent: false);
            }
        }

        // ── GUI ───────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            EditorGUILayout.Space(8);

            // Header
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("MidMan Studio — Package Update Checker",
                    EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
            }

            EditorGUILayout.Space(4);

            // Package info row
            using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
            {
                var old = GUI.color;
                GUI.color = ColDim;
                EditorGUILayout.LabelField("Package:", EditorStyles.miniLabel,
                    GUILayout.Width(55));
                GUI.color = old;
                EditorGUILayout.LabelField("com.midmanstudio.utilities",
                    EditorStyles.miniLabel);
                GUILayout.FlexibleSpace();
                GUI.color = ColDim;
                EditorGUILayout.LabelField("Installed:", EditorStyles.miniLabel,
                    GUILayout.Width(54));
                GUI.color = Color.white;
                EditorGUILayout.LabelField(_localVer, EditorStyles.miniBoldLabel,
                    GUILayout.Width(60));
                GUI.color = old;
            }

            EditorGUILayout.Space(6);
            DrawSeparator();

            // Status section
            DrawStatusSection();

            DrawSeparator();

            // Action buttons
            DrawButtons();

            EditorGUILayout.Space(4);

            // Last check time
            string lastCheck = EditorPrefs.GetString(PREF_LAST_CHECK, string.Empty);
            if (!string.IsNullOrEmpty(lastCheck) &&
                DateTime.TryParse(lastCheck, out DateTime lastCheckDt))
            {
                var old = GUI.color;
                GUI.color = ColDim;
                EditorGUILayout.LabelField(
                    $"Last checked: {lastCheckDt.ToLocalTime():yyyy-MM-dd HH:mm}  " +
                    $"(auto-checks every {AUTO_CHECK_INTERVAL_HOURS:F0}h)",
                    EditorStyles.miniLabel);
                GUI.color = old;
            }
        }

        private void DrawStatusSection()
        {
            EditorGUILayout.Space(6);

            switch (_state)
            {
                case CheckState.Idle:
                    EditorGUILayout.HelpBox("Press 'Check Now' to look for updates.",
                        MessageType.Info);
                    break;

                case CheckState.Checking:
                    // Simple animated dots using EditorApplication.timeSinceStartup
                    int dots  = (int)(EditorApplication.timeSinceStartup * 2) % 4;
                    string d  = new string('.', dots);
                    EditorGUILayout.HelpBox($"Checking for updates{d}", MessageType.Info);
                    Repaint(); // keep animating
                    break;

                case CheckState.UpToDate:
                    var old1 = GUI.color;
                    GUI.color = ColGreen;
                    EditorGUILayout.LabelField(
                        $"✓  You are up to date!  (v{_localVer})",
                        EditorStyles.boldLabel);
                    GUI.color = old1;
                    EditorGUILayout.HelpBox(
                        $"Latest release: v{_remoteVer}\nNo update required.",
                        MessageType.Info);
                    break;

                case CheckState.UpdateAvailable:
                    var old2 = GUI.color;
                    GUI.color = ColOrange;
                    EditorGUILayout.LabelField(
                        $"⬆  Update available: v{_remoteVer}",
                        EditorStyles.boldLabel);
                    GUI.color = old2;

                    EditorGUILayout.HelpBox(
                        $"Installed: v{_localVer}   →   Latest: v{_remoteVer}\n\n" +
                        "To update, replace the package git URL in Package Manager with:\n" +
                        $"https://github.com/{GITHUB_USER}/{GITHUB_REPO}.git" +
                        $"?path=/packages/com.midmanstudio.utilities#v{_remoteVer}",
                        MessageType.Warning);

                    if (!string.IsNullOrEmpty(_releaseNotes))
                    {
                        EditorGUILayout.Space(4);
                        EditorGUILayout.LabelField("Release Notes:", EditorStyles.boldLabel);
                        _notesScroll = EditorGUILayout.BeginScrollView(
                            _notesScroll, GUILayout.MaxHeight(120));
                        EditorGUILayout.LabelField(
                            _releaseNotes, EditorStyles.wordWrappedMiniLabel);
                        EditorGUILayout.EndScrollView();
                    }
                    break;

                case CheckState.Error:
                    var old3 = GUI.color;
                    GUI.color = ColRed;
                    EditorGUILayout.LabelField("✗  Check failed", EditorStyles.boldLabel);
                    GUI.color = old3;
                    EditorGUILayout.HelpBox(
                        $"Could not reach GitHub API.\n{_errorMsg}\n\n" +
                        "Check your internet connection or visit GitHub manually.",
                        MessageType.Error);
                    break;
            }

            EditorGUILayout.Space(6);
        }

        private void DrawButtons()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                bool busy = _state == CheckState.Checking;
                GUI.enabled = !busy;

                var old = GUI.backgroundColor;
                GUI.backgroundColor = new Color(0.3f, 0.85f, 0.3f);
                if (GUILayout.Button("⟳  Check Now", GUILayout.Height(30)))
                    _ = RunCheckAsync(silent: false);
                GUI.backgroundColor = old;

                if (_state == CheckState.UpdateAvailable && !string.IsNullOrEmpty(_releaseUrl))
                {
                    GUI.backgroundColor = new Color(0.4f, 0.6f, 1f);
                    if (GUILayout.Button("Open Release Page", GUILayout.Height(30)))
                        Application.OpenURL(_releaseUrl);
                    GUI.backgroundColor = old;
                }

                // Skip this version button
                if (_state == CheckState.UpdateAvailable &&
                    !string.IsNullOrEmpty(_remoteVer))
                {
                    GUI.backgroundColor = new Color(0.6f, 0.6f, 0.6f);
                    if (GUILayout.Button("Skip This Version", GUILayout.Height(30)))
                    {
                        EditorPrefs.SetString(PREF_SKIP_TAG, _remoteVer);
                        _state = CheckState.UpToDate;
                        Repaint();
                    }
                    GUI.backgroundColor = old;
                }

                GUI.enabled = true;
            }
        }

        // ── Check logic ───────────────────────────────────────────────────────

        private async Task RunCheckAsync(bool silent)
        {
            _state    = CheckState.Checking;
            _errorMsg = string.Empty;
            if (!silent) Repaint();

            try
            {
                string url      = string.Format(API_URL, GITHUB_USER, GITHUB_REPO);
                string jsonBody = await FetchAsync(url);

                if (string.IsNullOrEmpty(jsonBody))
                {
                    _state    = CheckState.Error;
                    _errorMsg = "Empty response from GitHub API.";
                    Repaint();
                    return;
                }

                ParseGitHubResponse(jsonBody);

                // Cache result and timestamp
                EditorPrefs.SetString(PREF_LAST_CHECK,
                    DateTime.UtcNow.ToString("o"));
                EditorPrefs.SetString(PREF_LAST_RESULT,
                    $"{_remoteVer}|{_releaseUrl}");
            }
            catch (Exception ex)
            {
                _state    = CheckState.Error;
                _errorMsg = ex.Message;
            }

            if (!silent) Repaint();

            // If running silently and there IS an update, open the window
            if (silent && _state == CheckState.UpdateAvailable)
            {
                string skipTag = EditorPrefs.GetString(PREF_SKIP_TAG, string.Empty);
                if (skipTag != _remoteVer)
                    Open();
            }
        }

        private static async Task SilentCheckAsync()
        {
            var checker = CreateInstance<MID_PackageUpdateChecker>();
            checker._localVer = ReadLocalVersion();
            await checker.RunCheckAsync(silent: true);
            DestroyImmediate(checker);
        }

        private void ParseGitHubResponse(string json)
        {
            // Manual JSON parsing — no Newtonsoft dependency.
            // GitHub's latest release response includes: tag_name, html_url, body

            _remoteVer    = ExtractJsonString(json, "tag_name")
                .TrimStart('v');  // strip leading 'v' from tag e.g. "v1.2.0" → "1.2.0"
            _releaseUrl   = ExtractJsonString(json, "html_url");
            _releaseNotes = ExtractJsonString(json, "body")
                .Replace("\\r\\n", "\n")
                .Replace("\\n",    "\n");

            string skipTag = EditorPrefs.GetString(PREF_SKIP_TAG, string.Empty);

            if (string.IsNullOrEmpty(_remoteVer))
            {
                _state    = CheckState.Error;
                _errorMsg = "Could not parse version tag from GitHub response.";
                return;
            }

            if (skipTag == _remoteVer)
            {
                _state = CheckState.UpToDate;
                return;
            }

            _state = CompareVersions(_localVer, _remoteVer) < 0
                ? CheckState.UpdateAvailable
                : CheckState.UpToDate;
        }

        private void ParseCachedResult(string cached)
        {
            // Cached format: "remoteVer|releaseUrl"
            var parts = cached.Split('|');
            if (parts.Length < 1) return;

            _remoteVer  = parts[0];
            _releaseUrl = parts.Length > 1 ? parts[1] : string.Empty;

            string skipTag = EditorPrefs.GetString(PREF_SKIP_TAG, string.Empty);
            if (skipTag == _remoteVer) return;

            _state = CompareVersions(_localVer, _remoteVer) < 0
                ? CheckState.UpdateAvailable
                : CheckState.UpToDate;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static async Task<string> FetchAsync(string url)
        {
            using var req = UnityWebRequest.Get(url);
            // GitHub API requires a User-Agent header
            req.SetRequestHeader("User-Agent",
                $"MidManStudio-UpdateChecker/{ReadLocalVersion()}");
            req.SetRequestHeader("Accept",
                "application/vnd.github.v3+json");

            var op = req.SendWebRequest();

            // Poll until done — we can't await UnityWebRequestAsyncOperation directly
            // without a helper, so we spin yield with Task.Delay
            while (!op.isDone)
                await Task.Delay(50);

            if (req.result != UnityWebRequest.Result.Success)
                throw new Exception(req.error);

            return req.downloadHandler.text;
        }

        private static string ReadLocalVersion()
        {
            try
            {
                string fullPath = System.IO.Path.GetFullPath(PACKAGE_JSON_PATH);
                if (!System.IO.File.Exists(fullPath)) return "0.0.0";
                string json = System.IO.File.ReadAllText(fullPath);
                return ExtractJsonString(json, "version");
            }
            catch
            {
                return "0.0.0";
            }
        }

        /// <summary>Extracts the string value of a JSON key using simple string search.</summary>
        private static string ExtractJsonString(string json, string key)
        {
            string search = $"\"{key}\"";
            int keyIdx    = json.IndexOf(search, StringComparison.Ordinal);
            if (keyIdx < 0) return string.Empty;

            int colonIdx = json.IndexOf(':', keyIdx + search.Length);
            if (colonIdx < 0) return string.Empty;

            int start = json.IndexOf('"', colonIdx + 1);
            if (start < 0) return string.Empty;

            int end = json.IndexOf('"', start + 1);
            // Handle escaped quotes inside the value
            while (end > 0 && json[end - 1] == '\\')
                end = json.IndexOf('"', end + 1);

            if (end < 0) return string.Empty;
            return json.Substring(start + 1, end - start - 1);
        }

        /// <summary>
        /// Compares two semantic version strings.
        /// Returns negative if a &lt; b, 0 if equal, positive if a &gt; b.
        /// </summary>
        private static int CompareVersions(string a, string b)
        {
            Version va = TryParseVersion(a);
            Version vb = TryParseVersion(b);
            return va.CompareTo(vb);
        }

        private static Version TryParseVersion(string raw)
        {
            raw = raw.Trim().TrimStart('v');
            return Version.TryParse(raw, out Version v) ? v : new Version(0, 0, 0);
        }

        private static void DrawSeparator()
        {
            var r = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(r, new Color(0.5f, 0.5f, 0.5f, 0.4f));
            EditorGUILayout.Space(4);
        }
    }

    // ── Auto-check on editor startup ─────────────────────────────────────────

    [InitializeOnLoad]
    public static class MID_UpdateCheckerAutoRun
    {
        static MID_UpdateCheckerAutoRun()
        {
            // Delay the check slightly so it doesn't slow down editor startup
            EditorApplication.delayCall += MID_PackageUpdateChecker.AutoCheck;
        }
    }
}
#endif
