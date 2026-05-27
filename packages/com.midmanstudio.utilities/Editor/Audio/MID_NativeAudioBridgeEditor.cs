// MID_NativeAudioBridgeEditor.cs — v2: reflects pool architecture

#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using MidManStudio.Core.Audio;

namespace MidManStudio.Core.EditorUtils.Audio
{
    [CustomEditor(typeof(MID_NativeAudioBridge))]
    public class MID_NativeAudioBridgeEditor : UnityEditor.Editor
    {
        private static readonly Color ColGreen  = new(0.28f, 0.90f, 0.45f, 1f);
        private static readonly Color ColYellow = new(1.00f, 0.85f, 0.25f, 1f);
        private static readonly Color ColBlue   = new(0.40f, 0.65f, 1.00f, 1f);
        private static readonly Color ColDim    = new(0.55f, 0.55f, 0.55f, 1f);
        private static readonly Color ColBarBg  = new(0.12f, 0.12f, 0.12f, 0.8f);

        private bool _fClips = true;
        private bool _fPool  = true;
        private bool _fSetup = false;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var bridge = (MID_NativeAudioBridge)target;
            var so     = serializedObject;
            so.Update();

            EditorGUILayout.Space(6);
            Sep();

            // ── Limiter warning ───────────────────────────────────────────────
            EditorGUILayout.HelpBox(
                "MID_NativeAudioBridge: AudioSource pool only — no DSP here.\n" +
                "For the peak limiter: add MID_AudioLimiter to your AudioListener GameObject.",
                MessageType.Info);

            // ── Clip list ─────────────────────────────────────────────────────
            _fClips = EditorGUILayout.BeginFoldoutHeaderGroup(_fClips, "Clip Slots");
            if (_fClips)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    var clipsProperty = so.FindProperty("_clips");
                    if (clipsProperty == null || clipsProperty.arraySize == 0)
                    {
                        EditorGUILayout.HelpBox(
                            "No clips assigned. Any Load Type works — Decompress On Load NOT required.",
                            MessageType.Info);
                    }
                    else
                    {
                        // Header
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            C("Slot", ColDim, 36); C("Clip Name", ColDim, 160);
                            C("Channels", ColDim, 64); C("Length", ColDim, 64);
                            C("Load Type", ColDim, 140);
                        }
                        Sep();

                        for (int i = 0; i < clipsProperty.arraySize; i++)
                        {
                            var elem = clipsProperty.GetArrayElementAtIndex(i);
                            var clip = elem.objectReferenceValue as AudioClip;

                            using (new EditorGUILayout.HorizontalScope())
                            {
                                C($"[{i}]", ColBlue, 36);
                                C(clip != null ? clip.name : "— null —",
                                  clip != null ? ColGreen : new Color(1, 0.4f, 0.4f), 160);

                                if (clip != null)
                                {
                                    C($"{clip.channels}ch", ColDim, 64);
                                    C($"{clip.length:F2}s",  ColDim, 64);

                                    bool decomp = clip.loadType == AudioClipLoadType.DecompressOnLoad;
                                    // Any load type is fine now — just show what it is
                                    var old = GUI.color;
                                    GUI.color = ColDim;
                                    EditorGUILayout.LabelField(
                                        clip.loadType.ToString(),
                                        EditorStyles.miniLabel, GUILayout.Width(140));
                                    GUI.color = old;

                                    GUI.enabled = Application.isPlaying;
                                    if (GUILayout.Button("▶", GUILayout.Width(24)))
                                        bridge.PlayClip(i, 0.8f);
                                    GUI.enabled = true;
                                }
                                else
                                {
                                    C("—", ColDim, 64); C("—", ColDim, 64); C("—", ColDim, 140);
                                }
                            }
                        }

