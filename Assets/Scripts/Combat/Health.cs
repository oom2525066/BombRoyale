using Unity.Netcode;
using UnityEngine;

/// <summary>
/// サーバー権威のHPコンポーネント。ダメージはサーバーで適用し、0以下でMatchManager.NotifyPlayerEliminated()を呼ぶ。
/// Owner本人には、被弾フラッシュ・HPバー・被ダメージ数字をOnGUIで表示する。
/// </summary>
public class Health : NetworkBehaviour
{
    [SerializeField] private float maxHealth = 150f;
    [Header("被弾フィードバック(Owner本人にのみ表示)")]
    [SerializeField] private float damageFlashDurationSeconds = 0.3f;
    [SerializeField] private float damageFlashMaxAlpha = 0.35f;
    [Tooltip("被弾した瞬間に鳴らす警告音の候補(Owner本人のみ。複数ならランダムに1つ。未設定なら鳴らさない)")]
    [SerializeField] private AudioClip[] damageSounds;
    [Range(0f, 1f)] [SerializeField] private float damageSoundVolume = 0.5f;
    [Header("HP表示(Owner本人にのみ表示)")]
    [Tooltip("SafeArea(左上)からの余白。GUIScaler.SafeAreaLeft/Topに加算して使う")]
    [SerializeField] private Vector2 hpBarPosition = new Vector2(30f, 24f);
    [Tooltip("HPバー全体(アイコン含む)のサイズ。Player.prefab側にシリアライズされた値が優先される")]
    [SerializeField] private Vector2 hpBarSize = new Vector2(300f, 48f);
    [Tooltip("HPが危険域(25%)を下回った瞬間に1回だけ鳴らす警告音(Owner本人のみ。未設定なら鳴らさない)")]
    [SerializeField] private AudioClip lowHealthWarningClip;
    [Range(0f, 1f)] [SerializeField] private float lowHealthWarningVolume = 0.6f;

