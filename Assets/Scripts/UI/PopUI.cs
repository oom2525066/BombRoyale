using UnityEngine;

/// <summary>
/// OnGUIベースのHUD共通のポップな見た目(角丸パネル、アクセントカラー、ボタン/ラベルのGUIStyle)を
/// 提供するユーティリティ。角丸テクスチャは実行時にピクセル単位で生成する。
/// </summary>
public static class PopUI
{
    /// <summary>パーティーゲームらしい明るいアクセントカラー群。用途に応じて呼び出し側が選ぶ</summary>
    public static readonly Color Yellow = new Color(1f, 0.82f, 0.15f);
    public static readonly Color Orange = new Color(1f, 0.55f, 0.12f);
    public static readonly Color Pink = new Color(0.98f, 0.35f, 0.62f);
    public static readonly Color Cyan = new Color(0.25f, 0.85f, 0.95f);
    public static readonly Color Purple = new Color(0.65f, 0.35f, 0.95f);
    public static readonly Color Green = new Color(0.35f, 0.85f, 0.4f);

    private static readonly Color[] AccentPalette = { Yellow, Orange, Pink, Cyan, Purple, Green };

    /// <summary>GUILayout.Buttonの置き換え。クリック時にUIクリック音が鳴る</summary>
    public static bool Button(string text, GUIStyle style, params GUILayoutOption[] options)
    {
        bool clicked = GUILayout.Button(text, style, options);
        if (clicked) PlayClickSound();
        return clicked;
    }

    /// <summary>GUILayout.Toggleの置き換え。値が変化した瞬間にだけクリック音が鳴る</summary>
    public static bool Toggle(bool value, string text, GUIStyle style, params GUILayoutOption[] options)
    {
        bool newValue = GUILayout.Toggle(value, text, style, options);
        if (newValue != value) PlayClickSound();
        return newValue;
    }

    /// <summary>GUI.Button(Rect版)の置き換え。クリック時にUIクリック音が鳴る(string/GUIContent両対応)</summary>
    public static bool RectButton(Rect rect, string text, GUIStyle style)
    {
        bool clicked = GUI.Button(rect, text, style);
        if (clicked) PlayClickSound();
        return clicked;
    }

    public static bool RectButton(Rect rect, GUIContent content, GUIStyle style)
    {
        bool clicked = GUI.Button(rect, content, style);
        if (clicked) PlayClickSound();
        return clicked;
    }

    private static void PlayClickSound()
    {
        SoundManager.Instance?.PlayUiClick();
    }

    /// <summary>インデックスからアクセントカラーを決定論的に選ぶ(同じ入力なら同じ色)</summary>
    public static Color AccentFor(int index)
    {
        int i = ((index % AccentPalette.Length) + AccentPalette.Length) % AccentPalette.Length;
        return AccentPalette[i];
    }

    private static Texture2D _panelTexture;
    private static Texture2D _solidTexture;
    private static Font _sharedFont;
    private static bool _skinFontApplied;

    /// <summary>日本語表示用の同梱フォント(Noto Sans CJK JP)。Resources/Fontsから読み込む
    /// (OnGUI既定フォントは日本語グリフを持たず、実機で文字化けするため)</summary>
    public static Font SharedFont
    {
        get
        {
            if (_sharedFont == null)
            {
                _sharedFont = Resources.Load<Font>("Fonts/NotoSansCJKjp-Regular");
                if (_sharedFont == null)
                {
                    Debug.LogWarning("[PopUI] Assets/Resources/Fonts/NotoSansCJKjp-Regular.otf が" +
                        "見つかりませんでした。既定フォント(Arial)のままになるため、実機では日本語が" +
                        "文字化けする可能性があります。");
                }
            }
            return _sharedFont;
        }
    }

    /// <summary>各OnGUI()の先頭で呼ぶ(適用は初回のみ)。GUI.skinの各要素は個別にフォントを持つため、
    /// 使用する全要素へ同梱フォントを設定する</summary>
    public static void EnsureSkinFontApplied()
    {
        if (_skinFontApplied) return;
        _skinFontApplied = true;

        Font font = SharedFont;
        if (font == null) return;

        GUI.skin.font = font;
        GUI.skin.label.font = font;
        GUI.skin.button.font = font;
        GUI.skin.box.font = font;
        GUI.skin.textField.font = font;
        GUI.skin.textArea.font = font;
    }

    /// <summary>濃紺〜黒の半透明の角丸パネル背景</summary>
    public static Texture2D PanelTexture => _panelTexture ??= BuildRoundedRectTexture(64, 64, 14, new Color(0.08f, 0.08f, 0.14f, 0.82f));

