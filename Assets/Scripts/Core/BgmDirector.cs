using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 試合の状態(MatchManager.Phase)に応じてBGMを切り替える常駐オブジェクト。
/// 未接続(タイトル)=Title、ロビー=無音、カウントダウン以降=InGame。
/// 曲は Resources/BGM/Title・InGame から読み込む。
/// </summary>
public class BgmDirector : MonoBehaviour
{
    private const string TitleClipPath = "BGM/Title";
    private const string InGameClipPath = "BGM/InGame";

    private enum Track { None, Title, InGame }

    private static BgmDirector _instance;

    private AudioClip _titleClip;
    private AudioClip _inGameClip;
    private Track _current = Track.None;
    private bool _warnedNoSoundManager;

    // ドメインリロード無効時にstaticの参照が残らないようにする
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        _instance = null;
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Bootstrap()
    {
        if (_instance != null) return;
        var go = new GameObject("BgmDirector(Auto)");
        DontDestroyOnLoad(go);
        _instance = go.AddComponent<BgmDirector>();
    }

    private void Awake()
    {
        _titleClip = Resources.Load<AudioClip>(TitleClipPath);
        _inGameClip = Resources.Load<AudioClip>(InGameClipPath);

        Debug.Log($"[BgmDirector] 起動しました title={(_titleClip != null ? _titleClip.name : "なし")} " +
            $"inGame={(_inGameClip != null ? _inGameClip.name : "なし")}");
        if (_titleClip == null) Debug.LogWarning($"[BgmDirector] Resources/{TitleClipPath} が見つかりません(タイトルBGMは鳴りません)");
        if (_inGameClip == null) Debug.LogWarning($"[BgmDirector] Resources/{InGameClipPath} が見つかりません(試合BGMは鳴りません)");
    }

    private void OnDestroy()
    {
        if (_instance == this) _instance = null;
    }

    private void Update()
    {
        Track desired = DecideTrack();
        if (desired == _current) return;

        SoundManager soundManager = SoundManager.Instance;
        if (soundManager == null)
        {
            if (!_warnedNoSoundManager)
            {
                Debug.LogWarning("[BgmDirector] SoundManagerがまだ居ないため、BGMの切り替えを待機します");
                _warnedNoSoundManager = true;
            }
            return;
        }

        AudioClip clip = desired == Track.InGame ? _inGameClip
            : desired == Track.Title ? _titleClip
            : null; // Track.None(ロビー)は無音
        Debug.Log($"[BgmDirector] BGMを切り替えます {_current} → {desired} " +
            $"(phase={(MatchManager.Instance != null ? MatchManager.Instance.Phase.ToString() : "-")} clip={(clip != null ? clip.name : "なし")})");
        _current = desired;

        if (clip == null)
        {
            soundManager.StopBgm();
            return;
        }
        soundManager.PlayBgm(clip, true);
    }

    /// <summary>接続状態とフェーズから、今鳴らすべき曲を決める</summary>
    private static Track DecideTrack()
    {
        NetworkManager networkManager = NetworkManager.Singleton;
        MatchManager matchManager = MatchManager.Instance;
        bool connected = networkManager != null && networkManager.IsListening;
        if (!connected || matchManager == null || !matchManager.IsSpawned) return Track.Title;

        switch (matchManager.Phase)
        {
            case MatchManager.GamePhase.Countdown:
            case MatchManager.GamePhase.Playing:
            case MatchManager.GamePhase.RoundEnd:
            case MatchManager.GamePhase.GameOver:
                return Track.InGame;
            default:
                return Track.None; // ロビーでは鳴らさない
        }
    }
}
