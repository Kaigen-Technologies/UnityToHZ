using UnityEngine;
using UnityEditor;
using HZExporter;

public static class CompileTest
{
    [MenuItem("HZ Engine/Export Test Cube")]
    public static void ExportTestCube()
    {
        // Create a temporary cube to get its mesh
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        var mesh = go.GetComponent<MeshFilter>().sharedMesh;

        string outputDir = "D:/dev/unity/UnityToHZ/ExportedAssets";
        string outputPath = outputDir + "/meshes/Cube.hza";

        HZMeshExporter.Export(mesh, "", outputPath);

        Object.DestroyImmediate(go);

        Debug.Log("[HZ] Cube export done!");
    }
}
