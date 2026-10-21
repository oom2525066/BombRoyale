using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using Unity.Services.Core;
using Unity.Services.Authentication;
using Unity.Services.Relay;
using Unity.Services.Relay.Models;
using Unity.Services.Lobbies;
using Unity.Services.Lobbies.Models;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Networking.Transport.Relay;

/// <summary>
/// UGS(Authentication / Relay / Lobby)をまとめて扱うマネージャー(DontDestroyOnLoad)。
/// 匿名サインイン、ホスト、ロビーコード参加、クイックマッチ(PlayNow)、退出、切断時の後始末を担当する。
/// </summary>
public class RelayManager : MonoBehaviour
{
    public static RelayManager Instance { get; private set; }

    [Tooltip("ロビーの最大人数")]
    [SerializeField] private int maxPlayers = 3;
    [SerializeField] private float lobbyPollIntervalSec = 1.5f;
    [SerializeField] private float lobbyHeartbeatIntervalSec = 15f;

    /// <summary>このロビーが受け付ける最大人数</summary>
    public int MaxPlayers => maxPlayers;

    private const string KeyRelayJoinCode = "relayJoinCode";

    private Lobby _currentLobby;
    private Coroutine _heartbeatCoroutine;
    private Coroutine _pollCoroutine;
    // LeaveGame()による自発的な切断中は、予期しない切断の後始末を重複させないためのフラグ
    private bool _leavingIntentionally;

    // 切断直後に即ホスト/参加すると接続が不安定になるため、実時間でこの秒数は間を空ける
    private const float ReconnectCooldownSeconds = 2f;
    private float _reconnectReadyRealtime = -1f;

    // StartClient()後、実際に接続成立するまで待つ上限秒数(ホストのいない「死んだロビー」の検出用)
    private const float ClientConnectionTimeoutSeconds = 8f;

    /// <summary>UGSの初期化/サインインがこの秒数以内に終わらなければ失敗扱いにして再試行できるようにする</summary>
    private const float InitializeTimeoutSeconds = 15f;

    /// <summary>UGSの初期化が完了し、Host/参加操作を受け付けられる状態か</summary>
    public bool IsReady { get; private set; }

    /// <summary>初期化に失敗した状態か。UIはエラー表示+再試行ボタンへ切り替える</summary>
    public bool HasFailedToInitialize { get; private set; }

    /// <summary>PlayNow()が空きロビーを探している間だけtrue(UIのスピナー表示用)</summary>
    public bool IsMatching { get; private set; }

    /// <summary>UI表示用のステータス通知</summary>
    public event Action<string> OnStatus;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    // アプリ終了時、後片付けの完了を待ってから実際に終了するためのフラグ
    private bool _readyToQuit;

    private async void Start()
    {
        // 自分自身が予期せず切断された時にステータスを更新するための購読
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientDisconnectCallback += HandleUnexpectedDisconnect;
        }

