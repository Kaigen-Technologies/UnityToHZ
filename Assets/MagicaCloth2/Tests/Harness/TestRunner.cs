// MagicaCloth2 characterization test suite.
//
// CLI-driven, no Unity Test Framework. Flow:
//   1. CLI: exec:MagicaCloth2.Tests.TestRunner.Run
//   2. Run() (editor main thread) clears results.json, sets a SessionState flag, enters Play Mode.
//   3. After the play-mode domain reload, Bootstrap() spawns a TestDriver that runs every registered
//      test as a coroutine (stepping fixed sub-steps), writes results.json, and exits Play Mode.
//   4. CLI reads results.json.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace MagicaCloth2.Tests
{
    public static class TestRunner
    {
        const string RunFlag = "MC2Tests.Run";
        const string FilterFile = "filter.txt"; // optional: substring; only matching test names run
        const string Mc2Version = "2.18.1";

        static readonly string ResultDir = Path.GetFullPath(Path.Combine("ExportedAssets", "test-results"));
        static string ResultPath => Path.Combine(ResultDir, "results.json");
        static string FilterPath => Path.Combine(ResultDir, FilterFile);

        // --- exec entrypoint (called via RemoteRefresh "exec:") -----------------------------

        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("[MC2Tests] Editor is already in/entering Play Mode; aborting.");
                return;
            }
            Directory.CreateDirectory(ResultDir);
            if (File.Exists(ResultPath)) File.Delete(ResultPath);
            SessionState.SetBool(RunFlag, true);
            Debug.Log("[MC2Tests] Entering Play Mode to run tests...");
            EditorApplication.EnterPlaymode();
        }

        // --- play-mode bootstrap ------------------------------------------------------------

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (!SessionState.GetBool(RunFlag, false)) return;
            SessionState.SetBool(RunFlag, false);

            var go = new GameObject("MC2TestDriver");
            go.hideFlags = HideFlags.HideAndDontSave;
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<TestDriver>();
        }

        // --- test registry ------------------------------------------------------------------

        public static List<TestEntry> GetTests()
        {
            var list = new List<TestEntry>();
            SmokeTests.Register(list);
            BuildTests.Register(list);
            BoneSimTests.Register(list);
            MeshSimTests.Register(list);
            ConstraintTests.Register(list);
            CollisionTests.Register(list);
            WindTests.Register(list);
            InertiaTests.Register(list);
            return list;
        }

        // --- driver -------------------------------------------------------------------------

        internal static IEnumerator RunAll()
        {
            var results = new List<TestResult>();
            string filter = File.Exists(FilterPath) ? File.ReadAllText(FilterPath).Trim() : "";

            Determinism.Enter();

            foreach (var entry in GetTests())
            {
                if (filter.Length > 0 && !entry.name.Contains(filter)) continue;

                // progress marker (so a hung test is identifiable even with no final results)
                try { File.WriteAllText(Path.Combine(ResultDir, "progress.txt"), entry.name); } catch { }

                var check = new Check(entry.name);
                IEnumerator body = null;
                try { body = entry.body(check); }
                catch (Exception ex) { check.Fail("setup", ex.ToString()); }

                if (body != null)
                {
                    bool more = true;
                    while (more)
                    {
                        try { more = body.MoveNext(); }
                        catch (Exception ex) { check.Fail("exception", ex.ToString()); break; }
                        if (more) yield return body.Current;
                    }
                }

                var r = check.ToResult();
                results.Add(r);
                Debug.Log($"[MC2Tests] {r.status}  {r.name}{(r.status == "FAIL" ? "  -> " + r.message : "")}");
                WriteResults(results, final: false); // incremental: survive a later hang
            }

            Determinism.Exit();
            WriteResults(results, final: true);
        }

        static void WriteResults(List<TestResult> results, bool final)
        {
            int pass = 0, fail = 0;
            foreach (var r in results) { if (r.status == "PASS") pass++; else fail++; }

            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append($"  \"mc2Version\": \"{Esc(Mc2Version)}\",\n");
            sb.Append($"  \"unityVersion\": \"{Esc(Application.unityVersion)}\",\n");
            sb.Append($"  \"frequency\": {Determinism.Frequency},\n");
            sb.Append($"  \"summary\": {{ \"pass\": {pass}, \"fail\": {fail}, \"total\": {results.Count} }},\n");
            sb.Append("  \"tests\": [\n");
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                sb.Append("    {\n");
                sb.Append($"      \"name\": \"{Esc(r.name)}\",\n");
                sb.Append($"      \"status\": \"{Esc(r.status)}\",\n");
                sb.Append($"      \"message\": \"{Esc(r.message)}\",\n");
                sb.Append("      \"metrics\": {");
                if (r.metrics != null)
                {
                    int j = 0;
                    foreach (var kv in r.metrics)
                    {
                        sb.Append($" \"{Esc(kv.Key)}\": \"{Esc(kv.Value)}\"");
                        if (++j < r.metrics.Count) sb.Append(",");
                    }
                    if (r.metrics.Count > 0) sb.Append(" ");
                }
                sb.Append("}\n");
                sb.Append(i + 1 < results.Count ? "    },\n" : "    }\n");
            }
            sb.Append("  ]\n");
            sb.Append("}\n");

            Directory.CreateDirectory(ResultDir);
            // incremental writes go to results.partial.json so the CLI's wait-for-results.json still
            // blocks until the run truly finishes; the partial file lets us see progress / diagnose hangs.
            File.WriteAllText(final ? ResultPath : Path.Combine(ResultDir, "results.partial.json"), sb.ToString());
            if (final) Debug.Log($"[MC2Tests] Wrote {results.Count} results ({pass} pass / {fail} fail) -> {ResultPath}");
        }

        static string Esc(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ").Replace("\t", " ");
        }
    }

    /// <summary>
    /// Hidden MonoBehaviour that drives the run inside Play Mode, then exits Play Mode.
    /// </summary>
    public class TestDriver : MonoBehaviour
    {
        IEnumerator Start()
        {
            yield return TestRunner.RunAll();
            EditorApplication.ExitPlaymode();
        }
    }
}
