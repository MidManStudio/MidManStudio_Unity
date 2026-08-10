// Hand-authored custom collider shape for RustSim's impact detector — for
// anything Unity's built-in colliders can't represent (or that
// RustSimTargetRegistrar's auto-detection approximates rather than
// represents exactly — see its "3D BOX LIMITATION" note).
//
// AUTHORING MODEL: place control points in the SCENE VIEW (not the
// Inspector — see RustSimCustomShapeAuthoringEditor, which uses
// SceneView.duringSceneGui move handles + click-to-add/right-click-to-remove,
// the same interaction paradigm as Unity's own EdgeCollider2D/
// PolygonCollider2D point editors), then those control points are baked down
// to a sampled point sequence using the SAME three interpolation modes
// ProjectilePatternSO already offers for spawn-pattern paths — CatmullRom,
// Bezier, Linear — so a shape can be a smooth curve, not just straight
// segments between hand-placed points, if that's what the control points are
// meant to describe. The math below mirrors ProjectilePatternSO's
// EvaluateLinear/EvaluateCatmullRom/EvaluateBezier exactly, generalized from
// Vector2 to Vector3 so this one component covers both 2D and 3D authoring.
//
// MOVING SHAPES ("must support moving stuff too"): baked points are stored
// relative to this transform and re-projected to world space via
// transform.TransformPoint on every registration tick — a shape on a moving
// platform, orbiting hazard, etc. follows correctly. Re-baking the curve
// itself (control points → sampled points) only happens when the control
// points are actually edited (in the scene-view editor) or Rebake() is
// called explicitly — not every tick, since the CURVE SHAPE relative to this
// transform doesn't change just because the transform moves.

using System.Collections.Generic;
using UnityEngine;
using MidManStudio.Projectiles.Core;

namespace MidManStudio.Projectiles.Managers
{
    public enum ShapeSplineType { Linear, CatmullRom, Bezier }

    [AddComponentMenu("MidMan Studio/Projectile System/RustSim Custom Shape Authoring")]
    public sealed class RustSimCustomShapeAuthoring : MonoBehaviour
    {
        #region Inspector

        [Header("Dimensionality")]
        [Tooltip("2D shapes ignore Z on every control point (kept for scene-view " +
                 "convenience — a point placed slightly off-plane by accident doesn't " +
                 "silently break anything, it's just never read).")]
        [SerializeField] private bool _is3D = false;

        [Header("Control Points")]
        [Tooltip("Edit these in the Scene view (select this object — handles and " +
                 "click-to-add appear automatically), not here. Listed in the " +
                 "Inspector too for precise numeric entry if you need it.")]
        [SerializeField] private List<Vector3> _controlPoints = new()
        {
            new Vector3(-0.5f, 0f, 0f), new Vector3(0.5f, 0f, 0f)
        };

        [Header("Curve")]
        [Tooltip("Linear: straight segments between control points, no smoothing — " +
                 "closest to what you placed. CatmullRom: smooth curve that passes " +
                 "through every control point. Bezier: smooth curve using every " +
                 "control point as a De Casteljau control vertex (does NOT generally " +
                 "pass through the interior points) — same three modes " +
                 "ProjectilePatternSO offers for spawn patterns.")]
        [SerializeField] private ShapeSplineType _splineType = ShapeSplineType.Linear;

        [Tooltip("Wrap the last baked point back to the first, forming a closed loop " +
                 "(e.g. an irregular polygon) instead of an open polyline/curve.")]
        [SerializeField] private bool _closedLoop = false;

        [Tooltip("How many points to sample the curve down to for RustSim — capped " +
                 "at ShapeCollider2D.MaxPoints (8) regardless of how many control " +
                 "points you've placed. More control points than this just makes the " +
                 "authored curve's SHAPE more precise; the baked output is always " +
                 "this many samples.")]
        [SerializeField, Range(2, ShapeCollider2D.MaxPoints)] private int _bakeResolution = 8;

        [Tooltip("Capsule-style thickness around the baked curve — 0 for a bare " +
                 "wireframe/edge, >0 to give the whole curve some girth (e.g. a " +
                 "rope, a thick wall segment).")]
        [SerializeField, Min(0f)] private float _thickness = 0f;

        [Header("Target Id")]
        [Tooltip("Leave at 0 to auto-derive — see RustSimTargetRegistrar's matching " +
                 "field for the exact same rule.")]
        [SerializeField] private uint _explicitTargetId = 0;

        [Header("Movement")]
        [Tooltip("See RustSimTargetRegistrar's matching field — identical behavior: " +
                 "on, this registers once in Start() and never re-ticks.")]
        [SerializeField] private bool _isStatic = false;

        [Header("Update Rate")]
        [SerializeField] private bool _useFixedUpdate = true;
        [SerializeField, Min(1)] private int _updateEveryNTicks = 1;

        #endregion

        #region State

