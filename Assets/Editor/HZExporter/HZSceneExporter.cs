using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HZExporter
{
    /// <summary>
    /// Exports a Unity scene as a SceneBlobAsset manifest + per-entity EntityBlobAsset files.
    /// </summary>
    public static class HZSceneExporter
    {
        // Struct sizes (must match engine)
        const int SIZEOF_SCENE_BLOB_ASSET = 72;       // header(48) + scene_dir StringBlob(8) + entities BlobPtr(16)
        const int SIZEOF_ENTITY_BLOB_ASSET = 72;      // header(48) + stable_id(8) + components BlobPtr(16)
        const int SIZEOF_SCENE_COMPONENT_BLOB = 32;   // stable_type_hash(8) + data_size(4) + data BlobArray(16) + 4 pad (u64 alignment)
        const int SIZEOF_BLOB_ASSET_REF = 24;
        const int SIZEOF_STRING_BLOB = 8;

        // Component stable_type_hash = fnv1a(name) as u64
        static readonly ulong HASH_SceneEntityTag = HZBlobWriter.FNV1a("SceneEntityTag");
        static readonly ulong HASH_EcsStableId = HZBlobWriter.FNV1a("EcsStableId");
        static readonly ulong HASH_EntityName = HZBlobWriter.FNV1a("EntityName");
        static readonly ulong HASH_EcsTransform = HZBlobWriter.FNV1a("EcsTransform");
        static readonly ulong HASH_EcsVisibility = HZBlobWriter.FNV1a("EcsVisibility");
        static readonly ulong HASH_EcsModelRef = HZBlobWriter.FNV1a("EcsModelRef");
        static readonly ulong HASH_EcsMaterialRef = HZBlobWriter.FNV1a("EcsMaterialRef");
        static readonly ulong HASH_EcsAABB = HZBlobWriter.FNV1a("EcsAABB");

        // Component data sizes (must match engine struct sizeof)
        const int SIZE_SceneEntityTag = 1;
        const int SIZE_EcsStableId = 8;
        const int SIZE_EntityName = 68;     // u32 len + char[64]
        const int SIZE_EcsTransform = 36;   // 3x vec3
        const int SIZE_EcsVisibility = 8;   // 2x b32
        const int SIZE_EcsModelRef = 24;    // BlobAssetRef(ModelBlobAsset)
        const int SIZE_EcsMaterialRef = 24; // BlobAssetRef(MaterialBlobAsset)
        const int SIZE_EcsAABB = 24;        // 2x vec3

        // sizeof(ModelBlobAsset) for BlobAssetRef typehash
        const int SIZEOF_MODEL_BLOB_ASSET = 160;
        const int SIZEOF_MATERIAL_BLOB_ASSET = 112;
        const int SIZEOF_ENTITY_BLOB_ASSET_REF = 72; // sizeof(EntityBlobAsset)

        /// <summary>
        /// Info about one entity to export.
        /// </summary>
        public struct EntityExportInfo
        {
            public string name;
            public Vector3 position;
            public Vector3 eulerAngles;
            public Vector3 scale;
            public bool visible;
            public bool castsShadow;
            public Bounds bounds;
            public string modelAssetPath;    // asset-relative path to model .hza
            public string materialAssetPath; // asset-relative path to material .hza
            public bool skinned;
        }

        /// <summary>
        /// Export a scene: writes manifest .hza + per-entity .hza files.
        /// </summary>
        /// <param name="entities">Entity data to export</param>
        /// <param name="scenePath">Output path for the manifest .hza</param>
        /// <param name="sceneDir">Output directory for entity .hza files</param>
        /// <param name="sceneDirAssetPath">Asset-relative path to the entity dir (stored in manifest)</param>
        public static void Export(EntityExportInfo[] entities, string scenePath,
            string sceneDir, string sceneDirAssetPath)
        {
            Directory.CreateDirectory(sceneDir);

            var rng = new System.Random();
            string[] entityFilePaths = new string[entities.Length];

            // Write each entity
            for (int i = 0; i < entities.Length; i++)
            {
                ulong stableId = (ulong)(rng.Next() | ((long)rng.Next() << 32));
                if (stableId == 0) stableId = 1;

                string entityFileName = $"{stableId}.hza";
                string entityFilePath = Path.Combine(sceneDir, entityFileName);
                entityFilePaths[i] = $"{sceneDirAssetPath}/{entityFileName}";

                WriteEntity(entities[i], stableId, entityFilePath);
            }

            // Write manifest
            WriteManifest(entityFilePaths, sceneDirAssetPath, scenePath);

            Debug.Log($"[HZSceneExporter] Exported scene → {scenePath} ({entities.Length} entities)");
        }

        static void WriteEntity(EntityExportInfo info, ulong stableId, string outputPath)
        {
            // Determine components
            bool hasModel = !string.IsNullOrEmpty(info.modelAssetPath);
            bool hasMaterial = !string.IsNullOrEmpty(info.materialAssetPath);

            int compCount = 4; // SceneEntityTag + EcsStableId + EntityName + EcsTransform
            compCount++; // EcsVisibility
            if (hasModel) compCount++; // EcsModelRef
            if (hasMaterial) compCount++; // EcsMaterialRef
            if (hasModel) compCount++; // EcsAABB

            var w = new HZBlobWriter(4096);
            int blobBase = w.Position;

            // === EntityBlobAsset header ===
            w.WriteHeader("EntityBlobAsset");
            w.WriteEmptyBlobPtr(SIZEOF_STRING_BLOB, "StringBlob"); // no deps

            w.WriteU64(stableId); // stable_id

            int compsPtrPos = w.ReserveBlobPtr(); // components

            Debug.Assert(w.Position == SIZEOF_ENTITY_BLOB_ASSET,
                $"EntityBlobAsset size mismatch: expected {SIZEOF_ENTITY_BLOB_ASSET}, got {w.Position}");

            // === SceneComponentBlob array ===
            int compsArrayPos = w.Position;
            w.PatchBlobPtr(compsPtrPos, compsArrayPos, compCount,
                SIZEOF_SCENE_COMPONENT_BLOB, "SceneComponentBlob");

            // We need to track each component's data BlobPtr position to patch later
            var compSlots = new List<(ulong hash, int dataSize, int dataPtrPos, Action<HZBlobWriter> writeData)>();

            // Helper to reserve a component slot
            void ReserveComponent(ulong hash, int dataSize, Action<HZBlobWriter> writeData)
            {
                w.WriteU64(hash);           // stable_type_hash (8)
                w.WriteU32((uint)dataSize); // data_size (4)
                int dataPtrPos = w.ReserveBlobPtr(); // data BlobArray(u8) (16)
                w.WriteZeros(4);            // padding to 32 (u64 alignment)
                compSlots.Add((hash, dataSize, dataPtrPos, writeData));
            }

            // --- SceneEntityTag ---
            ReserveComponent(HASH_SceneEntityTag, SIZE_SceneEntityTag, wr => {
                wr.WriteU8(0);
            });

            // --- EcsStableId ---
            ReserveComponent(HASH_EcsStableId, SIZE_EcsStableId, wr => {
                wr.WriteU64(stableId);
            });

            // --- EntityName ---
            ReserveComponent(HASH_EntityName, SIZE_EntityName, wr => {
                byte[] nameBytes = Encoding.UTF8.GetBytes(info.name ?? "");
                int len = Math.Min(nameBytes.Length, 63);
                wr.WriteU32((uint)len);
                wr.WriteBytes(nameBytes.Length >= 64
                    ? nameBytes[..64]
                    : nameBytes);
                // Pad to 64 chars
                int pad = 64 - Math.Min(nameBytes.Length, 64);
                if (pad > 0) wr.WriteZeros(pad);
            });

            // --- EcsTransform (coordinate conversion: negate X) ---
            ReserveComponent(HASH_EcsTransform, SIZE_EcsTransform, wr => {
                wr.WriteVec3(-info.position.x, info.position.y, info.position.z);
                // Euler rotation: negate Y and Z for LH→RH
                float rx = info.eulerAngles.x * Mathf.Deg2Rad;
                float ry = info.eulerAngles.y * Mathf.Deg2Rad;
                float rz = info.eulerAngles.z * Mathf.Deg2Rad;
                wr.WriteVec3(rx, -ry, -rz);
                wr.WriteVec3(info.scale.x, info.scale.y, info.scale.z);
            });

            // --- EcsVisibility ---
            ReserveComponent(HASH_EcsVisibility, SIZE_EcsVisibility, wr => {
                wr.WriteU32(info.visible ? 1u : 0u);
                wr.WriteU32(info.castsShadow ? 1u : 0u);
            });

            // --- EcsModelRef ---
            if (hasModel)
            {
                ReserveComponent(HASH_EcsModelRef, SIZE_EcsModelRef, null); // special: needs BlobAssetRef patching
            }

            // --- EcsMaterialRef ---
            if (hasMaterial)
            {
                ReserveComponent(HASH_EcsMaterialRef, SIZE_EcsMaterialRef, null); // special: needs BlobAssetRef patching
            }

            // --- EcsAABB ---
            if (hasModel)
            {
                ReserveComponent(HASH_EcsAABB, SIZE_EcsAABB, wr => {
                    var b = info.bounds;
                    wr.WriteVec3(-b.max.x, b.min.y, b.min.z);
                    wr.WriteVec3(-b.min.x, b.max.y, b.max.z);
                });
            }

            Debug.Assert(w.Position == compsArrayPos + compCount * SIZEOF_SCENE_COMPONENT_BLOB,
                $"Component array size mismatch");

            // === Write component data ===
            for (int i = 0; i < compSlots.Count; i++)
            {
                var slot = compSlots[i];
                int dataPos = w.Position;
                w.PatchBlobPtr(slot.dataPtrPos, dataPos, slot.dataSize, 1, "u8");

                if (slot.hash == HASH_EcsModelRef)
                {
                    // BlobAssetRef(ModelBlobAsset) — 24 bytes
                    // Write placeholder, then patch path string after
                    int refPos = w.Position;
                    w.WriteZeros(SIZEOF_BLOB_ASSET_REF);
                    // Write path string data and patch the BlobAssetRef
                    w.WriteAndPatchBlobAssetRef(refPos, info.modelAssetPath,
                        SIZEOF_MODEL_BLOB_ASSET, "ModelBlobAsset");
                }
                else if (slot.hash == HASH_EcsMaterialRef)
                {
                    int refPos = w.Position;
                    w.WriteZeros(SIZEOF_BLOB_ASSET_REF);
                    w.WriteAndPatchBlobAssetRef(refPos, info.materialAssetPath,
                        SIZEOF_MATERIAL_BLOB_ASSET, "MaterialBlobAsset");
                }
                else
                {
                    slot.writeData(w);
                }

                // Verify data size matches (for non-ref components the written size should match)
                // For ref components, the written size includes the string data after the struct
            }

            // Patch header.asset_size
            int finalSize = w.Position;
            w.PatchU64(blobBase + 8, (ulong)finalSize);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            w.WriteToFile(outputPath);
        }

        static void WriteManifest(string[] entityPaths, string sceneDirAssetPath, string outputPath)
        {
            var w = new HZBlobWriter(4096);
            int blobBase = w.Position;

            w.WriteHeader("SceneBlobAsset");
            w.WriteEmptyBlobPtr(SIZEOF_STRING_BLOB, "StringBlob"); // no deps

            int sceneDirPos = w.ReserveStringBlob();      // scene_dir
            int entitiesPtrPos = w.ReserveBlobPtr();       // entities

            Debug.Assert(w.Position == SIZEOF_SCENE_BLOB_ASSET,
                $"SceneBlobAsset size mismatch: expected {SIZEOF_SCENE_BLOB_ASSET}, got {w.Position}");

            // Entity BlobAssetRef array
            int entityArrayPos = w.Position;
            w.PatchBlobPtr(entitiesPtrPos, entityArrayPos, entityPaths.Length,
                SIZEOF_BLOB_ASSET_REF, "BlobAssetRef");

            int[] entityRefPositions = new int[entityPaths.Length];
            for (int i = 0; i < entityPaths.Length; i++)
            {
                entityRefPositions[i] = w.Position;
                w.WriteZeros(SIZEOF_BLOB_ASSET_REF);
            }

            // String data
            w.WriteAndPatchStringBlob(sceneDirPos, sceneDirAssetPath);

            for (int i = 0; i < entityPaths.Length; i++)
            {
                w.WriteAndPatchBlobAssetRef(entityRefPositions[i], entityPaths[i],
                    SIZEOF_ENTITY_BLOB_ASSET_REF, "EntityBlobAsset");
            }

            int finalSize = w.Position;
            w.PatchU64(blobBase + 8, (ulong)finalSize);

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath));
            w.WriteToFile(outputPath);
        }
    }
}
