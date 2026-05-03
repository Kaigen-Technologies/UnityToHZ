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

    /// <summary>Test-export a cube using the FloorTriplanar shader-graph material so we can
    /// verify HZShaderMap picks up the new JSON entry and routes _MainTexture/_Normal/etc.</summary>
    public static void ExportFloorTriplanarTest()
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>(
            "Assets/StylizedCharacterPack/Materials/Shaders/MetalPannel_ShaderMat.mat");
        if (mat == null) { Debug.LogError("[HZ] MetalPannel_ShaderMat.mat not found"); return; }
        Debug.Log($"[HZ] Testing shader='{mat.shader.name}'");
        var go = GameObject.CreatePrimitive(PrimitiveType.Cube);
        HZGlbWriter.ExportStaticMesh(go.GetComponent<MeshFilter>().sharedMesh,
            new Material[] { mat },
            OutputDir + "/glb/FloorTriplanar_Test.glb");
        Object.DestroyImmediate(go);
    }

    /// <summary>Export the active scene's skybox to an equirect Radiance .hdr.
    /// Engine cooks .hdr → cubemap KTX + HdrEnvironmentBlobAsset on its side.</summary>
    [MenuItem("HZ Engine/Export Skybox")]
    public static void ExportSkybox()
    {
        string sceneName = SceneManager.GetActiveScene().name;
        if (string.IsNullOrEmpty(sceneName)) sceneName = "Skybox";
        string outPath = OutputDir + "/environments/" + sceneName + "_skybox.hdr";
        HZSkyboxExporter.Export(outPath);
    }

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
        var mats = new Material[validSmrs.Count][];
        for (int i = 0; i < validSmrs.Count; i++)
            mats[i] = validSmrs[i].sharedMaterials;

        HZGlbWriter.ExportSkinnedMesh(smrArray, mats, primarySmr, instance.transform, OutputDir + "/glb/XBot.glb");

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

        // Collect GameObjects to skip: skeleton bones owned by each Animator. A nested Animator
        // (e.g. an attached separate skinned model) is its OWN character — its GO stays exportable
        // and its bones are skipped only by its own pass, not by an outer Animator's pass. So we
        // walk each Animator's descendants and stop at any deeper Animator boundary.
        var skipGOs = new HashSet<int>();
        foreach (var root in roots)
        {
            foreach (var animator in root.GetComponentsInChildren<Animator>(true))
            {
                var animGO = animator.gameObject;
                AddBoneSkips(animGO.transform, animGO, skipGOs);
            }
        }

        // GLB dedup: key by (mesh, materials[]) — same mesh with different material assignments
        // must produce distinct GLBs since materials (including textures) are embedded.
        var exportedGlbs = new Dictionary<string, string>();
        var usedGlbNames = new HashSet<string>();

        // Terrain dedup: shared TerrainData asset → exported once.
        // key = TerrainData asset path (or instance id when not a project asset) → asset-ref path.
        var exportedTerrains = new Dictionary<string, string>();

        // Deferred skinned mesh exports: collect animations per character before exporting
        // Key = character name (FBX filename with @... stripped)
        var deferredSkinned = new Dictionary<string, DeferredSkinnedExport>();

        // Pose every rig at "idle frame 0" before sampling entity transforms. The FBX bind
        // pose is sometimes degenerate (e.g. tail rig collapsed at the floor) — what the user
        // sees in Unity when the Animator runs is the idle animation, which puts everything
        // at its expected visual rest position. Sample the first AnimationClip at t=0 of each
        // Animator to reproduce that pose deterministically.
        foreach (var rootGO in roots)
        {
            foreach (var animator in rootGO.GetComponentsInChildren<Animator>(true))
            {
                if (animator.runtimeAnimatorController == null) continue;
                var clips = animator.runtimeAnimatorController.animationClips;
                if (clips == null || clips.Length == 0) continue;
                AnimationClip first = null;
                foreach (var c in clips)
                {
                    if (c == null || c.name.StartsWith("__preview__")) continue;
                    first = c; break;
                }
                if (first != null) first.SampleAnimation(animator.gameObject, 0f);
            }
        }

        // Second pass: export each GameObject as an entity.
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
                Transform parentAnchor = null;  // the Transform whose stableId we're parenting to
                Transform ancestor = t.parent;
                while (ancestor != null)
                {
                    int ancestorId = ancestor.gameObject.GetInstanceID();
                    if (!skipGOs.Contains(ancestorId) && goToStableId.TryGetValue(ancestorId, out ulong pid))
                    {
                        parentStableId = pid;
                        parentAnchor = ancestor;
                        break;
                    }
                    ancestor = ancestor.parent;
                }

                // Local TRS: when bones (or other skipped GOs) sit between t and the actual
                // exported parent, t.localPosition/Rotation/Scale would be relative to the
                // wrong frame (the immediate Unity parent). Recompute relative to parentAnchor.
                Vector3 localPos; Quaternion localRot; Vector3 localScl;
                if (parentAnchor != null && parentAnchor != t.parent)
                {
                    Matrix4x4 m = parentAnchor.worldToLocalMatrix * t.localToWorldMatrix;
                    Vector4 c3 = m.GetColumn(3);
                    localPos = new Vector3(c3.x, c3.y, c3.z);
                    localRot = m.rotation;
                    localScl = m.lossyScale;
                }
                else
                {
                    localPos = t.localPosition;
                    localRot = t.localRotation;
                    localScl = t.localScale;
                }

                // Base entity info — every entity gets this
                var info = new HZSceneExporter.EntityExportInfo {
                    name = go.name,
                    position = localPos,
                    rotation = localRot,
                    scale = localScl,
                    worldPosition = t.position,
                    worldRotation = t.rotation,
                    worldScale = t.lossyScale,
                    visible = go.activeInHierarchy,
                    castsShadow = false,
                    stableId = stableId,
                    parentStableId = parentStableId,
                };

                // Check for skinned mesh (root of skinned hierarchy). SMRs that belong to a
                // *deeper* Animator (a nested attached character) are filtered out — they get
                // exported when we visit that nested Animator's GO as its own entity.
                var thisAnimator = go.GetComponent<Animator>();
                var allSmrs = go.GetComponentsInChildren<SkinnedMeshRenderer>();
                var validSmrs = new List<SkinnedMeshRenderer>();
                foreach (var s in allSmrs)
                    if (s.sharedMesh != null && s.bones != null && s.bones.Length > 0
                        && NearestAnimator(s.transform) == thisAnimator)
                        validSmrs.Add(s);

                if (validSmrs.Count > 0 && thisAnimator != null)
                {
                    SkinnedMeshRenderer primarySmr = validSmrs[0];
                    foreach (var s in validSmrs)
                        if (s.bones.Length > primarySmr.bones.Length)
                            primarySmr = s;

                    // Character name = FBX filename with @... stripped
                    string charName = GetCharacterName(primarySmr.sharedMesh);
                    string glbAssetPath = $"{AssetPrefix}/glb/{charName}.glb";

                    // Defer export — collect mesh info on first encounter
                    if (!deferredSkinned.ContainsKey(charName))
                    {
                        var smrArray = validSmrs.ToArray();
                        // sharedMaterials[] preserves multi-material SMRs (one per submesh).
                        var mats = new Material[validSmrs.Count][];
                        for (int si = 0; si < validSmrs.Count; si++)
                            mats[si] = validSmrs[si].sharedMaterials;

                        deferredSkinned[charName] = new DeferredSkinnedExport {
                            smrs = smrArray,
                            materials = mats,
                            primarySmr = primarySmr,
                            entityRoot = go.transform,
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
                            info.animationAssetPath = $"{AssetPrefix}/glb/{charName}/{firstClipName}.hza";
                        }
                    }

                    Debug.Log($"[HZ] Entity: {go.name} (skinned, model={glbAssetPath})");
                }
                // Terrain — export TerrainBlobAsset + heightmap/splatmaps/layer textures.
                // Engine-side ECS (EcsTerrainRef in scene entity) isn't wired yet, so the entity
                // itself is emitted as an empty-transform anchor; the user wires the asset
                // manually until scene serialization catches up.
                else if (go.TryGetComponent<Terrain>(out var terrain) && terrain.terrainData != null)
                {
                    string key = AssetDatabase.GetAssetPath(terrain.terrainData);
                    if (string.IsNullOrEmpty(key)) key = $"iid:{terrain.terrainData.GetInstanceID()}";

                    if (!exportedTerrains.TryGetValue(key, out string terrainAssetPath))
                    {
                        string safe = SanitizeName(terrain.terrainData.name);
                        string outSubDir = $"{OutputDir}/terrain/{safe}";
                        string prefix = $"{AssetPrefix}/terrain/{safe}";
                        terrainAssetPath = HZTerrainExporter.Export(terrain, outSubDir, prefix);
                        if (terrainAssetPath != null)
                            exportedTerrains[key] = terrainAssetPath;
                    }
                    if (terrainAssetPath != null)
                    {
                        info.terrainAssetPath = terrainAssetPath;
                        info.terrainGridDim = 32;
                        info.terrainLodLevels = 6;
                    }
                    Debug.Log($"[HZ] Entity: {go.name} (terrain, asset={terrainAssetPath ?? "<failed>"})");
                }
                // Static mesh — export as .glb, dedup by (mesh, material combo).
                else if (go.TryGetComponent<MeshFilter>(out var mf) && mf.sharedMesh != null
                         && go.TryGetComponent<MeshRenderer>(out var mr))
                {
                    Mesh mesh = mf.sharedMesh;
                    Material[] mats = mr.sharedMaterials;
                    // Dedup by mesh only. Multiple entities sharing the same mesh with different
                    // materials reuse one GLB — the engine extracts material/textures from the
                    // GLB once into assets/unity/{materials,textures}/, and entity material
                    // overrides (when needed) come from external EcsMaterialRef. Splitting GLBs
                    // by material assignment causes the engine's GLB cooker to write the same
                    // texture filename from multiple sources → asset collision.
                    string meshKey = GetMeshKey(mesh);

                    if (!exportedGlbs.TryGetValue(meshKey, out string glbAssetPath))
                    {
                        string glbName = GetMeshSourceName(mesh);
                        string glbPath = $"{OutputDir}/glb/{glbName}.glb";
                        glbAssetPath = $"{AssetPrefix}/glb/{glbName}.glb";

                        HZGlbWriter.ExportStaticMesh(mesh, mats, glbPath);
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
                    // Engine applies srgb_to_linear to light color; encode the picker value
                    // (light.color, raw) so the engine recovers what Unity uses internally.
                    Color c = LinearToSrgb(light.color);
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
            HZGlbWriter.ExportSkinnedMesh(d.smrs, d.materials, d.primarySmr, d.entityRoot, d.glbPath, animArray);
            int animCount = animArray?.Length ?? 0;
            Debug.Log($"[HZ] Exported GLB: {kvp.Key}.glb (skinned, {animCount} animations)");
        }

        string sceneName = SanitizeName(scene.name);

        // Skybox / IBL env: bake the active Unity skybox to an equirect .hdr if the
        // scene has a skybox material or uses skybox-based ambient. Engine cooks the
        // .hdr → cubemap KTX + HdrEnvironmentBlobAsset on its side.
        bool wantsSkybox = RenderSettings.skybox != null;
        bool wantsEnv = wantsSkybox || RenderSettings.ambientMode == UnityEngine.Rendering.AmbientMode.Skybox;
        string envAssetPath = null;
        float envIntensity = 1.0f;
        if (wantsEnv)
        {
            string hdrPath = $"{OutputDir}/environments/{sceneName}_skybox.hdr";
            if (HZSkyboxExporter.Export(hdrPath))
            {
                envAssetPath = $"{AssetPrefix}/environments/{sceneName}_skybox.hza";
                envIntensity = RenderSettings.ambientMode == UnityEngine.Rendering.AmbientMode.Skybox
                    ? RenderSettings.ambientIntensity : 1.0f;
            }
        }

        // Scene settings (synthetic entity, no GameObject)
        {
            // Engine applies srgb_to_linear to ambient/shadow color; encode the picker value
            // (raw RenderSettings color, no .linear) so engine recovers what Unity uses.
            Color ambient = LinearToSrgb(RenderSettings.ambientLight);
            Color shadowCol = LinearToSrgb(RenderSettings.subtractiveShadowColor);
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
                shadowColor = shadowCol,
                hasEnv = envAssetPath != null,
                envAssetPath = envAssetPath,
                envIntensity = envIntensity,
                skybox = wantsSkybox,
            });
        }
        string scenePath = $"{OutputDir}/scenes/{sceneName}.hzt";
        string sceneDir = $"{OutputDir}/scenes/{sceneName}";
        string sceneDirAssetPath = $"{AssetPrefix}/{sceneName}";

        HZSceneExporter.Export(entities.ToArray(), scenePath, sceneDir, sceneDirAssetPath);
        Debug.Log($"[HZ] Scene export done! {entities.Count} entities, {exportedGlbs.Count} unique GLBs");
    }

    struct DeferredSkinnedExport
    {
        public SkinnedMeshRenderer[] smrs;
        public Material[][] materials;   // [smrIdx][submeshIdx] — preserves multi-material SMRs
        public SkinnedMeshRenderer primarySmr;
        public Transform entityRoot;   // GO with the Animator — anchor for bone chain bake
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

    /// <summary>
    /// Stable signature for a material array — by asset path when the material is a project asset,
    /// else by instance-id fallback. Drives dedup: two entities sharing the same mesh + same
    /// material combo reuse one GLB; different materials → separate GLBs.
    /// </summary>
    static string GetMaterialsSignature(Material[] mats)
    {
        if (mats == null || mats.Length == 0) return "none";
        var parts = new string[mats.Length];
        for (int i = 0; i < mats.Length; i++)
        {
            var m = mats[i];
            if (m == null) { parts[i] = "null"; continue; }
            string ap = AssetDatabase.GetAssetPath(m);
            parts[i] = !string.IsNullOrEmpty(ap) ? ap : $"iid:{m.GetInstanceID()}";
        }
        return string.Join("|", parts);
    }

    /// <summary>
    /// Produce a unique GLB filename: prefer `baseName`; on collision, append the first
    /// material's sanitized name, then a numeric suffix as needed. Preserves the legacy
    /// short name for the first entity that hits each baseName.
    /// </summary>
    static string UniqueGlbName(string baseName, Material[] mats, HashSet<string> used)
    {
        if (!used.Contains(baseName)) return baseName;
        string matSuffix = (mats != null && mats.Length > 0 && mats[0] != null)
            ? SanitizeName(mats[0].name) : "mat";
        string candidate = $"{baseName}_{matSuffix}";
        int n = 2;
        while (used.Contains(candidate)) { candidate = $"{baseName}_{matSuffix}_{n++}"; }
        return candidate;
    }

    static string GetMeshKey(Mesh mesh)
    {
        string assetPath = AssetDatabase.GetAssetPath(mesh);
        if (string.IsNullOrEmpty(assetPath) || assetPath.StartsWith("Library/"))
            return $"builtin:{mesh.name}";
        // Multiple sub-Meshes can live in one FBX (e.g. Chest + ChestLid). Include mesh.name
        // so each sub-Mesh gets a unique dedup key and filename.
        return $"{assetPath}::{mesh.name}";
    }

    static string GetMeshSourceName(Mesh mesh)
    {
        string assetPath = AssetDatabase.GetAssetPath(mesh);
        string meshName = SanitizeName(mesh.name);
        if (!string.IsNullOrEmpty(assetPath) && !assetPath.StartsWith("Library/"))
        {
            string fbx = SanitizeName(System.IO.Path.GetFileNameWithoutExtension(assetPath));
            return fbx == meshName ? fbx : $"{fbx}_{meshName}";
        }
        return meshName;
    }

    public static void DebugMetallicMap()
    {
        var mat = AssetDatabase.LoadAssetAtPath<Material>("Assets/StylizedCharacterPack/Materials/Characters/SeaGull_Material.mat");
        if (mat == null) { Debug.LogError("[HZ] SeaGull material not found"); return; }

        Texture2D tex = null;
        if (mat.HasProperty("_MetallicGlossMap")) tex = mat.GetTexture("_MetallicGlossMap") as Texture2D;
        if (tex == null) { Debug.LogError("[HZ] No _MetallicGlossMap"); return; }

        Debug.Log($"[HZ] MetallicGlossMap: {tex.name}, {tex.width}x{tex.height}, format={tex.format}, readable={tex.isReadable}");

        // Blit to read pixels
        var tmp = RenderTexture.GetTemporary(tex.width, tex.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
        Graphics.Blit(tex, tmp);
        RenderTexture.active = tmp;
        var read = new Texture2D(tex.width, tex.height, TextureFormat.RGBA32, false, true);
        read.ReadPixels(new UnityEngine.Rect(0, 0, tex.width, tex.height), 0, 0);
        read.Apply();
        RenderTexture.active = null;
        RenderTexture.ReleaseTemporary(tmp);

        // Dump R, G, B, A channels as separate greyscale PNGs
        int w = tex.width, h = tex.height;
        var pixels = read.GetPixels();
        string outDir = System.IO.Path.GetFullPath("ExportedAssets/debug");
        System.IO.Directory.CreateDirectory(outDir);

        for (int ch = 0; ch < 4; ch++)
        {
            string chName = ch == 0 ? "R" : ch == 1 ? "G" : ch == 2 ? "B" : "A";
            var chTex = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            var chPixels = new Color[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                float v = ch == 0 ? pixels[i].r : ch == 1 ? pixels[i].g : ch == 2 ? pixels[i].b : pixels[i].a;
                chPixels[i] = new Color(v, v, v, 1f);
            }
            chTex.SetPixels(chPixels);
            chTex.Apply();
            byte[] png = chTex.EncodeToPNG();
            string path = $"{outDir}/metallic_gloss_{chName}.png";
            System.IO.File.WriteAllBytes(path, png);
            Debug.Log($"[HZ] Dumped channel {chName} → {path} ({png.Length} bytes)");
            Object.DestroyImmediate(chTex);
        }

        // Also log some pixel values
        int cx = w / 2, cy = h / 2;
        var pc = read.GetPixel(cx, cy);
        Debug.Log($"[HZ] center pixel({cx},{cy}): R={pc.r:F4} G={pc.g:F4} B={pc.b:F4} A={pc.a:F4}");
        var pb = read.GetPixel(100, 100);
        Debug.Log($"[HZ] pixel(100,100): R={pb.r:F4} G={pb.g:F4} B={pb.b:F4} A={pb.a:F4}");
        var pw = read.GetPixel(300, 1400);
        Debug.Log($"[HZ] pixel(300,1400): R={pw.r:F4} G={pw.g:F4} B={pw.b:F4} A={pw.a:F4}");

        Object.DestroyImmediate(read);
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

    /// <summary>
    /// Apply linear→sRGB so the engine's sRGB→linear gives back Unity's value.
    /// </summary>
    static float LinearToSrgb(float x)
    {
        if (x <= 0.0031308f) return x * 12.92f;
        return 1.055f * Mathf.Pow(x, 1f / 2.4f) - 0.055f;
    }

    static Vector3 LinearToSrgb(Vector3 v)
    {
        return new Vector3(LinearToSrgb(v.x), LinearToSrgb(v.y), LinearToSrgb(v.z));
    }

    static Color LinearToSrgb(Color c)
    {
        return new Color(LinearToSrgb(c.r), LinearToSrgb(c.g), LinearToSrgb(c.b), c.a);
    }

    static string SanitizeName(string name)
    {
        return name.Replace(" ", "_").Replace("/", "_").Replace("\\", "_");
    }

    /// <summary>
    /// Walks up from `t` and returns the first Animator found (including on `t` itself).
    /// Used to determine which character a SMR / bone belongs to when an Animator is nested
    /// inside another Animator's hierarchy.
    /// </summary>
    static Animator NearestAnimator(Transform t)
    {
        while (t != null)
        {
            var a = t.GetComponent<Animator>();
            if (a != null) return a;
            t = t.parent;
        }
        return null;
    }

    /// <summary>
    /// Walks descendants of `cur` and adds them to `skipGOs` as bones of `ownerAnim`.
    /// Stops at any deeper Animator boundary — that subtree belongs to a nested character
    /// and is processed by its own pass. The owner's GO itself is NOT added (caller responsibility).
    /// </summary>
    static void AddBoneSkips(Transform cur, GameObject ownerAnim, HashSet<int> skipGOs)
    {
        for (int i = 0; i < cur.childCount; i++)
        {
            var child = cur.GetChild(i);
            if (child.GetComponent<Animator>() != null) continue; // nested character — its own pass
            skipGOs.Add(child.gameObject.GetInstanceID());
            AddBoneSkips(child, ownerAnim, skipGOs);
        }
    }

    /// <summary>
    /// Diagnostic: dump every SkinnedMeshRenderer in the active scene with its bones[] array.
    /// Flags null entries, identifies probable MagicaCloth2 proxy SMRs, and shows which one
    /// the current primarySmr picker would choose.
    /// </summary>
    /// <summary>
    /// Scan the active scene for static meshes with subMeshCount > 1 to confirm multi-primitive
    /// Case B is exercised. Logs each to Editor.log.
    /// </summary>
    public static void FindMultiSubmeshStaticMeshes()
    {
        var scene = SceneManager.GetActiveScene();
        var roots = scene.GetRootGameObjects();
        int count = 0;
        foreach (var root in roots)
        {
            foreach (var mr in root.GetComponentsInChildren<MeshRenderer>(true))
            {
                var mf = mr.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) continue;
                var mesh = mf.sharedMesh;
                if (mesh.subMeshCount > 1 || mr.sharedMaterials.Length > 1)
                {
                    count++;
                    Debug.Log($"[MultiSub] {mr.gameObject.name}: submeshCount={mesh.subMeshCount} materialCount={mr.sharedMaterials.Length} meshAsset={AssetDatabase.GetAssetPath(mesh)}");
                }
            }
        }
        Debug.Log($"[MultiSub] total={count}");
    }

    public static void DumpSkinnedBones()
    {
        string outPath = System.IO.Path.Combine(OutputDir, "dump_bones.txt");
        var sw = new System.IO.StreamWriter(outPath, false);
        void WLog(string s) { Debug.Log(s); sw.WriteLine(s); }

        var scene = SceneManager.GetActiveScene();
        var roots = scene.GetRootGameObjects();
        WLog($"[DumpBones] Scene: {scene.name}, {roots.Length} roots");

        foreach (var root in roots)
        {
            var smrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (smrs.Length == 0) continue;

            WLog($"[DumpBones] === Root: {root.name} ({smrs.Length} SMRs) ===");

            // Simulate primary picker logic
            SkinnedMeshRenderer primary = null;
            foreach (var s in smrs)
            {
                if (s.sharedMesh == null || s.bones == null || s.bones.Length == 0) continue;
                if (primary == null || s.bones.Length > primary.bones.Length) primary = s;
            }

            foreach (var smr in smrs)
            {
                string path = GetTransformPath(smr.transform, root.transform);
                var bones = smr.bones;
                var mesh = smr.sharedMesh;

                // Detect MC2: walk up parents looking for a MagicaCloth component (by type name, avoids compile-time ref)
                string mc2Owner = null;
                for (var t = smr.transform; t != null; t = t.parent)
                {
                    var comps = t.GetComponents<Component>();
                    foreach (var c in comps)
                    {
                        if (c == null) continue;
                        var tn = c.GetType().FullName;
                        if (tn != null && tn.Contains("MagicaCloth"))
                        {
                            mc2Owner = $"{t.name}:{c.GetType().Name}";
                            break;
                        }
                    }
                    if (mc2Owner != null) break;
                }

                int nullCount = 0;
                if (bones != null)
                    foreach (var b in bones) if (b == null) nullCount++;

                string primaryMark = (smr == primary) ? "  <-- primarySmr" : "";
                WLog($"[DumpBones] SMR '{smr.name}' path='{path}' " +
                     $"mesh='{(mesh != null ? mesh.name : "<null>")}' " +
                     $"bones={bones?.Length ?? 0} nulls={nullCount} " +
                     $"rootBone='{(smr.rootBone != null ? smr.rootBone.name : "<null>")}' " +
                     $"mc2={mc2Owner ?? "no"}{primaryMark}");

                if (nullCount > 0 && bones != null)
                {
                    for (int i = 0; i < bones.Length; i++)
                        if (bones[i] == null)
                            WLog($"[DumpBones]   null bone at index {i}");
                }

                if (bones != null && bones.Length > 0)
                {
                    // Dump EVERY bone entry so we can see exactly what's there
                    for (int i = 0; i < bones.Length; i++)
                    {
                        var b = bones[i];
                        string bname = b != null ? b.name : "<NULL>";
                        string bpath = b != null ? GetTransformPath(b, root.transform) : "<NULL>";
                        WLog($"[DumpBones]   bone[{i}]={bname} path={bpath}");
                    }
                }
            }
        }

        WLog("[DumpBones] done");
        sw.Flush();
        sw.Close();
        Debug.Log($"[DumpBones] wrote {outPath}");
    }

    static string GetTransformPath(Transform t, Transform root)
    {
        if (t == root) return t.name;
        var sb = new System.Text.StringBuilder(t.name);
        var cur = t.parent;
        while (cur != null && cur != root)
        {
            sb.Insert(0, cur.name + "/");
            cur = cur.parent;
        }
        if (cur == root) sb.Insert(0, root.name + "/");
        return sb.ToString();
    }

    /// <summary>
    /// Mirror the exporter's primary-SMR picker + per-SMR boneRemap, then count how many
    /// vertex weights would fall into the "remap=0 fallback" slot due to null/missing bones.
    /// That's the set of vertices that get skinned to the root and produce visual explosions.
    /// </summary>
    public static void AnalyzeSkinWeights()
    {
        string outPath = System.IO.Path.Combine(OutputDir, "skin_weight_analysis.txt");
        var sw = new System.IO.StreamWriter(outPath, false);
        void WLog(string s) { Debug.Log(s); sw.WriteLine(s); }

        var scene = SceneManager.GetActiveScene();
        var roots = scene.GetRootGameObjects();

        foreach (var root in roots)
        {
            var allSmrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var validSmrs = new List<SkinnedMeshRenderer>();
            foreach (var s in allSmrs)
                if (s.sharedMesh != null && s.bones != null && s.bones.Length > 0)
                    validSmrs.Add(s);
            if (validSmrs.Count == 0) continue;

            SkinnedMeshRenderer primary = validSmrs[0];
            foreach (var s in validSmrs)
                if (s.bones.Length > primary.bones.Length) primary = s;

            var primaryBones = primary.bones;
            var boneToIndex = new Dictionary<Transform, int>();
            for (int i = 0; i < primaryBones.Length; i++)
                if (primaryBones[i] != null) boneToIndex[primaryBones[i]] = i;

            WLog($"[Analyze] === Root '{root.name}' — primary SMR '{primary.name}' bones={primaryBones.Length} ===");

            int globalBadVerts = 0;
            int globalBadWeight = 0;

            foreach (var smr in validSmrs)
            {
                var bones = smr.bones;
                int[] remap = new int[bones.Length];
                bool[] isFallback = new bool[bones.Length];
                int nullInSmr = 0, missingFromPrimary = 0;
                for (int i = 0; i < bones.Length; i++)
                {
                    if (bones[i] == null)
                    {
                        remap[i] = 0; isFallback[i] = true; nullInSmr++;
                    }
                    else if (!boneToIndex.TryGetValue(bones[i], out remap[i]))
                    {
                        remap[i] = 0; isFallback[i] = true; missingFromPrimary++;
                    }
                }

                var mesh = smr.sharedMesh;
                var bws = mesh.boneWeights;
                int badVerts = 0;
                float totalBadWeight = 0f;
                var badBoneHist = new Dictionary<int, int>(); // bone index in smr.bones → count
                for (int v = 0; v < bws.Length; v++)
                {
                    var bw = bws[v];
                    bool hit = false;
                    void Check(int bi, float w)
                    {
                        if (bi >= 0 && bi < bones.Length && isFallback[bi] && w > 0f)
                        {
                            hit = true;
                            totalBadWeight += w;
                            badBoneHist.TryGetValue(bi, out int c);
                            badBoneHist[bi] = c + 1;
                        }
                    }
                    Check(bw.boneIndex0, bw.weight0);
                    Check(bw.boneIndex1, bw.weight1);
                    Check(bw.boneIndex2, bw.weight2);
                    Check(bw.boneIndex3, bw.weight3);
                    if (hit) badVerts++;
                }

                string tag = (smr == primary) ? " <PRIMARY>" : "";
                WLog($"[Analyze] SMR '{smr.name}'{tag} bones={bones.Length} " +
                     $"null_in_smr={nullInSmr} missing_from_primary={missingFromPrimary} " +
                     $"bad_verts={badVerts}/{bws.Length} totalBadWeight={totalBadWeight:F2}");

                if (badBoneHist.Count > 0)
                {
                    // Top 10 offending slots
                    var list = new List<KeyValuePair<int, int>>(badBoneHist);
                    list.Sort((a, b) => b.Value.CompareTo(a.Value));
                    int show = System.Math.Min(10, list.Count);
                    for (int i = 0; i < show; i++)
                    {
                        int bi = list[i].Key;
                        int cnt = list[i].Value;
                        string bn = bones[bi] != null ? bones[bi].name : "<NULL>";
                        bool inPrimary = bones[bi] != null && boneToIndex.ContainsKey(bones[bi]);
                        WLog($"[Analyze]   slot[{bi}]={bn} hits={cnt} inPrimary={inPrimary}");
                    }
                }

                globalBadVerts += badVerts;
                globalBadWeight += (int)totalBadWeight;
            }

            WLog($"[Analyze] === TOTAL bad_verts={globalBadVerts} totalBadWeight={globalBadWeight} ===");

            // Compare bindposes across SMRs: if two SMRs use the same bone but have different
            // bindpose matrices for it, a single shared inverseBindMatrices list in glTF is wrong.
            var primaryBP = primary.sharedMesh.bindposes;
            int bindposeMismatches = 0;
            foreach (var smr in validSmrs)
            {
                if (smr == primary) continue;
                var bps = smr.sharedMesh.bindposes;
                int mismatchCount = 0;
                float maxDelta = 0f;
                int firstMismatch = -1;
                int compareCount = System.Math.Min(bps.Length, primaryBP.Length);
                for (int i = 0; i < compareCount; i++)
                {
                    if (smr.bones[i] == null || primaryBones[i] == null) continue;
                    if (smr.bones[i] != primaryBones[i]) continue; // only compare when same bone
                    float d = 0f;
                    for (int r = 0; r < 4; r++)
                        for (int c = 0; c < 4; c++)
                            d += System.Math.Abs(bps[i][r, c] - primaryBP[i][r, c]);
                    if (d > 1e-4f)
                    {
                        mismatchCount++;
                        if (firstMismatch < 0) firstMismatch = i;
                        if (d > maxDelta) maxDelta = d;
                    }
                }
                if (mismatchCount > 0)
                {
                    bindposeMismatches++;
                    WLog($"[Analyze] BINDPOSE MISMATCH: SMR '{smr.name}' differs from primary on {mismatchCount}/{compareCount} bones (firstIdx={firstMismatch} maxDelta={maxDelta:F4})");
                }
                else
                {
                    WLog($"[Analyze] SMR '{smr.name}' bindposes match primary");
                }
            }
            WLog($"[Analyze] === {bindposeMismatches}/{validSmrs.Count - 1} SMRs have bindpose mismatches ===");

            // Dump preXform translation + bindpose-based delta for every SMR — look for large values
            WLog($"[Analyze] preXform per SMR (primary.worldToLocal * smr.localToWorld) vs bp[0]-based delta:");
            foreach (var smr in validSmrs)
            {
                Matrix4x4 pre = primary.transform.worldToLocalMatrix * smr.transform.localToWorldMatrix;
                Vector3 t = (Vector3)pre.GetColumn(3);
                string bpDelta = "n/a";
                if (primary.sharedMesh.bindposes.Length > 0 && smr.sharedMesh.bindposes.Length > 0)
                {
                    var pbp = primary.sharedMesh.bindposes[0];
                    var mbp = smr.sharedMesh.bindposes[0];
                    var deltaMat = pbp.inverse * mbp;  // the correct preXform per math
                    Vector3 dt = (Vector3)deltaMat.GetColumn(3);
                    bpDelta = $"({dt.x:F3},{dt.y:F3},{dt.z:F3})";
                }
                WLog($"[Analyze]   {smr.name}: preXform_t=({t.x:F3},{t.y:F3},{t.z:F3})  bp-based={bpDelta}  pos={smr.transform.position}");
            }

            // Print primary & mismatching SMR transforms and sample bindpose matrices
            void DumpTransform(SkinnedMeshRenderer s)
            {
                WLog($"[Analyze]   transform.position={s.transform.position} rotation={s.transform.rotation.eulerAngles} lossyScale={s.transform.lossyScale}");
                WLog($"[Analyze]   localToWorld=\n{s.transform.localToWorldMatrix}");
                var bp = s.sharedMesh.bindposes;
                if (bp.Length > 0) WLog($"[Analyze]   bindpose[0]=\n{bp[0]}");
                if (bp.Length > 38) WLog($"[Analyze]   bindpose[38]=\n{bp[38]}");
            }
            WLog($"[Analyze] PRIMARY '{primary.name}':");
            DumpTransform(primary);
            foreach (var smr in validSmrs)
            {
                if (smr == primary) continue;
                if (smr.name != "head_Def" && smr.name != "Headgear") continue;
                WLog($"[Analyze] MISMATCH '{smr.name}':");
                DumpTransform(smr);
                // Computed preXform
                Matrix4x4 pre = primary.transform.worldToLocalMatrix * smr.transform.localToWorldMatrix;
                WLog($"[Analyze]   preXform (primary.worldToLocal * smr.localToWorld)=\n{pre}");
                // Expected: primary_bp[i] should equal preXform * smr_bp[i] (?)
                // Actually: delta between bindposes if they include SMR xform
                if (primary.sharedMesh.bindposes.Length > 0 && smr.sharedMesh.bindposes.Length > 0)
                {
                    var pbp = primary.sharedMesh.bindposes[38];
                    var mbp = smr.sharedMesh.bindposes[38];
                    // primary_bp = bone.worldToLocal * primary.localToWorld
                    // mesh_bp    = bone.worldToLocal * mesh.localToWorld
                    // primary_bp * mesh_bp^-1 = primary.localToWorld * mesh.worldToLocal
                    var delta = pbp * mbp.inverse;
                    WLog($"[Analyze]   bindpose_delta[38] (primary_bp * mesh_bp.inv)=\n{delta}");
                    var expected = primary.transform.localToWorldMatrix * smr.transform.worldToLocalMatrix;
                    WLog($"[Analyze]   expected (primary.localToWorld * smr.worldToLocal)=\n{expected}");
                }
            }
        }

        sw.Flush(); sw.Close();
        Debug.Log($"[Analyze] wrote {outPath}");
    }
}
