using UnityEngine;

/// <summary>
/// Marks a track mesh collider as drivable road. Submeshes whose material name
/// contains "grass" count as off-road (the kart slows down there). The merged track
/// collision mesh has no renderer, so it lists its off-road submeshes explicitly.
/// </summary>
[RequireComponent(typeof(MeshCollider))]
public class TrackSurface : MonoBehaviour
{
    int[] submeshIndexStart; // index-buffer range per submesh
    int[] submeshIndexEnd;
    bool[] submeshIsOffroad;

    /// <summary>Per-submesh off-road flags for a collider-only mesh (no MeshRenderer).</summary>
    public bool[] offroadSubmeshes;

    void Awake() => Cache();

    void Cache()
    {
        var meshRenderer = GetComponent<MeshRenderer>();
        Mesh mesh = GetComponent<MeshCollider>().sharedMesh;
        if (mesh == null) return;
        Material[] mats = meshRenderer != null ? meshRenderer.sharedMaterials : new Material[0];
        submeshIndexStart = new int[mesh.subMeshCount];
        submeshIndexEnd = new int[mesh.subMeshCount];
        submeshIsOffroad = new bool[mesh.subMeshCount];
        for (int i = 0; i < mesh.subMeshCount; i++)
        {
            var sub = mesh.GetSubMesh(i);
            submeshIndexStart[i] = sub.indexStart;
            submeshIndexEnd[i] = sub.indexStart + sub.indexCount;
            string matName = i < mats.Length && mats[i] != null ? mats[i].name.ToLowerInvariant() : "";
            submeshIsOffroad[i] = offroadSubmeshes != null && offroadSubmeshes.Length > 0
                ? i < offroadSubmeshes.Length && offroadSubmeshes[i]
                : matName.Contains("grass");
        }
    }

    /// <summary>True if the given MeshCollider triangle is road (not grass).</summary>
    public bool IsDrivable(int triangleIndex)
    {
        if (submeshIndexEnd == null) Cache();
        if (submeshIndexEnd == null || triangleIndex < 0) return true;
        int index = triangleIndex * 3;
        for (int i = 0; i < submeshIndexEnd.Length; i++)
            if (index >= submeshIndexStart[i] && index < submeshIndexEnd[i]) return !submeshIsOffroad[i];
        return true;
    }
}
