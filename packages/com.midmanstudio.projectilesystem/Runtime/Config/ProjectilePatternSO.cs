// ProjectilePatternSO.cs
//
// ADDITIONS vs previous:
//   + PatternSplineType.Linear — connects control points with straight lines.
//     This gives EXACT rigid shapes: 3 points = triangle, 4 points = square,
//     5 = pentagon etc. No smoothing applied.
//     Use case: you want projectiles to fire at mathematically exact angles
//     defined by your control points with no curve between them.
//   + EvaluateLinear() private method.
//   + PatternShape presets from previous response retained.
//   + SampleDirections() uses full ±180° / 360° angle space for presets;
//     only the Spline path is limited by control-point positions.

using System;
using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MidManStudio.Projectiles.Config
{
    public enum PatternSplineType
    {
        /// Smooth curve passing through all control points.
        CatmullRom,

        /// Smooth curve with alternating anchor/handle points.
        Bezier,

        /// Straight lines between control points — exact angular positions.
        /// Use for rigid shapes: triangle (3 pts), square (4 pts), etc.
        Linear
    }

    public enum PatternShape
    {
        Spline  = 0,
        Ring360 = 1,
        Fan     = 2,
        VShape  = 3,
        Shotgun = 4,
        Star    = 5,
        Spiral  = 6,
    }

    [CreateAssetMenu(
        fileName = "ProjectilePattern",
        menuName  = "MidManStudio/Projectile System/Projectile Pattern",
        order     = 11)]
    public class ProjectilePatternSO : ScriptableObject
    {
        [Header("Pattern Shape")]
        [SerializeField] private PatternShape _shape = PatternShape.Spline;
        public PatternShape Shape => _shape;

        [Header("Spline (only when Shape = Spline)")]
        [Tooltip("CatmullRom = smooth through all points.\n" +
                 "Bezier = smooth with control handles.\n" +
                 "Linear = straight lines — exact rigid shapes (triangle, square etc).")]
        [SerializeField] private PatternSplineType _splineType = PatternSplineType.CatmullRom;
        public PatternSplineType SplineType => _splineType;

        [Tooltip("Control points: X = horizontal angle (°), Y = vertical angle (°).\n" +
                 "Linear mode: each point is an exact fire direction.\n" +
                 "Triangle example: (-60,0), (60,0), (0,60)\n" +
                 "Square example: (-45,0), (45,0), (45,45), (-45,45)")]
        [SerializeField] private Vector2[] _controlPoints = new Vector2[]
        {
            new Vector2(-15f, 0f),
            new Vector2(  0f, 0f),
            new Vector2( 15f, 0f)
        };
        public Vector2[] ControlPoints => _controlPoints;

        [Header("Projectile Count")]
        [SerializeField, Range(1, 64)] private int _projectileCount = 3;
        public int ProjectileCount => _projectileCount;

        [Header("Speed Variance")]
        [SerializeField, Range(0f, 0.5f)] private float _speedVariance = 0f;
        public float SpeedVariance => _speedVariance;

        [SerializeField] private uint _rngSeed = 12345;
        public uint RngSeed => _rngSeed;

        [Header("Fan Settings")]
        [SerializeField, Range(1f, 180f)] private float _fanHalfArcDeg = 45f;
        public float FanHalfArcDeg => _fanHalfArcDeg;
        [SerializeField, Range(-90f, 90f)] private float _fanVerticalDeg = 0f;

        [Header("V-Shape Settings")]
        [SerializeField, Range(1f, 90f)]  private float _vShapeAngleDeg = 30f;
        [SerializeField] private bool _vShapeIncludeCenter = false;
        [SerializeField, Range(-45f, 45f)] private float _vShapeVerticalDeg = 0f;

        [Header("Shotgun Settings")]
        [SerializeField, Range(1f, 90f)] private float _shotgunConeDeg = 15f;

        [Header("Star Settings")]
        [SerializeField, Range(3, 12)] private int   _starPoints     = 5;
        [SerializeField, Range(0f, 1f)] private float _starInnerScale = 0f;

        [Header("Spiral Settings")]
        [SerializeField, Range(0f, 360f)] private float _spiralAngleStep = 30f;

        // ─────────────────────────────────────────────────────────────────────
        //  Public API
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>
        /// Returns N (horizontalDeg, verticalDeg) direction pairs in local weapon space.
        ///
        /// Caller converts to world directions:
        ///   2D: Quaternion.Euler(0, 0, H) * baseDir
        ///   3D: Quaternion.Euler(-V, H, 0) * baseDir
        ///
        /// All preset shapes (Ring360, Fan, etc.) distribute in the full angle range
        /// required and work correctly in both 2D and 3D.
        /// </summary>
        public Vector2[] SampleDirections(int count = -1)
        {
            int n = count > 0 ? count : _projectileCount;
            if (n <= 0) n = 1;

            return _shape switch
            {
                PatternShape.Ring360 => SampleRing360(n),
                PatternShape.Fan     => SampleFan(n),
                PatternShape.VShape  => SampleVShape(n),
                PatternShape.Shotgun => SampleShotgun(n),
                PatternShape.Star    => SampleStar(n),
                PatternShape.Spiral  => SampleSpiral(n),
                _                    => SampleSpline(n),
            };
        }

        // ── Preset shapes ─────────────────────────────────────────────────────

        private Vector2[] SampleRing360(int n)
        {
            var result = new Vector2[n];
            float step = 360f / n;
            for (int i = 0; i < n; i++)
                result[i] = new Vector2(i * step, 0f);
            return result;
        }

        private Vector2[] SampleFan(int n)
        {
            var result = new Vector2[n];
            if (n == 1) { result[0] = new Vector2(0f, _fanVerticalDeg); return result; }
            for (int i = 0; i < n; i++)
            {
                float t = (float)i / (n - 1);
                result[i] = new Vector2(
                    Mathf.Lerp(-_fanHalfArcDeg, _fanHalfArcDeg, t),
                    _fanVerticalDeg);
            }
            return result;
        }

        private Vector2[] SampleVShape(int n)
        {
            bool  hasCenter = _vShapeIncludeCenter;
            int   perArm    = Mathf.Max((hasCenter ? n - 1 : n) / 2, 1);
            int   total     = hasCenter ? perArm * 2 + 1 : perArm * 2;
            var   result    = new Vector2[total];
            int   idx       = 0;

            for (int i = 0; i < perArm; i++)
            {
                float t = perArm == 1 ? 1f : (float)(i + 1) / perArm;
                result[idx++] = new Vector2( _vShapeAngleDeg * t, _vShapeVerticalDeg);
            }
            for (int i = 0; i < perArm; i++)
            {
                float t = perArm == 1 ? 1f : (float)(i + 1) / perArm;
                result[idx++] = new Vector2(-_vShapeAngleDeg * t, _vShapeVerticalDeg);
            }
            if (hasCenter && idx < total)
                result[idx] = new Vector2(0f, _vShapeVerticalDeg);

            return result;
        }

        private Vector2[] SampleShotgun(int n)
        {
            var  result = new Vector2[n];
            uint seed   = _rngSeed;
            for (int i = 0; i < n; i++)
            {
                float h = (Lcg(ref seed) * 2f - 1f) * _shotgunConeDeg;
                float v = (Lcg(ref seed) * 2f - 1f) * _shotgunConeDeg * 0.5f;
                result[i] = new Vector2(h, v);
            }
            return result;
        }

        private Vector2[] SampleStar(int n)
        {
            var result = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                float angle = i / (float)Mathf.Max(n, 1) * _starPoints * (360f / _starPoints);
                result[i]   = new Vector2(angle % 360f, 0f);
            }
            return result;
        }

        private Vector2[] SampleSpiral(int n)
        {
            var result = new Vector2[n];
            float ringStep = 360f / Mathf.Max(n, 1);
            for (int i = 0; i < n; i++)
                result[i] = new Vector2((i * ringStep + i * _spiralAngleStep) % 360f, 0f);
            return result;
        }

        // ── Spline (CatmullRom / Bezier / Linear) ─────────────────────────────

        private Vector2[] SampleSpline(int n)
        {
            if (_controlPoints == null || _controlPoints.Length == 0)
                return new Vector2[n];

            var result = new Vector2[n];
            if (n == 1) { result[0] = EvaluateSpline(0.5f); return result; }
            for (int i = 0; i < n; i++)
                result[i] = EvaluateSpline((float)i / (n - 1));
            return result;
        }

        public Vector2 EvaluateSpline(float t)
        {
            t = Mathf.Clamp01(t);
            if (_controlPoints == null || _controlPoints.Length == 0) return Vector2.zero;
            if (_controlPoints.Length == 1)                           return _controlPoints[0];

            return _splineType switch
            {
                PatternSplineType.Linear     => EvaluateLinear(t),
                PatternSplineType.Bezier     => EvaluateBezier(t),
                _                            => EvaluateCatmullRom(t),
            };
        }

        /// <summary>
        /// Linear interpolation between control points — EXACT positions, no smoothing.
        /// N control points define N-1 segments. t=0 → point[0], t=1 → point[N-1].
        ///
        /// Use for rigid shapes:
        ///   Triangle: 4 points (close the shape by repeating point[0] at end optional)
        ///   Square: (-45,0), (45,0), (45,45), (-45,45)
        ///
        /// With _projectileCount matching the point count, each bullet fires EXACTLY
        /// at a control point angle. With more projectiles than points, bullets are
        /// distributed along the line segments between points.
        /// </summary>
        private Vector2 EvaluateLinear(float t)
        {
            int   n      = _controlPoints.Length;
            float scaled = t * (n - 1);
            int   seg    = Mathf.Clamp((int)scaled, 0, n - 2);
            float segT   = scaled - seg;
            return Vector2.Lerp(_controlPoints[seg], _controlPoints[seg + 1], segT);
        }

        private Vector2 EvaluateCatmullRom(float t)
        {
            int n = _controlPoints.Length;
            if (n == 2) return Vector2.Lerp(_controlPoints[0], _controlPoints[1], t);

            float scaled = t * (n - 1);
            int   seg    = Mathf.Clamp((int)scaled, 0, n - 2);
            float segT   = scaled - seg;

            Vector2 p0 = _controlPoints[Mathf.Max(seg - 1, 0)];
            Vector2 p1 = _controlPoints[seg];
            Vector2 p2 = _controlPoints[Mathf.Min(seg + 1, n - 1)];
            Vector2 p3 = _controlPoints[Mathf.Min(seg + 2, n - 1)];

            float t2 = segT * segT, t3 = t2 * segT;
            return 0.5f * (
                2f * p1 +
                (-p0 + p2)                    * segT +
                (2f*p0 - 5f*p1 + 4f*p2 - p3) * t2 +
                (-p0 + 3f*p1 - 3f*p2 + p3)   * t3);
        }

        private Vector2 EvaluateBezier(float t)
        {
            var pts = (Vector2[])_controlPoints.Clone();
            int n   = pts.Length;
            for (int r = 1; r < n; r++)
                for (int i = 0; i < n - r; i++)
                    pts[i] = Vector2.Lerp(pts[i], pts[i + 1], t);
            return pts[0];
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Speed variance
        // ─────────────────────────────────────────────────────────────────────

        public float GetSpeedMultiplier(int projectileIndex, uint seed)
        {
            if (_speedVariance <= 0f) return 1f;
            uint s = seed.wrapping_add_cs((uint)projectileIndex * 1664525u + 1013904223u);
            s = s.wrapping_mul_cs(1664525u).wrapping_add_cs(1013904223u);
            float rand01 = (s >> 8) / 16777216f;
            return 1f + (rand01 * 2f - 1f) * _speedVariance;
        }

        private static float Lcg(ref uint seed)
        {
            seed = seed.wrapping_mul_cs(1664525u).wrapping_add_cs(1013904223u);
            return (seed >> 8) / 16777216f;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Editor: create default assets
        // ─────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR
        [MenuItem("MidManStudio/Projectile System/Create Default Pattern Assets", priority = 50)]
        public static void CreateDefaultPatterns()
        {
            const string dir = "Assets/MidManStudio/ProjectileSystem/Patterns";
            if (!System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);

            Create(dir, "Ring_8",       p => { p._shape = PatternShape.Ring360; p._projectileCount = 8; });
            Create(dir, "Ring_16",      p => { p._shape = PatternShape.Ring360; p._projectileCount = 16; });
            Create(dir, "Fan_5_90deg",  p => { p._shape = PatternShape.Fan; p._projectileCount = 5; p._fanHalfArcDeg = 45f; });
            Create(dir, "Fan_7_180deg", p => { p._shape = PatternShape.Fan; p._projectileCount = 7; p._fanHalfArcDeg = 90f; });
            Create(dir, "Shotgun_5",    p => { p._shape = PatternShape.Shotgun; p._projectileCount = 5; p._shotgunConeDeg = 20f; });
            Create(dir, "Shotgun_9",    p => { p._shape = PatternShape.Shotgun; p._projectileCount = 9; p._shotgunConeDeg = 25f; });
            Create(dir, "VShape_3",     p => { p._shape = PatternShape.VShape; p._projectileCount = 3; p._vShapeAngleDeg = 25f; p._vShapeIncludeCenter = true; });
            Create(dir, "Pentagon_5",   p => { p._shape = PatternShape.Star; p._projectileCount = 5; p._starPoints = 5; });
            Create(dir, "Hexagon_6",    p => { p._shape = PatternShape.Star; p._projectileCount = 6; p._starPoints = 6; });
            Create(dir, "Spiral_12",    p => { p._shape = PatternShape.Spiral; p._projectileCount = 12; p._spiralAngleStep = 15f; });

            // Linear rigid shapes — EXACT angular positions
            Create(dir, "Triangle_Linear", p =>
            {
                p._shape            = PatternShape.Spline;
                p._splineType       = PatternSplineType.Linear;
                p._projectileCount  = 3;
                p._controlPoints    = new[]
                {
                    new Vector2(-30f, -15f),
                    new Vector2(  0f,  25f),
                    new Vector2( 30f, -15f)
                };
            });

            Create(dir, "Square_Linear", p =>
            {
                p._shape            = PatternShape.Spline;
                p._splineType       = PatternSplineType.Linear;
                p._projectileCount  = 4;
                p._controlPoints    = new[]
                {
                    new Vector2(-30f, -30f),
                    new Vector2( 30f, -30f),
                    new Vector2( 30f,  30f),
                    new Vector2(-30f,  30f)
                };
            });

            Create(dir, "Spread_Wide_3", p =>
            {
                p._shape           = PatternShape.Spline;
                p._splineType      = PatternSplineType.CatmullRom;
                p._projectileCount = 3;
                p._controlPoints   = new[]
                {
                    new Vector2(-30f, 0f), new Vector2(0f, 0f), new Vector2(30f, 0f)
                };
            });

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Debug.Log($"[ProjectilePatternSO] Created default pattern assets in {dir}");
        }

        private static void Create(string dir, string name, Action<ProjectilePatternSO> cfg)
        {
            string path = $"{dir}/{name}.asset";
            if (System.IO.File.Exists(path)) return;
            var so = CreateInstance<ProjectilePatternSO>();
            cfg(so);
            AssetDatabase.CreateAsset(so, path);
        }
#endif
    }

    internal static class UIntWrapExtensions
    {
        internal static uint wrapping_add_cs(this uint a, uint b) => unchecked(a + b);
        internal static uint wrapping_mul_cs(this uint a, uint b) => unchecked(a * b);
    }
}
