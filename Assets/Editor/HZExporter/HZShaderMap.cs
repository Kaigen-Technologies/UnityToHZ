using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace HZExporter
{
    /// <summary>
    /// Declarative Unity-shader → engine-shader + property mapping, loaded from
    /// Assets/Editor/HZExporter/shader_mapping.json. Consumed by HZGlbWriter to decide
    /// which Unity material properties feed which glTF PBR slot (and eventually which
    /// engine-shader uniform for custom shaders).
    /// </summary>
    public static class HZShaderMap
    {
        const string ConfigRelPath = "Assets/Editor/HZExporter/shader_mapping.json";

        // ---- Config schema (matches shader_mapping.json) ----
        [System.Serializable]
        public class PropertyMap
        {
            public string unity;
            public string slot;
            public string kind;
        }

        [System.Serializable]
        public class ShaderEntry
        {
            public string unityShader;
            public string engineShader;
            public string engineSkinnedShader;
            public List<PropertyMap> properties;
        }

        [System.Serializable]
        class RawConfig
        {
            public string @default;
            public List<ShaderEntry> shaders;
        }

        // ---- Loader (reloads automatically when the JSON's mtime changes) ----
        static Dictionary<string, ShaderEntry> s_byShader;
        static ShaderEntry s_default;
        static System.DateTime s_mtime;

        static void EnsureLoaded()
        {
            string path = Path.GetFullPath(ConfigRelPath);
            bool exists = File.Exists(path);
            System.DateTime mtime = exists ? File.GetLastWriteTimeUtc(path) : System.DateTime.MinValue;
            if (s_byShader != null && mtime == s_mtime) return;

            s_byShader = new Dictionary<string, ShaderEntry>();
            s_mtime = mtime;
            if (!exists)
            {
                Debug.LogWarning($"[HZShaderMap] config not found at {path}; all materials will fall back to empty mapping.");
                s_default = new ShaderEntry { properties = new List<PropertyMap>() };
                return;
            }
            string text = File.ReadAllText(path);
            var raw = JsonUtility.FromJson<RawConfig>(text);
            if (raw?.shaders != null)
            {
                foreach (var e in raw.shaders)
                {
                    if (!string.IsNullOrEmpty(e.unityShader))
                        s_byShader[e.unityShader] = e;
                }
            }
            if (!string.IsNullOrEmpty(raw?.@default) && s_byShader.TryGetValue(raw.@default, out var def))
                s_default = def;
            else
                s_default = new ShaderEntry { properties = new List<PropertyMap>() };
        }

        /// <summary>Force a reload on the next lookup.</summary>
        public static void Reload() { s_byShader = null; s_default = null; s_mtime = System.DateTime.MinValue; }

        /// <summary>Lookup the mapping entry for a material's shader, falling back to the default.</summary>
        public static ShaderEntry Lookup(Material mat)
        {
            EnsureLoaded();
            if (mat != null && mat.shader != null && s_byShader.TryGetValue(mat.shader.name, out var e))
                return e;
            return s_default;
        }

        // ---- Material → factors + textures ----
        public struct Factors
        {
            public Color baseColor;     // color4 (slot "baseColor")
            public float metallic;      // float  (slot "metallic")
            public float roughness;     // float  (slot "roughness", via 1-smoothness)
            public Color emissive;      // color3 (slot "emissive")
        }

        /// <summary>Record describing an embedded texture. role: 0=albedo, 1=normal, 2=orm, 3=emissive.</summary>
        public struct ImageRecord
        {
            public int off;
            public int size;
            public string name;
            public int role;
        }

        // Slot → PBR role. Expanded later when custom engine shaders need non-PBR routing.
        static int SlotToRole(string slot)
        {
            switch (slot)
            {
                case "albedoTex":   return 0;
                case "normalTex":   return 1;
                case "ormTex":      return 2;
                case "emissiveTex": return 3;
                default:            return -1;
            }
        }

        /// <summary>
        /// Embed all textures referenced by a material per its shader mapping, plus read factor
        /// values. Invokes <paramref name="onImage"/> for every PNG embedded (caller records it).
        /// Handles the ORM special case (two Unity textures → one combined ORM PNG).
        /// </summary>
        public static Factors EmbedMaterial(
            Material mat,
            MemoryStream bin,
            System.Action<ImageRecord> onImage)
        {
            var f = new Factors
            {
                baseColor = Color.white,
                metallic = 0f,
                roughness = 0.5f,  // 1 - default smoothness 0.5
                emissive = Color.black,
            };
            if (mat == null) return f;

            var entry = Lookup(mat);
            var props = entry.properties ?? new List<PropertyMap>();

            // Track which slots are already filled so the first matching Unity property wins
            // (matches old behavior where e.g. _BaseColor wins over _Color).
            var filled = new HashSet<string>();

            Texture2D ormMetalSmooth = null;
            Texture2D ormOcclusion = null;

            foreach (var p in props)
            {
                if (string.IsNullOrEmpty(p.unity) || string.IsNullOrEmpty(p.slot) || string.IsNullOrEmpty(p.kind)) continue;
                if (!mat.HasProperty(p.unity)) continue;
                if (filled.Contains(p.slot)) continue;

                switch (p.kind)
                {
                    case "color4":
                    {
                        if (p.slot == "baseColor") { f.baseColor = mat.GetColor(p.unity); filled.Add(p.slot); }
                        break;
                    }
                    case "color3":
                    {
                        if (p.slot == "emissive") { f.emissive = mat.GetColor(p.unity); filled.Add(p.slot); }
                        break;
                    }
                    case "float":
                    {
                        float v = mat.GetFloat(p.unity);
                        if (p.slot == "metallic")  { f.metallic = v; filled.Add(p.slot); }
                        else if (p.slot == "roughness") { f.roughness = v; filled.Add(p.slot); }
                        break;
                    }
                    case "float_oneMinus":
                    {
                        float v = mat.GetFloat(p.unity);
                        if (p.slot == "roughness") { f.roughness = 1f - v; filled.Add(p.slot); }
                        else if (p.slot == "metallic") { f.metallic = 1f - v; filled.Add(p.slot); }
                        break;
                    }
                    case "texture":
                    case "texture_normal":
                    {
                        var tex = mat.GetTexture(p.unity) as Texture2D;
                        if (tex == null) break;
                        int role = SlotToRole(p.slot);
                        if (role < 0) break;
                        bool isNormal = p.kind == "texture_normal";
                        var readable = HZGlbWriter.MakeReadable(tex, isNormal);
                        HZGlbWriter.PadTo4(bin);
                        int off = (int)bin.Position;
                        byte[] png = readable.EncodeToPNG();
                        bin.Write(png, 0, png.Length);
                        HZGlbWriter.PadTo4(bin);
                        if (readable != tex) Object.DestroyImmediate(readable);
                        onImage(new ImageRecord { off = off, size = png.Length, name = HZGlbWriter.SanitizeName(tex.name ?? $"tex_role{role}"), role = role });
                        filled.Add(p.slot);
                        break;
                    }
                    case "texture_orm_metalsmooth":
                    {
                        if (ormMetalSmooth == null) ormMetalSmooth = mat.GetTexture(p.unity) as Texture2D;
                        break;
                    }
                    case "texture_orm_occlusion":
                    {
                        if (ormOcclusion == null) ormOcclusion = mat.GetTexture(p.unity) as Texture2D;
                        break;
                    }
                }
            }

            // ORM combining — only if at least one input texture was found.
            if (!filled.Contains("ormTex") && (ormMetalSmooth != null || ormOcclusion != null))
            {
                var orm = HZGlbWriter.MakeORM(ormMetalSmooth, ormOcclusion);
                if (orm != null)
                {
                    string ormName = HZGlbWriter.SanitizeName((mat.name ?? "material") + "_ORM");
                    HZGlbWriter.PadTo4(bin);
                    int off = (int)bin.Position;
                    byte[] png = orm.EncodeToPNG();
                    bin.Write(png, 0, png.Length);
                    HZGlbWriter.PadTo4(bin);
                    Object.DestroyImmediate(orm);
                    onImage(new ImageRecord { off = off, size = png.Length, name = ormName, role = 2 });
                    filled.Add("ormTex");
                }
            }

            return f;
        }
    }
}
