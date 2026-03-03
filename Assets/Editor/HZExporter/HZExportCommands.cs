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

    [MenuItem("HZ Engine/Export Test Cube")]
    public static void ExportTestCube()
    {
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var mesh = go.GetComponent<MeshFilter>().sharedMesh;

        HZMeshExporter.Export(mesh, "", OutputDir + "/meshes/Cube.hza");

        Object.DestroyImmediate(go);
        Debug.Log("[HZ] Cube export done!");
    }

    public static void ExportXBot()
    {
        ExportFbxMeshes("Assets/New Folder/X Bot.fbx", "X_Bot");
    }

    /// <summary>
    /// Loads an FBX at assetPath, finds all Mesh sub-assets, and exports each as a
    /// ModelBlobAsset to ExportedAssets/meshes/{prefix}_{meshName}.hza.
    /// If only one mesh, uses {prefix}.hza.
    /// </summary>
    static void ExportFbxMeshes(string assetPath, string prefix)
    {
        var assets = AssetDatabase.LoadAllAssetsAtPath(assetPath);
        if (assets == null || assets.Length == 0)
        {
            Debug.LogError($"[HZ] No assets found at {assetPath}");
            return;
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
            return;
        }

        int exported = 0;
        foreach (var asset in assets)
        {
            if (asset is Mesh mesh && mesh.vertexCount > 0)
            {
                string filename = meshCount == 1
                    ? $"{prefix}.hza"
                    : $"{prefix}_{SanitizeName(mesh.name)}.hza";

                string outputPath = $"{OutputDir}/meshes/{filename}";

                HZMeshExporter.Export(mesh, "", outputPath);
                exported++;
            }
        }

        Debug.Log($"[HZ] Exported {exported} mesh(es) from {assetPath}");
    }

    static string SanitizeName(string name)
    {
        return name.Replace(" ", "_").Replace("/", "_").Replace("\\", "_");
    }
}
