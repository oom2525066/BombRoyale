using UnityEngine;

/// <summary>
/// BGM/SEの音量を一元管理するシングルトン(DontDestroyOnLoad)。設定はPlayerPrefsに保存する。
/// BGMは PlayBgm(clip)、効果音は元の音量に SfxVolumeScale を掛けて再生する。
/// </summary>
public class SoundManager : MonoBehaviour
{
    public static SoundManager Instance { get; private set; }

    private const string MasterVolumeKey = "Sound_MasterVolume";
    private const string BgmVolumeKey = "Sound_BgmVolume";
    private const string SfxVolumeKey = "Sound_SfxVolume";

    // BGM音量の既定値(保存済みの設定が無い時はシーン上の値ではなくこれを使う)
    private const float DefaultBgmVolume = 0.6f;
    // 音量の目盛りを変えた際に古い保存値を一度だけ捨てるためのフラグ
    private const string BgmScaleMigratedKey = "Sound_BgmScaleV3";

    [Tooltip("BGM再生専用のAudioSource(ループ再生)")]
    [SerializeField] private AudioSource bgmSource;

    [Tooltip("UIボタン共通のクリック音")]
    [SerializeField] private AudioClip uiClickSound;
    [Tooltip("UIクリック音の音量")]
    [Range(0f, 1f)] [SerializeField] private float uiClickVolume = 0.85f;

    [Range(0f, 1f)] [SerializeField] private float masterVolume = 1f;
    [Range(0f, 1f)] [SerializeField] private float bgmVolume = DefaultBgmVolume;
    [Range(0f, 1f)] [SerializeField] private float sfxVolume = 1f;

    public float MasterVolume => masterVolume;
    public float BgmVolume => bgmVolume;
    public float SfxVolume => sfxVolume;

    /// <summary>効果音(PlayOneShot)を鳴らす際、元の音量にこれを掛けて再生すること</summary>
    public float SfxVolumeScale => sfxVolume * masterVolume * SfxMixGain;

    // 効果音全体の底上げ係数(1を超えた分はSfxPlayerが重ね鳴らしで増幅する)
    private const float SfxMixGain = 1.3f;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject);

        // シーン側でbgmSourceが未設定でも鳴るよう、無ければ自前で用意する
        if (bgmSource == null)
        {
            bgmSource = gameObject.AddComponent<AudioSource>();
            bgmSource.playOnAwake = false;
            bgmSource.loop = true;
            bgmSource.spatialBlend = 0f;
            Debug.LogWarning("[SoundManager] bgmSourceが未設定だったため、BGM用のAudioSourceを自動で追加しました");
        }

        LoadSettings();
        ApplyBgmVolume();
    }

    private void LoadSettings()
    {
        masterVolume = PlayerPrefs.GetFloat(MasterVolumeKey, masterVolume);
        if (!PlayerPrefs.HasKey(BgmScaleMigratedKey))
        {
            PlayerPrefs.DeleteKey(BgmVolumeKey);
            PlayerPrefs.SetInt(BgmScaleMigratedKey, 1);
            PlayerPrefs.Save();
            Debug.Log("[SoundManager] BGM音量の目盛りを組み替えたため、保存済みのBGM音量を既定(60%)へ戻しました");
        }

        bgmVolume = PlayerPrefs.GetFloat(BgmVolumeKey, DefaultBgmVolume);
        sfxVolume = PlayerPrefs.GetFloat(SfxVolumeKey, sfxVolume);
    }

    public void SetMasterVolume(float value)
    {
        masterVolume = Mathf.Clamp01(value);
        PlayerPrefs.SetFloat(MasterVolumeKey, masterVolume);
        MarkPrefsDirty();
        ApplyBgmVolume();
    }

    public void SetBgmVolume(float value)
    {
        bgmVolume = Mathf.Clamp01(value);
        PlayerPrefs.SetFloat(BgmVolumeKey, bgmVolume);
        MarkPrefsDirty();
        ApplyBgmVolume();
    }

    public void SetSfxVolume(float value)
    {
        sfxVolume = Mathf.Clamp01(value);
        PlayerPrefs.SetFloat(SfxVolumeKey, sfxVolume);
        MarkPrefsDirty();
        // SFXは再生のたびにSfxVolumeScaleを参照するため、鳴っている音への処理は不要
    }

    // BGMだけに掛ける底上げ係数(AudioSource.volumeは1が上限なので、スライダーが高い側は頭打ちになる)
    private const float BgmMixGain = 1.5f;

    private void ApplyBgmVolume()
    {
        if (bgmSource != null)
        {
            bgmSource.volume = bgmVolume * masterVolume * BgmMixGain;
        }
    }

    // スライダーのドラッグ中にSave()を毎回呼ぶとAndroidで引っかかるため、少し待ってまとめて保存する
    private const float PrefsSaveDelaySeconds = 0.5f;
    private bool _prefsDirty;
    private float _lastPrefsChangeTime;

    private void MarkPrefsDirty()
    {
        _prefsDirty = true;
        _lastPrefsChangeTime = Time.unscaledTime;
    }

    private void Update()
    {
        if (_prefsDirty && Time.unscaledTime - _lastPrefsChangeTime >= PrefsSaveDelaySeconds) SavePrefsNow();
    }

    private void OnApplicationPause(bool paused)
    {
        if (paused && _prefsDirty) SavePrefsNow();
    }

    private void OnApplicationQuit()
    {
        if (_prefsDirty) SavePrefsNow();
    }

    private void SavePrefsNow()
    {
        _prefsDirty = false;
        PlayerPrefs.Save();
        Debug.Log($"[SoundManager] 音量設定を保存しました master={masterVolume:0.00} bgm={bgmVolume:0.00} sfx={sfxVolume:0.00}");
    }

    /// <summary>BGMを再生する。既に同じクリップがループ再生中なら何もしない</summary>
    public void PlayBgm(AudioClip clip, bool loop = true)
    {
        if (bgmSource == null || clip == null) return;
        if (bgmSource.isPlaying && bgmSource.clip == clip) return;

        bgmSource.clip = clip;
        bgmSource.loop = loop;
        ApplyBgmVolume();
        bgmSource.Play();
    }

    public void StopBgm()
    {
        if (bgmSource != null) bgmSource.Stop();
    }

    /// <summary>PopUIのボタン全般で共通に鳴らすクリック音(2Dで再生)</summary>
    public void PlayUiClick()
    {
        if (uiClickSound == null)
        {
            Debug.LogWarning("[SoundManager] PlayUiClick: uiClickSoundが未設定のため再生をスキップしました");
            return;
        }
        float volume = uiClickVolume * SfxVolumeScale;
        Debug.Log($"[SoundManager] PlayUiClick: clip={uiClickSound.name} volume={volume:F2} (uiClickVolume={uiClickVolume} SfxVolumeScale={SfxVolumeScale})");
        SfxPlayer.Play2D(uiClickSound, volume);
    }
}
