using System;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

public enum GraphicsQualityLevel
{
    Low = 0,
    Medium = 1,
    High = 2,
}

/// <summary>
/// 画質プリセット(低/中/高)の設定。描画解像度・影・HDR・拾い物の描画距離・カメラ描画距離・fps上限を切り替える。
/// ゲームの中身には触れない、PlayerPrefs保存のクライアントローカル設定。
/// URPアセットは起動時の値を覚えておき、終了時に元へ戻す。カメラ単位の設定はGraphicsQualityRunnerが適用する。
/// </summary>
public static class GraphicsQualitySettings
{
    private const string LevelPrefsKey = "GraphicsQualityLevel";
    private const string ShowFpsPrefsKey = "ShowFps";

    private static bool _initialized;
    private static GraphicsQualityLevel _level;
    private static bool _showFps;

    // 書き換え中のURPアセットと、書き換える前の元の値(終了時に戻すため)
    private static UniversalRenderPipelineAsset _modifiedAsset;
    private static float _originalRenderScale;
    private static float _originalShadowDistance;

    public static event Action Changed;

    public static GraphicsQualityLevel Level
    {
        get { EnsureInitialized(); return _level; }
        set
        {
            EnsureInitialized();
            if (_level == value) return;
            GraphicsQualityLevel previous = _level;
            _level = value;
            PlayerPrefs.SetInt(LevelPrefsKey, (int)value);
            PlayerPrefs.Save();
            Debug.Log($"[GraphicsQuality] 画質を変更しました {previous} → {value}");
            Apply();
            Changed?.Invoke();
        }
    }

    /// <summary>設定画面のボタンから呼ぶ。低→中→高→低…と順送りする</summary>
    public static void CycleLevel()
    {
        Level = (GraphicsQualityLevel)(((int)Level + 1) % 3);
    }

    public static bool ShowFps
    {
        get { EnsureInitialized(); return _showFps; }
        set
        {
            EnsureInitialized();
            if (_showFps == value) return;
            _showFps = value;
            PlayerPrefs.SetInt(ShowFpsPrefsKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Debug.Log($"[GraphicsQuality] FPS表示を{(value ? "ON" : "OFF")}にしました");
            Changed?.Invoke();
        }
    }

    // ---- プリセットの中身 ----

    /// <summary>拾い物の見た目を描画する最大距離(m)。0以下は無制限</summary>
    public static float PickupDrawDistance
    {
        get
        {
            switch (Level)
            {
                case GraphicsQualityLevel.Low: return 25f;
                case GraphicsQualityLevel.Medium: return 40f;
                default: return 0f;
            }
        }
    }

    public static bool ShadowsEnabled => Level != GraphicsQualityLevel.Low;

    /// <summary>falseならHDRを強制的に切る。trueならカメラ本来の設定のまま</summary>
    public static bool HdrAllowed => Level == GraphicsQualityLevel.High;

    /// <summary>カメラの描画距離の上限(m)。0以下は変更しない(カメラ本来の値のまま)</summary>
    public static float CameraFarClipLimit => Level == GraphicsQualityLevel.Low ? 80f : 0f;

    private static float RenderScaleLimit
    {
        get
        {
            switch (Level)
            {
                case GraphicsQualityLevel.Low: return 0.6f;
                case GraphicsQualityLevel.Medium: return 0.7f;
                default: return float.MaxValue; // 高は元の値のまま
            }
        }
    }

    private static float ShadowDistanceLimit
    {
        get
        {
            switch (Level)
            {
                case GraphicsQualityLevel.Medium: return 25f;
                default: return float.MaxValue; // 低はカメラ側で影ごと切る/高は元の値のまま
            }
        }
    }

    private static int TargetFrameRate => Level == GraphicsQualityLevel.Low ? 30 : 60;

    /// <summary>設定画面のラベル用(Localizationのキー)</summary>
    public static string LevelLocalizationKey
    {
        get
        {
            switch (Level)
            {
                case GraphicsQualityLevel.Low: return "ui.graphicsLow";
                case GraphicsQualityLevel.Medium: return "ui.graphicsMedium";
                default: return "ui.graphicsHigh";
            }
        }
    }

    public static string LevelHintLocalizationKey
    {
        get
        {
            switch (Level)
            {
                case GraphicsQualityLevel.Low: return "ui.graphicsHintLow";
                case GraphicsQualityLevel.Medium: return "ui.graphicsHintMedium";
                default: return "ui.graphicsHintHigh";
            }
        }
    }

    // ---- 初期化・適用 ----

    // ドメインリロード無効時にstaticの値が残らないようにする
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        _initialized = false;
        _modifiedAsset = null;
        Changed = null;
    }