                        EditorGUILayout.Space(4);
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUI.enabled = Application.isPlaying;
                            var oldbg = GUI.backgroundColor;
                            GUI.backgroundColor = new Color(0.25f, 0.55f, 1f);
                            if (GUILayout.Button("▶▶ Play All", GUILayout.Height(24)))
                                for (int i = 0; i < bridge.ClipCount; i++) bridge.PlayClip(i, 0.8f);
                            GUI.backgroundColor = new Color(0.85f, 0.25f, 0.25f);
                            if (GUILayout.Button("■ Stop All", GUILayout.Height(24)))
                                bridge.StopAll();
                            GUI.backgroundColor = oldbg;
                            GUI.enabled = true;
                        }
                    }
                }
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            // ── Pool monitor ──────────────────────────────────────────────────
            if (Application.isPlaying)
            {
                _fPool = EditorGUILayout.BeginFoldoutHeaderGroup(_fPool, "Voice Pool Monitor");
                if (_fPool)
                {
                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        int active    = bridge.ActiveVoiceCount;
                        var poolProp  = so.FindProperty("_poolSize");
                        int poolSize  = poolProp?.intValue ?? 16;
                        float ratio   = poolSize > 0 ? (float)active / poolSize : 0f;

                        EditorGUILayout.LabelField($"Active Voices: {active} / {poolSize}",
                            EditorStyles.miniBoldLabel);

                        Rect r = EditorGUILayout.GetControlRect(false, 14);
                        r.x += 4; r.width -= 8; r.height = 10;
                        EditorGUI.DrawRect(r, ColBarBg);
                        if (ratio > 0f)
                        {
                            Rect fill = r; fill.width *= ratio;
                            Color voiceColor = ratio > 0.875f ? new Color(1, 0.4f, 0.4f)
                                             : ratio > 0.5f   ? ColYellow
                                             : ColGreen;
                            EditorGUI.DrawRect(fill, voiceColor);
                        }

                        if (active >= poolSize)
                        {
                            var old = GUI.color; GUI.color = ColYellow;
                            EditorGUILayout.LabelField("Pool full — next PlayClip() steals oldest voice.",
                                EditorStyles.wordWrappedMiniLabel);
                            GUI.color = old;
                        }
                    }
                }
                EditorGUILayout.EndFoldoutHeaderGroup();
            }

            // ── Setup guide ───────────────────────────────────────────────────
            _fSetup = EditorGUILayout.BeginFoldoutHeaderGroup(_fSetup, "Setup Guide");
            if (_fSetup)
            {
                EditorGUILayout.HelpBox(
                    "POOL SETUP\n" +
                    "1. Add MID_NativeAudioBridge to your Managers prefab.\n" +
                    "2. Assign AudioClips in the _clips array — any Load Type works.\n" +
                    "3. Call PlayClip(0, 1f) for impact, PlayClip(1) for muzzle, etc.\n\n" +
                    "LIMITER SETUP (optional but recommended for heavy projectile games)\n" +
                    "4. Find your AudioListener GameObject (usually Main Camera).\n" +
                    "5. Add MID_AudioLimiter component to that same GameObject.\n" +
                    "6. Adjust threshold/attack/release in the MID_AudioLimiter Inspector.\n\n" +
                    "WHAT CHANGED FROM v1:\n" +
                    "AudioClip.GetData() is no longer called. No Decompress On Load required.\n" +
                    "Any clip Load Type works. The Rust DLL is now a DSP limiter only.",
                    MessageType.None);
            }
            EditorGUILayout.EndFoldoutHeaderGroup();

            so.ApplyModifiedProperties();
            if (Application.isPlaying) Repaint();
        }

        private static void C(string text, Color col, float width)
        {
            var o = GUI.color; GUI.color = col;
            EditorGUILayout.LabelField(text, EditorStyles.miniLabel, GUILayout.Width(width));
            GUI.color = o;
        }

        private static void Sep()
        {
            Rect r = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(r, new Color(0.5f, 0.5f, 0.5f, 0.3f));
            EditorGUILayout.Space(2);
        }
    }

    // ── MID_AudioLimiter custom inspector ─────────────────────────────────────

    [CustomEditor(typeof(MID_AudioLimiter))]
    public class MID_AudioLimiterEditor : UnityEditor.Editor
    {
        private static readonly Color ColGreen = new(0.28f, 0.90f, 0.45f, 1f);
        private static readonly Color ColRed   = new(1.00f, 0.35f, 0.35f, 1f);
        private static readonly Color ColBarBg = new(0.12f, 0.12f, 0.12f, 0.8f);

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            if (!Application.isPlaying) return;

            EditorGUILayout.Space(6);

            // Gain display
            var gainProp = serializedObject.FindProperty("_currentGain");
            if (gainProp != null)
            {
                float gain  = gainProp.floatValue;
                float ratio = Mathf.Clamp01(gain);

                EditorGUILayout.LabelField(
                    $"Limiter Gain: {gain:F3}  ({(gain < 0.99f ? "LIMITING" : "transparent")})",
                    EditorStyles.miniBoldLabel);

                Rect r = EditorGUILayout.GetControlRect(false, 14);
                r.x += 4; r.width -= 8; r.height = 10;
                EditorGUI.DrawRect(r, ColBarBg);
                if (ratio > 0f)
                {
                    Rect fill = r; fill.width *= ratio;
                    EditorGUI.DrawRect(fill, gain < 0.5f ? ColRed : gain < 0.9f ? new Color(1, 0.85f, 0.25f) : ColGreen);
                }
            }

            // AudioListener check
            var limiter = (MID_AudioLimiter)target;
            if (limiter.GetComponent<AudioListener>() == null)
            {
                EditorGUILayout.HelpBox(
                    "MID_AudioLimiter MUST be on the AudioListener GameObject.\n" +
                    "Currently no AudioListener on this object — limiter will only affect this object's audio.",
                    MessageType.Error);
            }

            Repaint();
        }
    }
}
#endif
