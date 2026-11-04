// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.projectilesystem.md, section "NetworkProjectileBaseEditor.cs"
// ============================================================================
// NetworkProjectileBaseEditor
//
// Inspector for every class derived from NetworkProjectileBase.
//
// NetworkProjectileBase derives from NGO's NetworkTransform. NGO 1.7.1's
// NetworkTransformEditor (com.unity.netcode.gameobjects/Editor/
// NetworkTransformEditor.cs) is declared [CustomEditor(typeof(NetworkTransform),
// true)] and draws a hand-picked set of fields one at a time. It never calls
// DrawDefaultInspector(), so it would hide every field a subclass adds
// (n_VisualConfigId, _configReferenceForEditorOnly, _logLevel, _baseDamage,
// _damageLayerMask, _allowCallerVelocityOverride, TimeToLive and the rest).
//
// This editor targets NetworkProjectileBase, which is more derived than
// NetworkTransform, so Unity picks it for every projectile class and
// NetworkTransformEditor is not used for them. It does not target
// NetworkTransform itself: two editors on the exact same type resolve in an
// order-dependent way, and every other NetworkTransform in the project should
// keep NGO's own editor.
//
// It does not inherit NetworkTransformEditor to reuse its field list: that
// class is not guaranteed to stay public, unsealed or stable across NGO
// versions, and copying its list by hand would drift on an NGO upgrade.
// DrawDefaultInspector() draws every serialized field on the whole chain
// (NetworkTransform's own included). The layout is plainer than NGO's own
// grouping and help boxes, and it does not depend on NGO's editor internals.

using UnityEditor;
using UnityEngine;
using MidManStudio.Projectiles.Network;
using MidManStudio.Projectiles.Managers;
using MidManStudio.Projectiles.Config;

namespace MidManStudio.Projectiles.EditorUtils
{
    [CustomEditor(typeof(NetworkProjectileBase), true)]
    [CanEditMultipleObjects]
    public class NetworkProjectileBaseEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            EditorGUILayout.HelpBox(
                "Custom inspector for the NetworkProjectileBase chain — see " +
                "NetworkProjectileBaseEditor.cs's header comment for why this " +
                "exists. Every field below (including base NetworkTransform " +
                "fields like Interpolate / In Local Space) is drawn by " +
                "DrawDefaultInspector(), not NGO's own curated NetworkTransform " +
                "inspector.",
                MessageType.None);

            DrawDefaultInspector();

            DrawRuntimeConfigPreview();
        }

        /// <summary>
        /// Play-mode-only convenience: ProjectileRegistry is a runtime
        /// singleton (Awake-populated), so there's no way to resolve
        /// VisualConfigId -> a config NAME at edit time — ids are assigned
        /// dynamically at runtime and aren't stable across sessions (see
        /// ProjectileRegistry's own doc comment). While actually in Play
        /// Mode though, the registry IS populated, so show the live
        /// resolution here as a sanity check — this is exactly the kind of
        /// thing you'd otherwise have to add a temporary Debug.Log for.
        /// </summary>
        private void DrawRuntimeConfigPreview()
        {
            if (!Application.isPlaying) return;
            if (target is not PhysicsProjectileBase) return;
            if (!ProjectileRegistry.HasInstance) return;

            var so = serializedObject;
            var idProp = so.FindProperty("n_VisualConfigId");
            // NetworkVariable<T> serializes its wrapped value under a nested
            // "m_InternalValue" field (NGO 1.7.1) — fall back to just not
            // showing the preview if that ever changes shape rather than
            // guessing at a different internal field name.
            var valueProp = idProp?.FindPropertyRelative("m_InternalValue");
            if (valueProp == null) return;

            ushort id  = (ushort)valueProp.intValue;
            var    cfg = ProjectileRegistry.Instance.Get(id);

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                cfg != null
                    ? $"Live VisualConfigId {id} resolves to: {cfg.name}"
                    : $"Live VisualConfigId {id} does not resolve to any registered config.",
                cfg != null ? MessageType.Info : MessageType.Warning);
        }
    }
}