        // 接続承認の設定は起動時に一度だけ行い、ホスト/参加の順序に関わらず常に同じ状態にする
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.NetworkConfig.ConnectionApproval = true;
            NetworkManager.Singleton.ConnectionApprovalCallback = ApproveConnectionIfLobbyPhase;
        }

        Application.wantsToQuit += HandleWantsToQuit;

        await InitializeServicesAsync();
    }

    private void OnDestroy()
    {
        if (NetworkManager.Singleton != null)
        {
            NetworkManager.Singleton.OnClientDisconnectCallback -= HandleUnexpectedDisconnect;
        }
        Application.wantsToQuit -= HandleWantsToQuit;
    }

    /// <summary>
    /// アプリ終了直前に呼ばれる。ロビーの退出が終わる前にプロセスが終わらないよう、
    /// 一旦終了を保留して後片付けの完了を待ってからApplication.Quit()を呼び直す。
    /// </summary>
    private bool HandleWantsToQuit()
    {
        if (_readyToQuit) return true; // 後片付け完了後、Application.Quit()を呼び直した2回目の到達

        bool hasSomethingToCleanUp = _currentLobby != null || (NetworkManager.Singleton != null &&
            (NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsHost || NetworkManager.Singleton.IsServer));

        if (!hasSomethingToCleanUp) return true; // 何も参加していなければ後片付け不要、即座に終了を許可

        StartCoroutine(QuitAfterCleanupCoroutine());
        return false;
    }

    private IEnumerator QuitAfterCleanupCoroutine()
    {
        Task cleanupTask = LeaveGameInternalAsync();
        yield return new WaitUntil(() => cleanupTask.IsCompleted);

        _readyToQuit = true;
        Application.Quit();
    }

    /// <summary>
    /// 自分自身が切断された時に呼ばれる(承認拒否によるキック・ネットワークエラー等)。
    /// 自発的な切断(LeaveGame)なら何もせず、それ以外はLeaveGame()相当の後始末とステータス更新を行う。
    /// </summary>
    private void HandleUnexpectedDisconnect(ulong clientId)
    {
        if (NetworkManager.Singleton == null || clientId != NetworkManager.Singleton.LocalClientId) return;
        if (_leavingIntentionally) return;

        // 古いロビー参照や停止し損ねたコルーチンが次の操作と干渉しないよう後始末する
        if (_heartbeatCoroutine != null) { StopCoroutine(_heartbeatCoroutine); _heartbeatCoroutine = null; }
        if (_pollCoroutine != null) { StopCoroutine(_pollCoroutine); _pollCoroutine = null; }

        // Lobbyサービス側からも退出しないと、古いロビーにメンバーとして残留して409が多発する
        _ = LeaveLobbyServerSideAsync(_currentLobby);
        _currentLobby = null;

        // この切断コールバックはNGOの切断処理の延長で呼ばれるため、Shutdown()は1フレーム遅らせて再入を避ける
        StartCoroutine(ShutdownNextFrameCoroutine());

        Report(Localization.Get("relay.disconnectedUnexpectedly"));
    }

    private IEnumerator ShutdownNextFrameCoroutine()
    {
        yield return null;

        if (NetworkManager.Singleton != null &&
            (NetworkManager.Singleton.IsHost || NetworkManager.Singleton.IsServer || NetworkManager.Singleton.IsClient))
        {
            NetworkManager.Singleton.Shutdown();
            _reconnectReadyRealtime = Time.realtimeSinceStartup + ReconnectCooldownSeconds;
        }
    }

    /// <summary>Lobbyサービス側だけを退出する(失敗しても後続を止めないため警告のみ)</summary>
    private static async Task LeaveLobbyServerSideAsync(Lobby lobby)
    {
        if (lobby == null) return;

        try
        {
            string playerId = AuthenticationService.Instance.PlayerId;
            if (lobby.HostId == playerId)
            {
                await LobbyService.Instance.DeleteLobbyAsync(lobby.Id);
            }
            else
            {
                await LobbyService.Instance.RemovePlayerAsync(lobby.Id, playerId);
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[RelayManager] 予期しない切断後のロビー退出に失敗: {e}");
        }
    }

    /// <summary>初期化に失敗した後、UIの「再試行」ボタンから呼ぶ</summary>
    public async void RetryInitialize()
    {
        if (IsReady) return;
        HasFailedToInitialize = false;
        await InitializeServicesAsync();
    }

    /// <summary>UGSのコアサービスを初期化し、匿名サインインまで済ませる。タイムアウトや失敗時は再試行できる状態にする</summary>
    private async Task InitializeServicesAsync()
    {
        try
        {
            Task initTask = InitializeServicesInnerAsync();
            Task timeoutTask = Task.Delay(TimeSpan.FromSeconds(InitializeTimeoutSeconds));
            Task completed = await Task.WhenAny(initTask, timeoutTask);
            if (completed == timeoutTask)
            {
                throw new TimeoutException(
                    $"UGSの初期化/サインインが{InitializeTimeoutSeconds}秒以内に終わりませんでした(ネットワーク不調の可能性)");
            }
            await initTask; // 例外が出ていればここで再スローされる

            IsReady = true;
            HasFailedToInitialize = false;
            Report(Localization.Get("relay.ready"));
        }
        catch (Exception e)
        {
            Debug.LogError($"[RelayManager] 初期化失敗: {e} " +
                $"internetReachability={Application.internetReachability}");
            HasFailedToInitialize = true;
            // 回線自体が無い場合は、汎用の失敗文言より具体的に「接続が切れました」と案内する
            string message = Application.internetReachability == NetworkReachability.NotReachable
                ? Localization.Get("relay.offline")
                : Localization.Get("relay.initFailed");
            Report(message);
        }
    }

    private static async Task InitializeServicesInnerAsync()
    {
        if (UnityServices.State != ServicesInitializationState.Initialized)
        {
            await UnityServices.InitializeAsync();
        }

        if (!AuthenticationService.Instance.IsSignedIn)
        {
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
        }
    }

    // ============================================================
    // ホスト
    // ============================================================
    /// <summary>Relay割り当てとロビーを作成し、ホストとして開始する</summary>
    public async void HostGame(string lobbyName = "MyLobby")
    {
        if (!AuthenticationService.Instance.IsSignedIn)
        {
            Debug.LogWarning("[RelayManager] 未サインイン状態でHostGame()が呼ばれました");
            Report(Localization.Get("relay.hostFailed"));
            return;
        }

        await WaitForReconnectCooldownAsync();

        try
        {
            Report(Localization.Get("relay.creatingLobby"));

            // maxPlayers - 1 : ホスト自身の分は接続数に含めない
            Allocation allocation =
                await RelayService.Instance.CreateAllocationAsync(Mathf.Max(1, maxPlayers - 1));

            string relayJoinCode =
                await RelayService.Instance.GetJoinCodeAsync(allocation.AllocationId);

            var lobbyOptions = new CreateLobbyOptions
            {
                IsPrivate = false,
                Data = new Dictionary<string, DataObject>
                {
                    { KeyRelayJoinCode, new DataObject(DataObject.VisibilityOptions.Member, relayJoinCode) }
                }
            };

            _currentLobby = await LobbyService.Instance.CreateLobbyAsync(lobbyName, maxPlayers, lobbyOptions);

            var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
            transport.SetRelayServerData(new RelayServerData(allocation, "dtls"));

            // 直前のShutdown()の後片付けが終わってから、フェーズをLobbyへ戻してStartHost()する
            await WaitForNetworkManagerShutdownAsync();

            // 前回セッションの残留フェーズでホスト自身が承認拒否されないよう、StartHost()の前にリセットする
            MatchManager.Instance?.ResetForNewHostSession();

            NetworkManager.Singleton.StartHost();

            _heartbeatCoroutine = StartCoroutine(HeartbeatLobbyCoroutine(_currentLobby.Id, lobbyHeartbeatIntervalSec));

            Report(Localization.Format("relay.hostStarted", _currentLobby.LobbyCode));
        }
        catch (Exception e)
        {
            Debug.LogError($"[RelayManager] ホスト失敗: {e}");
            Report(Localization.Get("relay.hostFailed"));
        }
    }

    // ============================================================
    // ロビーコードを指定して参加
    // ============================================================
    /// <summary>ロビーコードを指定して参加する</summary>
    public async void JoinGameByCode(string lobbyCode)
    {
        if (string.IsNullOrWhiteSpace(lobbyCode))
        {
            Report(Localization.Get("relay.enterCode"));
            return;
        }

        if (!AuthenticationService.Instance.IsSignedIn)
        {
            Debug.LogWarning("[RelayManager] 未サインイン状態でJoinGameByCode()が呼ばれました");
            Report(Localization.Get("relay.joinFailed"));
            return;
        }

        await WaitForReconnectCooldownAsync();

        try
        {
            Report(Localization.Get("relay.joining"));

            _currentLobby = await JoinLobbyByCodeWithConflictRecoveryAsync(lobbyCode);

            await JoinRelayFromCurrentLobbyAsync();

            // StartClient()が例外を投げなくても接続成立とは限らないため、実際の接続を確認する
            if (!await WaitForClientConnectionAsync(ClientConnectionTimeoutSeconds))
            {
                await AbandonFailedJoinAsync();
                throw new TimeoutException("接続がタイムアウトしました(ホストが応答していません)");
            }

            if (_pollCoroutine != null) StopCoroutine(_pollCoroutine);
            _pollCoroutine = StartCoroutine(PollLobbyCoroutine());

            Report(Localization.Get("relay.joinSuccess"));
        }
        catch (Exception e)
        {
            Debug.LogError($"[RelayManager] 参加失敗: {e}");
            Report(Localization.Get("relay.joinFailed"));
        }
    }

    /// <summary>
    /// ロビーコードで参加する。レスポンス欠落で409(already a member)になった場合は、
    /// SDK側の自動復旧が効かないため、参加済みロビーを探して参加成功として扱う自前の復旧を行う。
    /// </summary>
    private async Task<Lobby> JoinLobbyByCodeWithConflictRecoveryAsync(string lobbyCode)
    {
        try
        {
            return await LobbyService.Instance.JoinLobbyByCodeAsync(lobbyCode);
        }
        catch (LobbyServiceException e) when (IsAlreadyMemberConflict(e))
        {
            Debug.LogWarning("[RelayManager] 参加時に409(already a member)を検出。自前でロビー復旧を試みます");
            Lobby recovered = await TryRecoverAlreadyJoinedLobbyAsync();
            if (recovered != null) return recovered;
            throw;
        }
    }

    private static bool IsAlreadyMemberConflict(LobbyServiceException e)
    {
        return e.Reason == LobbyExceptionReason.LobbyConflict ||
            (e.Message != null && e.Message.IndexOf("already a member", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    /// <summary>既にメンバーになっているロビーが1件だけあればそれを返す(複数件や失敗時はnull=復旧不可)</summary>
    private async Task<Lobby> TryRecoverAlreadyJoinedLobbyAsync()
    {
        try
        {
            List<string> joinedLobbyIds = await LobbyService.Instance.GetJoinedLobbiesAsync();
            if (joinedLobbyIds.Count != 1)
            {
                Debug.LogWarning($"[RelayManager] ロビー復旧を断念(参加済みロビー数={joinedLobbyIds.Count}、1件の時のみ復旧できます)");
                return null;
            }

            return await LobbyService.Instance.GetLobbyAsync(joinedLobbyIds[0]);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[RelayManager] ロビー復旧処理自体に失敗: {e}");
            return null;
        }
    }

    // ============================================================
    // 空いているロビーに自動参加
    // ============================================================
    /// <summary>空いているロビーを探して自動参加する(無ければ通知のみ)</summary>
    public async void QuickJoin()
    {
        try
        {
            Report(Localization.Get("relay.searching"));

            bool joined = await TryJoinOpenLobbyAsync();
            if (!joined)
            {
                Report(Localization.Get("relay.noOpenLobby"));
                return;
            }

            Report(Localization.Get("relay.joinSuccess"));
        }
        catch (Exception e)
        {
            Debug.LogError($"[RelayManager] クイックジョイン失敗: {e}");
            Report(Localization.Get("relay.quickJoinFailed"));
        }
    }

    // 1回目の探索で見つからなかった時、ホストになる前にもう一度探し直すまでの待ち時間
    private const float PlayNowSecondAttemptDelaySeconds = 4f;

    // ============================================================
    // 「START」ボタン用のマッチング
    // ============================================================
    /// <summary>空いているロビーがあれば参加し、無ければ自分がホストになるワンボタン導線</summary>
    public async void PlayNow()
    {
        Report(Localization.Get("relay.searching"));
        IsMatching = true;

        bool joined = false;
        try
        {
            joined = await TryJoinOpenLobbyAsync();
            if (!joined)
            {
                // 即ホストにはせず、少し待ってもう一度だけ探す(直前にホストを始めた人の取りこぼし防止)
                await Task.Delay(TimeSpan.FromSeconds(PlayNowSecondAttemptDelaySeconds));
                joined = await TryJoinOpenLobbyAsync();
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[RelayManager] 参加試行に失敗(ホストにフォールバックします): {e}");
        }

        IsMatching = false;

        if (joined)
        {
            Report(Localization.Get("relay.joinSuccess"));
            return;
        }

        HostGame();
    }

    /// <summary>空いているロビーを探し、あれば参加してtrueを返す。無ければfalse(呼び出し元でtry-catchすること)</summary>
    private async Task<bool> TryJoinOpenLobbyAsync()
    {
        if (!AuthenticationService.Instance.IsSignedIn)
        {
            Debug.LogWarning("[RelayManager] 未サインイン状態でTryJoinOpenLobbyAsync()が呼ばれました");
            return false;
        }

        await WaitForReconnectCooldownAsync();

        QueryResponse response = await LobbyService.Instance.QueryLobbiesAsync(new QueryLobbiesOptions
        {
            Count = 25,
            Filters = new List<QueryFilter>
            {
                new QueryFilter(QueryFilter.FieldOptions.AvailableSlots, "0", QueryFilter.OpOptions.GT)
            }
        });

        if (response.Results.Count == 0) return false;

        _currentLobby = await LobbyService.Instance.JoinLobbyByIdAsync(response.Results[0].Id);

        await JoinRelayFromCurrentLobbyAsync();

        // ホストのいない「死んだロビー」だと静かに失敗するため、実際の接続成立を確認する
        if (!await WaitForClientConnectionAsync(ClientConnectionTimeoutSeconds))
        {
            await AbandonFailedJoinAsync();
            return false;
        }

        if (_pollCoroutine != null) StopCoroutine(_pollCoroutine);
        _pollCoroutine = StartCoroutine(PollLobbyCoroutine());
        return true;
    }

    /// <summary>IsConnectedClientになるまでポーリングし、timeoutSeconds以内に繋がらなければfalse(死んだロビーの可能性)</summary>
    private static async Task<bool> WaitForClientConnectionAsync(float timeoutSeconds)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return false;

        float startTime = Time.realtimeSinceStartup;
        while (Time.realtimeSinceStartup - startTime < timeoutSeconds)
        {
            if (nm.IsConnectedClient) return true;
            if (!nm.IsClient) return false; // 何らかの理由で既にクライアントで無くなっている(切断済み等)
            await Task.Yield();
        }
        return false;
    }

    /// <summary>接続できなかった参加の後始末(NetworkManager停止とロビーからの離脱)</summary>
    private async Task AbandonFailedJoinAsync()
    {
        if (NetworkManager.Singleton != null &&
            (NetworkManager.Singleton.IsClient || NetworkManager.Singleton.IsHost || NetworkManager.Singleton.IsServer))
        {
            NetworkManager.Singleton.Shutdown();
        }
        _reconnectReadyRealtime = Time.realtimeSinceStartup + ReconnectCooldownSeconds;

        if (_currentLobby != null)
        {
            try
            {
                await LobbyService.Instance.RemovePlayerAsync(_currentLobby.Id, AuthenticationService.Instance.PlayerId);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[RelayManager] 失敗した参加先ロビーからの離脱に失敗: {e}");
            }
        }
        _currentLobby = null;
    }

    /// <summary>
    /// ホスト側でのみ呼ばれる接続承認。試合がPlaying中なら新規接続を拒否する。
    /// ホスト自身の接続は、前回の残留フェーズでプレイヤーオブジェクトが作られない事態を避けるため無条件で承認する。
    /// </summary>
    private void ApproveConnectionIfLobbyPhase(NetworkManager.ConnectionApprovalRequest request, NetworkManager.ConnectionApprovalResponse response)
    {
        bool isHostSelf = NetworkManager.Singleton != null && request.ClientNetworkId == NetworkManager.ServerClientId;
        bool approve = isHostSelf || MatchManager.Instance == null || MatchManager.Instance.Phase != MatchManager.GamePhase.Playing;
        response.Approved = approve;
        response.CreatePlayerObject = approve;
        response.Pending = false;
    }

    /// <summary>
    /// MatchManagerから試合フェーズの切り替わりごとに呼ばれる(ホストのみ実効)。
    /// 試合中はロビーをロックして、QuickJoin/PlayNowが進行中の試合に参加して弾かれるのを防ぐ。
    /// </summary>
    public async void SetLobbyLocked(bool locked)
    {
        if (_currentLobby == null) return;
        string lobbyId = _currentLobby.Id;

        try
        {
            Lobby updated = await LobbyService.Instance.UpdateLobbyAsync(lobbyId, new UpdateLobbyOptions { IsLocked = locked });

            // await中にLeaveGame()等でロビーが変わっていることがあるため、同じロビーの時だけ反映する
            if (_currentLobby != null && _currentLobby.Id == lobbyId)
            {
                _currentLobby = updated;
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[RelayManager] ロビーのロック状態変更に失敗: {e}");
        }
    }

    private async Task JoinRelayFromCurrentLobbyAsync()
    {
        string relayJoinCode = _currentLobby.Data[KeyRelayJoinCode].Value;

        JoinAllocation allocation = await RelayService.Instance.JoinAllocationAsync(relayJoinCode);

        var transport = NetworkManager.Singleton.GetComponent<UnityTransport>();
        transport.SetRelayServerData(new RelayServerData(allocation, "dtls"));

        // 直前のShutdown()の後片付けと競合しないよう、StartClient()の前にも完了を待つ
        await WaitForNetworkManagerShutdownAsync();

        NetworkManager.Singleton.StartClient();
    }

    /// <summary>NetworkManager.Shutdown()の後片付けが終わるまで待つ(最大3秒でタイムアウトして続行)</summary>
    private static async Task WaitForNetworkManagerShutdownAsync()
    {
        if (NetworkManager.Singleton == null) return;

        float startTime = Time.realtimeSinceStartup;
        while (NetworkManager.Singleton.ShutdownInProgress)
        {
            if (Time.realtimeSinceStartup - startTime > 3f)
            {
                Debug.LogWarning("[RelayManager] NetworkManagerのShutdown完了待ちがタイムアウトしました。処理を続行します");
                break;
            }
            await Task.Yield();
        }
    }

    /// <summary>直前の切断からReconnectCooldownSeconds秒経っていなければ、残り時間分だけ待つ</summary>
    private async Task WaitForReconnectCooldownAsync()
    {
        if (_reconnectReadyRealtime < 0f) return;

        float remaining = _reconnectReadyRealtime - Time.realtimeSinceStartup;
        if (remaining <= 0f) return;

        Report(Localization.Get("relay.reconnectCooldown"));
        await Task.Delay(TimeSpan.FromSeconds(remaining));
    }

    private IEnumerator HeartbeatLobbyCoroutine(string lobbyId, float waitSeconds)
    {
        var delay = new WaitForSecondsRealtime(waitSeconds);
        while (true)
        {
            LobbyService.Instance.SendHeartbeatPingAsync(lobbyId);
            yield return delay;
        }
    }

    private IEnumerator PollLobbyCoroutine()
    {
        var delay = new WaitForSecondsRealtime(lobbyPollIntervalSec);
        while (_currentLobby != null)
        {
            yield return delay;

            // 待機中にLeaveGame()等でnullになっていることがあるため再チェックする
            if (_currentLobby == null) yield break;

            Task<Lobby> task = LobbyService.Instance.GetLobbyAsync(_currentLobby.Id);
            yield return new WaitUntil(() => task.IsCompleted);

            if (task.IsCompletedSuccessfully)
            {
                _currentLobby = task.Result;
            }
        }
    }

    /// <summary>ロビーを退出(ホストならロビー削除)し、ネットワークを切断する</summary>
    public async void LeaveGame()
    {
        await LeaveGameInternalAsync();
    }

    /// <summary>LeaveGame()の実処理。アプリ終了時に完了を待てるようTaskで返す</summary>
    private async Task LeaveGameInternalAsync()
    {
        _leavingIntentionally = true;
        try
        {
            if (_heartbeatCoroutine != null) StopCoroutine(_heartbeatCoroutine);
            if (_pollCoroutine != null) StopCoroutine(_pollCoroutine);

            if (_currentLobby != null)
            {
                string playerId = AuthenticationService.Instance.PlayerId;

                if (_currentLobby.HostId == playerId)
                {
                    await LobbyService.Instance.DeleteLobbyAsync(_currentLobby.Id);
                }
                else
                {
                    await LobbyService.Instance.RemovePlayerAsync(_currentLobby.Id, playerId);
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[RelayManager] 退出処理でエラー: {e}");
        }
        finally
        {
            _currentLobby = null;

            if (NetworkManager.Singleton != null &&
                (NetworkManager.Singleton.IsHost || NetworkManager.Singleton.IsServer || NetworkManager.Singleton.IsClient))
            {
                NetworkManager.Singleton.Shutdown();
                _reconnectReadyRealtime = Time.realtimeSinceStartup + ReconnectCooldownSeconds;
            }

            Report(Localization.Get("relay.disconnected"));
            _leavingIntentionally = false;
        }
    }

    private void Report(string msg)
    {
        Debug.Log($"[RelayManager] {msg}");
        OnStatus?.Invoke(msg);
    }

    private void OnApplicationQuit()
    {
        LeaveGame();
    }
}
