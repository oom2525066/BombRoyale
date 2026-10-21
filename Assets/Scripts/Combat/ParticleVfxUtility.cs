using UnityEngine;

/// <summary>
/// パーティクル用の共有マテリアルに、中心が不透明で縁へ向けてフェードする円形テクスチャを割り当てるユーティリティ
/// (テクスチャ未設定だと粒が四角く見えるため)。
/// </summary>
public static class ParticleVfxUtility
{
    private static Texture2D _softRadialTexture;

    private static Texture2D SoftRadialTexture
    {
        get
        {
            if (_softRadialTexture == null) _softRadialTexture = BuildSoftRadialTexture();
            return _softRadialTexture;
        }
    }

    /// <summary>_BaseMap未設定のマテリアルに円形テクスチャを割り当てる(設定済みなら何もしない冪等処理)</summary>
    public static void EnsureSoftRoundParticleTexture(Material material)
    {
        if (material == null) return;
        if (!material.HasProperty("_BaseMap")) return;
        if (material.GetTexture("_BaseMap") != null) return;

        material.SetTexture("_BaseMap", SoftRadialTexture);
    }

    /// <summary>中心から縁へアルファがスムーズに落ちる64x64の円形テクスチャを生成する</summary>
    private static Texture2D BuildSoftRadialTexture()
    {
        const int size = 64;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear,
            name = "GeneratedSoftRadialParticleTexture"
        };

        Vector2 center = new Vector2((size - 1) * 0.5f, (size - 1) * 0.5f);
        float maxDist = center.magnitude;

        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dist = Vector2.Distance(new Vector2(x, y), center) / maxDist;
                // smoothstepで、中心付近は不透明を保ちつつ縁だけ素早くフェードさせる
                float alpha = Mathf.Clamp01(1f - dist);
                alpha = alpha * alpha * (3f - 2f * alpha);
                pixels[y * size + x] = new Color(1f, 1f, 1f, alpha);
            }
        }

        tex.SetPixels32(pixels);
        tex.Apply(false, false);
        return tex;
    }
}
