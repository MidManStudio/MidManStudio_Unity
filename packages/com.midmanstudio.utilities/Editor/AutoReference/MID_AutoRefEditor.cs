// Custom inspector for MID_AutoRef — a one-click "Resolve Now" button painted with
// a green-to-orange gradient (same mesh-paint technique as GradientBannerElement,
// since USS has no gradient support), plus a summary of the last run.

#if UNITY_EDITOR
using System;
using System.Linq;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;
using MidManStudio.Core.AutoReference;

using MidManStudio.Core.EditorTools;
namespace MidManStudio.Core.EditorUtils.AutoReference
{
    [CustomEditor(typeof(MID_AutoRef))]
    public class MID_AutoRefEditor : Editor
    {
        private Label _summaryLabel;

        public override VisualElement CreateInspectorGUI()
        {
            var root = new VisualElement();

            var uss = MidEditorUIHelpers.FindUss("MID_AutoRefEditor");
            if (uss != null) root.styleSheets.Add(uss);

            InspectorElement.FillDefaultInspector(root, serializedObject, this);

            var button = new GradientResolveButton("RESOLVE NOW", OnResolveClicked);
            button.AddToClassList("mid-autoref-btn");
            root.Add(button);

            _summaryLabel = new Label(string.Empty);
            _summaryLabel.AddToClassList("mid-autoref-summary");
            root.Add(_summaryLabel);

            return root;
        }

        private void OnResolveClicked()
        {
            var autoRef = (MID_AutoRef)target;
            var results = MID_AutoReferenceResolver.Resolve(autoRef.gameObject, autoRef.Options);

            int assigned   = results.Count(r => r.Outcome == MID_AutoRefOutcome.Assigned);
            int ambiguous  = results.Count(r => r.Outcome == MID_AutoRefOutcome.AmbiguousResolved);
            int unresolved = results.Count(r => r.Outcome == MID_AutoRefOutcome.NoCandidates);
            int skipped    = results.Count(r => r.Outcome == MID_AutoRefOutcome.SkippedAlreadySet);

            _summaryLabel.text = $"{assigned} assigned · {ambiguous} ambiguous · {unresolved} unresolved · {skipped} already set";
            _summaryLabel.RemoveFromClassList("mid-autoref-summary--warn");
            if (unresolved > 0) _summaryLabel.AddToClassList("mid-autoref-summary--warn");

            EditorUtility.SetDirty(autoRef);
        }
    }

    /// <summary>Clickable button painted with a green→orange gradient — no CSS gradient needed.</summary>
    internal sealed class GradientResolveButton : Button
    {
        private static readonly Color ColorStart = new Color(0.27f, 0.78f, 0.35f, 1f); // green
        private static readonly Color ColorEnd   = new Color(1.00f, 0.55f, 0.10f, 1f); // orange

        public GradientResolveButton(string label, Action onClick) : base(onClick)
        {
            text = label; 
            style.backgroundColor = Color.clear;
            style.borderTopWidth = style.borderBottomWidth = style.borderLeftWidth = style.borderRightWidth = 0;
            style.color = Color.white;
            style.unityFontStyleAndWeight = FontStyle.Bold;
            style.height = 32;
            style.marginTop = 8;
            style.marginBottom = 4;
            style.borderTopLeftRadius = style.borderTopRightRadius =
                style.borderBottomLeftRadius = style.borderBottomRightRadius = 6;
            generateVisualContent += Paint;
        }

        private void Paint(MeshGenerationContext ctx)
        {
            Rect r = contentRect;
            if (r.width < 1f || r.height < 1f) return;

            var m = ctx.Allocate(4, 6);
            float z = Vertex.nearZ;

            m.SetNextVertex(new Vertex { position = new Vector3(0f,      0f,       z), tint = ColorStart });
            m.SetNextVertex(new Vertex { position = new Vector3(r.width, 0f,       z), tint = ColorEnd   });
            m.SetNextVertex(new Vertex { position = new Vector3(0f,      r.height, z), tint = ColorStart });
            m.SetNextVertex(new Vertex { position = new Vector3(r.width, r.height, z), tint = ColorEnd   });

            m.SetNextIndex(0); m.SetNextIndex(1); m.SetNextIndex(2);
            m.SetNextIndex(2); m.SetNextIndex(1); m.SetNextIndex(3);
        }
    }
}
#endif
