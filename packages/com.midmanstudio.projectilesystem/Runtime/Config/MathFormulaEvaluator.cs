// packages/com.midmanstudio.projectilesystem/Runtime/Config/MathFormulaEvaluator.cs
// Self-contained recursive-descent expression parser and evaluator.
// Converts a math formula string to a float value with no external dependencies.
//
// Used by:
//   ProjectileShapeSO.BuildFormula()   — parametric vertex generation
//   ProjectilePatternSO.SampleFormula() — shot-angle generation
//
// SUPPORTED SYNTAX
//   Numbers:    1  1.5  .5  3.14
//   Variables:  t (0..1 param)  i (0-based index)  n (total count)  pi  tau  e
//   Operators:  + − * / ^ (power)  % (modulo)   − (unary)
//   Groups:     ( expression )
//   Functions:  sin cos tan  asin acos atan  atan2(y,x)
//               sqrt abs floor ceil round sign frac saturate
//               pow(x,y) log log2 exp
//               min(a,b) max(a,b) clamp(x,lo,hi) lerp(a,b,t)
//               mod(x,y) deg(r) rad(d)
//               step(edge,x) smoothstep(lo,hi,x)
//               pingpong(t,len) repeat(t,len)
//
// SHAPE USAGE (X and Y formulas, t ∈ [0,1))
//   X: cos(t * tau) * 0.5      Y: sin(t * tau) * 0.5   → circle
//   X: cos(t * tau) * (0.5 + 0.15 * cos(t * tau * 5))  → petal star
//
// PATTERN USAGE (H = horizontal °, V = vertical °)
//   H: i / n * 360            V: 0   → ring
//   H: i / (n - 1) * 180 - 90 V: 0   → fan

using System;
using System.Collections.Generic;
using UnityEngine;

namespace MidManStudio.Projectiles.Config
{
    /// Variables injected by the caller into a formula evaluation.
    public struct FormulaContext
    {
        /// Normalized parameter in [0, 1). For shapes: position along curve.
        public float t;
        /// Integer index cast to float. For patterns: projectile index.
        public float i;
        /// Total count cast to float.
        public float n;

        public static FormulaContext For(float t, float i = 0f, float n = 1f)
            => new FormulaContext { t = t, i = i, n = n };
    }

    /// Identifies the intended use of a formula string (drives example presets).
    public enum FormulaUsage { ShapeX, ShapeY, PatternH, PatternV }

    /// Recursive-descent expression parser and evaluator.
    /// Thread-safe — no shared mutable state.
    public static class MathFormulaEvaluator
    {
        // ── Token types ───────────────────────────────────────────────────────
        private enum TK { Num, Id, LP, RP, Cm, Add, Sub, Mul, Div, Hat, Pct, End, Err }

        private struct Token
        {
            public TK     Kind;
            public float  Num;
            public string Str;
        }

        // ── Lexer ─────────────────────────────────────────────────────────────
        private sealed class Lexer
        {
            private readonly string _src;
            private          int    _pos;

            public Token  Cur { get; private set; }
            public string Err { get; private set; }

            public Lexer(string src) { _src = src ?? string.Empty; Advance(); }

            public void Advance()
            {
                while (_pos < _src.Length && char.IsWhiteSpace(_src[_pos])) _pos++;

                if (_pos >= _src.Length) { Cur = new Token { Kind = TK.End }; return; }

                char c = _src[_pos];

                // Number literal (supports leading dot: .5)
                bool leadingDot = c == '.' && _pos + 1 < _src.Length
                               && char.IsDigit(_src[_pos + 1]);
                if (char.IsDigit(c) || leadingDot)
                {
                    int  start  = _pos;
                    bool hasDot = false;
                    while (_pos < _src.Length &&
                           (char.IsDigit(_src[_pos]) ||
                            (_src[_pos] == '.' && !hasDot)))
                    {
                        if (_src[_pos] == '.') hasDot = true;
                        _pos++;
                    }
                    string raw = _src.Substring(start, _pos - start);
                    if (float.TryParse(raw,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out float v))
                        Cur = new Token { Kind = TK.Num, Num = v };
                    else
                    { Err = $"Invalid number '{raw}'"; Cur = new Token { Kind = TK.Err }; }
                    return;
                }

                // Identifier
                if (char.IsLetter(c) || c == '_')
                {
                    int start = _pos;
                    while (_pos < _src.Length &&
                           (char.IsLetterOrDigit(_src[_pos]) || _src[_pos] == '_'))
                        _pos++;
                    Cur = new Token { Kind = TK.Id, Str = _src.Substring(start, _pos - start) };
                    return;
                }

                _pos++;
                switch (c)
                {
                    case '(': Cur = new Token { Kind = TK.LP  }; break;
                    case ')': Cur = new Token { Kind = TK.RP  }; break;
                    case ',': Cur = new Token { Kind = TK.Cm  }; break;
                    case '+': Cur = new Token { Kind = TK.Add }; break;
                    case '-': Cur = new Token { Kind = TK.Sub }; break;
                    case '*': Cur = new Token { Kind = TK.Mul }; break;
                    case '/': Cur = new Token { Kind = TK.Div }; break;
                    case '^': Cur = new Token { Kind = TK.Hat }; break;
                    case '%': Cur = new Token { Kind = TK.Pct }; break;
                    default:
                        Err = $"Unexpected character '{c}'";
                        Cur = new Token { Kind = TK.Err };
                        break;
                }
            }
        }

