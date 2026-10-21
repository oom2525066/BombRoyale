using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 試合全体の進行管理(シーン配置のNetworkObject)。
/// Lobby → StartMatch() → Countdown → Playing → RoundEnd → GameOver → ReturnToLobby() のフェーズを回し、
/// 生存者数・勝者・残り時間・キル通知・建物破壊の同期を担当する。
/// フェーズはenumをNetworkVariableにするとNGOのInspectorが例外を出すため、intで持ってPhaseプロパティ経由で扱う。
/// </summary>
public class MatchManager : NetworkBehaviour
{
    public enum GamePhase
    {
        Lobby,
        /// <summary>スポーン後、動き出せるようになるまでの「3, 2, 1, START!」の間</summary>
        Countdown,
        Playing,
        /// <summary>勝者確定からGameOver画面に切り替わるまでの数秒間</summary>
        RoundEnd,
        GameOver
    }

    public static MatchManager Instance { get; private set; }

    [SerializeField] private Transform[] spawnPoints;
    [Tooltip("スポーン地点が建物に埋まっていないかを調べる高さ(地点のY座標からの相対値)")]
    [SerializeField] private float spawnPointOverlapCheckHeight = 1.2f;
    [SerializeField] private float spawnPointOverlapCheckRadius = 0.4f;
    [SerializeField] private float matchTimeLimitSeconds = 300f; // 最大試合時間。0以下でタイムアップ判定なし
    [Tooltip("勝者が決まってからGameOver画面へ切り替わるまでの秒数")]
    [SerializeField] private float roundEndAnnounceSeconds = 4f;
    [Tooltip("試合開始から動けるようになるまでのカウントダウン秒数")]
    [SerializeField] private float countdownSeconds = 3f;

    [Header("カウントダウン効果音(全クライアントに聞こえるようClientRpcで再生)")]
    [Tooltip("カウントダウンの表示数字が変わる瞬間に鳴らすビープ音。未設定なら鳴らさない")]
    [SerializeField] private AudioClip countdownBeepClip;
    [Range(0f, 1f)] [SerializeField] private float countdownBeepVolume = 0.6f;
    [Tooltip("カウントダウンが0(START!)になる瞬間に鳴らす効果音。未設定なら鳴らさない")]
    [SerializeField] private AudioClip countdownStartClip;
    [Range(0f, 1f)] [SerializeField] private float countdownStartVolume = 0.7f;

    [Header("ラウンド終了ジングル(全クライアントに聞こえるようClientRpcで再生)")]
    [Tooltip("勝者確定の瞬間に鳴らすジングル。未設定なら鳴らさない")]
    [SerializeField] private AudioClip roundEndJingleClip;
    [Range(0f, 1f)] [SerializeField] private float roundEndJingleVolume = 0.7f;

    private float _roundEndTimer;

