// NetworkProjectileBaseEditor
//
// BUG THIS CLOSES ("physics projectiles being NetworkTransform, none of the
// fields in classes that inherit it show up in the editor"): confirmed
// against actual NGO 1.7.1 source
// (com.unity.netcode.gameobjects/Editor/NetworkTransformEditor.cs) —
// NetworkTransformEditor.OnInspectorGUI() draws a hand-curated set of
// SerializedProperty fields one at a time (m_InLocalSpaceProperty,
// m_InterpolateProperty, etc.) and never calls DrawDefaultInspector(). It's
// declared [CustomEditor(typeof(NetworkTransform), true)] — editorForChildClasses
// true — so it becomes the ONE inspector for every class in the
// NetworkTransform hierarchy, including NetworkProjectileBase and everything
// that inherits it. Every field this package's own classes add
// (n_VisualConfigId, _configReferenceForEditorOnly, _logLevel, _baseDamage,
// _damageLayerMask, _allowCallerVelocityOverride, TimeToLive, all of it) was
// completely invisible — not hidden by a foldout, just never drawn at all.
// This is also almost certainly why logging looked broken: _logLevel lives
// behind this same wall, so there was no way to see or change it away from
// whatever got serialized onto a given prefab instance.
//
// FIX: register a MORE-DERIVED [CustomEditor] target. Unity's editor
// resolution picks the most-specific registered editor for an object's
// actual type — NetworkProjectileBase is more derived than NetworkTransform,
// so this editor wins for every projectile class and NetworkTransformEditor
// is never even consulted for them. Deliberately NOT targeting
// NetworkTransform itself: doing that would make this editor compete
// directly with Unity's own NetworkTransformEditor for literally any
// NetworkTransform anywhere in the project (ambiguous/order-dependent
// resolution when two editors target the exact same type) — targeting this
// package's own NetworkProjectileBase instead means every OTHER
// NetworkTransform in the project keeps using Unity's real editor,
// untouched, exactly as before.
//
// DELIBERATELY NOT inheriting NetworkTransformEditor to "get its fields for
// free": that class isn't guaranteed public/unsealed/stable across NGO
// versions, and hand-replicating its curated field list here would silently
// drift out of sync on any NGO upgrade. DrawDefaultInspector() instead —
// less pretty than NetworkTransformEditor's own grouping/helpboxes, but
// draws every serialized field on the whole chain (NetworkTransform's own
// fields included) and won't break if NGO's internal editor changes shape.

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
