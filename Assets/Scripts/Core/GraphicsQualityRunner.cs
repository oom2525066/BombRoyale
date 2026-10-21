using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering.Universal;

/// <summary>
/// GraphicsQualitySettings のカメラ単位の設定(影/HDR/描画距離)を全カメラへ定期的に適用する常駐オブジェクト。
/// あわせてFPSの計測・画面表示と、[Perf] タグ付きの定期ログも担当する。
/// </summary>
public class GraphicsQualityRunner : MonoBehaviour
{
    public static GraphicsQualityRunner Instance { get; private set; }

    private const float CameraRefreshIntervalSeconds = 0.5f;
    private const float FpsDisplayWindowSeconds = 0.5f;
    private const float PerfLogIntervalSeconds = 15f;

    private struct CameraOriginal
    {
        public bool AllowHdr;
        public float FarClip;
    }

    // カメラ本来の値(高画質へ戻す時に使う)
    private readonly Dictionary<Camera, CameraOriginal> _originals = new Dictionary<Camera, CameraOriginal>();
    private readonly List<Camera> _deadCameraKeys = new List<Camera>();
    private Camera[] _cameraBuffer = new Camera[8];
    private float _nextCameraRefreshTime;

    // 画面表示用(0.5秒ごとに更新)
    private int _displayFrames;
    private float _displayTime;
    private float _displayWorstDelta;
    private float _displayFps;
    private float _displayWorstFps;

    // ログ用(15秒ごと)
    private int _logFrames;
    private float _logTime;
    private float _logWorstDelta;
    private float _nextPerfLogTime;

    private GUIStyle _fpsStyle;
    private GUIStyle _fpsShadowStyle;

