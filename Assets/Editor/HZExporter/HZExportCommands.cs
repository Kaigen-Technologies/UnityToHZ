using System.Collections.Generic;
using UnityEngine;
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

    static string SanitizeName(string name)
    {
        return name.Replace(" ", "_").Replace("/", "_").Replace("\\", "_");
    }
}
