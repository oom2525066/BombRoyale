using UnityEngine;

/// <summary>
/// 他プレイヤーのアウトライン表示のON/OFFと色の設定。PlayerPrefsに保存するクライアントローカル設定で、
/// 変更時はChangedイベントで購読中のPlayerOutlineへ通知する。
/// </summary>
public static class OutlineSettings
{
    private const string EnabledPrefsKey = "OutlineEnabled";
    private const string ColorIndexPrefsKey = "OutlineColorIndex";

    // 色プリセット(地面/建物の色とかぶらない彩度の高い色)
    public static readonly Color[] ColorPalette =
    {
        new Color(1f, 0.85f, 0.2f, 1f),   // 黄
        new Color(1f, 0.2f, 0.2f, 1f),    // 赤
        new Color(0.2f, 0.6f, 1f, 1f),    // 青
        new Color(0.3f, 1f, 0.4f, 1f),    // 緑
        new Color(1f, 1f, 1f, 1f),        // 白
    };

    private static bool _initialized;
    private static bool _enabled;
    private static int _colorIndex;

    public static event System.Action Changed;

    public static bool Enabled
    {
        get { EnsureInitialized(); return _enabled; }
        set
        {
            EnsureInitialized();
            if (_enabled == value) return;
            _enabled = value;
            PlayerPrefs.SetInt(EnabledPrefsKey, value ? 1 : 0);
            PlayerPrefs.Save();
            Changed?.Invoke();
        }
    }

    public static Color CurrentColor
    {
        get { EnsureInitialized(); return ColorPalette[_colorIndex]; }
    }

    /// <summary>メニューのボタンから呼ぶ。パレットの次の色へ順送りする</summary>
    public static void CycleColor()
    {
        EnsureInitialized();
        _colorIndex = (_colorIndex + 1) % ColorPalette.Length;
        PlayerPrefs.SetInt(ColorIndexPrefsKey, _colorIndex);
        PlayerPrefs.Save();
        Changed?.Invoke();
    }

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;

        _enabled = !PlayerPrefs.HasKey(EnabledPrefsKey) || PlayerPrefs.GetInt(EnabledPrefsKey) != 0;
        _colorIndex = PlayerPrefs.HasKey(ColorIndexPrefsKey)
            ? Mathf.Clamp(PlayerPrefs.GetInt(ColorIndexPrefsKey), 0, ColorPalette.Length - 1)
            : 0;
    }
}
