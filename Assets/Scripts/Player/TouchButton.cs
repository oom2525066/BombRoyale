using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// 画面上の「押しっぱなし」ボタン(発砲/投擲用)。押している間IsHeldがtrueになる。
/// ポインタはこのボタンにキャプチャされるため、ボタンを押したままのドラッグ量も蓄積し、視点操作に使えるようにしている。
/// </summary>
public class TouchButton : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IDragHandler
{
    public bool IsHeld { get; private set; }

    private Vector2 _accumulatedDragDelta;

    // 押している間だけ少し縮めて、押せたことを分かりやすくする(見た目のみ)
    private const float PressedScale = 0.92f;
    private Vector3 _baseScale = Vector3.one;

    public void OnPointerDown(PointerEventData eventData)
    {
        IsHeld = true;
        transform.localScale = _baseScale * PressedScale;
    }

    public void OnPointerUp(PointerEventData eventData)
    {
        IsHeld = false;
        transform.localScale = _baseScale;
    }

    public void OnDrag(PointerEventData eventData) => _accumulatedDragDelta += eventData.delta;

    /// <summary>非表示になるとOnPointerUpが届かないため、押下状態とドラッグ量を強制的にリセットする</summary>
    private void OnDisable()
    {
        IsHeld = false;
        _accumulatedDragDelta = Vector2.zero;
        transform.localScale = _baseScale;
    }

    /// <summary>蓄積したドラッグ量を返し、蓄積をリセットする(DragLookArea.ConsumeDeltaと同様)</summary>
    public Vector2 ConsumeDragDelta()
    {
        Vector2 result = _accumulatedDragDelta;
        _accumulatedDragDelta = Vector2.zero;
        return result;
    }

    private void Awake()
    {
        _baseScale = transform.localScale;
        ApplyCircleSpriteIfUnset();
    }

    /// <summary>Imageのスプライトが未設定の場合だけ、円形スプライトを自動で割り当てる</summary>
    private void ApplyCircleSpriteIfUnset()
    {
        var image = GetComponent<UnityEngine.UI.Image>();
        if (image == null || image.sprite != null) return;

        image.sprite = BuildCircleSprite(128);
        image.type = UnityEngine.UI.Image.Type.Simple;
    }

    private static Sprite _cachedCircleSprite;

    /// <summary>白い円のスプライトを実行時に生成してキャッシュする(VirtualJoystickの背景/ハンドルとも共有)</summary>
    public static Sprite BuildCircleSprite(int size)
    {
        if (_cachedCircleSprite != null) return _cachedCircleSprite;

        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float radius = size / 2f;
        Vector2 center = new Vector2(size / 2f, size / 2f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool inside = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center) <= radius;
                tex.SetPixel(x, y, inside ? Color.white : new Color(1f, 1f, 1f, 0f));
            }
        }

        tex.Apply();
        _cachedCircleSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        return _cachedCircleSprite;
    }

    // タッチ操作UI共通の見た目(暗い半透明の地+白い縁リング)
    public static readonly Color StyledFillColor = new Color(0.10f, 0.11f, 0.16f, 0.55f);
    public static readonly Color StyledRingColor = new Color(1f, 1f, 1f, 0.85f);

    private static Sprite _cachedStyledSprite;

    /// <summary>暗い半透明の円+白い縁取りリングのスプライトを生成してキャッシュする</summary>
    public static Sprite BuildStyledButtonSprite()
    {
        if (_cachedStyledSprite != null) return _cachedStyledSprite;

        const int size = 256;
        const float ringWidth = 7f;
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float radius = size / 2f - 1f;
        Vector2 center = new Vector2(size / 2f, size / 2f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float d = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                float outer = Mathf.Clamp01(radius - d + 0.5f);
                float ringT = Mathf.Clamp01(d - (radius - ringWidth) + 0.5f);
                Color c = Color.Lerp(StyledFillColor, StyledRingColor, ringT);
                c.a *= outer;
                tex.SetPixel(x, y, c);
            }
        }

        tex.wrapMode = TextureWrapMode.Clamp;
        tex.Apply();
        _cachedStyledSprite = Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        _cachedStyledSprite.name = "StyledTouchButton";
        return _cachedStyledSprite;
    }
}