    public static void EnsureExists()
    {
        if (Instance != null) return;
        var go = new GameObject("GraphicsQualityRunner(Auto)");
        DontDestroyOnLoad(go);
        go.AddComponent<GraphicsQualityRunner>();
    }

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
        useGUILayout = false; // FPS表示はGUI.Labelのみ。レイアウトイベントを省いてOnGUIの負荷を減らす
        _nextPerfLogTime = Time.unscaledTime + PerfLogIntervalSeconds;
        Debug.Log("[GraphicsQualityRunner] 起動しました");
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }

    private void Update()
    {
        float dt = Time.unscaledDeltaTime;

        _displayFrames++;
        _displayTime += dt;
        if (dt > _displayWorstDelta) _displayWorstDelta = dt;
        if (_displayTime >= FpsDisplayWindowSeconds)
        {
            _displayFps = _displayFrames / _displayTime;
            _displayWorstFps = _displayWorstDelta > 0f ? 1f / _displayWorstDelta : 0f;
            _displayFrames = 0;
            _displayTime = 0f;
            _displayWorstDelta = 0f;
        }

        _logFrames++;
        _logTime += dt;
        if (dt > _logWorstDelta) _logWorstDelta = dt;
        if (Time.unscaledTime >= _nextPerfLogTime)
        {
            _nextPerfLogTime = Time.unscaledTime + PerfLogIntervalSeconds;
            float avg = _logTime > 0f ? _logFrames / _logTime : 0f;
            float worst = _logWorstDelta > 0f ? 1f / _logWorstDelta : 0f;
            Debug.Log($"[Perf] 直近{PerfLogIntervalSeconds:0}秒 平均{avg:0.0}fps 最低{worst:0.0}fps 画質={GraphicsQualitySettings.Level} " +
                $"拾い物の描画 {WeaponPickup.ActiveCount - WeaponPickup.DistanceCulledCount}/{WeaponPickup.ActiveCount}個");
            _logFrames = 0;
            _logTime = 0f;
            _logWorstDelta = 0f;
        }

        if (Time.unscaledTime >= _nextCameraRefreshTime)
        {
            _nextCameraRefreshTime = Time.unscaledTime + CameraRefreshIntervalSeconds;
            ApplyToAllCameras();
        }
    }

    /// <summary>全カメラへ現在の画質プリセット(影/HDR/描画距離)を適用する</summary>
    public void ApplyToAllCameras()
    {
        int count = Camera.allCamerasCount;
        if (_cameraBuffer.Length < count) _cameraBuffer = new Camera[count * 2];
        count = Camera.GetAllCameras(_cameraBuffer);

        for (int i = 0; i < count; i++)
        {
            Camera cam = _cameraBuffer[i];
            _cameraBuffer[i] = null; // 破棄されたカメラへの参照を配列に残さない
            if (cam == null) continue;
            // RenderTextureへ描くカメラ(武器アイコン撮影用など)は対象外
            if (cam.targetTexture != null || cam.cameraType != CameraType.Game) continue;
            ApplyToCamera(cam);
        }

        if (_originals.Count > 32) PruneDestroyedCameras();
    }

    private void ApplyToCamera(Camera cam)
    {
        if (!_originals.TryGetValue(cam, out CameraOriginal original))
        {
            original = new CameraOriginal { AllowHdr = cam.allowHDR, FarClip = cam.farClipPlane };
            _originals[cam] = original;
            Debug.Log($"[GraphicsQualityRunner] カメラ {cam.name} の元の値を記録 allowHDR={original.AllowHdr} far={original.FarClip:0.#}");
        }

        bool hdr = GraphicsQualitySettings.HdrAllowed && original.AllowHdr;
        if (cam.allowHDR != hdr) cam.allowHDR = hdr;

        float farLimit = GraphicsQualitySettings.CameraFarClipLimit;
        float far = farLimit > 0f ? Mathf.Min(original.FarClip, farLimit) : original.FarClip;
        if (!Mathf.Approximately(cam.farClipPlane, far)) cam.farClipPlane = far;

        UniversalAdditionalCameraData data = cam.GetUniversalAdditionalCameraData();
        bool shadows = GraphicsQualitySettings.ShadowsEnabled;
        if (data != null && data.renderShadows != shadows) data.renderShadows = shadows;
    }

    private void PruneDestroyedCameras()
    {
        _deadCameraKeys.Clear();
        foreach (var pair in _originals)
        {
            if (pair.Key == null) _deadCameraKeys.Add(pair.Key);
        }
        foreach (var key in _deadCameraKeys) _originals.Remove(key);
    }

    /// <summary>FPS表示(設定でON時のみ)。他のHUDと同じGUIScalerの仮想座標系で左上に小さく描く</summary>
    private void OnGUI()
    {
        GUI.depth = -1000; // 他のOnGUI(HUD等)より手前に描く
        if (!GraphicsQualitySettings.ShowFps) return;
        if (Event.current.type != EventType.Repaint) return;

        if (_fpsStyle == null)
        {
            _fpsStyle = new GUIStyle(GUI.skin.label) { fontStyle = FontStyle.Bold };
            _fpsShadowStyle = new GUIStyle(_fpsStyle);
            _fpsShadowStyle.normal.textColor = new Color(0f, 0f, 0f, 0.85f);
        }

        PopUI.EnsureSkinFontApplied();
        GUIScaler.Begin();

        const int fontSize = 13;
        _fpsStyle.fontSize = fontSize;
        _fpsShadowStyle.fontSize = fontSize;
        _fpsStyle.normal.textColor = _displayFps >= 50f ? new Color(0.4f, 1f, 0.4f)
            : _displayFps >= 28f ? new Color(1f, 0.9f, 0.3f)
            : new Color(1f, 0.4f, 0.4f);

        string text = $"{_displayFps:0} FPS (min {_displayWorstFps:0})";
        var rect = new Rect(GUIScaler.SafeAreaLeft + 4f, GUIScaler.SafeAreaTop + 2f, fontSize * 11f, 18f);
        GUI.Label(new Rect(rect.x + 1f, rect.y + 1f, rect.width, rect.height), text, _fpsShadowStyle);
        GUI.Label(rect, text, _fpsStyle);

        GUIScaler.End();
    }
}
