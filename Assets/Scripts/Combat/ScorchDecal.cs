using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 爆発跡(スコーチマーク)を被弾地点に貼る共通ヘルパー。
/// 呼び出し側はRaycastで得た実際の当たり判定面上の点と法線だけを渡すこと(空中に浮かないようにするため)。
/// </summary>
public static class ScorchDecal
{
    private const float SurfaceOffset = 0.02f; // Zファイティング防止の微小な浮き
    private static Texture2D _softCircleTexture;
    private static Material _sharedMaterial;

    /// <summary>スコーチデカールを1枚追加する。activeDecalsがmaxCountを超えたら古いものから消す</summary>
    public static void SpawnAndTrim(Transform parent, List<GameObject> activeDecals, int maxCount,
        Vector3 hitPoint, Vector3 hitNormal, float size)
    {
        if (hitNormal.sqrMagnitude < 0.0001f) hitNormal = Vector3.up; // 念のためのフォールバック

        var decal = GameObject.CreatePrimitive(PrimitiveType.Quad);
        decal.name = "ScorchDecal";

        // 連鎖爆発の判定がデカールを拾わないよう、Destroyではなくenabled=falseで即座に物理判定から外す
        var collider = decal.GetComponent<Collider>();
        if (collider != null) collider.enabled = false;

        decal.transform.SetParent(parent, true);
        decal.transform.position = hitPoint + hitNormal.normalized * SurfaceOffset;

        // 法線がほぼVector3.upと平行だとLookRotationが不定になるため、その場合だけ別の上方向基準を使う
        Vector3 upHint = Mathf.Abs(Vector3.Dot(hitNormal.normalized, Vector3.up)) > 0.95f ? Vector3.forward : Vector3.up;
        decal.transform.rotation = Quaternion.LookRotation(hitNormal.normalized, upHint);
        decal.transform.localScale = new Vector3(size, size, 1f);

        var renderer = decal.GetComponent<Renderer>();
        renderer.material = GetSharedMaterial();
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;

        activeDecals.Add(decal);
        while (activeDecals.Count > maxCount)
        {
            GameObject oldest = activeDecals[0];
            activeDecals.RemoveAt(0);
            if (oldest != null) Object.Destroy(oldest);
        }
    }

    /// <summary>リスト内のデカールを全て破棄してリストを空にする</summary>
    public static void ClearAll(List<GameObject> activeDecals)
    {
        foreach (var decal in activeDecals)
        {
            if (decal != null) Object.Destroy(decal);
        }
        activeDecals.Clear();
    }

    private static Material GetSharedMaterial()
    {
        if (_sharedMaterial != null) return _sharedMaterial;

        Shader shader = Resources.Load<Shader>("Shaders/UnlitTransparent");
        if (shader == null)
        {
            Debug.LogWarning("[ScorchDecal] Resources/Shaders/UnlitTransparent.shaderが見つかりません。フォールバックシェーダーで代用します。");
            shader = Shader.Find("Hidden/InternalErrorShader");
        }

        _sharedMaterial = new Material(shader);
        _sharedMaterial.SetTexture("_MainTex", GetSoftCircleTexture());
        _sharedMaterial.SetColor("_Color", new Color(0.06f, 0.05f, 0.045f, 0.65f));
        return _sharedMaterial;
    }

    /// <summary>中心が濃く外周へ滑らかに透明になる円形アルファテクスチャを生成してキャッシュする</summary>
    private static Texture2D GetSoftCircleTexture()
    {
        if (_softCircleTexture != null) return _softCircleTexture;

        const int size = 32;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float nx = (x + 0.5f) / size * 2f - 1f;
                float ny = (y + 0.5f) / size * 2f - 1f;
                float dist = Mathf.Sqrt(nx * nx + ny * ny);
                float alpha = Mathf.Clamp01(1f - dist);
                alpha = alpha * alpha * alpha; // 3乗で中心付近だけ残す
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
            }
        }
        tex.Apply();
        tex.wrapMode = TextureWrapMode.Clamp;
        _softCircleTexture = tex;
        return tex;
    }
}
