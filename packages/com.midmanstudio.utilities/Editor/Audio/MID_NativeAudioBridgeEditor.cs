// MID_NativeAudioBridgeEditor.cs
// Custom inspector for MID_NativeAudioBridge.
// Shows: platform mode, bank slot status, live voice count, per-clip test buttons.

#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using MidManStudio.Core.Audio;

namespace MidManStudio.Core.EditorUtils.Audio
{
    [CustomEditor(typeof(MID_NativeAudioBridge))]
    public class MID_NativeAudioBridgeEditor : UnityEditor.Editor
    {
        // ── Colours ───────────────────────────────────────────────────────────
        private static readonly Color ColGreen  = new Color(0.28f, 0.90f, 0.45f, 1f);
        private static readonly Color ColRed    = new Color(1.00f, 0.35f, 0.35f, 1f);
        private static readonly Color ColYellow = new Color(1.00f, 0.85f, 0.25f, 1f);
        private static readonly Color ColBlue   = new Color(0.40f, 0.65f, 1.00f, 1f);
        private static readonly Color ColDim    = new Color(0.55f, 0.55f, 0.55f, 1f);
        private static readonly Color ColBarBg  = new Color(0.12f, 0.12f, 0.12f, 0.8f);

        // Bench context from Criterion results (used in perf summary display)
        private const float BENCH_SCHEDULE_NS     = 50f;       // sub-criterion floor, ~10-50 ns estimated
        private const float BENCH_16V_512S_MS     = 0.0135f;   // 16 voices, 512 samples
        private const float BENCH_FULL_CYCLE_1_MS = 0.0046f;   // 1-impact full cycle
        private const float BENCH_FULL_CYCLE_16_MS= 0.0136f;   // 16-impact full cycle

        private bool _fPlatform  = true;
        private bool _fBank      = true;
        private bool _fLive      = true;
        private bool _fPerf      = false;
        private bool _fSetup     = false;