        private uint _targetId;
        private int  _tickCounter;
        private bool _hasRegisteredOnce;

        private readonly Vector3[] _bakedLocalPoints = new Vector3[ShapeCollider2D.MaxPoints];
        private int _bakedCount;

        #endregion

        #region Editor-facing accessors
        //
        // Used by RustSimCustomShapeAuthoringEditor's scene-view handles — kept
        // as explicit methods (not a public List<Vector3> property) so every
        // edit path goes through Rebake(), and so play-mode edits (if anyone
        // scripts against this at runtime) can't leave _bakedLocalPoints stale.

        public IReadOnlyList<Vector3> ControlPoints => _controlPoints;
        public bool Is3D => _is3D;
        public bool ClosedLoop => _closedLoop;
        public ShapeSplineType SplineType => _splineType;

        /// <summary>
        /// The actual sampled points RustSim receives, post-bake — what
        /// RustSimCustomShapeAuthoringEditor draws as the orange curve versus
        /// the raw gray control polygon. Read-only: baking only ever happens
        /// through Rebake() itself, triggered by the Editor-facing mutators
        /// below or an explicit Rebake() call.
        /// </summary>
        public int BakedCount => _bakedCount;
        public Vector3 GetBakedPoint(int index) => _bakedLocalPoints[index];

        public void EditorSetControlPoint(int index, Vector3 localPoint)
        {
            if (index < 0 || index >= _controlPoints.Count) return;
            _controlPoints[index] = _is3D ? localPoint : new Vector3(localPoint.x, localPoint.y, 0f);
            Rebake();
        }

        public void EditorAddControlPoint(Vector3 localPoint)
        {
            _controlPoints.Add(_is3D ? localPoint : new Vector3(localPoint.x, localPoint.y, 0f));
            Rebake();
        }

        public void EditorInsertControlPoint(int index, Vector3 localPoint)
        {
            index = Mathf.Clamp(index, 0, _controlPoints.Count);
            _controlPoints.Insert(index, _is3D ? localPoint : new Vector3(localPoint.x, localPoint.y, 0f));
            Rebake();
        }

        public void EditorRemoveControlPoint(int index)
        {
            if (_controlPoints.Count <= 2) return; // a shape needs at least 2 points
            if (index < 0 || index >= _controlPoints.Count) return;
            _controlPoints.RemoveAt(index);
            Rebake();
        }

        #endregion

        #region Lifecycle

        private void Start()
        {
            _targetId = ResolveTargetId();
            Rebake();
            RegisterNow();
        }

        private void OnEnable()
        {
            if (_hasRegisteredOnce) RegisterNow();
        }

        private void OnDisable() => Deactivate();
        private void OnDestroy() => Deactivate();

        private void Update()
        {
            if (_isStatic || _useFixedUpdate) return;
            Tick();
        }

        private void FixedUpdate()
        {
            if (_isStatic || !_useFixedUpdate) return;
            Tick();
        }

        #endregion

        #region Implementation

        private void Tick()
        {
            if (++_tickCounter < _updateEveryNTicks) return;
            _tickCounter = 0;
            RegisterNow();
        }

        /// <summary>
        /// Samples the spline at _bakeResolution evenly-spaced t values and
        /// stores the result in _bakedLocalPoints. Called once at Start(), and
        /// again any time a control point changes via the Editor-facing
        /// accessors above (including live edits in the scene view while the
        /// GameObject is selected, in or out of Play mode).
        /// </summary>
        public void Rebake()
        {
            int n = Mathf.Clamp(_bakeResolution, 2, ShapeCollider2D.MaxPoints);
            _bakedCount = n;

            if (_controlPoints == null || _controlPoints.Count == 0)
            {
                for (int i = 0; i < n; i++) _bakedLocalPoints[i] = Vector3.zero;
                return;
            }
            if (_controlPoints.Count == 1)
            {
                for (int i = 0; i < n; i++) _bakedLocalPoints[i] = _controlPoints[0];
                return;
            }

            for (int i = 0; i < n; i++)
            {
                float t = n == 1 ? 0.5f : (float)i / (n - 1);
                _bakedLocalPoints[i] = EvaluateSpline(t);
            }
        }

        // ── Spline evaluation — mirrors ProjectilePatternSO's EvaluateLinear/
        // EvaluateCatmullRom/EvaluateBezier exactly, generalized to Vector3 ──

        private Vector3 EvaluateSpline(float t)
        {
            t = Mathf.Clamp01(t);
            return _splineType switch
            {
                ShapeSplineType.Linear     => EvaluateLinear(t),
                ShapeSplineType.Bezier     => EvaluateBezier(t),
                _                          => EvaluateCatmullRom(t),
            };
        }

