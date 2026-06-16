// MagicaCloth2 characterization test suite.
using UnityEngine;

namespace MagicaCloth2.Tests
{
    /// <summary>
    /// Pins time and simulation cadence so runs are reproducible and 1 rendered frame == 1 fixed
    /// sub-step (clean, accumulator-free). Must be called from inside Play Mode (managers exist).
    ///
    /// Time.captureDeltaTime forces every frame to advance by exactly dt regardless of real wall time;
    /// SetMaxSimulationCountPerFrame(1) + matching frequency means the accumulator never batches/skips.
    /// </summary>
    public static class Determinism
    {
        public const int Frequency = 60;

        public static void Enter(int frequency = Frequency)
        {
            Time.captureDeltaTime = 1f / frequency;
            QualitySettings.vSyncCount = 0;

            // These are no-ops unless MagicaManager.IsPlaying() is true.
            MagicaManager.SetSimulationFrequency(frequency);
            MagicaManager.SetMaxSimulationCountPerFrame(1);
            MagicaManager.SetGlobalTimeScale(1f);
        }

        public static void Exit()
        {
            // 0 = disable capture framerate (back to real delta time).
            Time.captureDeltaTime = 0f;
        }
    }
}
