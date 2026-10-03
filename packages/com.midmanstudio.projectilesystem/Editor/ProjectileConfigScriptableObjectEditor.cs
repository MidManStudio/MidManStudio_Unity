// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.projectilesystem.md, section "ProjectileConfigScriptableObjectEditor.cs"
// ============================================================================
// Custom Inspector for ProjectileConfigScriptableObject. Draws the normal
// Inspector untouched, then adds an "Apply JSON" panel — paste a JSON object
// whose keys match this asset's serialized field names (base class fields
// included — MaxRange, MovementType, MinSpeed, etc., same as the ones
// declared directly on this class) and it fills in every primitive/enum/
// Vector2/Vector3 field it can, then reports exactly what it couldn't:
// unmatched keys (typos) separately from unsupported ones (asset references,
// AnimationCurve, Gradient — those need manual drag-and-drop same as
// ProjectileConfigEntry.configSO did in the enum-mapping importer).
//
// Example JSON — not exhaustive, just enough to show the shape:
//   {
//     "_movementType": "Straight",
//     "_minSpeed": 25, "_maxSpeed": 30,
//     "_lifetime": 3, "_maxRange": 50,
//     "_piercingType": "None",
//     "_projectileType": "basic", "_projectileClass": "basic",
//     "_capColliderSize": { "x": 0.2, "y": 0.08 },
//     "_minDamage": 8, "_maxDamage": 12,
//     "_minHeadShotDamage": 20, "_maxHeadShotDamage": 28
//   }

using MidManStudio.Core.EditorUtils;
using MidManStudio.Projectiles.Config;
using UnityEditor;
using UnityEngine;

namespace MidManStudio.Projectiles.EditorUtils
{
    /// <summary>
    /// Inspector for ProjectileConfigSO and any subclass of it
    /// (editorForChildClasses is on). Draws every serialized field except the
    /// custom-path ones, then the custom path panel (single selection only) and
    /// the Apply JSON panel. Extra fields on a game-specific subclass show up
    /// automatically. A subclass that wants different UI can embed
    /// ProjectileConfigJsonPanel and ProjectileCustomPathPanel in its own editor;
    /// this class is sealed, so it is the reference usage rather than a base.
    /// </summary>
    [CustomEditor(typeof(ProjectileConfigSO), true)]
    [CanEditMultipleObjects]
    public sealed partial class ProjectileConfigScriptableObjectEditor : UnityEditor.Editor
    {
        // Extracted to ProjectileConfigJsonPanel.cs so a game-specific
        // subclass's own custom editor can embed the exact same panel — see
        // that file's own doc comment for the full explanation and a usage
        // example. This class is just ITS reference usage now.
        private readonly ProjectileConfigJsonPanel _jsonPanel = new();

        // POLISHED PATH EDITOR: same reusable-panel pattern as _jsonPanel
        // above — see ProjectileCustomPathPanel's own doc comment. Replaces
        // the plain default List<Vector2>/string fields the CustomCurve path
        // used to fall back to with formula validation, an example dropdown,
        // a draggable point list, and a live preview.
        private readonly ProjectileCustomPathPanel _pathPanel = new();

        // Path fields excluded from the default draw below — DrawPropertiesExcluding
        // skips exactly these, then _pathPanel.Draw renders its own rich UI for
        // them afterward. Every other field (base class and subclass alike)
        // still draws normally, same as DrawDefaultInspector() did before.
        private static readonly string[] PathFieldsHandledByPanel =
        {
            "_customPathShape", "_customPathSplineType", "_customPathPoints",
            "_customPathFormulaX", "_customPathFormulaY",
        };

        public override void OnInspectorGUI()
        {
            // DrawPropertiesExcluding(SerializedObject, ...) neither refreshes nor
            // applies the serialized object (DrawDefaultInspector does both), so the
            // pair around it is required for any field edit, including the custom
            // icon, to reach the asset. Works for multi-selection too.
            serializedObject.Update();

            var iconProp = serializedObject.FindProperty("_customIcon");
            int  iconBefore      = iconProp != null ? iconProp.objectReferenceInstanceIDValue : 0;
            bool iconMixedBefore = iconProp != null && iconProp.hasMultipleDifferentValues;

            DrawPropertiesExcluding(serializedObject, PathFieldsHandledByPanel);

            bool iconChanged = iconProp != null
                && (iconProp.objectReferenceInstanceIDValue != iconBefore
                    || iconProp.hasMultipleDifferentValues != iconMixedBefore);
            serializedObject.ApplyModifiedProperties();

            // This editor takes precedence over MID_BaseSOEditor for these assets, so
            // it has to do that editor's icon-cache invalidation itself or the
            // Project window keeps painting the old icon until the next domain reload.
            // Only done when the icon itself changed: any other field edit (a slider
            // drag fires every frame) would otherwise walk every selected asset and
            // repaint the Project window each time.
            if (iconChanged)
            {
                foreach (var t in targets)
                {
                    if (t == null) continue;
                    string guid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(t));
                    if (!string.IsNullOrEmpty(guid))
                        MID_BaseSOProjectIconDrawer.InvalidateCache(guid);
                }
                EditorApplication.RepaintProjectWindow();
            }

            // Single selection only: a draggable point list and preview has no
            // sensible meaning across several assets.
            if (targets.Length == 1 && target is ProjectileConfigSO cfg)
                _pathPanel.Draw(serializedObject, cfg);
            else if (targets.Length > 1)
                EditorGUILayout.HelpBox(
                    "Custom Movement Path editing is single-object only — select just one config to edit its path.",
                    MessageType.None);

            _jsonPanel.Draw(serializedObject);
        }
    }
}
