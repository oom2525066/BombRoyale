using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 自分が与えたダメージを、相手の位置に追従する数字として表示するHUD。
/// Health.LocalHitConfirmedイベントを購読し、同じ相手への短時間の連続ヒットは1つの数字へ合算する。
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class DamageNumberHud : NetworkBehaviour
{
    [Tooltip("1つの数字が画面に出ている時間(秒)")]
    [SerializeField] private float displayDurationSeconds = 0.8f;
    [Tooltip("同じ相手への連続ヒットをこの秒数以内なら1つの数字へ合算する")]
    [SerializeField] private float batchWindowSeconds = 0.3f;
    [Tooltip("表示中に上昇する距離(px、仮想解像度基準)")]
    [SerializeField] private float riseDistance = 46f;
    [Tooltip("この値以上のダメージは大ダメージ扱い(黄色・大きめの文字)")]
    [SerializeField] private float bigDamageThreshold = 35f;
    [Tooltip("ランダムにずらす範囲(px、仮想解像度基準)")]
    [SerializeField] private float randomOffsetRange = 18f;

    private class Entry
    {
        public Transform Target;
        public ulong TargetNetworkObjectId;
        public float Amount;
        public float StartTime;
        public float ExpireTime;
        public bool IsKill;
        public Health.DamageType Type;
        public Vector2 RandomOffset;
    }

    private readonly List<Entry> _entries = new List<Entry>();
    private readonly Dictionary<ulong, Entry> _recentByTarget = new Dictionary<ulong, Entry>();

    public override void OnNetworkSpawn()
    {
        if (IsOwner) Health.LocalHitConfirmed += HandleLocalHitConfirmed;
    }

    public override void OnNetworkDespawn()
    {
        if (IsOwner) Health.LocalHitConfirmed -= HandleLocalHitConfirmed;
    }

    /// <summary>ヒット通知の購読先。被弾者のNetworkObjectIdをキーに、短時間の連続ダメージを合算する</summary>
    private void HandleLocalHitConfirmed(Health victim, float amount, bool isKill, Health.DamageType damageType)
    {
        if (victim == null) return;

        float now = Time.time;
        ulong targetId = victim.NetworkObjectId;

        if (_recentByTarget.TryGetValue(targetId, out Entry existing) && now - existing.StartTime < batchWindowSeconds)
        {
            existing.Amount += amount;
            existing.IsKill |= isKill;
            if (damageType != Health.DamageType.Normal) existing.Type = damageType;
            existing.ExpireTime = now + displayDurationSeconds;
            return;
        }

        var entry = new Entry
        {
            Target = victim.transform,
            TargetNetworkObjectId = targetId,
            Amount = amount,
            StartTime = now,
            ExpireTime = now + displayDurationSeconds,
            IsKill = isKill,
            Type = damageType,
            RandomOffset = new Vector2(Random.Range(-randomOffsetRange, randomOffsetRange), Random.Range(-randomOffsetRange * 0.5f, 0f)),
        };
        _entries.Add(entry);
        _recentByTarget[targetId] = entry;
    }

    private void Update()
    {
        if (_entries.Count == 0) return;

        float now = Time.time;
        for (int i = _entries.Count - 1; i >= 0; i--)
        {
            Entry e = _entries[i];
            if (now >= e.ExpireTime || e.Target == null)
            {
                _entries.RemoveAt(i);
                if (_recentByTarget.TryGetValue(e.TargetNetworkObjectId, out Entry current) && current == e)
                {
                    _recentByTarget.Remove(e.TargetNetworkObjectId);
                }
            }
        }
    }

    private void OnGUI()
    {
        if (!IsSpawned || !IsOwner) return;
        if (!DamageDisplaySettings.Enabled) return;
        if (MatchManager.Instance != null && !MatchManager.Instance.IsMatchActiveForInput) return;
        if (_entries.Count == 0) return;

        Camera cam = PlayerController.LocalPlayer != null ? PlayerController.LocalPlayer.PlayerCamera : null;
        if (cam == null) return;

        PopUI.EnsureSkinFontApplied();
        GUIScaler.Begin();

        foreach (Entry e in _entries)
        {
            if (e.Target == null) continue;
            DrawEntry(cam, e);
        }

        GUIScaler.End();
    }

    /// <summary>相手の頭上のワールド座標をスクリーン座標へ変換し、GUIScalerの仮想座標系(左上原点)で数字を描く</summary>
    private void DrawEntry(Camera cam, Entry e)
    {
        Vector3 worldPos = e.Target.position + Vector3.up * 1.6f;
        Vector3 screenPos = cam.WorldToScreenPoint(worldPos);
        if (screenPos.z < 0f) return; // カメラの後ろ(背後にいる相手)は描かない

        float guiX = screenPos.x / GUIScaler.Scale;
        float guiY = (Screen.height - screenPos.y) / GUIScaler.Scale;

        float t = Mathf.Clamp01((Time.time - e.StartTime) / displayDurationSeconds);
        float alpha = 1f - t;
        float riseOffset = riseDistance * t;

        bool isBig = e.Amount >= bigDamageThreshold;
        Color color = e.IsKill ? new Color(1f, 0.25f, 0.2f)
            : e.Type == Health.DamageType.Fire ? new Color(1f, 0.55f, 0.15f)
            : isBig ? PopUI.Yellow
            : Color.white;
        int fontSize = e.IsKill ? 46 : isBig ? 38 : 30;

        var style = new GUIStyle(GUI.skin.label) { fontSize = fontSize, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        style.normal.textColor = new Color(color.r, color.g, color.b, alpha);

        string text = Mathf.RoundToInt(e.Amount).ToString();
        const float boxWidth = 200f;
        const float boxHeight = 64f;
        var rect = new Rect(
            guiX + e.RandomOffset.x - boxWidth / 2f,
            guiY + e.RandomOffset.y - riseOffset - boxHeight / 2f,
            boxWidth, boxHeight);

        // 黒縁取り+本体色で描く
        Color outlineColor = new Color(0f, 0f, 0f, alpha * 0.85f);
        PopUI.DrawOutlinedLabel(rect, text, style, outlineColor, 2.5f);
    }
}
