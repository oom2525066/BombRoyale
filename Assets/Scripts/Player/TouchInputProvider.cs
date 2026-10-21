using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// タッチUI(VirtualJoystick+DragLookArea+各ボタン)とデバッグ用のキーボード/マウスから、
/// 1フレーム分の入力をPlayerInputFrameにまとめるシーン単位のシングルトン。
/// キーボード: WASD/矢印で移動、右クリック中のマウス移動で視点、1〜9で武器切替、Eで拾得、Gで手放し、Spaceでジャンプ。
/// 発砲は画面右下のfireButton経由のみ。新Input Systemのstaticプロパティで直接ポーリングする。
/// </summary>
public class TouchInputProvider : MonoBehaviour
{
    public static TouchInputProvider Instance { get; private set; }

    [SerializeField] private VirtualJoystick moveJoystick;
    [SerializeField] private DragLookArea lookArea;
    [Tooltip("画面右下の発砲/投擲ボタン(押しっぱなし判定)")]
    [SerializeField] private TouchButton fireButton;
    [Tooltip("fireButton中心のアイコン。装備中の武器に応じて切り替わる")]
    [SerializeField] private Image fireButtonIcon;
    [Tooltip("ジャンプボタン。押した瞬間だけジャンプする")]
    [SerializeField] private TouchButton jumpButton;
    [SerializeField] private Image jumpButtonIcon;
    [Tooltip("拾得ボタン")]
    [SerializeField] private TouchButton pickupButton;
    [SerializeField] private Image pickupButtonIcon;
    [Tooltip("装備中の武器を手放すボタン(キーボードはGキー)")]
    [SerializeField] private TouchButton dropButton;
    [SerializeField] private Image dropButtonIcon;
    [Tooltip("ドラッグ量(px)から視点回転角(度)への変換係数")]
    [SerializeField] private float lookSensitivity = 0.15f;
    [Tooltip("マウス移動量(px)から視点回転角(度)への変換係数")]
    [SerializeField] private float mouseLookSensitivity = 0.1f;
    [Tooltip("ジョイスティック/視点操作エリアを含むCanvasルート。操作できないフェーズでは非表示にする")]
    [SerializeField] private GameObject touchControlsRoot;

    private bool _wasJumpButtonHeldLastFrame;
    private bool _wasDropButtonHeldLastFrame;
    private bool _wasPickupButtonHeldLastFrame;

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;

        if (jumpButtonIcon != null) jumpButtonIcon.sprite = BuildJumpIconSprite(64);
        if (pickupButtonIcon != null) pickupButtonIcon.sprite = BuildPickupIconSprite(64);

        // ボタンの見た目を統一し、攻撃/拾得ボタンには文字ラベルを実行時に添える
        StyleTouchButton(fireButton);
        StyleTouchButton(jumpButton);
        StyleTouchButton(pickupButton);
        StyleTouchButton(dropButton);
        BuildFireButtonDecorations();
        BuildPickupButtonDecorations();
        _safeAreaTarget = fireButton != null ? fireButton.transform.parent as RectTransform : null;
        ApplySafeAreaIfChanged();
        if (dropButtonIcon != null) dropButtonIcon.sprite = BuildDropIconSprite(64);