        public override void OnInspectorGUI()
        {
            // Always draw default fields first
            DrawDefaultInspector();

            var bridge = (MID_NativeAudioBridge)target;
            var so = serializedObject;
            so.Update();

            EditorGUILayout.Space(6);
            DrawLine();

            // ── Platform indicator ────────────────────────────────────────────
            _fPlatform = EditorGUILayout.BeginFoldoutHeaderGroup(_fPlatform, "Platform & Status");
            if (_fPlatform)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    bool isNative = bridge.IsUsingNativeDSP;
                    ColorLabel(
                        isNative ? "⚙  Native DSP Active  (Rust mid_audio DLL)" :
                                   "🌐  WebGL Managed Pool  (AudioSource fallback)",
                        isNative ? ColGreen : ColYellow);

                    if (!isNative)
                    {
                        EditorGUILayout.HelpBox(
                            "WebGL platform detected. The Rust DLL is not loaded.\n" +
                            "PlayClip() uses the managed AudioSource pool instead.\n" +
                            "Voice count and scheduling are equivalent; no limiter on WebGL path.",
                            MessageType.Info);
                    }

                    EditorGUILayout.Space(2);

                    // AudioSource check
                    var src = bridge.GetComponent<AudioSource>();
                    bool srcOk = src != null;
                    bool srcPlaying = srcOk && src.isPlaying;

                    using (new EditorGUILayout.HorizontalScope())
                    {
                        ColorLabel("AudioSource:", ColDim);
                        if (!srcOk)         ColorLabel("✗ MISSING — add an AudioSource to this GameObject!", ColRed);
                        else if (!srcPlaying && Application.isPlaying && isNative)
                                            ColorLabel("⚠ Not Playing — OnAudioFilterRead will not fire", ColYellow);
                        else if (Application.isPlaying)
                                            ColorLabel("✓ Playing", ColGreen);
                        else                ColorLabel("(check in Play Mode)", ColDim);
                    }

                    // AudioSource config hints
                    if (srcOk && !Application.isPlaying)
                    {
                        bool clipSet = src.clip != null;
                        bool looping = src.loop;
                        if (clipSet || !looping)
                        {
                            EditorGUILayout.HelpBox(
                                "AudioSource should have: clip = None, loop = true, " +
                                "Play On Awake = false. The Bridge sets these in Awake().",
                                MessageType.Warning);
                        }
                    }
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // ── Bank slots ────────────────────────────────────────────────────
            _fBank = EditorGUILayout.BeginFoldoutHeaderGroup(_fBank, "PCM Bank Slots");
            if (_fBank)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    var clipsProperty = so.FindProperty("_clips");
                    if (clipsProperty == null || clipsProperty.arraySize == 0)
                    {
                        EditorGUILayout.HelpBox("No clips assigned. Drag AudioClips into the _clips array above.", MessageType.Info);
                    }
                    else
                    {
                        // Header
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            var old = GUI.color; GUI.color = ColDim;
                            EditorGUILayout.LabelField("Slot", GUILayout.Width(36));
                            EditorGUILayout.LabelField("Clip Name", GUILayout.Width(160));
                            EditorGUILayout.LabelField("Samples", GUILayout.Width(80));
                            EditorGUILayout.LabelField("Load Type", GUILayout.Width(130));
                            EditorGUILayout.LabelField("Status", GUILayout.Width(80));
                            GUI.color = old;
                        }
                        DrawLine();

                        for (int i = 0; i < clipsProperty.arraySize; i++)
                        {
                            var clipElement = clipsProperty.GetArrayElementAtIndex(i);
                            var clip = clipElement.objectReferenceValue as AudioClip;

                            using (new EditorGUILayout.HorizontalScope())
                            {
                                var old = GUI.color;

                                // Slot index
                                GUI.color = ColBlue;
                                EditorGUILayout.LabelField($"[{i}]", GUILayout.Width(36));

                                // Clip name
                                GUI.color = clip != null ? ColGreen : ColRed;
                                EditorGUILayout.LabelField(
                                    clip != null ? clip.name : "— null —",
                                    GUILayout.Width(160));

                                // Sample count
                                GUI.color = ColDim;
                                if (clip != null)
                                {
                                    EditorGUILayout.LabelField(
                                        $"{clip.samples * clip.channels:N0}",
                                        GUILayout.Width(80));

                                    // Load type warning
                                    bool decompressed = clip.loadType == AudioClipLoadType.DecompressOnLoad;
                                    GUI.color = decompressed ? ColGreen : ColYellow;
                                    EditorGUILayout.LabelField(
                                        decompressed ? "Decompress ✓" : "⚠ Not Decompress",
                                        GUILayout.Width(130));

                                    // Upload status (only meaningful at runtime)
                                    if (Application.isPlaying && bridge.IsUsingNativeDSP)
                                    {
                                        GUI.color = ColGreen;
                                        EditorGUILayout.LabelField("Uploaded", GUILayout.Width(80));
                                    }
                                    else if (Application.isPlaying)
                                    {
                                        GUI.color = ColBlue;
                                        EditorGUILayout.LabelField("In Pool", GUILayout.Width(80));
                                    }
                                    else
                                    {
                                        GUI.color = ColDim;
                                        EditorGUILayout.LabelField("(Play Mode)", GUILayout.Width(80));
                                    }

                                    // Test button
                                    GUI.color = old;
                                    GUI.enabled = Application.isPlaying;
                                    if (GUILayout.Button("▶", GUILayout.Width(24)))
                                        bridge.PlayClip(i, 0.8f);
                                    GUI.enabled = true;
                                }
                                else
                                {
                                    EditorGUILayout.LabelField("—", GUILayout.Width(80));
                                    EditorGUILayout.LabelField("—", GUILayout.Width(130));
                                    EditorGUILayout.LabelField("—", GUILayout.Width(80));
                                }

                                GUI.color = old;
                            }
                        }

                        // Global test actions
                        EditorGUILayout.Space(4);
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUI.enabled = Application.isPlaying;
                            var oldBg = GUI.backgroundColor;

                            GUI.backgroundColor = new Color(0.25f, 0.55f, 1f);
                            if (GUILayout.Button("▶▶ Play All Clips", GUILayout.Height(24)))
                            {
                                if (bridge._clips != null)
                                    for (int i = 0; i < bridge._clips.Length; i++)
                                        bridge.PlayClip(i, 0.8f);
                            }

                            GUI.backgroundColor = new Color(0.85f, 0.25f, 0.25f);
                            if (GUILayout.Button("■ Reset Voices", GUILayout.Height(24)))
                                bridge.ResetAllVoices();

                            GUI.backgroundColor = oldBg;
                            GUI.enabled = true;
                        }
                    }
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // ── Live voice monitor ────────────────────────────────────────────
            if (Application.isPlaying)
            {
                _fLive = EditorGUILayout.BeginFoldoutHeaderGroup(_fLive, "Live Voice Monitor");
                if (_fLive)
                {
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        int voices = bridge.ActiveVoiceCount;
                        float ratio = Mathf.Clamp01(voices / 16f);

                        // Voice count bar
                        EditorGUILayout.LabelField($"Active Voices: {voices} / 16",
                            EditorStyles.miniBoldLabel);

                        Rect barRect = EditorGUILayout.GetControlRect(false, 14);
                        barRect.x += 4; barRect.width -= 8; barRect.height = 10;
                        EditorGUI.DrawRect(barRect, ColBarBg);

                        if (ratio > 0f)
                        {
                            Rect fill = barRect;
                            fill.width *= ratio;
                            Color voiceColor = ratio > 0.875f ? ColRed
                                             : ratio > 0.5f   ? ColYellow
                                             : ColGreen;
                            EditorGUI.DrawRect(fill, voiceColor);
                        }

                        EditorGUILayout.Space(2);

                        if (voices >= 16)
                        {
                            var old = GUI.color; GUI.color = ColYellow;
                            EditorGUILayout.LabelField(
                                "Pool full — next PlayClip() triggers voice stealing.",
                                EditorStyles.wordWrappedMiniLabel);
                            GUI.color = old;
                        }

                        // Platform note
                        EditorGUILayout.Space(2);
                        var oldc = GUI.color; GUI.color = ColDim;
                        EditorGUILayout.LabelField(
                            bridge.IsUsingNativeDSP
                                ? "Rust DSP: voice count via atomic read from mid_audio."
                                : "WebGL: voice count via AudioSource.isPlaying scan.",
                            EditorStyles.wordWrappedMiniLabel);
                        GUI.color = oldc;
                    }
                }
                EditorGUILayout.EndFoldoutHeaderGroup();
            }

            // ── Perf context ──────────────────────────────────────────────────
            _fPerf = EditorGUILayout.BeginFoldoutHeaderGroup(_fPerf, "Benchmark Context (Criterion results)");
            if (_fPerf)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    PerfRow("schedule_voice",          $"~{BENCH_SCHEDULE_NS:F0} ns",   ColGreen,
                        "Below Criterion resolution — effectively free (atomic writes only)");
                    PerfRow("process_buffer  0 voices, 512 samp", $"~0.004 ms",         ColGreen,
                        "Limiter recovery only — 0.019% of 21ms audio frame");
                    PerfRow("process_buffer 16 voices, 512 samp", $"{BENCH_16V_512S_MS:F4} ms", ColBlue,
                        $"0.064% of 21ms frame — essentially free");
                    PerfRow("Full cycle  1 impact  (schedule+mix)", $"{BENCH_FULL_CYCLE_1_MS:F4} ms", ColBlue,
                        "Per-impact cost when audio frame fires simultaneously");
                    PerfRow("Full cycle 16 impacts (schedule+mix)", $"{BENCH_FULL_CYCLE_16_MS:F4} ms", ColBlue,
                        "16 simultaneous hits + buffer mix = 0.065% of frame");

                    EditorGUILayout.Space(4);
                    var oldc = GUI.color; GUI.color = ColDim;
                    EditorGUILayout.LabelField(
                        "Criterion run on ubuntu-latest. Audio frame = 512 samp @ 48 kHz = 10.67 ms.\n" +
                        "Unity default = 1024 samp @ 48 kHz = 21.33 ms. Both budgets safely covered.",
                        EditorStyles.wordWrappedMiniLabel);
                    GUI.color = oldc;
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // ── Setup guide ───────────────────────────────────────────────────
            _fSetup = EditorGUILayout.BeginFoldoutHeaderGroup(_fSetup, "Setup & Testing Guide");
            if (_fSetup)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.HelpBox(
                        "SCENE SETUP\n" +
                        "1. Add MID_NativeAudioBridge to your Managers prefab.\n" +
                        "2. Add AudioSource to same GameObject: clip=None, loop=True, playOnAwake=False.\n" +
                        "3. Assign AudioClips (impact, muzzle, shell) — must be Decompress On Load.\n" +
                        "4. Add GlobalFXManager to the same or a sibling GameObject.\n" +
                        "5. Assign 3 ParticleSystem GameObjects (impact, muzzle, shell) in World Space.\n\n" +
                        "TESTING FUNCTIONALITY\n" +
                        "• Use the ▶ buttons above to test each clip slot in Play Mode.\n" +
                        "• Call GlobalFXManager.Instance.TriggerImpact(pos, normal) from a test script.\n" +
                        "• Check Window > Analysis > Profiler > CPU > GC Alloc column while impacts fire.\n" +
                        "• MID_NativeAudioBridge.PlayClip() should show 0 B GC Alloc in the Profiler.\n\n" +
                        "STRESS TEST\n" +
                        "• Open MidManStudio > Utilities > Tests > Audio Bench to run formal benchmarks.\n" +
                        "• Simulate 100 impacts in 1 frame — verify voice stealing fires without crash.\n" +
                        "• Check audio doesn't clip: Project Settings > Audio > DSP Buffer = Best Latency.",
                        MessageType.None);
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            so.ApplyModifiedProperties();

            // Auto-repaint in play mode for live voice monitor
            if (Application.isPlaying) Repaint();
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void PerfRow(string label, string value, Color col, string note)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var old = GUI.color;
                GUI.color = ColDim;
                EditorGUILayout.LabelField(label, EditorStyles.miniLabel, GUILayout.Width(220));
                GUI.color = col;
                EditorGUILayout.LabelField(value, EditorStyles.miniBoldLabel, GUILayout.Width(80));
                GUI.color = ColDim;
                EditorGUILayout.LabelField(note, EditorStyles.miniLabel);
                GUI.color = old;
            }
        }

        private static void ColorLabel(string text, Color col)
        {
            var old = GUI.color; GUI.color = col;
            EditorGUILayout.LabelField(text, EditorStyles.wordWrappedMiniLabel);
            GUI.color = old;
        }

        private static void DrawLine()
        {
            Rect r = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(r, new Color(0.5f, 0.5f, 0.5f, 0.3f));
            EditorGUILayout.Space(2);
        }
    }
}
#endif