    /// <summary>単色1x1テクスチャ(GUI.colorで着色して使う汎用の塗りつぶし用)</summary>
    public static Texture2D SolidTexture
    {
        get
        {
            if (_solidTexture == null)
            {
                _solidTexture = new Texture2D(1, 1);
                _solidTexture.SetPixel(0, 0, Color.white);
                _solidTexture.Apply();
            }
            return _solidTexture;
        }
    }

    private static readonly Vector2[] OutlineDirections =
    {
        new Vector2(-1f, -1f), new Vector2(0f, -1f), new Vector2(1f, -1f),
        new Vector2(-1f, 0f),                         new Vector2(1f, 0f),
        new Vector2(-1f, 1f), new Vector2(0f, 1f), new Vector2(1f, 1f),
    };

    /// <summary>文字を8方向にオフセットした複製で縁取りしてから本体を重ねて描く
    /// (疑似太字でにじむ大きな文字の輪郭をはっきりさせる)</summary>
    public static void DrawOutlinedLabel(Rect rect, string text, GUIStyle style, Color outlineColor, float outlineWidth = 2f)
    {
        var outlineStyle = new GUIStyle(style);
        outlineStyle.normal.textColor = outlineColor;
        foreach (Vector2 dir in OutlineDirections)
        {
            var offsetRect = new Rect(rect.x + dir.x * outlineWidth, rect.y + dir.y * outlineWidth, rect.width, rect.height);
            GUI.Label(offsetRect, text, outlineStyle);
        }
        GUI.Label(rect, text, style);
    }

