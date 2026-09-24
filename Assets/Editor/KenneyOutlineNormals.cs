using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Bakes smoothed normals into the tangents (xyz, w = 1) of every Kenney mesh so the
/// Kart/ToonOutline inverted hull stays closed on hard-edged (split-normal) low-poly models.
/// Tangents are skinned by Unity, so this also works for the characters.
/// </summary>
public class KenneyOutlineNormals : AssetPostprocessor
{
    public override uint GetVersion() => 3;

    void OnPostprocessModel(GameObject root)
    {
        if (!assetPath.StartsWith("Assets/ThirdParty/Kenney/")) return;
        var meshes = new HashSet<Mesh>();
        foreach (var mf in root.GetComponentsInChildren<MeshFilter>(true)) if (mf.sharedMesh != null) meshes.Add(mf.sharedMesh);
        foreach (var sm in root.GetComponentsInChildren<SkinnedMeshRenderer>(true)) if (sm.sharedMesh != null) meshes.Add(sm.sharedMesh);
        foreach (var mesh in meshes) Bake(mesh);
    }

    public static void Bake(Mesh mesh)
    {
        Vector3[] vertices = mesh.vertices;
        Vector3[] normals = mesh.normals;
        if (vertices.Length == 0 || normals.Length != vertices.Length) return;

        var sums = new Dictionary<Vector3Int, Vector3>();
        Vector3Int Key(Vector3 v) => new Vector3Int(Mathf.RoundToInt(v.x * 1000f), Mathf.RoundToInt(v.y * 1000f), Mathf.RoundToInt(v.z * 1000f));
        for (int i = 0; i < vertices.Length; i++)
        {
            var k = Key(vertices[i]);
            sums[k] = sums.TryGetValue(k, out var s) ? s + normals[i] : normals[i];
        }
        var tangents = new Vector4[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 n = sums[Key(vertices[i])];
            n = n.sqrMagnitude > 1e-8f ? n.normalized : normals[i];
            tangents[i] = new Vector4(n.x, n.y, n.z, 1f);
        }
        mesh.tangents = tangents;
    }
}