    public NetworkVariable<float> CurrentHealth = new NetworkVariable<float>(
        100f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public bool IsDead => CurrentHealth.Value <= 0f;

    private float _damageFlashExpireTime;
    private static Texture2D _flashTexture;

    // 危険域を「跨いだ瞬間」だけ警告音を鳴らすためのフラグ(スポーン直後に鳴らないよう初期値はtrue)
    private bool _wasAboveCriticalHealth = true;

    /// <summary>ダメージの種類(ダメージ数字の色分け用)</summary>
    public enum DamageType { Normal, Fire }

    /// <summary>攻撃者本人にだけ届く「当てた」通知(ヒットマーカー/ダメージ数字が購読)。引数はヒットした相手・ダメージ量・撃破か・種類</summary>
    public static event System.Action<Health, float, bool, DamageType> LocalHitConfirmed;

    /// <summary>自分自身へのダメージ(自爆/誘爆の巻き込まれ)専用の通知。HPバー横の表示が購読する</summary>
    public static event System.Action<float, bool> LocalSelfDamaged;

    public override void OnNetworkSpawn()
    {
        if (IsServer) CurrentHealth.Value = maxHealth;
        CurrentHealth.OnValueChanged += HandleHealthChanged;
        if (IsOwner) LocalSelfDamaged += HandleLocalSelfDamaged;
    }

    public override void OnNetworkDespawn()
    {
        CurrentHealth.OnValueChanged -= HandleHealthChanged;
        if (IsOwner) LocalSelfDamaged -= HandleLocalSelfDamaged;
    }

    private float _selfDamageExpireTime;
    private float _selfDamageAmount;
    private bool _selfDamageIsKill;
    private const float SelfDamageDisplaySeconds = 0.8f;

    private void HandleLocalSelfDamaged(float amount, bool isKill)
    {
        _selfDamageAmount = amount;
        _selfDamageIsKill = isKill;
        _selfDamageExpireTime = Time.time + SelfDamageDisplaySeconds;
    }

    /// <summary>HP減少(被弾)の瞬間だけ、Owner本人に被弾フラッシュと警告音を出す(回復や全快リセットでは出さない)</summary>
    private void HandleHealthChanged(float previousValue, float newValue)
    {
        if (!IsOwner || newValue >= previousValue) return;
        _damageFlashExpireTime = Time.time + damageFlashDurationSeconds;

        if (damageSounds != null && damageSounds.Length > 0)
        {
            AudioClip clip = damageSounds[Random.Range(0, damageSounds.Length)];
            float scale = SoundManager.Instance != null ? SoundManager.Instance.SfxVolumeScale : 1f;
            SfxPlayer.Play2D(clip, damageSoundVolume * scale); // Owner本人だけに鳴る音なので2Dで
        }
    }

    /// <summary>危険域を下回った瞬間に呼ばれる単発の警告音(Owner本人のみ)</summary>
    private void PlayLowHealthWarningSound()
    {
        if (lowHealthWarningClip == null) return;
        float scale = SoundManager.Instance != null ? SoundManager.Instance.SfxVolumeScale : 1f;
        SfxPlayer.Play2D(lowHealthWarningClip, lowHealthWarningVolume * scale); // Owner本人だけに鳴る音なので2Dで
    }

    private void OnGUI()
    {
        if (!IsOwner || !IsSpawned) return;

        PopUI.EnsureSkinFontApplied();
        GUIScaler.Begin();

        DrawHealthBar();

        float remaining = _damageFlashExpireTime - Time.time;
        if (remaining > 0f)
        {
            float alpha = Mathf.Clamp01(remaining / damageFlashDurationSeconds) * damageFlashMaxAlpha;
            DrawDamageFlash(alpha);
        }

        GUIScaler.End();
    }

    /// <summary>画面左上のHPバー(十字アイコン+色で残量を示すバー+控えめな数値)。残量が少ないとバーが明滅する</summary>
    private void DrawHealthBar()
    {
        float ratio = maxHealth > 0f ? Mathf.Clamp01(CurrentHealth.Value / maxHealth) : 0f;

        const float iconSize = 38f;
        const float barHeight = 34f;
        const float iconGap = 8f;

        float originX = GUIScaler.SafeAreaLeft + hpBarPosition.x;
        float originY = GUIScaler.SafeAreaTop + hpBarPosition.y;

        if (_healthIconTexture == null) _healthIconTexture = BuildHealthIconTexture(64);
        var iconRect = new Rect(originX, originY + (barHeight - iconSize) / 2f, iconSize, iconSize);
        Color prevIconColor = GUI.color;
        GUI.color = new Color(1f, 0.4f, 0.4f); // 体力アイコンらしい赤みがかった色
        GUI.DrawTexture(iconRect, _healthIconTexture);
        GUI.color = prevIconColor;

        float barX = iconRect.xMax + iconGap;
        float barWidth = Mathf.Max(80f, hpBarSize.x - iconSize - iconGap);
        var backRect = new Rect(barX, originY, barWidth, barHeight);

        GUI.DrawTexture(backRect, PopUI.PanelTexture);

        // 緑→黄→赤の2区間補間
        Color barColor = ratio > 0.5f
            ? Color.Lerp(PopUI.Yellow, new Color(0.25f, 0.85f, 0.25f), (ratio - 0.5f) * 2f)
            : Color.Lerp(new Color(0.85f, 0.15f, 0.1f), PopUI.Yellow, ratio * 2f);

        // 残量が少ない間は、バーの色を白側へ脈打たせる
        const float criticalThreshold = 0.25f;
        bool isCritical = ratio > 0f && ratio < criticalThreshold;
        if (isCritical)
        {
            float pulse = (Mathf.Sin(Time.time * 6f) + 1f) * 0.5f; // 0〜1
            barColor = Color.Lerp(barColor, Color.white, pulse * 0.35f);
        }

        // 閾値を下回った瞬間だけ警告音を鳴らす
        if (isCritical && _wasAboveCriticalHealth) PlayLowHealthWarningSound();
        _wasAboveCriticalHealth = !isCritical;

        Color previous = GUI.color;
        GUI.color = barColor;
        const float padding = 4f;
        GUI.DrawTexture(new Rect(backRect.x + padding, backRect.y + padding, (backRect.width - padding * 2) * ratio, backRect.height - padding * 2), PopUI.SolidTexture);
        GUI.color = previous;

        // 数値はバーの下に控えめに添える(矩形を高めに取り、グリフが欠けないようにする)
        var numberStyle = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.UpperLeft };
        numberStyle.normal.textColor = new Color(1f, 1f, 1f, 0.85f);
        var numberRect = new Rect(originX, backRect.yMax + 4f, hpBarSize.x, 24f);
        GUI.Label(numberRect, $"{Mathf.CeilToInt(CurrentHealth.Value)} / {Mathf.CeilToInt(maxHealth)}", numberStyle);

        DrawSelfDamageNumber(backRect);
    }

    /// <summary>自爆ダメージを、HPバーの右側に0.8秒だけ浮かせて表示する</summary>
    private void DrawSelfDamageNumber(Rect hpBarRect)
    {
        if (!DamageDisplaySettings.Enabled) return;
        float remaining = _selfDamageExpireTime - Time.time;
        if (remaining <= 0f) return;

        float t = 1f - Mathf.Clamp01(remaining / SelfDamageDisplaySeconds);
        float alpha = 1f - t;
        float riseOffset = 16f * t;

        var style = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
        style.normal.textColor = _selfDamageIsKill
            ? new Color(1f, 0.25f, 0.2f, alpha)
            : new Color(1f, 0.75f, 0.3f, alpha); // 自爆は警告寄りのオレンジ系
        string text = $"-{Mathf.RoundToInt(_selfDamageAmount)}";
        var rect = new Rect(hpBarRect.xMax + 10f, hpBarRect.y - riseOffset, 90f, hpBarRect.height);
        PopUI.DrawOutlinedLabel(rect, text, style, new Color(0f, 0f, 0f, alpha * 0.85f), 2f);
    }

    private static Texture2D _healthIconTexture;

