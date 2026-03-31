using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using HZExporter;

/// <summary>
/// CLI-callable export commands. Each public static method can be invoked via:
///   unity_cmd.ps1 -cmd "exec:HZExportCommands.MethodName"
/// </summary>
public static class HZExportCommands
{
    const string OutputDir = "D:/dev/unity/UnityToHZ/ExportedAssets";

    // Asset-relative prefix (how the engine sees the paths, relative to assets/)
    const string AssetPrefix = "unity";

    [MenuItem("HZ Engine/Export Test Cube")]
    public static void ExportTestCube()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var meshFilter = go.GetComponent<MeshFilter>();
        var meshRenderer = go.GetComponent<MeshRenderer>();
        var mesh = meshFilter.sharedMesh;
        var mat = meshRenderer.sharedMaterial;

        HZGlbWriter.ExportStaticMesh(mesh, mat, OutputDir + "/meshes/Cube.glb");

        Object.DestroyImmediate(go);
        Debug.Log("[HZ] Cube GLB export done!");
    }

    public static void ExportTexturedCube()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var meshFilter = go.GetComponent<MeshFilter>();
        var mesh = meshFilter.sharedMesh;

        // Create material with a texture
        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>("Assets/TutorialInfo/Icons/URP.png");
        if (tex == null)
        {
            Debug.LogError("[HZ] Could not find URP.png texture");
            Object.DestroyImmediate(go);
            return;
        }

        var mat = new Material(Shader.Find("Universal Render Pipeline/Lit"));
        mat.SetTexture("_BaseMap", tex);
        mat.SetColor("_BaseColor", Color.white);

        string matAssetPath = ExportMaterial(mat, "TexturedCube", false);
        HZMeshExporter.Export(mesh, matAssetPath, OutputDir + "/meshes/TexturedCube.hza");

        Object.DestroyImmediate(mat);
        Object.DestroyImmediate(go);
        Debug.Log("[HZ] Textured cube export done!");
    }

    public static void ExportXBot()
    {
        ExportFbxMeshes("Assets/New Folder/X Bot.fbx", "X_Bot");
    }

    /// <summary>
    /// Loads an FBX at assetPath, finds all Mesh sub-assets, and exports each as a
    /// ModelBlobAsset. Detects SkinnedMeshRenderer to export with skinning data.
    /// </summary>
    static void ExportFbxMeshes(string assetPath, string prefix)
    {
        var assets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
        if (assets == null || assets.Length == 0)
        {
            Debug.LogError($"[HZ] No assets found at {assetPath}");
            return;
        }

        // Instantiate the FBX to access SkinnedMeshRenderers (they need a live hierarchy)
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
        GameObject instance = null;
        if (prefab != null)
            instance = Object.Instantiate(prefab);

        // Build a map of mesh name → SkinnedMeshRenderer from the instance
        var smrByMeshName = new Dictionary<string, SkinnedMeshRenderer>();
        if (instance != null)
        {
            foreach (var smr in instance.GetComponentsInChildren<SkinnedMeshRenderer>())
            {
                if (smr.sharedMesh != null)
                    smrByMeshName[smr.sharedMesh.name] = smr;
            }
        }

        int meshCount = 0;
        foreach (var asset in assets)
        {
            if (asset is Mesh mesh && mesh.vertexCount > 0)
                meshCount++;
        }

        if (meshCount == 0)
        {
            Debug.LogError($"[HZ] No meshes found in {assetPath}");
            if (instance != null) Object.DestroyImmediate(instance);
            return;
        }

        int exported = 0;
        foreach (var asset in assets)
        {
            if (asset is Mesh mesh && mesh.vertexCount > 0)
            {
                string safeName = meshCount == 1
                    ? prefix
                    : $"{prefix}_{SanitizeName(mesh.name)}";
                string filename = safeName + ".hza";
                string outputPath = $"{OutputDir}/meshes/{filename}";

                bool skinned = smrByMeshName.ContainsKey(mesh.name);

                // Export material
                Material mat = null;
                if (skinned && smrByMeshName.TryGetValue(mesh.name, out var smr))
                {
                    mat = smr.sharedMaterial;
                }
                string matAssetPath = ExportMaterial(mat, safeName, skinned);

                if (skinned)
                    HZMeshExporter.Export(smrByMeshName[mesh.name], matAssetPath, outputPath);
                else
                    HZMeshExporter.Export(mesh, matAssetPath, outputPath);

                exported++;
            }
        }

        if (instance != null)
            Object.DestroyImmediate(instance);

        Debug.Log($"[HZ] Exported {exported} mesh(es) from {assetPath}");
    }

    /// <summary>
    /// Export a material and its textures. Returns the asset-relative path to the material .hza.
    /// </summary>
    static string ExportMaterial(Material mat, string baseName, bool skinned)
    {
        string matFilename = $"{baseName}_mat.hzt";
        string matOutputPath = $"{OutputDir}/materials/{matFilename}";
        string matAssetPath = $"{AssetPrefix}/{matFilename}";

        // Check for albedo texture
        string texAssetPath = null;
        if (mat != null)
        {
            Texture2D albedo = null;
            if (mat.HasProperty("_BaseMap"))
                albedo = mat.GetTexture("_BaseMap") as Texture2D;
            else if (mat.HasProperty("_MainTex"))
                albedo = mat.GetTexture("_MainTex") as Texture2D;

            if (albedo != null)
            {
                string texName = $"{baseName}_albedo";
                string texHztPath = $"{OutputDir}/textures/{texName}_tex.hzt";
                string texPngPath = $"{OutputDir}/textures/{texName}.png";
                texAssetPath = $"{AssetPrefix}/{texName}_tex.hzt";

                string imageBasePath = $"/{AssetPrefix}/{texName}";

                bool hasAlpha = albedo.format == TextureFormat.RGBA32 ||
                                albedo.format == TextureFormat.ARGB32 ||
                                albedo.format == TextureFormat.DXT5 ||
                                albedo.alphaIsTransparency;

                HZTextureExporter.ExportHzt(albedo, imageBasePath, true, hasAlpha, texHztPath, texPngPath);
            }
        }

        HZMaterialExporter.ExportHzt(mat, skinned, texAssetPath, matOutputPath);
        return matAssetPath;
    }

    public static void ExportBodyBlock()
    {
        // Get skeleton bone order from X Bot — must match the model .hza we exported.
        var xbotPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/New Folder/X Bot.fbx");
        var xbotInstance = Object.Instantiate(xbotPrefab);

        SkinnedMeshRenderer smr = null;
        foreach (var s in xbotInstance.GetComponentsInChildren<SkinnedMeshRenderer>())
        {
            if (s.sharedMesh != null && s.sharedMesh.name == "Beta_Surface")
            {
                smr = s;
                break;
            }
        }

        if (smr == null || smr.bones == null || smr.bones.Length == 0)
        {
            Debug.LogError("[HZ] No Beta_Surface SkinnedMeshRenderer found on X Bot");
            Object.DestroyImmediate(xbotInstance);
            return;
        }

        Debug.Log($"[HZ] Using X Bot '{smr.sharedMesh.name}' SMR with {smr.bones.Length} bones");

        var boneNameToIndex = new Dictionary<string, int>();
        for (int i = 0; i < smr.bones.Length; i++)
            boneNameToIndex[smr.bones[i].name] = i;

        int jointCount = smr.bones.Length;
        Object.DestroyImmediate(xbotInstance);

        var clips = AssetDatabase.LoadAllAssetsAtPath("Assets/New Folder/Body Block.fbx");
        AnimationClip clip = null;
        foreach (var asset in clips)
        {
            if (asset is AnimationClip ac && !ac.name.StartsWith("__preview__"))
            {
                clip = ac;
                break;
            }
        }

        if (clip == null)
        {
            Debug.LogError("[HZ] No AnimationClip found in Body Block.fbx");
            return;
        }

        var bbPrefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/New Folder/Body Block.fbx");
        var bbInstance = Object.Instantiate(bbPrefab);
        var bbSmr = bbInstance.GetComponentInChildren<SkinnedMeshRenderer>();

        var animBoneMap = new Dictionary<string, int>();
        if (bbSmr != null)
        {
            foreach (var bone in bbSmr.bones)
            {
                if (bone != null && boneNameToIndex.TryGetValue(bone.name, out int idx))
                    animBoneMap[bone.name] = idx;
            }
        }

        Debug.Log($"[HZ] Matched {animBoneMap.Count} Body Block bones to X Bot skeleton (XBot has {jointCount}, BB has {bbSmr?.bones.Length ?? 0})");

        string outputPath = $"{OutputDir}/animations/Body_Block.hza";
        HZAnimationExporter.Export(clip, bbInstance, animBoneMap, jointCount, outputPath);

        Object.DestroyImmediate(bbInstance);
        Debug.Log("[HZ] Body Block animation export done!");
    }

    static System.Random s_stableIdRng;

    static ulong NextStableId()
    {
        byte[] buf = new byte[8];
        s_stableIdRng.NextBytes(buf);
        ulong id = System.BitConverter.ToUInt64(buf, 0);
        if (id == 0) id = 1;
        return id;
    }

    public static void ExportScene()
    {
        var scene = SceneManager.GetActiveScene();
        var roots = scene.GetRootGameObjects();
        var entities = new List<HZSceneExporter.EntityExportInfo>();

        s_stableIdRng = new System.Random();

        // Map from GameObject instance ID to stable ID for parent references
        var goToStableId = new Dictionary<int, ulong>();

        // First pass: assign stable IDs to all GameObjects
        foreach (var root in roots)
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                goToStableId[t.gameObject.GetInstanceID()] = NextStableId();

        // Collect GameObjects to skip: skeleton bones + SMR children of skinned roots
        var skipGOs = new HashSet<int>();
        foreach (var root in roots)
        {
            var animator = root.GetComponent<Animator>();
            if (animator == null) continue;

            // Skip all descendants of the skinned root EXCEPT the root itself
            foreach (var childT in root.GetComponentsInChildren<Transform>(true))
            {
                if (childT.gameObject == root) continue;
                skipGOs.Add(childT.gameObject.GetInstanceID());
            }
        }

        // Second pass: export each GameObject as an entity
        foreach (var root in roots)
        {
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                var go = t.gameObject;

                // Skip skeleton bones and SMR children of skinned roots
                if (skipGOs.Contains(go.GetInstanceID())) continue;

                ulong stableId = goToStableId[go.GetInstanceID()];

                // Find nearest exported ancestor (skip skeleton bones)
                ulong parentStableId = 0;
                Transform ancestor = t.parent;
                while (ancestor != null)
                {
                    int ancestorId = ancestor.gameObject.GetInstanceID();
                    if (!skipGOs.Contains(ancestorId) && goToStableId.TryGetValue(ancestorId, out ulong pid))
                    {
                        parentStableId = pid;
                        break;
                    }
                    ancestor = ancestor.parent;
                }

                // Base entity info — every entity gets this
                var info = new HZSceneExporter.EntityExportInfo {
                    name = go.name,
                    position = t.localPosition,
                    rotation = t.localRotation,
                    scale = t.localScale,
                    visible = go.activeInHierarchy,
                    castsShadow = false,
                    stableId = stableId,
                    parentStableId = parentStableId,
                };

                // Check for skinned mesh (root of skinned hierarchy)
                var allSmrs = go.GetComponentsInChildren<SkinnedMeshRenderer>();
                var validSmrs = new List<SkinnedMeshRenderer>();
                // Only consider direct SMRs on this GO or its skeleton children
                foreach (var s in allSmrs)
                    if (s.sharedMesh != null && s.bones != null && s.bones.Length > 0)
                        validSmrs.Add(s);

                if (validSmrs.Count > 0 && go.GetComponent<Animator>() != null)
                {
                    // This is a skinned mesh root — export multi-mesh model
                    SkinnedMeshRenderer primarySmr = validSmrs[0];
                    foreach (var s in validSmrs)
                        if (s.bones.Length > primarySmr.bones.Length)
                            primarySmr = s;

                    string rootName = SanitizeName(go.name);
                    var smrArray = validSmrs.ToArray();
                    var matPaths = new string[validSmrs.Count];
                    for (int si = 0; si < validSmrs.Count; si++)
                    {
                        string smrName = $"{rootName}_{SanitizeName(validSmrs[si].gameObject.name)}";
                        matPaths[si] = ExportMaterial(validSmrs[si].sharedMaterial, smrName, true);
                    }

                    string modelPath = $"{OutputDir}/meshes/{rootName}.hza";
                    info.modelAssetPath = $"{AssetPrefix}/{rootName}.hza";
                    info.materialAssetPath = matPaths[0];
                    info.bounds = primarySmr.bounds;
                    info.castsShadow = true;
                    info.skinned = true;
                    HZMeshExporter.ExportMultiSkinned(smrArray, matPaths, primarySmr, modelPath);

                    // Animations
                    var animator = go.GetComponent<Animator>();
                    if (animator.runtimeAnimatorController != null)
                    {
                        var clips = animator.runtimeAnimatorController.animationClips;
                        if (clips.Length > 0)
                        {
                            var boneNameToIndex = new Dictionary<string, int>();
                            for (int bi = 0; bi < primarySmr.bones.Length; bi++)
                                if (primarySmr.bones[bi] != null)
                                    boneNameToIndex[primarySmr.bones[bi].name] = bi;

                            for (int ci = 0; ci < clips.Length; ci++)
                            {
                                var clip = clips[ci];
                                string clipName = SanitizeName(clip.name);
                                string animOutputPath = $"{OutputDir}/animations/{clipName}.hza";
                                HZAnimationExporter.Export(clip, go, boneNameToIndex, primarySmr.bones.Length, animOutputPath);
                            }
                            info.animationAssetPath = $"{AssetPrefix}/{SanitizeName(clips[0].name)}.hza";
                        }
                    }

                    Debug.Log($"[HZ] Entity: {go.name} (skinned, {validSmrs.Count} meshes)");
                }
                // Static mesh
                else if (go.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null
                         && go.TryGetComponent<MeshRenderer>(out var mr))
                {
                    string safeName = SanitizeName(go.name);
                    string matAssetPath = ExportMaterial(mr.sharedMaterial, safeName, false);
                    string modelPath = $"{OutputDir}/meshes/{safeName}.hza";
                    HZMeshExporter.Export(mf.sharedMesh, matAssetPath, modelPath);

                    info.modelAssetPath = $"{AssetPrefix}/{safeName}.hza";
                    info.materialAssetPath = matAssetPath;
                    info.bounds = mr.bounds;
                    info.castsShadow = mr.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off;

                    Debug.Log($"[HZ] Entity: {go.name} (static mesh)");
                }
                // Camera
                else if (go.TryGetComponent<Camera>(out var cam))
                {
                    info.isCamera = true;
                    info.fov = cam.fieldOfView;
                    info.nearPlane = cam.nearClipPlane;
                    info.farPlane = cam.farClipPlane;

                    Debug.Log($"[HZ] Entity: {go.name} (camera)");
                }
                // Lights
                else if (go.TryGetComponent<Light>(out var light))
                {
                    Color c = light.color.linear;
                    if (light.type == LightType.Directional)
                    {
                        Vector3 fwd = t.forward;
                        info.isDirectionalLight = true;
                        info.lightDirection = new Vector3(-fwd.x, fwd.y, fwd.z);
                        info.lightColor = new Vector3(c.r, c.g, c.b);
                        info.lightIntensity = light.intensity;
                        info.castShadow = light.shadows != LightShadows.None;
                        info.shadowDistance = QualitySettings.shadowDistance;
                        Debug.Log($"[HZ] Entity: {go.name} (directional light, shadows={info.castShadow})");
                    }
                    else if (light.type == LightType.Point)
                    {
                        info.isPointLight = true;
                        info.lightColor = new Vector3(c.r, c.g, c.b);
                        info.pointLightRadius = light.range;
                        info.lightIntensity = light.intensity;
                        Debug.Log($"[HZ] Entity: {go.name} (point light)");
                    }
                    else
                    {
                        Debug.Log($"[HZ] Entity: {go.name} (empty)");
                    }
                }
                else
                {
                    // Empty / container object
                    Debug.Log($"[HZ] Entity: {go.name} (empty)");
                }

                entities.Add(info);
            }
        }

        // Scene settings (synthetic entity, no GameObject)
        {
            Color ambient = RenderSettings.ambientLight.linear;
            entities.Add(new HZSceneExporter.EntityExportInfo {
                name = "SceneSettings",
                position = Vector3.zero,
                rotation = Quaternion.identity,
                scale = Vector3.one,
                visible = true,
                stableId = NextStableId(),
                isSceneSettings = true,
                ambientColor = new Vector3(ambient.r, ambient.g, ambient.b),
                ambientIntensity = RenderSettings.ambientIntensity,
            });
        }

        string scenePath = $"{OutputDir}/scenes/SampleScene.hzt";
        string sceneDir = $"{OutputDir}/scenes/SampleScene";
        string sceneDirAssetPath = $"{AssetPrefix}/SampleScene";

        HZSceneExporter.Export(entities.ToArray(), scenePath, sceneDir, sceneDirAssetPath);
        Debug.Log($"[HZ] Scene export done! {entities.Count} entities");
    }

    static string SanitizeName(string name)
    {
        return name.Replace(" ", "_").Replace("/", "_").Replace("\\", "_");
    }
}
