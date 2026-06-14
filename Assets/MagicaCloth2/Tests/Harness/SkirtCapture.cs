// MagicaCloth2 characterization: per-frame capture of the SkirtDemo scene sim.
//
// Loads SkirtDemo.unity in Play Mode, drives the skirt with the SAME sway as the
// engine's demo_cloth_skirt.c (x = 0.2*sin(2*pi*1.2*t), t = frame/60), and dumps
// per-frame proxy particle positions (sim + display) plus build info to
// ExportedAssets/test-results/skirt_capture_unity.json.
//
// Run: exec:MagicaCloth2.Tests.SkirtCapture.Run
using System.Collections;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;

namespace MagicaCloth2.Tests
{
    public static class SkirtCapture
    {
        const string RunFlag = "MC2Tests.SkirtCapture";
        const string ScenePath =
            "Assets/MagicaCloth2/Example (Can be deleted)/DemoScenes/URP/SkirtDemo.unity";
        const int Frequency = 90;       // engine demo: simulation_frequency = 90
        const float FrameDt = 1f / 60f; // engine demo: CLOTH_SIM_DT
        const float SwayAmplitude = 0.2f;
        const float SwayFrequency = 1.2f;
        const int CaptureFrames = 360;  // 6 s

        static readonly string OutPath = Path.GetFullPath(
            Path.Combine("ExportedAssets", "test-results", "skirt_capture_unity.json"));

        public static void Run()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("[SkirtCapture] Editor already in Play Mode; aborting.");
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(OutPath));
            if (File.Exists(OutPath)) File.Delete(OutPath);
            SessionState.SetBool(RunFlag, true);
            Debug.Log("[SkirtCapture] Entering Play Mode...");
            EditorApplication.EnterPlaymode();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Bootstrap()
        {
            if (!SessionState.GetBool(RunFlag, false)) return;
            SessionState.SetBool(RunFlag, false);
            var go = new GameObject("SkirtCaptureDriver");
            go.hideFlags = HideFlags.HideAndDontSave;
            Object.DontDestroyOnLoad(go);
            go.AddComponent<SkirtCaptureDriver>();
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
            yield return null; // scene becomes active next frame

            var skirtGo = GameObject.Find("skirt");
            var cloth = Object.FindAnyObjectByType<MagicaCloth>();
            if (skirtGo == null || cloth == null)
            {
                Debug.LogError($"[SkirtCapture] skirt={skirtGo} cloth={cloth} — scene wrong");
                yield break;
            }

            // we drive the position ourselves with an exact t = frame/60
            // (HZSkirtSway is in Assembly-CSharp, not referencable from this asmdef)
            var sway = skirtGo.GetComponent("HZSkirtSway") as Behaviour;
            if (sway != null) sway.enabled = false;
            Vector3 basePos = skirtGo.transform.position;

            // wait for async cloth build
            int guard = 0;
            while (!cloth.IsValid() && guard++ < 600) yield return null;
            if (!cloth.IsValid())
            {
                Debug.LogError("[SkirtCapture] cloth never became valid");
                yield break;
            }
            // let it settle into the build pose for one frame
            yield return null;

            var sb = new StringBuilder(1 << 24);
            var ci = CultureInfo.InvariantCulture;
            sb.Append("{\n");
            sb.Append($"\"frequency\":{Frequency},\"frameDt\":{FrameDt.ToString("R", ci)},");
            sb.Append($"\"swayAmplitude\":{SwayAmplitude.ToString("R", ci)},\"swayFrequency\":{SwayFrequency.ToString("R", ci)},\n");
            sb.Append($"\"proxyVertexCount\":{ClothState.ProxyVertexCount(cloth)},");
            sb.Append($"\"proxyEdgeCount\":{ClothState.ProxyEdgeCount(cloth)},");
            sb.Append($"\"proxyTriangleCount\":{ClothState.ProxyTriangleCount(cloth)},");
            int pcount = ClothState.ParticleCount(cloth);
            sb.Append($"\"particleCount\":{pcount},\n");

            int fixedCount = 0;
            sb.Append("\"attributes\":[");
            for (int i = 0; i < pcount; i++)
            {
                var a = ClothState.ProxyAttribute(cloth, i);
                int v = a.IsFixed() ? 1 : (a.IsMove() ? 2 : 0);
                if (a.IsFixed()) fixedCount++;
                sb.Append(v);
                if (i + 1 < pcount) sb.Append(',');
            }
            sb.Append("],\n");
            sb.Append("\"depths\":[");
            for (int i = 0; i < pcount; i++)
            {
                sb.Append(ClothState.ProxyDepth(cloth, i).ToString("F5", ci));
                if (i + 1 < pcount) sb.Append(',');
            }
            sb.Append("],\n");
            Debug.Log($"[SkirtCapture] built. particles={pcount} fixed={fixedCount} " +
                      $"verts={ClothState.ProxyVertexCount(cloth)} edges={ClothState.ProxyEdgeCount(cloth)} tris={ClothState.ProxyTriangleCount(cloth)}");

            AppendPositions(sb, "restPositions", ClothState.Positions(cloth), ci);
            sb.Append(",\n\"frames\":[\n");

            for (int f = 0; f < CaptureFrames; f++)
            {
                float t = f * FrameDt;
                float fx = SwayAmplitude * Mathf.Sin(t * 2f * Mathf.PI * SwayFrequency);
                skirtGo.transform.position = new Vector3(basePos.x + fx, basePos.y, basePos.z);

                yield return null; // frame simulates

                sb.Append("{\"f\":").Append(f).Append(",\"swayX\":").Append(fx.ToString("F6", ci)).Append(',');
                AppendPositions(sb, "pos", ClothState.Positions(cloth), ci);
                sb.Append(',');
                AppendPositions(sb, "disp", ClothState.DisplayPositions(cloth), ci);
                sb.Append('}');
                sb.Append(f + 1 < CaptureFrames ? ",\n" : "\n");
            }
            sb.Append("]\n}\n");

            File.WriteAllText(OutPath, sb.ToString());
            Debug.Log($"[SkirtCapture] wrote {CaptureFrames} frames -> {OutPath}");
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

    public class SkirtCaptureDriver : MonoBehaviour
    {
        IEnumerator Start()
        {
            yield return SkirtCapture.Capture();
            EditorApplication.ExitPlaymode();
        }
    }
}
