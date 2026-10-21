using UnityEngine;

/// <summary>
/// 実メッシュの見た目として実体化したPrefabの後片付け用ユーティリティ。
/// FBXに紛れ込んだCamera/AudioListenerがシーン内のカメラと競合しないよう取り除く。
/// </summary>
public static class VisualPrefabUtility
{
    /// <summary>Camera/AudioListenerを、GameObjectごと破棄する(URPの付随コンポーネントの依存でコンポーネント単体では消せないため)</summary>
    public static void StripStrayVisualComponents(GameObject visualRoot)
    {
        if (visualRoot == null) return;

        foreach (var cam in visualRoot.GetComponentsInChildren<Camera>(true)) Object.Destroy(cam.gameObject);
        foreach (var listener in visualRoot.GetComponentsInChildren<AudioListener>(true)) Object.Destroy(listener.gameObject);
    }
}
