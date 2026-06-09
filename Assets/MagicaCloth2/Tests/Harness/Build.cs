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
        public Transform[] bones;     // for BoneCloth: the chain transforms, root first
        public Renderer renderer;     // for MeshCloth: the source renderer
        public int meshVertexCount;   // for MeshCloth: source grid vertex count

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

        /// <summary>
        /// A BoneCloth grid: <paramref name="cols"/> vertical chains (each <paramref name="rows"/> bones)
        /// laid out side by side along +X, descending along -Y. rootBones = the top bone of each column,
        /// in column order (so SequentialNonLoopMesh connection stitches adjacent columns into triangles).
        /// </summary>
        public static Rig BoneGrid(int cols, int rows, float spacing,
            Action<ClothSerializeData> configure = null, bool autoBuild = true)
        {
            var rig = new Rig();
            var holder = new GameObject("GridHolder");
            rig.objects.Add(holder);

            var clothObj = new GameObject("BoneClothGrid");
            rig.objects.Add(clothObj);
            var cloth = clothObj.AddComponent<MagicaCloth>();
            rig.cloth = cloth;

            var sd = cloth.SerializeData;
            sd.clothType = ClothProcess.ClothType.BoneCloth;

            var bones = new List<Transform>(cols * rows);
            for (int c = 0; c < cols; c++)
            {
                Transform prev = holder.transform;
                Transform colRoot = null;
                for (int r = 0; r < rows; r++)
                {
                    var b = new GameObject($"b_{c}_{r}");
                    b.transform.SetParent(prev, false);
                    b.transform.localPosition = r == 0
                        ? new Vector3(c * spacing, 0f, 0f)
                        : new Vector3(0f, -spacing, 0f);
                    if (r == 0) colRoot = b.transform;
                    bones.Add(b.transform);
                    prev = b.transform;
                }
                sd.rootBones.Add(colRoot);
            }
            rig.bones = bones.ToArray();

            configure?.Invoke(sd);

            if (autoBuild)
                cloth.BuildAndRun();

            return rig;
        }

        /// <summary>
        /// A MeshCloth over a procedurally-generated planar grid (nx*ny vertices in the XY plane,
        /// +Y up). A paint-map texture marks the top strip (uv.v &gt; <paramref name="fixedTopV"/>) as
        /// Fixed (red) and the rest as Move (green), via paintMode Texture_Fixed_Move — the same path
        /// RuntimeBuildDemo uses. Reduction is disabled by default so proxy verts == grid verts.
        /// </summary>
        public static Rig MeshGrid(int nx, int ny, float spacing, float fixedTopV = 0.9f,
            bool disableReduction = true, bool planeXZ = false, float fixedBottomV = -1f,
            Action<ClothSerializeData> configure = null, bool autoBuild = true)
        {
            var rig = new Rig();
            rig.meshVertexCount = nx * ny;

            // --- mesh ---
            var mesh = new Mesh { name = "TestGrid" };
            var verts = new Vector3[nx * ny];
            var uvs = new Vector2[nx * ny];
            for (int j = 0; j < ny; j++)
                for (int i = 0; i < nx; i++)
                {
                    int idx = j * nx + i;
                    // planeXZ=false: vertical curtain in XY (j = +Y). planeXZ=true: flat sheet in XZ (j = +Z),
                    // so the painted "far" row is pinned and the rest droops under -Y gravity.
                    verts[idx] = planeXZ
                        ? new Vector3(i * spacing, 0f, j * spacing)
                        : new Vector3(i * spacing, j * spacing, 0f);
                    // Texel-center UVs in [0,1): the selection decoder wraps uv==1.0 back to 0.0,
                    // so a top row at exactly v=1 would sample the bottom of the paint map. Insetting
                    // by half a cell avoids the wrap. Top row (j=ny-1) lands at v=(ny-0.5)/ny.
                    uvs[idx] = new Vector2((i + 0.5f) / nx, (j + 0.5f) / ny);
                }
            var tris = new List<int>((nx - 1) * (ny - 1) * 6);
            for (int j = 0; j < ny - 1; j++)
                for (int i = 0; i < nx - 1; i++)
                {
                    int a = j * nx + i, b = a + 1, c = a + nx, d = c + 1;
                    tris.Add(a); tris.Add(c); tris.Add(b);
                    tris.Add(b); tris.Add(c); tris.Add(d);
                }
            mesh.vertices = verts;
            mesh.uv = uvs;
            mesh.triangles = tris.ToArray();
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            var meshObj = new GameObject("GridMesh");
            rig.objects.Add(meshObj);
            meshObj.AddComponent<MeshFilter>().sharedMesh = mesh;
            var mr = meshObj.AddComponent<MeshRenderer>();
            rig.renderer = mr;

            // --- paint map (R=Fixed, G=Move) ---
            const int ts = 128;
            var tex = new Texture2D(ts, ts, TextureFormat.RGBA32, false);
            var px = new Color32[ts * ts];
            var red = new Color32(255, 0, 0, 255);
            var green = new Color32(0, 255, 0, 255);
            for (int py = 0; py < ts; py++)
            {
                float v = py / (float)(ts - 1);
                var col = (v > fixedTopV || v < fixedBottomV) ? red : green; // pin top and (optionally) bottom strip
                for (int pxi = 0; pxi < ts; pxi++) px[py * ts + pxi] = col;
            }
            tex.SetPixels32(px);
            tex.Apply(false);

            // --- cloth ---
            var clothObj = new GameObject("MeshCloth");
            rig.objects.Add(clothObj);
            var cloth = clothObj.AddComponent<MagicaCloth>();
            rig.cloth = cloth;

            var sd = cloth.SerializeData;
            sd.clothType = ClothProcess.ClothType.MeshCloth;
            sd.sourceRenderers.Add(mr);
            sd.paintMode = ClothSerializeData.PaintMode.Texture_Fixed_Move;
            sd.paintMaps.Add(tex);
            if (disableReduction)
            {
                sd.reductionSetting.simpleDistance = 0f;
                sd.reductionSetting.shapeDistance = 0f;
            }

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

        /// <summary>
        /// Steps until per-5-frame particle motion drops below <paramref name="threshold"/> (settled) or
        /// <paramref name="maxFrames"/> is reached; writes the frame count to <paramref name="outFrames"/>[0].
        /// </summary>
        public static IEnumerator StepUntilSettled(MagicaCloth cloth, float threshold, int maxFrames, int[] outFrames)
        {
            int frames = 0;
            var prev = ClothState.Positions(cloth);
            while (frames < maxFrames)
            {
                yield return Step(5);
                frames += 5;
                var cur = ClothState.Positions(cloth);
                if (ClothState.MaxDisplacement(prev, cur) < threshold) break;
                prev = cur;
            }
            outFrames[0] = frames;
        }
    }
}