    /// <summary>Countdown中の残り秒数。全クライアントのUIが「3, 2, 1」の表示に使う</summary>
    public NetworkVariable<float> CountdownRemaining = new NetworkVariable<float>(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // enumを直接NetworkVariableにするとNGOのInspectorが例外を出すため、intで持つ
    private NetworkVariable<int> _phaseRaw = new NetworkVariable<int>(
        (int)GamePhase.Lobby, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>現在のゲームフェーズ(読み取りは誰でも可、書き込みはサーバーのみ)</summary>
    public GamePhase Phase
    {
        get => (GamePhase)_phaseRaw.Value;
        private set => _phaseRaw.Value = (int)value;
    }

    /// <summary>発砲/投擲/拾得といった戦闘系の操作を受け付けてよいフェーズか(Playing/RoundEnd)</summary>
    public bool IsMatchActiveForInput => Phase == GamePhase.Playing || Phase == GamePhase.RoundEnd;

    /// <summary>移動・視点操作を受け付けてよいフェーズか(GameOver以外は常に許可)</summary>
    public bool IsMovementActiveForInput => Phase != GamePhase.GameOver;

    public NetworkVariable<float> TimeRemaining = new NetworkVariable<float>(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    public NetworkVariable<int> AliveCount = new NetworkVariable<int>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>ulong.MaxValue = まだ勝者なし/引き分け。値はOwnerClientId</summary>
    public NetworkVariable<ulong> WinnerClientId = new NetworkVariable<ulong>(
        ulong.MaxValue, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // 脱落済みのクライアントID。重複通知や切断でAliveCountを二重に減らさないために記録する
    private readonly HashSet<ulong> _eliminatedClientIds = new HashSet<ulong>();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Debug.LogWarning("[MatchManager] シーン内に複数のMatchManagerが存在します。後発の方を無効化します。");
            enabled = false;
            return;
        }

        Instance = this;
    }

    public override void OnNetworkSpawn()
    {
        if (IsServer)
        {
            Phase = GamePhase.Lobby;
            NetworkManager.Singleton.OnClientDisconnectCallback += HandleClientDisconnected;
        }
    }

    public override void OnNetworkDespawn()
    {
        if (IsServer && NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientDisconnectCallback -= HandleClientDisconnected;
        }
    }

    private void Update()
    {
        // Shutdown直後はIsServerが残ったままNetworkVariableへの書き込みが無効になるため、IsSpawnedも見る
        if (!IsSpawned || !IsServer) return;

        if (Phase == GamePhase.RoundEnd)
        {
            _roundEndTimer -= Time.deltaTime;
            if (_roundEndTimer <= 0f)
            {
                Phase = GamePhase.GameOver;
            }
            return;
        }

        if (Phase == GamePhase.Countdown)
        {
            // 表示上の整数(CeilToInt)が変わった瞬間だけ全クライアントへビープ音を鳴らす
            int previousDisplayNumber = Mathf.CeilToInt(CountdownRemaining.Value);
            CountdownRemaining.Value = Mathf.Max(0f, CountdownRemaining.Value - Time.deltaTime);
            int newDisplayNumber = Mathf.CeilToInt(CountdownRemaining.Value);
            if (newDisplayNumber != previousDisplayNumber)
            {
                PlayCountdownSoundClientRpc(newDisplayNumber <= 0);
            }

            if (CountdownRemaining.Value <= 0f)
            {
                Phase = GamePhase.Playing;
            }
            return;
        }

        if (Phase != GamePhase.Playing) return;
        if (matchTimeLimitSeconds <= 0f) return;

        if (TimeRemaining.Value > 0f)
        {
            TimeRemaining.Value -= Time.deltaTime;
        }
        else
        {
            EndMatchByTimeout();
        }
    }

    /// <summary>ホストがロビーで「ゲーム開始」を押したときに呼ぶ。全員をスポーン地点へ配置してカウントダウンを始める</summary>
    public void StartMatch()
    {
        if (!IsServer || Phase == GamePhase.Playing) return;

        var players = new List<PlayerController>();
        foreach (var pc in PlayerController.ActiveInstances)
        {
            if (pc != null) players.Add(pc);
        }

        if (players.Count == 0) return;

        // スポーン地点が足りないと重なってスポーンするため、ホストにだけ警告する
        if (spawnPoints == null || spawnPoints.Length < players.Count)
        {
            Debug.LogWarning($"[MatchManager] スポーン地点(spawnPoints)が{(spawnPoints == null ? 0 : spawnPoints.Length)}個しかなく、" +
                $"プレイヤー数({players.Count}人)に足りていません。シーン上のspawnPoints配列に地点を追加してください。");
        }

        List<Transform> points = GetShuffledSpawnPoints(players.Count);

        for (int i = 0; i < players.Count; i++)
        {
            if (points.Count > 0)
            {
                Transform point = points[i % points.Count];
                players[i].ServerResetForMatchStart(point.position, point.rotation);
            }
            else
            {
                players[i].ServerResetForMatchStart(Vector3.zero, Quaternion.identity);
            }

            players[i].KillCount.Value = 0;
            players[i].TotalDamageDealt.Value = 0f;
        }

        _eliminatedClientIds.Clear();
        AliveCount.Value = players.Count;
        WinnerClientId.Value = ulong.MaxValue;
        TimeRemaining.Value = matchTimeLimitSeconds;

        // すぐPlayingに入らずカウントダウンを挟む(この間は移動のみ可能)
        CountdownRemaining.Value = countdownSeconds;
        Phase = GamePhase.Countdown;

        // Lobbyの検索は未ロックのロビーしか返さないため、試合中はロックして途中参加を防ぐ
        RelayManager.Instance?.SetLobbyLocked(true);

        WeaponSpawner.Instance?.ServerSpawnAll();

        // 前の試合で破壊された建物を全て元に戻す
        ResetCityBuildingsClientRpc();
        VoxelBuildingPart.ServerResetAll();
    }

    /// <summary>プレイヤーが脱落したときにサーバーで呼ぶ。生存者が1人以下になれば勝者を確定する</summary>
    public void NotifyPlayerEliminated(ulong clientId)
    {
        if (!IsServer || Phase != GamePhase.Playing) return;
        if (!_eliminatedClientIds.Add(clientId)) return; // 既に脱落済みなら何もしない(二重減算防止)

        AliveCount.Value = Mathf.Max(0, AliveCount.Value - 1);

        if (AliveCount.Value <= 1)
        {
            DeclareWinnerFromSurvivors();
        }
    }

    /// <summary>誰かが誰かを倒した瞬間にHealth.ServerApplyDamage()から呼ぶ。キル数を加算し、全クライアントへ通知する。
    /// positionは現在未使用</summary>
    public void BroadcastKill(ulong victimClientId, ulong killerClientId, Vector3 position)
    {
        if (!IsServer) return;

        // 自滅はキル数に数えない
        if (killerClientId != victimClientId)
        {
            foreach (var pc in PlayerController.ActiveInstances)
            {
                if (pc != null && pc.OwnerClientId == killerClientId)
                {
                    pc.KillCount.Value++;
                    break;
                }
            }
        }

        BroadcastKillClientRpc(victimClientId, killerClientId, position);
    }

    /// <summary>全クライアントに配られるキル通知(被害者ID, 撃破者ID)。観戦対象の選択に使う</summary>
    public static event System.Action<ulong, ulong> KillBroadcast;

    [ClientRpc]
    private void BroadcastKillClientRpc(ulong victimClientId, ulong killerClientId, Vector3 position)
    {
        KillFeedUI.Instance?.AddEntry(victimClientId, killerClientId);
        KillBroadcast?.Invoke(victimClientId, killerClientId);
    }

    /// <summary>建物の被ダメージ(座標・法線・半径)を全クライアントへ配る。建物は個別のNetworkObjectを持たないため、このオブジェクトのRPCに相乗りする</summary>
    public void ServerShowBuildingDamage(string buildingId, Vector3 worldHitPoint, Vector3 worldNormal, Vector3 damageCenter, float explosionRadius, float power)
    {
        if (!IsServer) return;
        ShowBuildingDamageClientRpc(buildingId, worldHitPoint, worldNormal, damageCenter, explosionRadius, power);
    }

    [ClientRpc]
    private void ShowBuildingDamageClientRpc(string buildingId, Vector3 worldHitPoint, Vector3 worldNormal, Vector3 damageCenter, float explosionRadius, float power)
    {
        DestructibleBuildingPart.ApplyDamageEffectById(buildingId, worldHitPoint, worldNormal, damageCenter, explosionRadius, power);
    }

    /// <summary>次の試合開始時に、破壊された街の建物を全クライアントで元に戻す</summary>
    [ClientRpc]
    private void ResetCityBuildingsClientRpc()
    {
        DestructibleBuildingPart.ResetAll();
    }

    /// <summary>試合中に切断したクライアントを脱落として扱う</summary>
    private void HandleClientDisconnected(ulong clientId)
    {
        if (!IsServer || Phase != GamePhase.Playing) return;
        if (!_eliminatedClientIds.Add(clientId)) return; // 既に脱落済みなら二重で減らさない

        AliveCount.Value = Mathf.Max(0, AliveCount.Value - 1);

        if (AliveCount.Value <= 1)
        {
            DeclareWinnerFromSurvivors();
        }
    }

    private void DeclareWinnerFromSurvivors()
    {
        foreach (var pc in PlayerController.ActiveInstances)
        {
            if (pc != null && !_eliminatedClientIds.Contains(pc.OwnerClientId))
            {
                WinnerClientId.Value = pc.OwnerClientId;
                break;
            }
        }

        EnterRoundEnd();
    }

    /// <summary>タイムアップ時は、生存者の中でキル数が最も多いプレイヤーを勝者にする(該当なしなら引き分け)</summary>
    private void EndMatchByTimeout()
    {
        ulong bestClientId = ulong.MaxValue;
        int bestKillCount = -1;

        foreach (var pc in PlayerController.ActiveInstances)
        {
            if (pc == null || _eliminatedClientIds.Contains(pc.OwnerClientId)) continue;
            if (pc.KillCount.Value <= bestKillCount) continue;

            bestKillCount = pc.KillCount.Value;
            bestClientId = pc.OwnerClientId;
        }

        WinnerClientId.Value = bestClientId;
        EnterRoundEnd();
    }

    /// <summary>勝者確定後にRoundEndフェーズへ入る(Update()がタイマーを進めてGameOverへ切り替える)</summary>
    private void EnterRoundEnd()
    {
        _roundEndTimer = roundEndAnnounceSeconds;
        Phase = GamePhase.RoundEnd;
        PlayRoundEndJingleClientRpc();
    }

    /// <summary>カウントダウン中のビープ音(isStart=false)と、0になった瞬間の音(isStart=true)を全クライアントで鳴らす</summary>
    [ClientRpc]
    private void PlayCountdownSoundClientRpc(bool isStart)
    {
        PlayGlobalSfx(isStart ? countdownStartClip : countdownBeepClip, isStart ? countdownStartVolume : countdownBeepVolume);
    }

    /// <summary>勝者確定の瞬間に全クライアントでジングルを鳴らす</summary>
    [ClientRpc]
    private void PlayRoundEndJingleClientRpc()
    {
        PlayGlobalSfx(roundEndJingleClip, roundEndJingleVolume);
    }

    /// <summary>試合全体の通知音を2Dで鳴らす(positionはログ用。ローカルプレイヤーが居なければ自身の位置)</summary>
    private void PlayGlobalSfx(AudioClip clip, float volume)
    {
        if (clip == null) return;
        Vector3 position = PlayerController.LocalPlayer != null ? PlayerController.LocalPlayer.transform.position : transform.position;
        float scale = SoundManager.Instance != null ? SoundManager.Instance.SfxVolumeScale : 1f;
        SfxPlayer.Play2D(clip, volume * scale); // 試合全体の通知音なので2Dで(positionはログ用に残す)
        Debug.Log($"[MatchManager] 全体効果音を再生 clip={clip.name} volume={volume * scale:0.00} position={position}");
    }

    /// <summary>ホストがGameOver後にロビーへ戻すときに呼ぶ。建物の修復と全員の復活・再配置も行う</summary>
    public void ReturnToLobby()
    {
        if (!IsServer) return;
        Phase = GamePhase.Lobby;
        WinnerClientId.Value = ulong.MaxValue;
        CountdownRemaining.Value = 0f;
        RelayManager.Instance?.SetLobbyLocked(false); // 再戦募集を再開するのでロックを解除する

        // ロビー中も街が壊れたままにならないよう、ここでも建物を修復する
        VoxelBuildingPart.ServerResetAll();

        // 死亡したままロビーで固まらないよう、StartMatch()と同じ経路で全員を復活させてスポーン地点へ戻す
        List<PlayerController> playersToRevive = new List<PlayerController>(PlayerController.ActiveInstances);
        List<Transform> revivePoints = GetShuffledSpawnPoints(playersToRevive.Count);
        for (int i = 0; i < playersToRevive.Count; i++)
        {
            PlayerController pc = playersToRevive[i];
            if (pc == null) continue;

            if (revivePoints.Count > 0)
            {
                Transform point = revivePoints[i % revivePoints.Count];
                pc.ServerResetForMatchStart(point.position, point.rotation);
            }
            else
            {
                pc.ServerResetForMatchStart(Vector3.zero, Quaternion.identity);
            }
        }
    }

    /// <summary>
    /// RelayManager.HostGame()がStartHost()の直前に呼ぶ。
    /// シーン配置のため前回セッションのフェーズが残留するので、StartHost()前にLobbyへ戻して接続承認の拒否を防ぐ。
    /// </summary>
    public void ResetForNewHostSession()
    {
        try
        {
            _phaseRaw.Value = (int)GamePhase.Lobby;
            WinnerClientId.Value = ulong.MaxValue;
            AliveCount.Value = 0;
            TimeRemaining.Value = 0f;
            CountdownRemaining.Value = 0f;
            _roundEndTimer = 0f;
            _eliminatedClientIds.Clear();
        }
        catch (Exception e)
        {
            // 前セッションの後片付け中だと書き込みに失敗することがあるが、直後のOnNetworkSpawn()が再度Lobbyへ戻すので致命的ではない
            Debug.LogWarning($"[MatchManager] ResetForNewHostSession()で例外が発生しましたが無視して続行します: {e.Message}");
        }
    }

    /// <summary>スポーン地点をシャッフルして返す。建物に埋まっている点は後ろに回す</summary>
    private List<Transform> GetShuffledSpawnPoints(int count)
    {
        var list = new List<Transform>();
        if (spawnPoints == null || spawnPoints.Length == 0)
        {
            return list;
        }

        var indices = new List<int>();
        for (int i = 0; i < spawnPoints.Length; i++) indices.Add(i);

        for (int i = indices.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (indices[i], indices[j]) = (indices[j], indices[i]);
        }

        // 埋まっていない点を先に、埋まっている点を後ろに並べる(有効な点が足りる限り埋まった点は使われない)
        var clear = new List<Transform>();
        var embedded = new List<Transform>();
        foreach (var idx in indices)
        {
            Transform point = spawnPoints[idx];
            if (point == null) continue;

            if (IsSpawnPointEmbeddedInSolid(point.position))
            {
                Debug.LogWarning($"[MatchManager] スポーン地点\"{point.name}\"({point.position})が建物等の中に" +
                    "埋まっている可能性があります。シーン上の座標を見直してください(今回は優先度を下げて使用します)");
                embedded.Add(point);
            }
            else
            {
                clear.Add(point);
            }
        }

        list.AddRange(clear);
        list.AddRange(embedded);
        return list;
    }

    private bool IsSpawnPointEmbeddedInSolid(Vector3 position)
    {
        Vector3 checkPoint = position + Vector3.up * spawnPointOverlapCheckHeight;
        return Physics.CheckSphere(checkPoint, spawnPointOverlapCheckRadius, ~0, QueryTriggerInteraction.Ignore);
    }
}