    // PerformanceSettings(BeforeSceneLoad)より後に走らせ、プリセットのfps上限で上書きする
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        EnsureInitialized();
        Application.quitting -= RestoreAsset;
        Application.quitting += RestoreAsset;
        GraphicsQualityRunner.EnsureExists();
        Apply();
    }

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;

        if (PlayerPrefs.HasKey(LevelPrefsKey))
        {
            _level = (GraphicsQualityLevel)Mathf.Clamp(PlayerPrefs.GetInt(LevelPrefsKey), 0, 2);
        }
        else
        {
            // 初回起動の既定値: スマホは軽さ優先、PCは高画質
            _level = Application.isMobilePlatform ? GraphicsQualityLevel.Low : GraphicsQualityLevel.High;
        }
        _showFps = PlayerPrefs.GetInt(ShowFpsPrefsKey, 0) == 1;

        Debug.Log($"[GraphicsQuality] 設定を読み込みました level={_level} showFps={_showFps} " +
            $"(保存済み={PlayerPrefs.HasKey(LevelPrefsKey)} isMobilePlatform={Application.isMobilePlatform})");
    }

    /// <summary>現在のプリセットを全体へ反映する(fps上限・URPアセット・全カメラ)</summary>
    public static void Apply()
    {
        EnsureInitialized();

        QualitySettings.vSyncCount = 0; // vSyncCountが0でないとtargetFrameRateが無視される
        Application.targetFrameRate = TargetFrameRate;

        ApplyAsset();
        if (GraphicsQualityRunner.Instance != null) GraphicsQualityRunner.Instance.ApplyToAllCameras();

        Debug.Log($"[GraphicsQuality] 適用 level={Level} targetFps={TargetFrameRate} " +
            $"renderScale={(_modifiedAsset != null ? _modifiedAsset.renderScale.ToString("0.00") : "-")} " +
            $"shadowDistance={(_modifiedAsset != null ? _modifiedAsset.shadowDistance.ToString("0.#") : "-")} " +
            $"shadows={ShadowsEnabled} hdr={HdrAllowed} pickupDrawDistance={PickupDrawDistance} farClipLimit={CameraFarClipLimit}");
    }

    private static void ApplyAsset()
    {
        var asset = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
        if (asset == null)
        {
            Debug.LogWarning("[GraphicsQuality] URPアセットを取得できなかったため、描画解像度/影距離は変更しません");
            return;
        }

        if (_modifiedAsset != asset)
        {
            // 使用中のアセットが変わった場合は、前のアセットを戻してから覚え直す
            RestoreAsset();
            _modifiedAsset = asset;
            _originalRenderScale = asset.renderScale;
            _originalShadowDistance = asset.shadowDistance;
            Debug.Log($"[GraphicsQuality] URPアセット {asset.name} の元の値を記録 renderScale={_originalRenderScale:0.00} shadowDistance={_originalShadowDistance:0.#}");
        }

        asset.renderScale = Mathf.Min(_originalRenderScale, RenderScaleLimit);
        asset.shadowDistance = Mathf.Min(_originalShadowDistance, ShadowDistanceLimit);
    }

    /// <summary>URPアセットを起動時の値へ戻す(Editorで共有アセットが書き換わったまま残らないようにする)</summary>
    private static void RestoreAsset()
    {
        if (_modifiedAsset == null) return;
        _modifiedAsset.renderScale = _originalRenderScale;
        _modifiedAsset.shadowDistance = _originalShadowDistance;
        Debug.Log($"[GraphicsQuality] URPアセット {_modifiedAsset.name} を元の値へ戻しました renderScale={_originalRenderScale:0.00} shadowDistance={_originalShadowDistance:0.#}");
        _modifiedAsset = null;
    }
}
