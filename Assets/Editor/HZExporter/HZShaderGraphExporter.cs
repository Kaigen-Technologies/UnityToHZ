using System.IO;
using UnityEngine;
using HZExporter.ShaderGraph;

namespace HZExporter
{
    // Public entry point for translating Unity ShaderGraph (.shadergraph) files
    // into hz-engine-v5 .hz.glsl shader sources.
    //
    // Pipeline (each stage operates on a typed model — only SgParser touches
    // the raw JSON):
    //
    //     .shadergraph (JSON)
    //         │
    //         ▼  SgParser           → SgGraph (Nodes, Slots, Edges, Variant)
    //         ▼  SgLowering         → list of GLSL statements with SSA temps
    //         ▼  SgCodegen          → template fill (variant picks template)
    //         ▼
    //     .hz.glsl
    //
    // Adding support for a new node type usually means one entry in the
    // SgNodes registry (SgLowering.cs); only nodes with multi-output, custom
    // varying needs, or property access need a custom NodeHandler.
    public static class HZShaderGraphExporter
    {
        public static void ExportFile(string shadergraphPath, string outDir)
        {
            string text = File.ReadAllText(shadergraphPath);
            var graph = SgParser.Parse(text);
            // Source name preserved in the generated comment (traceability),
            // but the on-disk filename is snake-cased so all engine paths stay
            // consistent — `1_UnlitTest.shadergraph` → `1_unlit_test.hz.glsl`.
            string name = Path.GetFileNameWithoutExtension(shadergraphPath);
            string fileStem = SgNames.ToSnakeCase(name);
            string glsl = SgCodegen.Emit(graph, name);

            Directory.CreateDirectory(outDir);
            string outPath = Path.Combine(outDir, fileStem + ".hz.glsl");
            File.WriteAllText(outPath, glsl);
            Debug.Log($"[HZ-SG] {name} -> {outPath} (variant={graph.Variant})");
        }
    }
}