        // ── Parser ────────────────────────────────────────────────────────────
        private sealed class Parser
        {
            private readonly Lexer          _lex;
            private readonly FormulaContext _ctx;
            public           string         Error { get; private set; }

            public Parser(Lexer lex, FormulaContext ctx) { _lex = lex; _ctx = ctx; }

            // Sets Error and returns 0f — used inline in expressions.
            private float Fail(string msg) { Error = msg; return 0f; }

            // expr → term ( ('+' | '−') term )*
            public float Expr()
            {
                float v = Term();
                while (Error == null &&
                       (_lex.Cur.Kind == TK.Add || _lex.Cur.Kind == TK.Sub))
                {
                    bool add = _lex.Cur.Kind == TK.Add;
                    _lex.Advance();
                    float r = Term();
                    v = add ? v + r : v - r;
                }
                return v;
            }

            // term → power ( ('*' | '/' | '%') power )*
            private float Term()
            {
                float v = Power();
                while (Error == null &&
                       (_lex.Cur.Kind == TK.Mul ||
                        _lex.Cur.Kind == TK.Div ||
                        _lex.Cur.Kind == TK.Pct))
                {
                    var k = _lex.Cur.Kind;
                    _lex.Advance();
                    float r = Power();
                    if (Error != null) break;
                    if      (k == TK.Mul) v *= r;
                    else if (k == TK.Div)
                        v = Mathf.Approximately(r, 0f) ? Fail("Division by zero") : v / r;
                    else
                        v = r == 0f ? Fail("Modulo by zero") : v % r;
                }
                return v;
            }

            // power → unary ('^' unary)*  right-associative
            private float Power()
            {
                float v = Unary();
                if (Error == null && _lex.Cur.Kind == TK.Hat)
                {
                    _lex.Advance();
                    float e = Unary();
                    v = Mathf.Pow(v, e);
                }
                return v;
            }

            // unary → '−' unary | atom
            private float Unary()
            {
                if (_lex.Cur.Kind == TK.Sub) { _lex.Advance(); return -Atom(); }
                return Atom();
            }

            // atom → number | '(' expr ')' | ident [ '(' args ')' ]
            private float Atom()
            {
                var tok = _lex.Cur;

                if (tok.Kind == TK.Num)
                {
                    _lex.Advance();
                    return tok.Num;
                }

                if (tok.Kind == TK.LP)
                {
                    _lex.Advance();
                    float v = Expr();
                    if (Error != null) return 0f;
                    if (_lex.Cur.Kind != TK.RP) return Fail("Expected ')'");
                    _lex.Advance();
                    return v;
                }

                if (tok.Kind == TK.Id)
                {
                    _lex.Advance();
                    string name = tok.Str;

                    if (_lex.Cur.Kind == TK.LP)
                    {
                        _lex.Advance();
                        var args = new List<float>(4);
                        if (_lex.Cur.Kind != TK.RP)
                        {
                            args.Add(Expr());
                            while (Error == null && _lex.Cur.Kind == TK.Cm)
                            { _lex.Advance(); args.Add(Expr()); }
                        }
                        if (Error != null) return 0f;
                        if (_lex.Cur.Kind != TK.RP)
                            return Fail("Expected ')' after function arguments");
                        _lex.Advance();
                        return Func(name, args);
                    }

                    return Var(name);
                }

                if (tok.Kind == TK.Err || _lex.Err != null)
                    return Fail(_lex.Err ?? tok.Str ?? "Lexer error");

                return Fail($"Unexpected token '{tok.Kind}'");
            }

            private float Var(string name)
            {
                switch (name.ToLowerInvariant())
                {
                    case "t":   return _ctx.t;
                    case "i":   return _ctx.i;
                    case "n":   return _ctx.n;
                    case "pi":  return Mathf.PI;
                    case "tau": return Mathf.PI * 2f;
                    case "e":   return (float)Math.E;
                    default:    return Fail($"Unknown variable '{name}'");
                }
            }

