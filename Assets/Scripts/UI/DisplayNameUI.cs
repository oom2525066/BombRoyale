using UnityEngine;

/// <summary>
/// プレイヤーのニックネーム(表示名)をPlayerPrefsに保存/読み込みする静的ユーティリティ。
/// 入力欄の描画はLobbyUIが担当し、こちらは永続化とバリデーションのみ行う。
/// </summary>
public static class DisplayNameUI
{
    private const string PrefsKey = "BombRoyale.DisplayName";
    private const int MaxLength = 12;

    /// <summary>保存済みの名前を返す。未設定なら"Player" + ランダムな4桁を生成して保存する</summary>
    public static string GetSavedName()
    {
        string saved = PlayerPrefs.GetString(PrefsKey, string.Empty);
        if (!string.IsNullOrWhiteSpace(saved)) return saved;

        string generated = $"Player{Random.Range(1000, 9999)}";
        PlayerPrefs.SetString(PrefsKey, generated);
        PlayerPrefs.Save();
        return generated;
    }

    /// <summary>前後の空白を除去し、最大文字数で切り詰めてから保存する。空文字なら保存しない</summary>
    public static void SetSavedName(string rawName)
    {
        string trimmed = (rawName ?? string.Empty).Trim();
        if (trimmed.Length == 0) return;
        if (trimmed.Length > MaxLength) trimmed = trimmed.Substring(0, MaxLength);

        PlayerPrefs.SetString(PrefsKey, trimmed);
        PlayerPrefs.Save();
    }
}
