using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using HZExporter;

/// <summary>
/// CLI-callable export commands. Each public static method can be invoked via remote TCP:
///   unity_cmd.ps1 -cmd "exec:HZExportCommands.MethodName"
/// </summary>
public static class HZExportCommands
{
    static readonly string OutputDir = System.IO.Path.GetFullPath("ExportedAssets");

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

    public static void ExportXBotGlb()
    {
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>("Assets/New Folder/X Bot.fbx");
        var instance = Object.Instantiate(prefab);

        var allSmrs = instance.GetComponentsInChildren<SkinnedMeshRenderer>();
        var validSmrs = new List<SkinnedMeshRenderer>();
        foreach (var s in allSmrs)
            if (s.sharedMesh != null && s.bones != null && s.bones.Length > 0)
                validSmrs.Add(s);

        if (validSmrs.Count == 0)
        {
            Debug.LogError("[HZ] No valid SMRs found on X Bot");
            Object.DestroyImmediate(instance);
            return;
        }

        SkinnedMeshRenderer primarySmr = validSmrs[0];
        foreach (var s in validSmrs)
            if (s.bones.Length > primarySmr.bones.Length)
                primarySmr = s;

        var smrArray = validSmrs.ToArray();
        var mats = new Material[validSmrs.Count];
        for (int i = 0; i < validSmrs.Count; i++)
            mats[i] = validSmrs[i].sharedMaterial;

        HZGlbWriter.ExportSkinnedMesh(smrArray, mats, primarySmr, OutputDir + "/glb/XBot.glb");

        Object.DestroyImmediate(instance);
        Debug.Log("[HZ] X Bot GLB export done!");
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

    [MenuItem("HZ Engine/Export Active Scene")]
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

        // Collect GameObjects to skip: skeleton bones + SMR children of ANY Animator in the hierarchy
        var skipGOs = new HashSet<int>();
        foreach (var root in roots)
        {
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            {
                var animGO = animator.gameObject;
                foreach (var childT in animGO.GetComponentsInChildren<Transform>(true))
                {
                    if (childT.gameObject == animGO) continue;
                    skipGOs.Add(childT.gameObject.GetInstanceID());
                }
            }
        }

        // GLB dedup: track exported meshes by source asset key → engine asset path
        var exportedGlbs = new Dictionary<string, string>();

        // Deferred skinned mesh exports: collect animations per character before exporting
        // Key = character name (FBX filename with @... stripped)
        var deferredSkinned = new Dictionary<string, DeferredSkinnedExport>();

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
                    worldPosition = t.position,
                    worldRotation = t.rotation,
                    worldScale = t.lossyScale,
                    visible = go.activeInHierarchy,
                    castsShadow = false,
                    stableId = stableId,
                    parentStableId = parentStableId,
                };

                // Check for skinned mesh (root of skinned hierarchy)
                var allSmrs = go.GetComponentsInChildren<SkinnedMeshRenderer>();
                var validSmrs = new List<SkinnedMeshRenderer>();
                foreach (var s in allSmrs)
                    if (s.sharedMesh != null && s.bones != null && s.bones.Length > 0)
                        validSmrs.Add(s);

