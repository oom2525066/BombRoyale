using UnityEditor;
using UnityEngine;

/// <summary>
/// Assets/VoxelBuildings/以下のパレットテクスチャ(256x1px)に、色が滲まないインポート設定
/// (Point補間・ミップマップ無し・非圧縮)を強制する。
/// </summary>
public class VoxelPaletteTextureImporter : AssetPostprocessor
{
    private void OnPreprocessTexture()
    {
        if (!assetPath.Contains("VoxelBuildings")) return;

        var importer = (TextureImporter)assetImporter;
        importer.textureType = TextureImporterType.Default;
        importer.filterMode = FilterMode.Point;
        importer.wrapMode = TextureWrapMode.Clamp;
        importer.mipmapEnabled = false;
        importer.textureCompression = TextureImporterCompression.Uncompressed;
        importer.alphaIsTransparency = true;
    }
}
