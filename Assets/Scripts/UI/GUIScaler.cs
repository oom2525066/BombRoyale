using UnityEngine;

/// <summary>
/// OnGUI(IMGUI)ベースのHUDを、画面サイズに関わらず一貫した見た目にする共通スケーラー。
/// 基準解像度(ReferenceWidth×ReferenceHeight)に対する幅/高さの比率の小さい方で拡縮する。
///
/// 使い方: 各OnGUI()の先頭でBegin()、末尾(早期returnの経路も含む)でEnd()を呼ぶ。
/// その間はScreen.width/heightの代わりにVirtualWidth/VirtualHeightを使って位置計算すること。
/// </summary>
public static class GUIScaler
{
    /// <summary>UIをデザインした基準の画面幅/高さ</summary>
    public const float ReferenceWidth = 1280f;
    public const float ReferenceHeight = 720f;

    /// <summary>極端に小さい/大きいウィンドウでの安全弁(スケールのクランプ範囲)</summary>
    public const float MinScale = 0.35f;
    public const float MaxScale = 3f;

    private static Matrix4x4 _previousMatrix;
    private static bool _active;
    private static bool _loggedOnce;

    /// <summary>現在のスケール倍率(実際の1pxが何pxとして描画されるか)</summary>
    public static float Scale { get; private set; } = 1f;

    /// <summary>スケール後の座標系での画面幅。OnGUI内でScreen.widthの代わりに使う</summary>
    public static float VirtualWidth { get; private set; } = ReferenceWidth;

    /// <summary>スケール後の座標系での画面高さ。OnGUI内でScreen.heightの代わりに使う</summary>
    public static float VirtualHeight { get; private set; } = ReferenceHeight;

    /// <summary>Screen.safeAreaを仮想座標系(スケール後・左上原点)へ変換した安全マージン。
    /// ノッチ等の無い環境では全て0</summary>
    public static float SafeAreaLeft { get; private set; }
    public static float SafeAreaRight { get; private set; }
    public static float SafeAreaTop { get; private set; }
    public static float SafeAreaBottom { get; private set; }

    /// <summary>スケール用のGUI.matrixを設定し、仮想サイズと安全マージンを更新する</summary>
    public static void Begin()
    {
        _previousMatrix = GUI.matrix;
        _active = true;

        float widthRatio = Screen.width / ReferenceWidth;
        float heightRatio = Screen.height / ReferenceHeight;
        Scale = Mathf.Clamp(Mathf.Min(widthRatio, heightRatio), MinScale, MaxScale);

        VirtualWidth = Screen.width / Scale;
        VirtualHeight = Screen.height / Scale;

        // リサイズ直後はsafeAreaだけ古い値を引きずり負の余白になることがあるため、0未満はClampする
        Rect safeArea = Screen.safeArea;
        SafeAreaLeft = Mathf.Max(0f, safeArea.x / Scale);
        SafeAreaRight = Mathf.Max(0f, (Screen.width - safeArea.xMax) / Scale);
        // Screen.safeAreaは左下原点(Yは下から)だが、OnGUIは左上原点のため上下を入れ替える
        SafeAreaTop = Mathf.Max(0f, (Screen.height - safeArea.yMax) / Scale);
        SafeAreaBottom = Mathf.Max(0f, safeArea.y / Scale);

        if (!_loggedOnce)
        {
            _loggedOnce = true;
            Debug.Log($"[GUIScaler] Scale={Scale} Screen=({Screen.width}x{Screen.height}) " +
                $"VirtualSize=({VirtualWidth:F0}x{VirtualHeight:F0}) " +
                $"Reference=({ReferenceWidth}x{ReferenceHeight}) MinScale={MinScale} MaxScale={MaxScale}");
        }

        GUIUtility.ScaleAroundPivot(new Vector2(Scale, Scale), Vector2.zero);
    }

    /// <summary>Begin()と対で必ず呼ぶ(呼び忘れると以降の全OnGUI描画がスケールされたままになる)</summary>
    public static void End()
    {
        if (!_active) return;
        _active = false;
        GUI.matrix = _previousMatrix;
    }
}