    /// <summary>指定色の角丸矩形テクスチャを生成して返す(毎フレーム呼ぶ用途ではキャッシュ済みのものを使う)</summary>
    public static Texture2D BuildRoundedRectTexture(int width, int height, int cornerRadius, Color color)
    {
        var tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
        int r = Mathf.Clamp(cornerRadius, 0, Mathf.Min(width, height) / 2);

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                bool inside = true;

                // 4隅だけ、角の中心からの距離で丸めるかどうかを判定する
                if (x < r && y < r) inside = Vector2.Distance(new Vector2(x, y), new Vector2(r, r)) <= r;
                else if (x >= width - r && y < r) inside = Vector2.Distance(new Vector2(x, y), new Vector2(width - r - 1, r)) <= r;
                else if (x < r && y >= height - r) inside = Vector2.Distance(new Vector2(x, y), new Vector2(r, height - r - 1)) <= r;
                else if (x >= width - r && y >= height - r) inside = Vector2.Distance(new Vector2(x, y), new Vector2(width - r - 1, height - r - 1)) <= r;

                tex.SetPixel(x, y, inside ? color : new Color(0f, 0f, 0f, 0f));
            }
        }

        tex.Apply();
        return tex;
    }

    /// <summary>暗いパネル上に乗せる白文字の標準ラベルスタイル</summary>
    public static GUIStyle LabelStyle(int fontSize = 14, bool wordWrap = false)
    {
        var style = new GUIStyle(GUI.skin.label) { fontSize = fontSize, wordWrap = wordWrap };
        style.normal.textColor = Color.white;
        return style;
    }

    /// <summary>タイトル/見出し用の太字・中央揃えスタイル</summary>
    public static GUIStyle TitleStyle(int fontSize, Color color)
    {
        var style = new GUIStyle(GUI.skin.label)
        {
            fontSize = fontSize,
            fontStyle = FontStyle.Bold,
            alignment = TextAnchor.MiddleCenter,
        };
        style.normal.textColor = color;
        return style;
    }

    private static Texture2D _secondaryButtonNormalTexture;
    private static Texture2D _secondaryButtonHoverTexture;

    /// <summary>通常ボタン用の背景(通常/ホバー)。既定スキンの背景へフォールバックさせず、
    /// ホバー時は「わずかに明るくなる」だけにする</summary>
    public static Texture2D SecondaryButtonNormalTexture => _secondaryButtonNormalTexture ??= BuildRoundedRectTexture(64, 64, 14, new Color(1f, 1f, 1f, 0.09f));
    public static Texture2D SecondaryButtonHoverTexture => _secondaryButtonHoverTexture ??= BuildRoundedRectTexture(64, 64, 14, new Color(1f, 1f, 1f, 0.18f));

    /// <summary>標準ボタンのスタイル(角丸背景+太字、ホバー時は黄色文字)</summary>
    public static GUIStyle ButtonStyle(int fontSize = 16)
    {
        var style = new GUIStyle(GUI.skin.button)
        {
            fontSize = fontSize,
            fontStyle = FontStyle.Bold,
            border = new RectOffset(16, 16, 16, 16),
        };
        style.normal.background = SecondaryButtonNormalTexture;
        style.hover.background = SecondaryButtonHoverTexture;
        style.active.background = SecondaryButtonHoverTexture;
        style.focused.background = SecondaryButtonNormalTexture;
        style.normal.textColor = Color.white;
        style.hover.textColor = Yellow;
        style.active.textColor = Yellow;
        style.focused.textColor = Color.white;
        return style;
    }

    /// <summary>使用頻度の低い操作向けの、ButtonStyleより控えめな「チップ」スタイル
    /// (Toggle用にon状態の背景/文字色も設定済み)</summary>
    public static GUIStyle ChipButtonStyle(int fontSize = 13)
    {
        var style = new GUIStyle(GUI.skin.button)
        {
            fontSize = fontSize,
            fontStyle = FontStyle.Normal,
            border = new RectOffset(12, 12, 10, 10),
            padding = new RectOffset(12, 12, 6, 6),
        };
        style.normal.background = SecondaryButtonNormalTexture;
        style.hover.background = SecondaryButtonHoverTexture;
        style.active.background = SecondaryButtonHoverTexture;
        style.focused.background = SecondaryButtonNormalTexture;
        style.onNormal.background = SecondaryButtonHoverTexture;
        style.onHover.background = SecondaryButtonHoverTexture;
        style.onActive.background = SecondaryButtonHoverTexture;
        style.onFocused.background = SecondaryButtonHoverTexture;
        var textColor = new Color(1f, 1f, 1f, 0.85f);
        style.normal.textColor = textColor;
        style.hover.textColor = Yellow;
        style.active.textColor = Yellow;
        style.focused.textColor = textColor;
        style.onNormal.textColor = Yellow;
        style.onHover.textColor = Yellow;
        style.onActive.textColor = Yellow;
        style.onFocused.textColor = Yellow;
        return style;
    }

    private static Texture2D _primaryButtonTexture;
    private static Texture2D _primaryButtonHoverTexture;
    private static Texture2D _primaryButtonActiveTexture;

    /// <summary>最優先アクション(「プレイ」等)用の、黄色塗りつぶしボタンの背景</summary>
    public static Texture2D PrimaryButtonTexture => _primaryButtonTexture ??= BuildRoundedRectTexture(64, 64, 14, Yellow);

    // ホバー=わずかに明るく、押下=わずかに暗く
    private static readonly Color PrimaryHoverColor = new Color(1f, 0.87f, 0.35f);
    private static readonly Color PrimaryActiveColor = new Color(0.9f, 0.73f, 0.1f);
    public static Texture2D PrimaryButtonHoverTexture => _primaryButtonHoverTexture ??= BuildRoundedRectTexture(64, 64, 14, PrimaryHoverColor);
    public static Texture2D PrimaryButtonActiveTexture => _primaryButtonActiveTexture ??= BuildRoundedRectTexture(64, 64, 14, PrimaryActiveColor);

    /// <summary>黄色塗りつぶし+濃色文字の主役ボタンスタイル</summary>
    public static GUIStyle PrimaryButtonStyle(int fontSize = 20)
    {
        var style = new GUIStyle(GUI.skin.button)
        {
            fontSize = fontSize,
            fontStyle = FontStyle.Bold,
            border = new RectOffset(20, 20, 20, 20),
        };
        style.normal.background = PrimaryButtonTexture;
        style.hover.background = PrimaryButtonHoverTexture;
        style.active.background = PrimaryButtonActiveTexture;
        var darkText = new Color(0.08f, 0.08f, 0.1f);
        style.normal.textColor = darkText;
        style.hover.textColor = darkText;
        style.active.textColor = darkText;
        return style;
    }

    private static GUIStyle _panelStyle;

    /// <summary>GUILayout.BeginArea等に渡せる、角丸パネル背景のGUIStyle</summary>
    public static GUIStyle PanelStyle()
    {
        if (_panelStyle == null)
        {
            _panelStyle = new GUIStyle(GUI.skin.box);
            _panelStyle.normal.background = PanelTexture;
            _panelStyle.border = new RectOffset(20, 20, 20, 20);
            _panelStyle.padding = new RectOffset(14, 14, 12, 12);
        }
        return _panelStyle;
    }
}
