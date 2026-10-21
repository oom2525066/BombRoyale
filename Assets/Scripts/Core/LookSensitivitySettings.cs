using UnityEngine;

/// <summary>
/// 視点感度の倍率設定。PlayerPrefsに保存するクライアントローカル設定で、入力生成の末尾で倍率として掛けられる。
/// </summary>
public static class LookSensitivitySettings
{
    private const string PrefsKey = "LookSensitivityMultiplier";

    public const float Min = 0.3f;
    public const float Max = 3f;
    public const float Default = 1f;

    private static bool _initialized;
    private static float _multiplier;

    public static float Multiplier
    {
        get { EnsureInitialized(); return _multiplier; }
        set
        {
            EnsureInitialized();
            float clamped = Mathf.Clamp(value, Min, Max);
            if (Mathf.Approximately(_multiplier, clamped)) return;
            _multiplier = clamped;
            PlayerPrefs.SetFloat(PrefsKey, _multiplier);
            PlayerPrefs.Save();
        }
    }

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;

        _multiplier = PlayerPrefs.HasKey(PrefsKey)
            ? Mathf.Clamp(PlayerPrefs.GetFloat(PrefsKey), Min, Max)
            : Default;
    }
}
