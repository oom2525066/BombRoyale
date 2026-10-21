using UnityEngine;

/// <summary>
/// 使い捨て効果音の再生ヘルパー(PlayClipAtPointの代替)。
/// 近距離は減衰させず遠くだけ小さくし、1.0を超える音量は同じ音を重ねて増幅する。UI・通知音は2Dで鳴らす。
/// </summary>
public static class SfxPlayer
{
    /// <summary>爆発音はこの距離までは減衰しない</summary>
    public const float ExplosionMinDistance = 15f;
    /// <summary>通常の効果音はこの距離までは減衰しない</summary>
    public const float DefaultMinDistance = 8f;
    private const float MaxDistance = 150f;
    private const int MaxLayers = 3;

    /// <summary>SoundManagerの効果音音量(設定画面の全体×効果音×底上げ係数)。SoundManagerが無ければ1</summary>
    public static float CurrentSfxScale => SoundManager.Instance != null ? SoundManager.Instance.SfxVolumeScale : 1f;

    /// <summary>ワールド座標で鳴らす3Dの効果音。volumeは1を超えてもよい(重ね鳴らしで増幅する)</summary>
    public static void PlayAt(AudioClip clip, Vector3 position, float volume, float minDistance = DefaultMinDistance)
    {
        Play(clip, position, volume, spatial: true, minDistance);
    }

    /// <summary>位置に関係なく同じ大きさで鳴らす2Dの効果音(UI・通知・自分だけに聞かせる音)</summary>
    public static void Play2D(AudioClip clip, float volume)
    {
        Play(clip, Vector3.zero, volume, spatial: false, DefaultMinDistance);
    }

    private static void Play(AudioClip clip, Vector3 position, float volume, bool spatial, float minDistance)
    {
        if (clip == null || volume <= 0f) return;

        // AudioSource.volumeは1が上限なので、超過分は同位相で重ねて(PlayScheduledで揃えて)増幅する
        int layers = Mathf.Clamp(Mathf.CeilToInt(volume), 1, MaxLayers);
        float perLayerVolume = Mathf.Clamp01(volume / layers);
        double startTime = AudioSettings.dspTime + 0.01;
        // PlayClipAtPointと同じ寿命の決め方(タイムスケールが極端に小さい時も消えるようにする)
        float lifetime = clip.length * (Time.timeScale < 0.01f ? 0.01f : Time.timeScale) + 0.1f;

        for (int i = 0; i < layers; i++)
        {
            var go = new GameObject(spatial ? "OneShotAudio3D" : "OneShotAudio2D");
            go.transform.position = position;
            var source = go.AddComponent<AudioSource>();
            source.clip = clip;
            source.volume = perLayerVolume;
            source.playOnAwake = false;
            source.dopplerLevel = 0f;
            source.spatialBlend = spatial ? 1f : 0f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = minDistance;
            source.maxDistance = MaxDistance;
            if (layers > 1) source.PlayScheduled(startTime);
            else source.Play();
            Object.Destroy(go, lifetime);
        }
    }
}