                if (validSmrs.Count > 0 && go.GetComponent<Animator>() != null)
                {
                    SkinnedMeshRenderer primarySmr = validSmrs[0];
                    foreach (var s in validSmrs)
                        if (s.bones.Length > primarySmr.bones.Length)
                            primarySmr = s;

                    // Character name = FBX filename with @... stripped
                    string charName = GetCharacterName(primarySmr.sharedMesh);
                    string glbAssetPath = $"{AssetPrefix}/{charName}.glb";

                    // Defer export — collect mesh info on first encounter
                    if (!deferredSkinned.ContainsKey(charName))
                    {
                        var smrArray = validSmrs.ToArray();
                        var mats = new Material[validSmrs.Count];
                        for (int si = 0; si < validSmrs.Count; si++)
                            mats[si] = validSmrs[si].sharedMaterial;

                        deferredSkinned[charName] = new DeferredSkinnedExport {
                            smrs = smrArray,
                            materials = mats,
                            primarySmr = primarySmr,
                            glbPath = $"{OutputDir}/glb/{charName}.glb",
                            glbAssetPath = glbAssetPath,
                            animClips = new List<HZGlbWriter.AnimClipInfo>(),
                        };
                    }

                    info.modelAssetPath = glbAssetPath;
                    info.bounds = primarySmr.bounds;
                    info.castsShadow = true;

                    // Collect animation clips for this character
                    var animator = go.GetComponent<Animator>();
                    if (animator.runtimeAnimatorController != null)
                    {
                        var deferred = deferredSkinned[charName];
                        var existingNames = new HashSet<string>();
                        foreach (var existing in deferred.animClips)
                            existingNames.Add(existing.name);

                        foreach (var clip in animator.runtimeAnimatorController.animationClips)
                        {
                            string clipName = SanitizeName(clip.name);
                            if (!existingNames.Contains(clipName))
                            {
                                deferred.animClips.Add(new HZGlbWriter.AnimClipInfo {
                                    clip = clip,
                                    name = clipName,
                                    skeletonRoot = go,
                                });
                                existingNames.Add(clipName);
                            }
                        }

                        if (deferred.animClips.Count > 0)
                        {
                            // Point to the cooked animation .hza that the engine extracts from the GLB
                            // Engine cooks unity/SeaGull.glb → cooked/unity/SeaGull/{clipName}.hza
                            string firstClipName = SanitizeName(animator.runtimeAnimatorController.animationClips[0].name);
                            info.animationAssetPath = $"{AssetPrefix}/{charName}/{firstClipName}.hza";
                        }
                    }

                    Debug.Log($"[HZ] Entity: {go.name} (skinned, model={glbAssetPath})");
                }
                // Static mesh — export as .glb, dedup by source mesh
                else if (go.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null
                         && go.TryGetComponent<MeshRenderer>(out var mr))
                {
                    Mesh mesh = mf.sharedMesh;
                    string meshKey = GetMeshKey(mesh);

                    if (!exportedGlbs.TryGetValue(meshKey, out string glbAssetPath))
                    {
                        string glbName = GetMeshSourceName(mesh);
                        string glbPath = $"{OutputDir}/glb/{glbName}.glb";
                        glbAssetPath = $"{AssetPrefix}/{glbName}.glb";

                        HZGlbWriter.ExportStaticMesh(mesh, mr.sharedMaterial, glbPath);
                        exportedGlbs[meshKey] = glbAssetPath;
                        Debug.Log($"[HZ] Exported GLB: {glbName}.glb (static, from {meshKey})");
                    }

                    info.modelAssetPath = glbAssetPath;
                    info.bounds = mr.bounds;
                    info.castsShadow = mr.shadowCastingMode != UnityEngine.Rendering.ShadowCastingMode.Off;

                    Debug.Log($"[HZ] Entity: {go.name} (static, model={info.modelAssetPath})");
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

        // Export deferred skinned meshes (with collected animations)
        foreach (var kvp in deferredSkinned)
        {
            var d = kvp.Value;
            var animArray = d.animClips.Count > 0 ? d.animClips.ToArray() : null;
            HZGlbWriter.ExportSkinnedMesh(d.smrs, d.materials, d.primarySmr, d.glbPath, animArray);
            int animCount = animArray?.Length ?? 0;
            Debug.Log($"[HZ] Exported GLB: {kvp.Key}.glb (skinned, {animCount} animations)");
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

        string sceneName = SanitizeName(scene.name);
        string scenePath = $"{OutputDir}/scenes/{sceneName}.hzt";
        string sceneDir = $"{OutputDir}/scenes/{sceneName}";
        string sceneDirAssetPath = $"{AssetPrefix}/{sceneName}";

        HZSceneExporter.Export(entities.ToArray(), scenePath, sceneDir, sceneDirAssetPath);
        Debug.Log($"[HZ] Scene export done! {entities.Count} entities, {exportedGlbs.Count} unique GLBs");
    }

    struct DeferredSkinnedExport
    {
        public SkinnedMeshRenderer[] smrs;
        public Material[] materials;
        public SkinnedMeshRenderer primarySmr;
        public string glbPath;
        public string glbAssetPath;
        public List<HZGlbWriter.AnimClipInfo> animClips;
    }

    /// <summary>
    /// Get character name from mesh FBX path. Strips @... suffix.
    /// E.g. "Big@Idle.fbx" → "Big", "SeaGull.fbx" → "SeaGull"
    /// </summary>
    static string GetCharacterName(Mesh mesh)
    {
        string name = GetMeshSourceName(mesh);
        int atIdx = name.IndexOf('@');
        if (atIdx > 0) name = name.Substring(0, atIdx);
        return name;
    }

    static string GetMeshKey(Mesh mesh)
    {
        string assetPath = AssetDatabase.GetAssetPath(mesh);
        if (string.IsNullOrEmpty(assetPath) || assetPath.StartsWith("Library/"))
            return $"builtin:{mesh.name}";
        return assetPath;
    }

    static string GetMeshSourceName(Mesh mesh)
    {
        string assetPath = AssetDatabase.GetAssetPath(mesh);
        if (!string.IsNullOrEmpty(assetPath) && !assetPath.StartsWith("Library/"))
            return SanitizeName(System.IO.Path.GetFileNameWithoutExtension(assetPath));
        return SanitizeName(mesh.name);
    }

    /// <summary>
    /// Export Big@Idle.fbx animation using the old .hza exporter + dump reference values as text.
    /// </summary>
    public static void ExportBigIdleReference()
    {
        // Load the SeaGull model to get the skeleton (it uses the "Big" rig)
        string modelFbx = "Assets/StylizedCharacterPack/Models/SeaGull.fbx";
        string animFbx = "Assets/StylizedCharacterPack/Animations/Big@Idle.fbx";

        var modelPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(modelFbx);
        var modelInstance = Object.Instantiate(modelPrefab);
        var smr = modelInstance.GetComponentInChildren<SkinnedMeshRenderer>();

        if (smr == null || smr.bones == null || smr.bones.Length == 0)
        {
            Debug.LogError("[HZ] No SMR found on SeaGull");
            Object.DestroyImmediate(modelInstance);
            return;
        }

        // Build bone name → index from the model's skeleton
        var boneNameToIndex = new Dictionary<string, int>();
        for (int i = 0; i < smr.bones.Length; i++)
            if (smr.bones[i] != null)
                boneNameToIndex[smr.bones[i].name] = i;

        int jointCount = smr.bones.Length;
        Object.DestroyImmediate(modelInstance);

        // Load animation clip from Big@Idle.fbx
        var assets = AssetDatabase.LoadAllAssetsAtPath(animFbx);
        AnimationClip clip = null;
        foreach (var asset in assets)
        {
            if (asset is AnimationClip ac && !ac.name.StartsWith("__preview__"))
            {
                clip = ac;
                break;
            }
        }

        if (clip == null)
        {
            Debug.LogError($"[HZ] No AnimationClip found in {animFbx}");
            return;
        }

        Debug.Log($"[HZ] Big@Idle clip: name={clip.name}, length={clip.length}s, " +
                  $"skeleton has {jointCount} joints, matched bones={boneNameToIndex.Count}");

        // Export .hza using old exporter
        var animPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(animFbx);
        var animInstance = Object.Instantiate(animPrefab);

        var animBoneMap = new Dictionary<string, int>();
        var animSmr = animInstance.GetComponentInChildren<SkinnedMeshRenderer>();
        if (animSmr != null)
        {
            foreach (var bone in animSmr.bones)
                if (bone != null && boneNameToIndex.TryGetValue(bone.name, out int idx))
                    animBoneMap[bone.name] = idx;
        }
        // Fallback: match by transform names in hierarchy
        if (animBoneMap.Count == 0)
        {
            foreach (var t in animInstance.GetComponentsInChildren<Transform>())
                if (boneNameToIndex.TryGetValue(t.name, out int idx) && !animBoneMap.ContainsKey(t.name))
                    animBoneMap[t.name] = idx;
        }

        Debug.Log($"[HZ] Matched {animBoneMap.Count}/{jointCount} bones for Big@Idle");

        string hzaPath = $"{OutputDir}/animations/Big_Idle_ref.hza";
        HZAnimationExporter.Export(clip, animInstance, animBoneMap, jointCount, hzaPath);

        // Also dump reference text with sampled values
        float sampleRate = 30f;
        float duration = clip.length;
        int keyframeCount = Mathf.Max(1, Mathf.RoundToInt(duration * sampleRate) + 1);

        var transformByName = new Dictionary<string, Transform>();
        foreach (var t in animInstance.GetComponentsInChildren<Transform>())
            if (!transformByName.ContainsKey(t.name))
                transformByName[t.name] = t;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"# Big@Idle reference animation");
        sb.AppendLine($"# clip={clip.name} duration={duration} keyframes={keyframeCount} joints={jointCount} matched={animBoneMap.Count}");
        sb.AppendLine($"# format: keyframe time | joint_index bone_name | tx ty tz | rx ry rz rw | sx sy sz");
        sb.AppendLine();

        // Sample first 3 keyframes and last keyframe
        int[] sampleFrames = keyframeCount <= 4
            ? new int[keyframeCount]
            : new int[] { 0, 1, 2, keyframeCount - 1 };
        if (keyframeCount <= 4)
            for (int i = 0; i < keyframeCount; i++) sampleFrames[i] = i;

        foreach (int ki in sampleFrames)
        {
            float time = (ki < keyframeCount - 1) ? ki / sampleRate : duration;
            clip.SampleAnimation(animInstance, time);

            sb.AppendLine($"keyframe {ki} time={time:F6}");
            foreach (var kvp in animBoneMap)
            {
                if (!transformByName.TryGetValue(kvp.Key, out Transform bt)) continue;
                var pos = bt.localPosition;
                var rot = bt.localRotation;
                var scl = bt.localScale;

                // Show both raw Unity values and converted values
                sb.AppendLine($"  joint[{kvp.Value}] {kvp.Key}");
                sb.AppendLine($"    unity:     pos=({pos.x:F6}, {pos.y:F6}, {pos.z:F6}) rot=({rot.x:F6}, {rot.y:F6}, {rot.z:F6}, {rot.w:F6}) scl=({scl.x:F6}, {scl.y:F6}, {scl.z:F6})");
                sb.AppendLine($"    converted: pos=({-pos.x:F6}, {pos.y:F6}, {pos.z:F6}) rot=({rot.x:F6}, {-rot.y:F6}, {-rot.z:F6}, {rot.w:F6}) scl=({scl.x:F6}, {scl.y:F6}, {scl.z:F6})");
            }
            sb.AppendLine();
        }

        string refPath = $"{OutputDir}/animations/Big_Idle_ref.txt";
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(refPath));
        System.IO.File.WriteAllText(refPath, sb.ToString());
        Debug.Log($"[HZ] Reference dump → {refPath}");

        Object.DestroyImmediate(animInstance);
    }

    static string SanitizeName(string name)
    {
        return name.Replace(" ", "_").Replace("/", "_").Replace("\\", "_");
    }
}
