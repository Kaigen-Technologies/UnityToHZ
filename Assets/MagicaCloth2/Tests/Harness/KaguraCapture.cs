// MagicaCloth2 characterization: per-frame capture of the KAGURA cloths
// (skirt + sode L/R) in SkirtDemo.unity while the WAIT02 animation plays.
// Dumps per-frame proxy particle positions (sim + base) per cloth to
// ExportedAssets/test-results/kagura_capture_unity.json.
//
// Run: exec:MagicaCloth2.Tests.KaguraCapture.Run
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace MagicaCloth2.Tests
{
    public static class KaguraCapture
    {
        const string RunFlag = "MC2Tests.KaguraCapture";
        const string ScenePath =
            "Assets/MagicaCloth2/Example (Can be deleted)/DemoScenes/URP/SkirtDemo.unity";
        const int Frequency = 90;       // engine demo: simulation_frequency = 90
        const float FrameDt = 1f / 60f;
        const int CaptureFrames = 360;  // 6 s

        static readonly string[] Wanted =
            { "Magica Cloth (skirt)", "Magica Cloth (sode L)", "Magica Cloth (sode R)" };

        static readonly string OutPath = Path.GetFullPath(
            Path.Combine("ExportedAssets", "test-results", "kagura_capture_unity.json"));

        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("[KaguraCapture] Editor already in Play Mode; aborting.");
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(OutPath));
            if (File.Exists(OutPath)) File.Delete(OutPath);
            SessionState.SetBool(RunFlag, true);
            Debug.Log("[KaguraCapture] Entering Play Mode...");
            EditorApplication.EnterPlaymode();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (!SessionState.GetBool(RunFlag, false)) return;
            SessionState.SetBool(RunFlag, false);
            var go = new GameObject("KaguraCaptureDriver");
            go.hideFlags = HideFlags.HideAndDontSave;
            Object.DontDestroyOnLoad(go);
            go.AddComponent<KaguraCaptureDriver>();
        }

        internal static IEnumerator Capture()
        {
            Time.captureDeltaTime = FrameDt;
            QualitySettings.vSyncCount = 0;
            MagicaManager.SetSimulationFrequency(Frequency);
            MagicaManager.SetMaxSimulationCountPerFrame(3);
            MagicaManager.SetGlobalTimeScale(1f);

            var loadParams = new UnityEngine.SceneManagement.LoadSceneParameters(
                UnityEngine.SceneManagement.LoadSceneMode.Single);
            UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(ScenePath, loadParams);
            yield return null;

            // the PREFAB's cloths (root has the Animator) — not the standalone
            // skirt setup that may also live in the scene
            var cloths = new List<MagicaCloth>();
            foreach (var w in Wanted)
            {
                MagicaCloth found = null;
                foreach (var c in Object.FindObjectsByType<MagicaCloth>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                {
                    if (c.gameObject.name.Trim() != w) continue;
                    if (c.transform.root.GetComponentInChildren<Animator>() == null) continue;
                    found = c;
                    break;
                }
                if (found == null)
                {
                    Debug.LogError($"[KaguraCapture] cloth '{w}' not found");
                    yield break;
                }
                cloths.Add(found);
            }

            int guard = 0;
            bool allValid = false;
            while (!allValid && guard++ < 600)
            {
                allValid = true;
                foreach (var c in cloths) allValid &= c.IsValid();
                if (!allValid) yield return null;
            }
            if (!allValid)
            {
                Debug.LogError("[KaguraCapture] cloths never became valid");
                yield break;
            }
            yield return null;

            var sb = new StringBuilder(1 << 25);
            var ci = CultureInfo.InvariantCulture;
            sb.Append("{\n");
            sb.Append($"\"frequency\":{Frequency},\"frameDt\":{FrameDt.ToString("R", ci)},\"frames\":{CaptureFrames},\n");
            sb.Append("\"cloths\":[\n");
            for (int c = 0; c < cloths.Count; c++)
            {
                var cloth = cloths[c];
                int pcount = ClothState.ParticleCount(cloth);
                int nfix = 0;
                for (int i = 0; i < pcount; i++)
                    if (ClothState.ProxyAttribute(cloth, i).IsFixed()) nfix++;
                sb.Append("{");
                sb.Append($"\"name\":\"{cloth.gameObject.name.Trim()}\",");
                sb.Append($"\"particleCount\":{pcount},\"fixed\":{nfix},");
                sb.Append($"\"edges\":{ClothState.ProxyEdgeCount(cloth)},\"tris\":{ClothState.ProxyTriangleCount(cloth)},");
                sb.Append("\"depths\":[");
                for (int i = 0; i < pcount; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(ClothState.ProxyDepth(cloth, i).ToString("F4", ci));
                }
                sb.Append("]}");
                sb.Append(c + 1 < cloths.Count ? ",\n" : "\n");
                Debug.Log($"[KaguraCapture] {cloth.gameObject.name}: particles={pcount} fixed={nfix}");
            }
            sb.Append("],\n\"framesData\":[\n");

            for (int f = 0; f < CaptureFrames; f++)
            {
                yield return null; // frame simulates (Animator advances by captureDeltaTime)

                sb.Append("{\"f\":").Append(f).Append(",\"c\":[");
                for (int c = 0; c < cloths.Count; c++)
                {
                    if (c > 0) sb.Append(',');
                    sb.Append('{');
                    AppendPositions(sb, "pos", ClothState.Positions(cloths[c]), ci);
                    sb.Append(',');
                    AppendPositions(sb, "base", ClothState.BasePositions(cloths[c]), ci);
                    sb.Append('}');
                }
                sb.Append("]}");
                sb.Append(f + 1 < CaptureFrames ? ",\n" : "\n");
            }
            sb.Append("]\n}\n");

            File.WriteAllText(OutPath, sb.ToString());
            Debug.Log($"[KaguraCapture] wrote {CaptureFrames} frames -> {OutPath}");
            Time.captureDeltaTime = 0f;
        }

        static void AppendPositions(StringBuilder sb, string key, float3[] pts, CultureInfo ci)
        {
            sb.Append('"').Append(key).Append("\":[");
            for (int i = 0; i < pts.Length; i++)
            {
                sb.Append(pts[i].x.ToString("F5", ci)).Append(',');
                sb.Append(pts[i].y.ToString("F5", ci)).Append(',');
                sb.Append(pts[i].z.ToString("F5", ci));
                if (i + 1 < pts.Length) sb.Append(',');
            }
            sb.Append(']');
        }
    }

    public class KaguraCaptureDriver : MonoBehaviour
    {
        IEnumerator Start()
        {
            yield return KaguraCapture.Capture();
            EditorApplication.ExitPlaymode();
        }
    }
}
