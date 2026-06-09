// MagicaCloth2 characterization test suite.
using System;
using System.Collections;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;

namespace MagicaCloth2.Tests
{
    /// <summary>
    /// A built test rig + the GameObjects it owns, so a scenario can tear everything down.
    /// </summary>
    public class Rig : IDisposable
    {
        public MagicaCloth cloth;
        public readonly List<GameObject> objects = new List<GameObject>();
        public Transform[] bones; // for BoneCloth: the chain transforms, root first

        public void Dispose()
        {
            for (int i = 0; i < objects.Count; i++)
                if (objects[i] != null) UnityEngine.Object.Destroy(objects[i]);
            objects.Clear();
            cloth = null;
            bones = null;
        }
    }

    /// <summary>
    /// Code-only scenario construction (no scene assets), mirroring the RuntimeBuildDemo pattern:
    /// AddComponent&lt;MagicaCloth&gt; -> set SerializeData -> BuildAndRun.
    /// </summary>
    public static class Build
    {
        /// <summary>
        /// A BoneCloth chain of <paramref name="count"/> bones spaced <paramref name="spacing"/> along
        /// <paramref name="axis"/> (chain root at world origin under a holder). The chain is NOT built yet
        /// when <paramref name="configure"/> runs; this lets the test set parameters first. BuildAndRun is
        /// called after configure unless <paramref name="autoBuild"/> is false.
        /// </summary>
        public static Rig BoneChain(int count, float3 axis, float spacing,
            Action<ClothSerializeData> configure = null, bool autoBuild = true)
        {
            var rig = new Rig();
            var bones = new Transform[count];

            var holder = new GameObject("ChainHolder");
            rig.objects.Add(holder);

            Transform prev = holder.transform;
            for (int i = 0; i < count; i++)
            {
                var bone = new GameObject($"bone{i}");
                bone.transform.SetParent(prev, false);
                bone.transform.localPosition = i == 0 ? Vector3.zero : (Vector3)(axis * spacing);
                bones[i] = bone.transform;
                prev = bone.transform;
            }
            rig.bones = bones;

            var clothObj = new GameObject("BoneCloth");
            rig.objects.Add(clothObj);
            var cloth = clothObj.AddComponent<MagicaCloth>();
            rig.cloth = cloth;

            var sd = cloth.SerializeData;
            sd.clothType = ClothProcess.ClothType.BoneCloth;
            sd.rootBones.Add(bones[0]);

            configure?.Invoke(sd);

            if (autoBuild)
                cloth.BuildAndRun();

            return rig;
        }

        // --- coroutine helpers --------------------------------------------------------------

        /// <summary>Step frames until the cloth reports valid, or timeout. Each frame == 1 sub-step.</summary>
        public static IEnumerator WaitForBuild(MagicaCloth cloth, int timeoutFrames = 300)
        {
            int f = 0;
            while ((cloth == null || !cloth.IsValid()) && f++ < timeoutFrames)
                yield return null;
        }

        /// <summary>Advance <paramref name="frames"/> simulation sub-steps.</summary>
        public static IEnumerator Step(int frames)
        {
            for (int i = 0; i < frames; i++)
                yield return null;
        }
    }
}
