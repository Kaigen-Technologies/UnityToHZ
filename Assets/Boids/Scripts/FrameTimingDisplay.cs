using System.Diagnostics;
using Unity.Entities;
using UnityEngine;
using UnityEngine.Rendering;

public class FrameTimingDisplay : MonoBehaviour
{
    private const int SAMPLE_COUNT = 20;

    private FrameTiming[] _frameTimings = new FrameTiming[1];

    private double[] _cpuSamples = new double[SAMPLE_COUNT];
    private double[] _gpuSamples = new double[SAMPLE_COUNT];
    private double[] _ecsSamples = new double[SAMPLE_COUNT];
    private double[] _frameTimeSamples = new double[SAMPLE_COUNT];
    private int _sampleIndex;

    private double _cpuTimeMs;
    private double _gpuTimeMs;
    private double _ecsTimeMs;
    private double _frameTimeMs;
    private double _fps;

    private float _logTimer;
    private const float LOG_INTERVAL = 1.0f;

    private Stopwatch _ecsStopwatch = new Stopwatch();
    private GUIStyle _style;
    private GUIStyle _shadowStyle;
    private Texture2D _backgroundTexture;

    void Start()
    {
        // Note: For release builds, enable "Frame Timing Stats" in:
        // Edit > Project Settings > Player > Other Settings
        FrameTimingManager.CaptureFrameTimings();
    }

    void Update()
    {
        // Manual ECS update with timing. ManualWorldBootstrap.World is the modern
        // World.DefaultGameObjectInjectionWorld (Entities 1.x default world accessor).
        var world = World.DefaultGameObjectInjectionWorld;
        if (world != null && world.IsCreated)
        {
            _ecsStopwatch.Restart();
            world.Update();
            _ecsStopwatch.Stop();
            _ecsSamples[_sampleIndex] = _ecsStopwatch.Elapsed.TotalMilliseconds;
        }

        FrameTimingManager.CaptureFrameTimings();

        if (FrameTimingManager.GetLatestTimings(1, _frameTimings) > 0)
        {
            // _cpuSamples[_sampleIndex] = _frameTimings[0].cpuMainThreadFrameTime - _frameTimings[0].cpuMainThreadPresentWaitTime;
            _cpuSamples[_sampleIndex] = _frameTimings[0].cpuMainThreadFrameTime + _frameTimings[0].cpuRenderThreadFrameTime;;
            // _cpuSamples[_sampleIndex] = _frameTimings[0].cpuFrameTime - _frameTimings[0].cpuMainThreadPresentWaitTime;
            _gpuSamples[_sampleIndex] = _frameTimings[0].gpuFrameTime;
        }

        _frameTimeSamples[_sampleIndex] = Time.unscaledDeltaTime * 1000.0;

        _sampleIndex = (_sampleIndex + 1) % SAMPLE_COUNT;

        // Calculate averages
        _ecsTimeMs = Average(_ecsSamples);
        _cpuTimeMs = Average(_cpuSamples);
        _gpuTimeMs = Average(_gpuSamples);
        _frameTimeMs = Average(_frameTimeSamples);
        _fps = _frameTimeMs > 0 ? 1000.0 / _frameTimeMs : 0;

        _logTimer += Time.unscaledDeltaTime;
        if (_logTimer >= LOG_INTERVAL)
        {
            _logTimer = 0f;
            UnityEngine.Debug.Log($"CPU: {_cpuTimeMs:F2} ms | GPU: {_gpuTimeMs:F2} ms | ECS: {_ecsTimeMs:F2} ms | Frame: {_frameTimeMs:F1} ms | FPS: {_fps:F0}");
        }
    }

    private double Average(double[] samples)
    {
        double sum = 0;
        for (int i = 0; i < samples.Length; i++)
            sum += samples[i];
        return sum / samples.Length;
    }

    void OnGUI()
    {
        if (_style == null)
        {
            _style = new GUIStyle(GUI.skin.label);
            _style.fontSize = 31;
            _style.fontStyle = FontStyle.Bold;
            _style.normal.textColor = Color.white;

            _shadowStyle = new GUIStyle(_style);
            _shadowStyle.normal.textColor = Color.black;

            _backgroundTexture = new Texture2D(1, 1);
            _backgroundTexture.SetPixel(0, 0, new Color(0, 0, 0, 0.3f));
            _backgroundTexture.Apply();
        }

        string text = $"CPU: {_cpuTimeMs:F2} ms\nGPU: {_gpuTimeMs:F2} ms\nECS: {_ecsTimeMs:F2} ms\nFPS: {_fps:F0} ({_frameTimeMs:F1} ms)";

        const float padding = 6f;
        Rect rect = new Rect(12, 12, 300, 160);
        Rect backgroundRect = new Rect(rect.x - padding, rect.y - padding, rect.width + padding * 2, rect.height + padding * 2);
        Rect shadowRect = new Rect(14, 14, 300, 160);

        GUI.DrawTexture(backgroundRect, _backgroundTexture);
        GUI.Label(shadowRect, text, _shadowStyle);
        GUI.Label(rect, text, _style);
    }
}