        // 拾得ボタンは近くに拾える物がある時だけ表示する(SetPickupButtonVisibleで切り替え)
        if (pickupButton != null) pickupButton.gameObject.SetActive(false);
    }

    /// <summary>拾得ボタンの表示/非表示を切り替える(拾える物が近くにある時だけ表示)</summary>
    public void SetPickupButtonVisible(bool visible)
    {
        if (pickupButton == null) return;
        if (pickupButton.gameObject.activeSelf == visible) return;
        // 言語切り替えに追従するため、表示時に文言を入れ直す
        if (visible && _pickupLabel != null) _pickupLabel.text = Localization.Get("touch.pickup");
        pickupButton.gameObject.SetActive(visible);
    }

    /// <summary>攻撃ボタンのアイコンを設定する(nullなら非表示)</summary>
    public void SetFireButtonIcon(Sprite sprite)
    {
        if (fireButtonIcon == null) return;
        fireButtonIcon.sprite = sprite;
        fireButtonIcon.enabled = sprite != null;
    }

    /// <summary>装備変更時に呼ぶ。アイコン・投擲系か・装備有無を反映する(未装備ならボタンを薄くする)</summary>
    public void SetFireButtonState(Sprite icon, bool isThrow, bool hasWeapon)
    {
        SetFireButtonIcon(icon);
        _fireIsThrow = isThrow;
        _fireHasWeapon = hasWeapon;
        _fireLabelHeldState = null; // 文言を入れ直させる
        if (_fireCanvasGroup != null) _fireCanvasGroup.alpha = hasWeapon ? 1f : DisabledFireButtonAlpha;
        UpdateFireLabel();
        Debug.Log($"[TouchInputProvider] 攻撃ボタンの表示を更新 hasWeapon={hasWeapon} isThrow={isThrow} icon={(icon != null ? icon.name : "なし")}");
    }

    // ---- 攻撃ボタン/拾得ボタンの装飾(実行時に組み立てる) ----
    private const float DisabledFireButtonAlpha = 0.45f;
    private const int FireLabelFontSize = 30;
    private const int FireLabelHeldFontSize = 24;
    private Text _fireLabel;
    private Text _pickupLabel;
    private CanvasGroup _fireCanvasGroup;
    private bool _fireIsThrow;
    private bool _fireHasWeapon;
    private bool? _fireLabelHeldState;

    private static void StyleTouchButton(TouchButton button)
    {
        if (button == null) return;
        var image = button.GetComponent<Image>();
        if (image == null) return;
        image.sprite = TouchButton.BuildStyledButtonSprite();
        image.type = Image.Type.Simple;
        image.color = Color.white;
    }

    /// <summary>攻撃ボタン: 武器アイコンを上寄りに置き、下に「投げる/撃つ」のラベルを作る</summary>
    private void BuildFireButtonDecorations()
    {
        if (fireButton == null) return;
        if (fireButtonIcon != null)
        {
            fireButtonIcon.rectTransform.anchoredPosition = new Vector2(0f, 18f);
            fireButtonIcon.rectTransform.sizeDelta = new Vector2(88f, 88f);
            fireButtonIcon.preserveAspect = true;
            fireButtonIcon.raycastTarget = false;
        }
        _fireLabel = CreateButtonLabel(fireButton.transform, "FireLabel", new Vector2(0f, -50f), new Vector2(160f, 38f), FireLabelFontSize);
        _fireCanvasGroup = fireButton.GetComponent<CanvasGroup>();
        if (_fireCanvasGroup == null) _fireCanvasGroup = fireButton.gameObject.AddComponent<CanvasGroup>();
        // 武器を拾うまでは薄く表示する
        _fireCanvasGroup.alpha = DisabledFireButtonAlpha;
        _fireLabel.enabled = false;
    }

    private const float PickupButtonSize = 124f;
    private static readonly Vector2 PickupButtonPosition = new Vector2(-350f, 240f);

    // ---- SafeArea対応: ボタン群の親のアンカーをScreen.safeAreaに合わせる ----
    private RectTransform _safeAreaTarget;
    private Rect _appliedSafeArea;
    private Vector2Int _appliedScreenSize;

    private void ApplySafeAreaIfChanged()
    {
        if (_safeAreaTarget == null || Screen.width <= 0 || Screen.height <= 0) return;
        Rect safe = Screen.safeArea;
        var screenSize = new Vector2Int(Screen.width, Screen.height);
        if (safe == _appliedSafeArea && screenSize == _appliedScreenSize) return;
        _appliedSafeArea = safe;
        _appliedScreenSize = screenSize;

        _safeAreaTarget.anchorMin = new Vector2(safe.xMin / Screen.width, safe.yMin / Screen.height);
        _safeAreaTarget.anchorMax = new Vector2(safe.xMax / Screen.width, safe.yMax / Screen.height);
        _safeAreaTarget.offsetMin = Vector2.zero;
        _safeAreaTarget.offsetMax = Vector2.zero;
        Debug.Log($"[TouchInputProvider] タッチ操作UIをSafeAreaに合わせました safeArea={safe} screen={Screen.width}x{Screen.height}");
    }

    /// <summary>拾得ボタン: 大きさと位置を調整し、アイコンとラベルを配置する</summary>
    private void BuildPickupButtonDecorations()
    {
        if (pickupButton == null) return;

        var pickupRect = (RectTransform)pickupButton.transform;
        pickupRect.sizeDelta = new Vector2(PickupButtonSize, PickupButtonSize);
        pickupRect.anchoredPosition = PickupButtonPosition;

        if (pickupButtonIcon != null)
        {
            pickupButtonIcon.rectTransform.anchoredPosition = new Vector2(0f, 13f);
            pickupButtonIcon.rectTransform.sizeDelta = new Vector2(50f, 50f);
            pickupButtonIcon.raycastTarget = false;
        }
        _pickupLabel = CreateButtonLabel(pickupButton.transform, "PickupLabel", new Vector2(0f, -33f), new Vector2(110f, 30f), 24);
        _pickupLabel.text = Localization.Get("touch.pickup");
    }

    private static Text CreateButtonLabel(Transform parent, string name, Vector2 anchoredPosition, Vector2 size, int fontSize)
    {
        var go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent, false);
        var rect = (RectTransform)go.transform;
        rect.anchorMin = new Vector2(0.5f, 0.5f);
        rect.anchorMax = new Vector2(0.5f, 0.5f);
        rect.pivot = new Vector2(0.5f, 0.5f);
        rect.anchoredPosition = anchoredPosition;
        rect.sizeDelta = size;

        var text = go.AddComponent<Text>();
        Font font = PopUI.SharedFont;
        if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        text.font = font;
        text.fontSize = fontSize;
        text.fontStyle = FontStyle.Bold;
        text.alignment = TextAnchor.MiddleCenter;
        text.color = Color.white;
        text.raycastTarget = false;
        text.horizontalOverflow = HorizontalWrapMode.Overflow;
        text.verticalOverflow = VerticalWrapMode.Overflow;

        // 明るい背景でも読めるよう影を付ける
        var shadow = go.AddComponent<Shadow>();
        shadow.effectColor = new Color(0f, 0f, 0f, 0.6f);
        shadow.effectDistance = new Vector2(1.5f, -1.5f);
        return text;
    }

    /// <summary>攻撃ボタンのラベルを更新する(投擲系は構え中だけ「離して投げる」。状態が変わった時だけ書き換え)</summary>
    private void UpdateFireLabel()
    {
        if (_fireLabel == null) return;
        if (!_fireHasWeapon)
        {
            if (_fireLabel.enabled) _fireLabel.enabled = false;
            return;
        }

        bool held = _fireIsThrow && fireButton != null && fireButton.IsHeld;
        if (_fireLabelHeldState.HasValue && _fireLabelHeldState.Value == held && _fireLabel.enabled) return;
        _fireLabelHeldState = held;

        _fireLabel.enabled = true;
        if (!_fireIsThrow)
        {
            _fireLabel.text = Localization.Get("touch.shoot");
            _fireLabel.fontSize = FireLabelFontSize;
        }
        else if (held)
        {
            _fireLabel.text = Localization.Get("touch.throwRelease");
            _fireLabel.fontSize = FireLabelHeldFontSize;
        }
        else
        {
            _fireLabel.text = Localization.Get("touch.throw");
            _fireLabel.fontSize = FireLabelFontSize;
        }
    }

    private void Update()
    {
        UpdateFireLabel();
        ApplySafeAreaIfChanged();
        if (touchControlsRoot == null) return;

        // 自分のプレイヤーがスポーン済みで、移動可能なフェーズの時だけ操作UIを表示する
        bool canMove = PlayerController.LocalPlayer != null &&
            (MatchManager.Instance == null || MatchManager.Instance.IsMovementActiveForInput);
        if (touchControlsRoot.activeSelf != canMove) touchControlsRoot.SetActive(canMove);
    }

    /// <summary>1フレーム分の入力(タッチ+キーボード/マウス)をまとめて返す。Ownerが毎フレーム呼ぶ</summary>
    public PlayerInputFrame BuildInputFrame()
    {
        Vector2 move = moveJoystick != null ? moveJoystick.Value : Vector2.zero;
        Vector2 lookDelta = lookArea != null ? lookArea.ConsumeDelta() : Vector2.zero;
        // 発砲ボタンを押したままのドラッグも視点操作に合流させる(ポインタがボタンにキャプチャされるため)
        lookDelta += fireButton != null ? fireButton.ConsumeDragDelta() : Vector2.zero;

        move += ReadKeyboardMove();
        lookDelta += ReadMouseLookDelta();

        var frame = new PlayerInputFrame
        {
            horizontal = Mathf.Clamp(move.x, -1f, 1f),
            vertical = Mathf.Clamp(move.y, -1f, 1f),
            // 設定画面で変更できる感度倍率を最後に掛ける
            lookYawDelta = lookDelta.x * lookSensitivity * LookSensitivitySettings.Multiplier,
            // 上方向のドラッグでカメラが上を向く(HandleLookLocallyは _pitch -= pitchDelta)
            lookPitchDelta = lookDelta.y * lookSensitivity * LookSensitivitySettings.Multiplier,
            wantsFirePrimaryHeld = fireButton != null && fireButton.IsHeld,
            weaponSwitchIndexOrNegativeOne = ReadWeaponSwitchIndex(),
            wantsPickupDown = ReadPickupDown(),
            wantsDropDown = ReadDropDown(),
            wantsJumpDown = ReadJumpDown(),
        };

        return frame;
    }

    private static Vector2 ReadKeyboardMove()
    {
        var kb = Keyboard.current;
        if (kb == null) return Vector2.zero;

        float x = 0f, y = 0f;
        if (kb.aKey.isPressed || kb.leftArrowKey.isPressed) x -= 1f;
        if (kb.dKey.isPressed || kb.rightArrowKey.isPressed) x += 1f;
        if (kb.sKey.isPressed || kb.downArrowKey.isPressed) y -= 1f;
        if (kb.wKey.isPressed || kb.upArrowKey.isPressed) y += 1f;
        return new Vector2(x, y);
    }

    /// <summary>右クリックを押している間だけマウス移動を視点操作として扱う</summary>
    private Vector2 ReadMouseLookDelta()
    {
        var mouse = Mouse.current;
        if (mouse == null) return Vector2.zero;
        if (!mouse.rightButton.isPressed) return Vector2.zero;

        // 最終係数はlookSensitivity側で掛けるため、ここでは比率だけ反映する
        return mouse.delta.ReadValue() * (mouseLookSensitivity / Mathf.Max(0.0001f, lookSensitivity));
    }

    private static int ReadWeaponSwitchIndex()
    {
        var kb = Keyboard.current;
        if (kb == null) return -1;

        if (kb.digit1Key.wasPressedThisFrame) return 0;
        if (kb.digit2Key.wasPressedThisFrame) return 1;
        if (kb.digit3Key.wasPressedThisFrame) return 2;
        if (kb.digit4Key.wasPressedThisFrame) return 3;
        if (kb.digit5Key.wasPressedThisFrame) return 4;
        if (kb.digit6Key.wasPressedThisFrame) return 5;
        if (kb.digit7Key.wasPressedThisFrame) return 6;
        if (kb.digit8Key.wasPressedThisFrame) return 7;
        if (kb.digit9Key.wasPressedThisFrame) return 8;
        return -1;
    }

    /// <summary>拾得ボタン(タッチ)+Eキー。ボタンは押下状態しか持たないため、前フレームとの比較で押した瞬間を検出する</summary>
    private bool ReadPickupDown()
    {
        bool touchHeld = pickupButton != null && pickupButton.IsHeld;
        bool touchPressed = touchHeld && !_wasPickupButtonHeldLastFrame;
        _wasPickupButtonHeldLastFrame = touchHeld;

        var kb = Keyboard.current;
        bool keyboardPressed = kb != null && kb.eKey.wasPressedThisFrame;

        return touchPressed || keyboardPressed;
    }

    /// <summary>手放しボタン(タッチ)+Gキー。押した瞬間を検出する</summary>
    private bool ReadDropDown()
    {
        bool touchHeld = dropButton != null && dropButton.IsHeld;
        bool touchPressed = touchHeld && !_wasDropButtonHeldLastFrame;
        _wasDropButtonHeldLastFrame = touchHeld;

        var kb = Keyboard.current;
        bool keyboardPressed = kb != null && kb.gKey.wasPressedThisFrame;

        return touchPressed || keyboardPressed;
    }

    /// <summary>ジャンプボタン(タッチ)+Spaceキー。押した瞬間のみ検出する(連続ジャンプ防止)</summary>
    private bool ReadJumpDown()
    {
        bool touchHeld = jumpButton != null && jumpButton.IsHeld;
        bool touchPressed = touchHeld && !_wasJumpButtonHeldLastFrame;
        _wasJumpButtonHeldLastFrame = touchHeld;

        var kb = Keyboard.current;
        bool keyboardPressed = kb != null && kb.spaceKey.wasPressedThisFrame;

        return touchPressed || keyboardPressed;
    }

    /// <summary>ジャンプ用の上向き三角形アイコンを生成する</summary>
    private static Sprite BuildJumpIconSprite(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, 0f));
            }
        }

        int top = size - size / 8;
        int bottom = size / 6;
        int halfWidthAtBottom = size / 2 - size / 8;
        for (int y = bottom; y < top; y++)
        {
            float t = (float)(y - bottom) / Mathf.Max(1, top - bottom);
            int halfWidth = Mathf.RoundToInt(halfWidthAtBottom * (1f - t));
            int cx = size / 2;
            for (int x = cx - halfWidth; x < cx + halfWidth; x++)
            {
                if (x < 0 || x >= size) continue;
                tex.SetPixel(x, y, Color.white);
            }
        }

        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
    }

    /// <summary>拾得用の下向き矢印+受け皿アイコンを生成する</summary>
    private static Sprite BuildPickupIconSprite(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, 0f));
            }
        }

        // 縦棒(矢印の軸)
        int shaftX0 = size / 2 - size / 14;
        int shaftX1 = size / 2 + size / 14;
        int shaftTop = size - size / 6;
        int shaftBottom = size / 3;
        for (int y = shaftBottom; y < shaftTop; y++)
        {
            for (int x = shaftX0; x < shaftX1; x++)
            {
                tex.SetPixel(x, y, Color.white);
            }
        }

        // 矢印の先端(下向き三角)
        int headTop = shaftBottom + size / 6;
        int headBottom = size / 6;
        int halfWidthAtTop = size / 6;
        for (int y = headBottom; y < headTop; y++)
        {
            float t = (float)(y - headBottom) / Mathf.Max(1, headTop - headBottom);
            int halfWidth = Mathf.RoundToInt(halfWidthAtTop * t);
            int cx = size / 2;
            for (int x = cx - halfWidth; x < cx + halfWidth; x++)
            {
                if (x < 0 || x >= size) continue;
                tex.SetPixel(x, y, Color.white);
            }
        }

        // 受け皿(下の横棒)
        int trayY0 = 0;
        int trayY1 = size / 10;
        for (int y = trayY0; y < trayY1; y++)
        {
            for (int x = size / 6; x < size - size / 6; x++)
            {
                tex.SetPixel(x, y, Color.white);
            }
        }

        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
    }

    /// <summary>手放し用アイコン(上の横棒から下向きに離れる矢印)を生成する</summary>
    private static Sprite BuildDropIconSprite(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, 0f));
            }
        }

        // 手/ホットバーを表す上の横棒
        int barY0 = size - size / 10;
        int barY1 = size;
        for (int y = barY0; y < barY1; y++)
        {
            for (int x = size / 6; x < size - size / 6; x++)
            {
                tex.SetPixel(x, y, Color.white);
            }
        }

        // 縦棒(矢印の軸)。上の横棒から下へ伸びる
        int shaftX0 = size / 2 - size / 14;
        int shaftX1 = size / 2 + size / 14;
        int shaftTop = size - size / 3;
        int shaftBottom = size / 6;
        for (int y = shaftBottom; y < shaftTop; y++)
        {
            for (int x = shaftX0; x < shaftX1; x++)
            {
                tex.SetPixel(x, y, Color.white);
            }
        }

        // 矢印の先端(下向き三角、下端で離れていく様子)
        int headTop = shaftBottom;
        int headBottom = 0;
        int halfWidthAtTop = size / 6;
        for (int y = headBottom; y < headTop; y++)
        {
            float t = (float)(y - headBottom) / Mathf.Max(1, headTop - headBottom);
            int halfWidth = Mathf.RoundToInt(halfWidthAtTop * (1f - t));
            int cx = size / 2;
            for (int x = cx - halfWidth; x < cx + halfWidth; x++)
            {
                if (x < 0 || x >= size) continue;
                tex.SetPixel(x, y, Color.white);
            }
        }

        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
    }
}

