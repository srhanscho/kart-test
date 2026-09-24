using UnityEditor;
using UnityEngine;

/// <summary>
/// Import settings for Kenney textures: bilinear filtering, mipmaps, anisotropic filtering
/// and no compression, so the colour atlases look smooth instead of pixelated.
/// </summary>
public class KenneyTextureImport : AssetPostprocessor
{
    const string Folder = "Assets/ThirdParty/Kenney/";

    void OnPreprocessTexture()
    {
        if (!assetPath.StartsWith(Folder)) return;
        Apply((TextureImporter)assetImporter);
    }

    /// <summary>Returns true if anything changed.</summary>
    public static bool Apply(TextureImporter ti)
    {
        bool changed = false;
        if (ti.filterMode != FilterMode.Bilinear) { ti.filterMode = FilterMode.Bilinear; changed = true; }
        if (!ti.mipmapEnabled) { ti.mipmapEnabled = true; changed = true; }
        if (ti.anisoLevel < 8) { ti.anisoLevel = 8; changed = true; }
        if (ti.textureCompression != TextureImporterCompression.Uncompressed) { ti.textureCompression = TextureImporterCompression.Uncompressed; changed = true; }
        if (ti.maxTextureSize < 2048) { ti.maxTextureSize = 2048; changed = true; }
        return changed;
    }
}