            private float Func(string name, List<float> a)
            {
                int c = a.Count;
                switch (name.ToLowerInvariant())
                {
                    case "sin"        when c >= 1: return Mathf.Sin(a[0]);
                    case "cos"        when c >= 1: return Mathf.Cos(a[0]);
                    case "tan"        when c >= 1: return Mathf.Tan(a[0]);
                    case "asin"       when c >= 1: return Mathf.Asin(Mathf.Clamp(a[0], -1f, 1f));
                    case "acos"       when c >= 1: return Mathf.Acos(Mathf.Clamp(a[0], -1f, 1f));
                    case "atan"       when c >= 1: return Mathf.Atan(a[0]);
                    case "atan2"      when c >= 2: return Mathf.Atan2(a[0], a[1]);
                    case "sqrt"       when c >= 1: return Mathf.Sqrt(Mathf.Max(0f, a[0]));
                    case "abs"        when c >= 1: return Mathf.Abs(a[0]);
                    case "floor"      when c >= 1: return Mathf.Floor(a[0]);
                    case "ceil"       when c >= 1: return Mathf.Ceil(a[0]);
                    case "round"      when c >= 1: return Mathf.Round(a[0]);
                    case "sign"       when c >= 1: return Mathf.Sign(a[0]);
                    case "frac"       when c >= 1: return a[0] - Mathf.Floor(a[0]);
                    case "saturate"   when c >= 1: return Mathf.Clamp01(a[0]);
                    case "pow"        when c >= 2: return Mathf.Pow(a[0], a[1]);
                    case "log"        when c >= 1: return Mathf.Log(Mathf.Max(a[0], 1e-30f));
                    case "log2"       when c >= 1: return Mathf.Log(Mathf.Max(a[0], 1e-30f), 2f);
                    case "exp"        when c >= 1: return Mathf.Exp(a[0]);
                    case "min"        when c >= 2: return Mathf.Min(a[0], a[1]);
                    case "max"        when c >= 2: return Mathf.Max(a[0], a[1]);
                    case "clamp"      when c >= 3: return Mathf.Clamp(a[0], a[1], a[2]);
                    case "lerp"       when c >= 3: return Mathf.Lerp(a[0], a[1], a[2]);
                    case "mod"        when c >= 2: return a[1] == 0f ? Fail("mod by zero") : a[0] % a[1];
                    case "deg"        when c >= 1: return a[0] * Mathf.Rad2Deg;
                    case "rad"        when c >= 1: return a[0] * Mathf.Deg2Rad;
                    case "step"       when c >= 2: return a[1] >= a[0] ? 1f : 0f;
                    case "smoothstep" when c >= 3: return Mathf.SmoothStep(a[0], a[1], a[2]);
                    case "pingpong"   when c >= 2: return Mathf.PingPong(a[0], a[1]);
                    case "repeat"     when c >= 2: return Mathf.Repeat(a[0], a[1]);
                    default:
                        return Fail($"Unknown function '{name}' ({c} arg{(c == 1 ? "" : "s")})");
                }
            }
        }

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Evaluate a formula string with the given context.
        /// Returns 0 and sets <paramref name="error"/> on any failure.
        /// </summary>
        public static float Evaluate(string formula, FormulaContext ctx, out string error)
        {
            if (string.IsNullOrWhiteSpace(formula))
            { error = "Formula is empty"; return 0f; }

            var lex = new Lexer(formula);
            if (lex.Err != null) { error = lex.Err; return 0f; }

            var   parser = new Parser(lex, ctx);
            float result = parser.Expr();

            if (parser.Error != null || lex.Err != null)
            { error = parser.Error ?? lex.Err; return 0f; }

            if (lex.Cur.Kind != TK.End)
            { error = "Unexpected content after expression"; return 0f; }

            if (float.IsNaN(result) || float.IsInfinity(result))
            { error = "Result is NaN or Infinity (check for divide/log of zero)"; return 0f; }

            error = null;
            return result;
        }

        /// <summary>
        /// Returns true if the formula parses and evaluates without errors.
        /// Tests with t=0.5, i=0, n=8.
        /// </summary>
        public static bool Validate(string formula, out string error)
        {
            Evaluate(formula, FormulaContext.For(0.5f, 0f, 8f), out error);
            return error == null;
        }

        /// <summary>Built-in example formulas for each usage context.</summary>
        public static string[] GetExamples(FormulaUsage usage)
        {
            return usage switch
            {
                FormulaUsage.ShapeX => new[]
                {
                    "cos(t * tau) * 0.5",
                    "cos(t * tau) * (0.5 + 0.15 * cos(t * tau * 5))",
                    "cos(t * tau) * (0.3 + 0.2 * abs(cos(t * tau * 3)))",
                    "(t - 0.5)",
                },
                FormulaUsage.ShapeY => new[]
                {
                    "sin(t * tau) * 0.5",
                    "sin(t * tau) * (0.5 + 0.15 * cos(t * tau * 5))",
                    "sin(t * tau) * (0.3 + 0.2 * abs(cos(t * tau * 3)))",
                    "sin(t * pi) * 0.4",
                },
                FormulaUsage.PatternH => new[]
                {
                    "i / n * 360",
                    "i / (n - 1) * 180 - 90",
                    "sin(i / n * tau) * 45",
                    "i / n * 360 + sin(i / n * tau * 3) * 20",
                },
                FormulaUsage.PatternV => new[]
                {
                    "0",
                    "cos(i / n * tau) * 30",
                    "sin(i / n * pi) * 45",
                    "i / (n - 1) * 60 - 30",
                },
                _ => Array.Empty<string>()
            };
        }
    }
}
