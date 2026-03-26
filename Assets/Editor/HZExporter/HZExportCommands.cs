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

        // Export material
        string matAssetPath = ExportMaterial(mat, "Cube", false);

        // Export mesh with material reference
        HZMeshExporter.Export(mesh, matAssetPath, OutputDir + "/meshes/Cube.hza");

        Object.DestroyImmediate(go);
        Debug.Log("[HZ] Cube export done!");
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
        string matFilename = $"{baseName}_mat.hza";
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
                string texHzaPath = $"{OutputDir}/textures/{texName}_tex.hza";
                string texPngPath = $"{OutputDir}/textures/{texName}.png";
                texAssetPath = $"{AssetPrefix}/{texName}_tex.hza";

                // Image base path: engine appends _bc1.ktx (leading / matches engine convention)
                string imageBasePath = $"/{AssetPrefix}/{texName}";

                // Detect alpha
                bool hasAlpha = albedo.format == TextureFormat.RGBA32 ||
                                albedo.format == TextureFormat.ARGB32 ||
                                albedo.format == TextureFormat.DXT5 ||
                                albedo.alphaIsTransparency;

                HZTextureExporter.Export(albedo, imageBasePath, true, hasAlpha, texHzaPath, texPngPath);
            }
        }

        HZMaterialExporter.Export(mat, skinned, texAssetPath, matOutputPath);
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

    public static void ExportScene()
    {
        var scene = SceneManager.GetActiveScene();
        var roots = scene.GetRootGameObjects();
        var entities = new List<HZSceneExporter.EntityExportInfo>();

        foreach (var root in roots)
        {
            // Static meshes
            foreach (var mf in root.GetComponentsInChildren<MeshFilter>())
            {
                var mr = mf.GetComponent<MeshRenderer>();
                if (mr == null || mf.sharedMesh == null) continue;

                string safeName = SanitizeName(mf.gameObject.name);
                var mesh = mf.sharedMesh;
                var mat = mr.sharedMaterial;

                string matAssetPath = ExportMaterial(mat, safeName, false);
                string modelPath = $"{OutputDir}/meshes/{safeName}.hza";
                string modelAssetPath = $"{AssetPrefix}/{safeName}.hza";
                HZMeshExporter.Export(mesh, matAssetPath, modelPath);

                var t = mf.transform;
                entities.Add(new HZSceneExporter.EntityExportInfo {
                    name = mf.gameObject.name,
                    position = t.position,
                    rotation = t.rotation,
                    scale = t.lossyScale,
                    visible = mr.enabled,
                    castsShadow = mr.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off,
                    bounds = mr.bounds,
                    modelAssetPath = modelAssetPath,
                    materialAssetPath = matAssetPath,
                    skinned = false,
                });

                Debug.Log($"[HZ] Scene entity: {mf.gameObject.name} (static) → {modelAssetPath}");
            }

            // Skinned meshes
            var allSmrs = root.GetComponentsInChildren<SkinnedMeshRenderer>();
            var validSmrs = new List<SkinnedMeshRenderer>();
            foreach (var s in allSmrs)
                if (s.sharedMesh != null && s.bones != null && s.bones.Length > 0)
                    validSmrs.Add(s);

            if (validSmrs.Count == 0) continue;

            // Find primary SMR (most bones) for skeleton/animation
            SkinnedMeshRenderer primarySmr = validSmrs[0];
            foreach (var s in validSmrs)
                if (s.bones.Length > primarySmr.bones.Length)
                    primarySmr = s;

            string rootName = SanitizeName(root.name);

            // Export all SMRs as one multi-mesh model
            var smrArray = validSmrs.ToArray();
            var matPaths = new string[validSmrs.Count];
            for (int si = 0; si < validSmrs.Count; si++)
            {
                string smrName = $"{rootName}_{SanitizeName(validSmrs[si].gameObject.name)}";
                matPaths[si] = ExportMaterial(validSmrs[si].sharedMaterial, smrName, true);
            }

            string skinnedModelPath = $"{OutputDir}/meshes/{rootName}.hza";
            string skinnedModelAssetPath = $"{AssetPrefix}/{rootName}.hza";
            HZMeshExporter.ExportMultiSkinned(smrArray, matPaths, primarySmr, skinnedModelPath);

            // Export animations
            string animAssetPath = null;
            var animator = root.GetComponent<Animator>();
            if (animator != null && animator.runtimeAnimatorController != null)
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
                        HZAnimationExporter.Export(clip, root, boneNameToIndex, primarySmr.bones.Length, animOutputPath);
                        Debug.Log($"[HZ] Exported animation: {clip.name} → {animOutputPath}");
                    }

                    string firstClipName = SanitizeName(clips[0].name);
                    animAssetPath = $"{AssetPrefix}/{firstClipName}.hza";
                }
            }

            var rootT = root.transform;
            entities.Add(new HZSceneExporter.EntityExportInfo {
                name = root.name,
                position = rootT.position,
                rotation = rootT.rotation,
                scale = rootT.lossyScale,
                visible = true,
                castsShadow = true,
                bounds = primarySmr.bounds,
                modelAssetPath = skinnedModelAssetPath,
                materialAssetPath = matPaths[0],
                animationAssetPath = animAssetPath,
                skinned = true,
            });

            Debug.Log($"[HZ] Scene entity: {root.name} (skinned, {validSmrs.Count} meshes{(animAssetPath != null ? ", animated" : "")}) → {skinnedModelAssetPath}");
        }

        // Cameras
        foreach (var root in roots)
        {
            var cam = root.GetComponent<Camera>();
            if (cam == null) continue;

            var t = root.transform;
            entities.Add(new HZSceneExporter.EntityExportInfo {
                name = root.name,
                position = t.position,
                eulerAngles = t.eulerAngles,
                scale = t.lossyScale,
                visible = true,
                castsShadow = false,
                isCamera = true,
                fov = cam.fieldOfView,
                nearPlane = cam.nearClipPlane,
                farPlane = cam.farClipPlane,
                rotation = t.rotation,
            });

            Debug.Log($"[HZ] Scene entity: {root.name} (camera, fov={cam.fieldOfView})");
        }

        // Directional lights
        foreach (var root in roots)
        {
            foreach (var light in root.GetComponentsInChildren<Light>())
            {
                var t = light.transform;
                Color c = light.color.linear;

                if (light.type == LightType.Directional)
                {
                    Vector3 fwd = t.forward;
                    Vector3 dir = new Vector3(-fwd.x, fwd.y, fwd.z);

                    entities.Add(new HZSceneExporter.EntityExportInfo {
                        name = light.gameObject.name,
                        position = t.position,
                        rotation = t.rotation,
                        scale = Vector3.one,
                        visible = true,
                        castsShadow = false,
                        isDirectionalLight = true,
                        lightDirection = dir,
                        lightColor = new Vector3(c.r, c.g, c.b),
                        lightIntensity = light.intensity,
                        castShadow = false,
                        shadowDistance = 0,
                    });
                    Debug.Log($"[HZ] Scene entity: {light.gameObject.name} (directional light)");
                }
                else if (light.type == LightType.Point)
                {
                    entities.Add(new HZSceneExporter.EntityExportInfo {
                        name = light.gameObject.name,
                        position = t.position,
                        rotation = t.rotation,
                        scale = Vector3.one,
                        visible = true,
                        castsShadow = false,
                        isPointLight = true,
                        lightColor = new Vector3(c.r, c.g, c.b),
                        pointLightRadius = light.range,
                        lightIntensity = light.intensity,
                    });
                    Debug.Log($"[HZ] Scene entity: {light.gameObject.name} (point light, range={light.range})");
                }
            }
        }

        // Scene settings (ambient light)
        {
            Color ambient = RenderSettings.ambientLight.linear;
            entities.Add(new HZSceneExporter.EntityExportInfo {
                name = "SceneSettings",
                position = Vector3.zero,
                rotation = Quaternion.identity,
                scale = Vector3.one,
                visible = true,
                castsShadow = false,
                isSceneSettings = true,
                ambientColor = new Vector3(ambient.r, ambient.g, ambient.b),
                ambientIntensity = RenderSettings.ambientIntensity,
            });
            Debug.Log($"[HZ] Scene entity: SceneSettings (ambient)");
        }

        if (entities.Count == 0)
        {
            Debug.LogError("[HZ] No exportable entities found in scene");
            return;
        }

        string scenePath = $"{OutputDir}/scenes/SampleScene.hza";
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