        private Vector3 EvaluateLinear(float t)
        {
            int   n      = _controlPoints.Count;
            float scaled = t * (n - 1);
            int   seg    = Mathf.Clamp((int)scaled, 0, n - 2);
            float segT   = scaled - seg;
            return Vector3.Lerp(_controlPoints[seg], _controlPoints[seg + 1], segT);
        }

        private Vector3 EvaluateCatmullRom(float t)
        {
            int n = _controlPoints.Count;
            if (n == 2) return Vector3.Lerp(_controlPoints[0], _controlPoints[1], t);

            float scaled = t * (n - 1);
            int   seg    = Mathf.Clamp((int)scaled, 0, n - 2);
            float segT   = scaled - seg;

            Vector3 p0 = _controlPoints[Mathf.Max(seg - 1, 0)];
            Vector3 p1 = _controlPoints[seg];
            Vector3 p2 = _controlPoints[Mathf.Min(seg + 1, n - 1)];
            Vector3 p3 = _controlPoints[Mathf.Min(seg + 2, n - 1)];

            float t2 = segT * segT, t3 = t2 * segT;
            return 0.5f * (
                2f * p1 +
                (-p0 + p2)                    * segT +
                (2f*p0 - 5f*p1 + 4f*p2 - p3) * t2 +
                (-p0 + 3f*p1 - 3f*p2 + p3)   * t3);
        }

        private Vector3 EvaluateBezier(float t)
        {
            var pts = _controlPoints.ToArray();
            int n   = pts.Length;
            for (int r = 1; r < n; r++)
                for (int i = 0; i < n - r; i++)
                    pts[i] = Vector3.Lerp(pts[i], pts[i + 1], t);
            return pts[0];
        }

        // ── Registration ─────────────────────────────────────────────────────

        private void RegisterNow()
        {
            var system = MID_MasterProjectileSystem.HasInstance
                ? MID_MasterProjectileSystem.Instance : null;
            if (system == null) return;

            float scaledThickness = _thickness > 0f ? ScaledThickness() : 0f;

            if (_is3D)
            {
                var shape = new ShapeCollider3D
                {
                    TargetId = _targetId, ShapeType = (byte)ShapeColliderType.Polygon,
                    PointCount = (byte)_bakedCount,
                    Closed = (byte)(_closedLoop ? 1 : 0), Active = 1,
                    Thickness = scaledThickness
                };
                for (int i = 0; i < _bakedCount; i++)
                    shape.SetPoint(i, transform.TransformPoint(_bakedLocalPoints[i]));
                system.RegisterShape3D(in shape, gameObject);
            }
            else
            {
                var shape = new ShapeCollider2D
                {
                    TargetId = _targetId, ShapeType = (byte)ShapeColliderType.Polygon,
                    PointCount = (byte)_bakedCount,
                    Closed = (byte)(_closedLoop ? 1 : 0), Active = 1,
                    Thickness = scaledThickness
                };
                for (int i = 0; i < _bakedCount; i++)
                {
                    Vector3 world = transform.TransformPoint(_bakedLocalPoints[i]);
                    shape.SetPoint(i, new Vector2(world.x, world.y));
                }
                system.RegisterShape2D(in shape, gameObject);
            }

            _hasRegisteredOnce = true;
        }

        private float ScaledThickness()
        {
            var s = transform.lossyScale;
            return _thickness * (_is3D ? Mathf.Max(s.x, s.y, s.z) : Mathf.Max(s.x, s.y));
        }

        private void Deactivate()
        {
            if (!_hasRegisteredOnce) return;

            var system = MID_MasterProjectileSystem.HasInstance
                ? MID_MasterProjectileSystem.Instance : null;
            if (system == null) return;

            if (_is3D) system.DeactivateShape3D(_targetId);
            else       system.DeactivateShape2D(_targetId);
        }

        private uint ResolveTargetId()
        {
            if (_explicitTargetId != 0) return _explicitTargetId;

            var netObj = GetComponent<Unity.Netcode.NetworkObject>();
            if (netObj != null) return (uint)netObj.NetworkObjectId;

            return unchecked((uint)GetInstanceID());
        }

        #endregion

        #region Editor gizmo support

#if UNITY_EDITOR
        // Cheap always-on visualization even when this object isn't selected
        // (RustSimCustomShapeAuthoringEditor draws the full interactive handle
        // set only while selected) — a faint baked-curve outline so a level
        // designer can spot these shapes while browsing a scene.
        private void OnDrawGizmos()
        {
            if (_bakedCount < 2) return;
            Gizmos.color = new Color(1f, 0.55f, 0.1f, 0.6f);
            int segCount = _closedLoop ? _bakedCount : _bakedCount - 1;
            for (int i = 0; i < segCount; i++)
            {
                Vector3 a = transform.TransformPoint(_bakedLocalPoints[i]);
                Vector3 b = transform.TransformPoint(_bakedLocalPoints[(i + 1) % _bakedCount]);
                Gizmos.DrawLine(a, b);
            }
        }
#endif

        #endregion
    }
}
