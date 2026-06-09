// MagicaCloth2 characterization test suite.
// Behavioral oracle for porting MagicaCloth2 to hz-engine-v5.
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;

namespace MagicaCloth2.Tests
{
    /// <summary>
    /// One test body: a coroutine that steps frames and records asserts into <see cref="Check"/>.
    /// </summary>
    public delegate IEnumerator TestBody(Check check);

    /// <summary>
    /// A registered test (stable name + body).
    /// </summary>
    public struct TestEntry
    {
        public string name;
        public TestBody body;

        public TestEntry(string name, TestBody body)
        {
            this.name = name;
            this.body = body;
        }
    }

    /// <summary>
    /// Final per-test outcome written to results.json.
    /// </summary>
    public struct TestResult
    {
        public string name;
        public string status;   // "PASS" | "FAIL"
        public string message;  // joined failures (empty when PASS)
        public Dictionary<string, string> metrics;
    }

    /// <summary>
    /// The assertion recorder handed to each test.
    ///
    /// Philosophy (see plan): assert what CONVERGES across implementations, not exact float
    /// trajectories. A failure appends to <see cref="failures"/>; the test passes iff none fired.
    /// Every test also logs raw measured numbers via Metric(...) so the engine port can diff later.
    /// Tolerances should be passed in scale-relative (fraction of cloth length / spacing / radius / degrees),
    /// never as magic absolutes.
    /// </summary>
    public class Check
    {
        public readonly string name;
        readonly List<string> failures = new List<string>();
        readonly Dictionary<string, string> metrics = new Dictionary<string, string>();

        public Check(string name) { this.name = name; }

        public bool Passed => failures.Count == 0;

        // --- raw failure / pass-through -------------------------------------------------

        public void Fail(string label, string detail) => failures.Add($"{label}: {detail}");

        public void IsTrue(string label, bool cond)
        {
            if (!cond) Fail(label, "expected true");
        }

        public void IsFalse(string label, bool cond)
        {
            if (cond) Fail(label, "expected false");
        }

        // --- numeric asserts ------------------------------------------------------------

        /// <summary>|a - b| must be <= tol.</summary>
        public void Approx(string label, float a, float b, float tol)
        {
            float d = math.abs(a - b);
            if (!(d <= tol)) Fail(label, $"|{a}-{b}|={d} > {tol}");
        }

        /// <summary>Distance(a,b) must be <= tol (vector form).</summary>
        public void Approx(string label, float3 a, float3 b, float tol)
        {
            float d = math.distance(a, b);
            if (!(d <= tol)) Fail(label, $"|{F(a)}-{F(b)}|={d} > {tol}");
        }

        /// <summary>a must be strictly less than b.</summary>
        public void Less(string label, float a, float b)
        {
            if (!(a < b)) Fail(label, $"{a} !< {b}");
        }

        /// <summary>a must be strictly greater than b.</summary>
        public void Greater(string label, float a, float b)
        {
            if (!(a > b)) Fail(label, $"{a} !> {b}");
        }

        /// <summary>a must be <= b (with optional slack).</summary>
        public void AtMost(string label, float a, float b, float slack = 0f)
        {
            if (!(a <= b + slack)) Fail(label, $"{a} > {b} (+{slack})");
        }

        /// <summary>a must be >= b (with optional slack).</summary>
        public void AtLeast(string label, float a, float b, float slack = 0f)
        {
            if (!(a >= b - slack)) Fail(label, $"{a} < {b} (-{slack})");
        }

        public void InRange(string label, float v, float lo, float hi)
        {
            if (v < lo || v > hi) Fail(label, $"{v} not in [{lo},{hi}]");
        }

        public void NoNaN(string label, float3[] pts)
        {
            for (int i = 0; i < pts.Length; i++)
            {
                if (math.any(math.isnan(pts[i])) || math.any(math.isinf(pts[i])))
                {
                    Fail(label, $"NaN/Inf at index {i}");
                    return;
                }
            }
        }

        // --- metrics (golden record, never fail) ----------------------------------------

        public void Metric(string key, float v) => metrics[key] = v.ToString("R");
        public void Metric(string key, int v) => metrics[key] = v.ToString();
        public void Metric(string key, float3 v) => metrics[key] = F(v);
        public void Metric(string key, string v) => metrics[key] = v;

        // --- output ---------------------------------------------------------------------

        public TestResult ToResult() => new TestResult
        {
            name = name,
            status = Passed ? "PASS" : "FAIL",
            message = string.Join("; ", failures),
            metrics = metrics,
        };

        static string F(float3 v) => $"({v.x:R},{v.y:R},{v.z:R})";
    }
}
