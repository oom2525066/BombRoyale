using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 画面左上に直近のキル履歴を一定時間表示するキルフィード(OnGUIベース)。
/// MatchManager.BroadcastKillClientRpc()がAddEntry()を呼ぶ。シーンに1つだけ置くシングルトンで、
/// 全クライアントに共通のHUDのためOwner判定は行わない。
/// </summary>
public class KillFeedUI : MonoBehaviour
{
    public static KillFeedUI Instance { get; private set; }

    [SerializeField] private float entryLifetimeSeconds = 5f;
    [SerializeField] private int maxVisibleEntries = 5;
    [Tooltip("キル確定時(自滅含む全キル)に全クライアントで鳴らす効果音。未設定なら鳴らさない")]
    [SerializeField] private AudioClip killConfirmSound;
    [Range(0f, 1f)] [SerializeField] private float killConfirmVolume = 0.6f;

    private class Entry
    {
        public string Text;
        public float ExpireTime;
    }

    private readonly List<Entry> _entries = new List<Entry>();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    /// <summary>キルを1件追加する。victimClientId==killerClientIdなら自滅として表示を変える</summary>
    public void AddEntry(ulong victimClientId, ulong killerClientId)
    {
        string victimName = PlayerController.ResolveDisplayName(victimClientId);
        string killerName = PlayerController.ResolveDisplayName(killerClientId);
        string text = victimClientId == killerClientId
            ? $"{victimName} が自滅"
            : $"{killerName} ➜ {victimName}";

        _entries.Add(new Entry { Text = text, ExpireTime = Time.time + entryLifetimeSeconds });

        // 履歴が際限なく増えないよう上限で切り詰める
        const int maxStoredEntries = 30;
        while (_entries.Count > maxStoredEntries) _entries.RemoveAt(0);

        PlayKillConfirmSound();
    }

    /// <summary>キル確定の通知音を鳴らす(位置に関係なく2D再生)</summary>
    private void PlayKillConfirmSound()
    {
        if (killConfirmSound == null) return;

        Vector3 position = PlayerController.LocalPlayer != null ? PlayerController.LocalPlayer.transform.position : transform.position;
        float scale = SoundManager.Instance != null ? SoundManager.Instance.SfxVolumeScale : 1f;
        SfxPlayer.Play2D(killConfirmSound, killConfirmVolume * scale);
    }

    private void OnGUI()
    {
        _entries.RemoveAll(e => Time.time >= e.ExpireTime);

        PopUI.EnsureSkinFontApplied();
        GUIScaler.Begin();

        if (_entries.Count > 0)
        {
            const float width = 340f;
            const float lineHeight = 30f;
            const float gap = 5f;
            float y = 16f;

            var style = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
            style.normal.textColor = Color.white;
            style.padding = new RectOffset(12, 10, 0, 0);

            int start = Mathf.Max(0, _entries.Count - maxVisibleEntries);
            for (int i = start; i < _entries.Count; i++)
            {
                var rect = new Rect(16f, y, width, lineHeight);
                GUI.DrawTexture(rect, PopUI.PanelTexture);

                // 最新の行だけアクセントカラーの縦帯を付ける
                bool isLatest = i == _entries.Count - 1;
                if (isLatest)
                {
                    Color prevColor = GUI.color;
                    GUI.color = PopUI.Yellow;
                    GUI.DrawTexture(new Rect(rect.x, rect.y + 4f, 4f, rect.height - 8f), PopUI.SolidTexture);
                    GUI.color = prevColor;
                }

                GUI.Label(rect, _entries[i].Text, style);
                y += lineHeight + gap;
            }
        }

        GUIScaler.End();
    }
}
