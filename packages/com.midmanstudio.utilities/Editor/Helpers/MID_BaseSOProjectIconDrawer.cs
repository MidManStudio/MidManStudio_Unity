// ============================================================================
// NOTICE: Full documentation, design decisions, and fix history for this file
// live in docs/com.midmanstudio.utilities/editor-helpers.md, section "MID_BaseSOProjectIconDrawer.cs"
// ============================================================================
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace MidManStudio.Core.EditorUtils
{
    /// <summary>
    /// Draws the icon of each <see cref="MID_BaseSO"/> asset (its own icon, or its type's group icon) over the
    /// asset's thumbnail in the Project window, through <c>EditorApplication.projectWindowItemOnGUI</c>.
    /// Unity's <c>EditorGUIUtility.SetIconForObject</c> only supports GameObjects and scripts, so it cannot set
    /// the icon of a ScriptableObject asset.
    ///
    /// Unity has already painted its own icon when the callback runs, and there is no hook to stop that. The
    /// drawer first paints an opaque rectangle in an approximate row background colour, then draws the icon on top,
    /// so letterboxed edges and transparent pixels do not show the default icon. The colours are the standard dark
    /// and light skin colours with a rough blue tint for a selected row. They are not sampled from the real row,
    /// so they can be slightly off. This is cosmetic.
    ///
    /// Results are cached by GUID, including "no icon", and the cache is cleared on script reload. The inspector
    /// calls <see cref="InvalidateCache"/> when the icon field changes.
    /// </summary>
    [InitializeOnLoad]
    public static class MID_BaseSOProjectIconDrawer
    {
        private static readonly Dictionary<string, Texture2D> _iconCache = new Dictionary<string, Texture2D>();
        private static readonly HashSet<string> _confirmedNotMidBaseSO = new HashSet<string>();

        // Approximate Editor default row/tile backgrounds (dark/light skin)
        // and a rough selection tint. See the class summary for why these are
        // approximations rather than sampled values.
        private static readonly Color _bgDark         = new Color(0.219f, 0.219f, 0.219f, 1f);
        private static readonly Color _bgLight         = new Color(0.760f, 0.760f, 0.760f, 1f);
        private static readonly Color _bgDarkSelected  = new Color(0.243f, 0.372f, 0.588f, 1f);
        private static readonly Color _bgLightSelected = new Color(0.243f, 0.490f, 0.827f, 1f);

        static MID_BaseSOProjectIconDrawer()
        {
            EditorApplication.projectWindowItemOnGUI += DrawCustomIcon;
        }

        private static void DrawCustomIcon(string guid, Rect selectionRect)
        {
            if (_confirmedNotMidBaseSO.Contains(guid)) return;

            if (!_iconCache.TryGetValue(guid, out var icon))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                var so = AssetDatabase.LoadAssetAtPath<MID_BaseSO>(path);
                if (so == null)
                {
                    _confirmedNotMidBaseSO.Add(guid);
                    return;
                }

                icon = so.ResolveIcon();
                _iconCache[guid] = icon; // cache even null — "no custom icon" is a valid, stable result
            }

            if (icon == null) return; // falls through to Unity's own default script icon

            // List view rows are short and wide; icon-view tiles are roughly
            // square. Use height as the size cue for list view so the icon
            // doesn't overflow into the label text next to it.
            bool isListView = selectionRect.height <= 20f;
            float size = isListView ? selectionRect.height
                                    : Mathf.Min(selectionRect.width, selectionRect.height);

            var iconRect = new Rect(selectionRect.x, selectionRect.y, size, size);

            // Erase the default icon Unity already painted into this rect
            // before drawing ours (see the class summary). Without this, letterboxed
            // edges and any transparent pixels in `icon` show the old icon.
            var selectedGuids = Selection.assetGUIDs;
            bool isSelected = selectedGuids != null && System.Array.IndexOf(selectedGuids, guid) >= 0;

            Color bg = EditorGUIUtility.isProSkin
                ? (isSelected ? _bgDarkSelected  : _bgDark)
                : (isSelected ? _bgLightSelected : _bgLight);
            EditorGUI.DrawRect(iconRect, bg);

            GUI.DrawTexture(iconRect, icon, ScaleMode.ScaleToFit);
        }

        /// <summary>
        /// Called by MID_BaseSOEditor when an asset's custom icon field changes,
        /// so the Project window picks up the new icon immediately instead of
        /// showing a stale cached one until the next domain reload.
        /// </summary>
        public static void InvalidateCache(string guid)
        {
            _iconCache.Remove(guid);
        }

        [InitializeOnLoadMethod]
        private static void ClearCacheOnScriptReload()
        {
            _iconCache.Clear();
            _confirmedNotMidBaseSO.Clear();
        }
    }
}
