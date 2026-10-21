using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// ホットバー用の武器アイコン生成。武器のheldVisualPrefab(3Dモデル)を画面外のステージで一度だけ
/// レンダリングしてTexture2D化し、キャッシュする。
/// </summary>
public static class WeaponIconRenderer
{
    private const int IconSize = 128;
    private static readonly Dictionary<GameObject, Texture2D> _cache = new Dictionary<GameObject, Texture2D>();
    private static int _lastRenderFrame = -1;

    /// <summary>キャッシュ済みアイコンを返す(未生成ならnull。生成はしない)。OnGUIからはこちらだけを呼ぶ</summary>
    public static Texture2D TryGetIcon(GameObject prefab)
    {
        if (prefab == null) return null;
        return _cache.TryGetValue(prefab, out var cached) ? cached : null;
    }

    /// <summary>未生成ならアイコンを生成する。OnGUI内で複数回レンダリングするとURPのカメラデータが衝突するため、
    /// Update()から呼び、1フレームに最大1個に制限する</summary>
    public static void EnsureGenerated(GameObject prefab)
    {
        if (prefab == null || _cache.ContainsKey(prefab)) return;
        if (Time.frameCount == _lastRenderFrame) return;
        _lastRenderFrame = Time.frameCount;

        _cache[prefab] = RenderIcon(prefab);
    }

    private static Texture2D RenderIcon(GameObject prefab)
    {
        // ゲーム世界から見えない遠方に、モデル+カメラ+ライトだけの使い捨てステージを組む
        var stageRoot = new GameObject("WeaponIconStage_TEMP");
        stageRoot.transform.position = new Vector3(0f, -9000f, 0f);

        var instance = Object.Instantiate(prefab, stageRoot.transform, false);
        instance.transform.localPosition = Vector3.zero;

        Bounds bounds = ComputeBounds(instance, stageRoot.transform.position);
        float radius = Mathf.Max(bounds.extents.magnitude, 0.05f);

        var cameraGo = new GameObject("WeaponIconCamera_TEMP");
        cameraGo.transform.SetParent(stageRoot.transform, false);
        var cam = cameraGo.AddComponent<Camera>();
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = new Color(0f, 0f, 0f, 0f);
        cam.orthographic = false;
        cam.fieldOfView = 28f;
        cam.nearClipPlane = 0.01f;
        cam.farClipPlane = radius * 8f + 1f;

        // 斜め上から見下ろす汎用アングル
        Vector3 dir = new Vector3(0.55f, 0.45f, -1f).normalized;
        float distance = radius / Mathf.Sin(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) * 1.2f;
        cameraGo.transform.position = bounds.center - dir * distance;
        cameraGo.transform.LookAt(bounds.center);

        // 環境光が無いステージで暗く沈まないよう、キー/フィル/トップの3灯を置く
        var keyLightGo = new GameObject("WeaponIconKeyLight_TEMP");
        keyLightGo.transform.SetParent(stageRoot.transform, false);
        var keyLight = keyLightGo.AddComponent<Light>();
        keyLight.type = LightType.Directional;
        keyLight.intensity = 2.4f;
        keyLight.shadows = LightShadows.None;
        keyLight.transform.rotation = Quaternion.Euler(45f, -30f, 0f);

        var fillLightGo = new GameObject("WeaponIconFillLight_TEMP");
        fillLightGo.transform.SetParent(stageRoot.transform, false);
        var fillLight = fillLightGo.AddComponent<Light>();
        fillLight.type = LightType.Directional;
        fillLight.intensity = 1.4f;
        fillLight.shadows = LightShadows.None;
        fillLight.transform.rotation = Quaternion.Euler(30f, 150f, 0f);

        var topLightGo = new GameObject("WeaponIconTopLight_TEMP");
        topLightGo.transform.SetParent(stageRoot.transform, false);
        var topLight = topLightGo.AddComponent<Light>();
        topLight.type = LightType.Directional;
        topLight.intensity = 1f;
        topLight.shadows = LightShadows.None;
        topLight.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

        // 撮影の間だけ明るいフラットアンビエントに差し替え、終わったら元に戻す
        AmbientMode previousAmbientMode = RenderSettings.ambientMode;
        Color previousAmbientLight = RenderSettings.ambientLight;
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.45f, 0.45f, 0.45f);

        var rt = new RenderTexture(IconSize, IconSize, 16, RenderTextureFormat.ARGB32);
        cam.targetTexture = rt;
        cam.Render();

        RenderSettings.ambientMode = previousAmbientMode;
        RenderSettings.ambientLight = previousAmbientLight;

        RenderTexture previousActive = RenderTexture.active;
        RenderTexture.active = rt;
        var tex = new Texture2D(IconSize, IconSize, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0f, 0f, IconSize, IconSize), 0, 0);
        tex.Apply();
        RenderTexture.active = previousActive;

        cam.targetTexture = null;
        rt.Release();
        Object.Destroy(rt);
        Object.Destroy(stageRoot);

        return tex;
    }

    private static Bounds ComputeBounds(GameObject go, Vector3 fallbackCenter)
    {
        var renderers = go.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return new Bounds(fallbackCenter, Vector3.one * 0.5f);
        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
        return b;
    }
}
