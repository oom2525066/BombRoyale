using UnityEngine;

/// <summary>
/// ダメージ数字表示(ビルボード/自爆時の被ダメージ)のON/OFF設定。PlayerPrefsに保存するクライアントローカル設定。
/// </summary>
public static class DamageDisplaySettings
{
    private const string EnabledPrefsKey = "DamageDisplayEnabled";

    private static bool _initialized;
    private static bool _enabled;

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

    private static void EnsureInitialized()
    {
        if (_initialized) return;
        _initialized = true;
        _enabled = !PlayerPrefs.HasKey(EnabledPrefsKey) || PlayerPrefs.GetInt(EnabledPrefsKey) != 0;
    }
}
