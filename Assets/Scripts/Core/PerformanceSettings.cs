using UnityEngine;

/// <summary>
/// 起動時に一度だけフレームレート上限(60fps)を敷く。Androidで無制限に回って発熱・スロットリングするのを防ぐ。
/// </summary>
public static class PerformanceSettings
{
    private const int TargetFrameRate = 60;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Bootstrap()
    {
        QualitySettings.vSyncCount = 0; // vSyncCountが0でないとtargetFrameRateが無視される
        Application.targetFrameRate = TargetFrameRate;
    }
}
