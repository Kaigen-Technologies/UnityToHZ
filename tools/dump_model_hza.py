"""
Dump and validate a ModelBlobAsset .hza file.
Usage: python dump_model_hza.py <path_to.hza>
"""
import sys
from hza import HzaReader


def dump_model(path: str):
    r = HzaReader(path)
    r.validate_header("ModelBlobAsset")
    r.dump_header()

    # === ModelBlobAsset fields (after 48-byte header) ===
    off = 48
    mesh_count = r.read_u32(off); off += 4
    joint_count = r.read_u32(off); off += 4
    off += 8  # 8 bytes padding for mat4 16-byte alignment (CGLM_ALIGN_MAT)
    mat4 = r.read_mat4(off); off += 64
    meshes_bp = r.read_blob_ptr(off); off += 16
    joints_bp = r.read_blob_ptr(off); off += 16
    joint_names_bp = r.read_blob_ptr(off); off += 16

    is_identity = all(abs(mat4[i * 4 + j] - (1.0 if i == j else 0.0)) < 1e-6
                      for i in range(4) for j in range(4))

    print(f"\n=== ModelBlobAsset (ends at byte {off}) ===")
    print(f"  mesh_count:      {mesh_count}")
    print(f"  joint_count:     {joint_count}")
    print(f"  armature_xform:  {'identity' if is_identity else [f'{v:.4f}' for v in mat4]}")
    print(f"  meshes BlobPtr:  {meshes_bp}")
    r.check_blob_ptr(meshes_bp, "MeshBlobAsset", 168, "meshes")
    print(f"  joints BlobPtr:  {joints_bp}")
    if joint_count > 0:
        r.check_blob_ptr(joints_bp, "JointBlobAsset", 96, "joints")
    print(f"  jnames BlobPtr:  {joint_names_bp}")
    if joint_count > 0:
        r.check_blob_ptr(joint_names_bp, "StringBlob", 8, "joint_names")

    # === MeshBlobAsset(s) ===
    MESH_BLOB_SIZE = 168
    for mi in range(mesh_count):
        mesh_off = meshes_bp.offset + mi * MESH_BLOB_SIZE
        mesh_base = mesh_off

        print(f"\n=== MeshBlobAsset[{mi}] (at byte {mesh_off}) ===")

        name = r.read_string_blob(mesh_off, mesh_base); mesh_off += 8
        material = r.read_string_blob(mesh_off, mesh_base); mesh_off += 8
        print(f"  name:           \"{name}\"")
        print(f"  material:       \"{material}\"")

        index_format = r.read_u32(mesh_off); mesh_off += 4
        index_count = r.read_u32(mesh_off); mesh_off += 4
        vertex_count = r.read_u32(mesh_off); mesh_off += 4
        print(f"  index_format:   {index_format} ({'U16' if index_format == 0 else 'U32'})")
        print(f"  index_count:    {index_count}")
        print(f"  vertex_count:   {vertex_count}")

        aabb_min = r.read_vec3(mesh_off); mesh_off += 12
        aabb_max = r.read_vec3(mesh_off); mesh_off += 12
        print(f"  aabb_min:       ({aabb_min[0]:.4f}, {aabb_min[1]:.4f}, {aabb_min[2]:.4f})")
        print(f"  aabb_max:       ({aabb_max[0]:.4f}, {aabb_max[1]:.4f}, {aabb_max[2]:.4f})")

        indices_bp = r.read_blob_ptr(mesh_off); mesh_off += 16
        positions_bp = r.read_blob_ptr(mesh_off); mesh_off += 16
        normals_bp = r.read_blob_ptr(mesh_off); mesh_off += 16
        tangents_bp = r.read_blob_ptr(mesh_off); mesh_off += 16
        uvs_bp = r.read_blob_ptr(mesh_off); mesh_off += 16

        has_skin = r.read_u32(mesh_off); mesh_off += 4
        joint_indices_bp = r.read_blob_ptr(mesh_off); mesh_off += 16
        joint_weights_bp = r.read_blob_ptr(mesh_off); mesh_off += 16

        print(f"  has_skin:       {has_skin}")

        if has_skin:
            r.check_blob_ptr(joint_indices_bp, "u8", 1, "joint_indices")
            r.check(joint_indices_bp.count == vertex_count * 4,
                    f"joint_indices: expected {vertex_count * 4} u8, got {joint_indices_bp.count}")
            r.check_blob_ptr(joint_weights_bp, "f32", 4, "joint_weights")
            r.check(joint_weights_bp.count == vertex_count * 4,
                    f"joint_weights: expected {vertex_count * 4} f32, got {joint_weights_bp.count}")
            print(f"  joint_indices:  {joint_indices_bp}")
            print(f"  joint_weights:  {joint_weights_bp}")

        idx_ts = 2 if index_format == 0 else 4
        idx_tn = "u16" if index_format == 0 else "u32"

        print(f"  indices:        {indices_bp}")
        r.check_blob_ptr(indices_bp, idx_tn, idx_ts, "indices")
        r.check(indices_bp.count == index_count,
                f"indices BlobPtr count {indices_bp.count} != index_count {index_count}")

        print(f"  positions:      {positions_bp}")
        r.check_blob_ptr(positions_bp, "f32", 4, "positions")
        r.check(positions_bp.count == vertex_count * 3,
                f"positions: expected {vertex_count * 3} floats, got {positions_bp.count}")

        print(f"  normals:        {normals_bp}")
        r.check_blob_ptr(normals_bp, "f32", 4, "normals")

        print(f"  tangents:       {tangents_bp}")
        r.check_blob_ptr(tangents_bp, "f32", 4, "tangents")
        r.check(tangents_bp.count == vertex_count * 4,
                f"tangents: expected {vertex_count * 4} floats, got {tangents_bp.count}")

        print(f"  uvs:            {uvs_bp}")
        r.check_blob_ptr(uvs_bp, "f32", 4, "uvs")
        r.check(uvs_bp.count == vertex_count * 2,
                f"uvs: expected {vertex_count * 2} floats, got {uvs_bp.count}")

        # Sample data
        if positions_bp.count >= 3:
            pos_abs = mesh_base + positions_bp.offset
            print(f"\n  First 4 vertices (positions):")
            for vi in range(min(4, vertex_count)):
                px, py, pz = r.read_vec3(pos_abs + vi * 12)
                print(f"    [{vi}] ({px:.4f}, {py:.4f}, {pz:.4f})")

        if index_count >= 3:
            idx_abs = mesh_base + indices_bp.offset
            print(f"  First 6 indices:")
            for ii in range(min(6, index_count)):
                if index_format == 0:
                    val = r.read(idx_abs + ii * 2, "H")[0]
                else:
                    val = r.read_u32(idx_abs + ii * 4)
                print(f"    [{ii}] {val}")

    # === JointBlobAsset(s) ===
    JOINT_BLOB_SIZE = 96
    if joint_count > 0:
        print(f"\n=== Skeleton ({joint_count} joints) ===")

        # Read joint names from joint_names BlobArray(StringBlob)
        jnames_abs = joint_names_bp.offset  # offset from ModelBlobAsset base (0)
        names = []
        for ji in range(joint_count):
            sb_off = jnames_abs + ji * 8
            name = r.read_string_blob(sb_off, sb_off)
            names.append(name)

        joints_abs = joints_bp.offset  # offset from ModelBlobAsset base (0)
        for ji in range(joint_count):
            j_off = joints_abs + ji * JOINT_BLOB_SIZE
            j_base = j_off

            # inverse_bind_matrix (64 bytes)
            ibm = r.read_mat4(j_off); j_off += 64

            # parent_index (4 bytes)
            parent_idx = r.read_i32(j_off); j_off += 4

            # children BlobPtr (16 bytes) — at offset 68 within JointBlobAsset
            children_bp = r.read_blob_ptr(j_off); j_off += 16

            # Validate children BlobPtr
            r.check_blob_ptr(children_bp, "u32", 4, f"joint[{ji}].children")
            r.check(children_bp.type_size > 0,
                    f"joint[{ji}].children: type_size is 0 (would cause divide by zero)")

            # Validate parent_index range
            if parent_idx != -1:
                r.check(0 <= parent_idx < joint_count,
                        f"joint[{ji}].parent_index={parent_idx} out of range [0,{joint_count})")

            # Validate children indices
            if not children_bp.empty:
                child_abs = j_base + children_bp.offset
                for ci in range(children_bp.count):
                    child_idx = r.read_u32(child_abs + ci * 4)
                    r.check(child_idx < joint_count,
                            f"joint[{ji}].children[{ci}]={child_idx} out of range [0,{joint_count})")

            # Print first few and last few joints
            if ji < 4 or ji >= joint_count - 2:
                jname = names[ji] if ji < len(names) else "?"
                parent_name = names[parent_idx] if 0 <= parent_idx < len(names) else "ROOT"
                n_children = children_bp.count if not children_bp.empty else 0
                print(f"  [{ji:3d}] \"{jname}\" parent={parent_idx}({parent_name}) children={n_children}")
            elif ji == 4:
                print(f"  ... ({joint_count - 6} more joints) ...")

    r.print_result()
    sys.exit(0 if r.ok else 1)


if __name__ == "__main__":
    if len(sys.argv) < 2:
        print(f"Usage: python {sys.argv[0]} <file.hza>")
        sys.exit(1)
    dump_model(sys.argv[1])