    /// <summary>十字(プラス記号)の体力アイコンを実行時に生成する</summary>
    private static Texture2D BuildHealthIconTexture(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        var clear = new Color(1f, 1f, 1f, 0f);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                tex.SetPixel(x, y, clear);
            }
        }

        int thickness = Mathf.Max(2, size / 3);
        int barStart = (size - thickness) / 2;
        int barEnd = barStart + thickness;
        int margin = size / 8;

        for (int y = margin; y < size - margin; y++)
        {
            for (int x = barStart; x < barEnd; x++)
            {
                tex.SetPixel(x, y, Color.white);
            }
        }
        for (int x = margin; x < size - margin; x++)
        {
            for (int y = barStart; y < barEnd; y++)
            {
                tex.SetPixel(x, y, Color.white);
            }
        }

        tex.Apply();
        return tex;
    }

    /// <summary>画面の四辺に赤い帯を描く簡易ビネット(被弾フラッシュ)</summary>
    private static void DrawDamageFlash(float alpha)
    {
        if (_flashTexture == null)
        {
            _flashTexture = new Texture2D(1, 1);
            _flashTexture.SetPixel(0, 0, Color.white);
            _flashTexture.Apply();
        }

        Color previous = GUI.color;
        GUI.color = new Color(0.9f, 0f, 0f, alpha);

        float vw = GUIScaler.VirtualWidth;
        float vh = GUIScaler.VirtualHeight;
        float edge = Mathf.Min(vw, vh) * 0.12f;
        GUI.DrawTexture(new Rect(0, 0, vw, edge), _flashTexture);
        GUI.DrawTexture(new Rect(0, vh - edge, vw, edge), _flashTexture);
        GUI.DrawTexture(new Rect(0, 0, edge, vh), _flashTexture);
        GUI.DrawTexture(new Rect(vw - edge, 0, edge, vh), _flashTexture);

        GUI.color = previous;
    }

    /// <summary>試合開始時にHPを全快に戻す(プレイヤーはDespawnされず使い回されるため)</summary>
    public void ServerResetHealth()
    {
        if (!IsServer) return;
        CurrentHealth.Value = maxHealth;
    }

    /// <summary>サーバー専用のダメージ適用。死亡済みなら何もしない。damageTypeは数字の色分け用</summary>
    public void ServerApplyDamage(float amount, ulong instigatorClientId, DamageType damageType = DamageType.Normal)
    {
        if (!IsServer || IsDead || amount <= 0f) return;

        CurrentHealth.Value = Mathf.Max(0f, CurrentHealth.Value - amount);
        Debug.Log($"[Health] {gameObject.name}(client {OwnerClientId}) が {amount:F0} ダメージ (残りHP {CurrentHealth.Value:F0}, 攻撃者 client {instigatorClientId}, 種類={damageType})");

        // 与ダメージ合計(リザルト用)。自爆は含めない
        if (instigatorClientId != OwnerClientId)
        {
            foreach (var pc in PlayerController.ActiveInstances)
            {
                if (pc != null && pc.OwnerClientId == instigatorClientId)
                {
                    pc.TotalDamageDealt.Value += amount;
                    break;
                }
            }
        }

        bool isKillingBlow = CurrentHealth.Value <= 0f;

        // 自爆はヒットマーカーではなく、自分専用の被ダメージ通知を使う
        if (instigatorClientId != OwnerClientId)
        {
            var rpcParams = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { instigatorClientId } } };
            NotifyHitConfirmedClientRpc(amount, isKillingBlow, damageType, rpcParams);
            Debug.Log($"[Health] ヒットマーカー通知を送信 攻撃者 client {instigatorClientId} へ (isKill={isKillingBlow})");
        }
        else
        {
            var selfRpcParams = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } } };
            NotifySelfDamagedClientRpc(amount, isKillingBlow, selfRpcParams);
            Debug.Log($"[Health] 自爆のため自分専用の被ダメージ通知を送信 (client {OwnerClientId})");
        }

        if (isKillingBlow)
        {
            Debug.Log($"[Health] {gameObject.name}(client {OwnerClientId}) が撃破されました (攻撃者 client {instigatorClientId})");
            MatchManager.Instance?.BroadcastKill(OwnerClientId, instigatorClientId, transform.position);
            MatchManager.Instance?.NotifyPlayerEliminated(OwnerClientId);
        }
    }

    [ClientRpc]
    private void NotifyHitConfirmedClientRpc(float amount, bool isKill, DamageType damageType, ClientRpcParams rpcParams = default)
    {
        Debug.Log($"[Health] ヒットマーカー通知を受信 amount={amount:F0} isKill={isKill} (購読者={(LocalHitConfirmed == null ? 0 : LocalHitConfirmed.GetInvocationList().Length)}件)");
        LocalHitConfirmed?.Invoke(this, amount, isKill, damageType);
    }

    [ClientRpc]
    private void NotifySelfDamagedClientRpc(float amount, bool isKill, ClientRpcParams rpcParams = default)
    {
        LocalSelfDamaged?.Invoke(amount, isKill);
    }
}
