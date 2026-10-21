using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// 画面左下のフローティング仮想スティック(移動用)。当たり判定ゾーン上で最初に触れた位置を基準点にし、
/// そこからの指のズレを-1〜1に正規化してValueとして返す。離すとValueは0に戻る。
/// uGUIのポインタイベントを使うので、タッチでもマウスでも動く。
/// </summary>
[RequireComponent(typeof(RectTransform))]
public class VirtualJoystick : MonoBehaviour, IPointerDownHandler, IDragHandler, IPointerUpHandler
{
    [Tooltip("触れた位置に出現するハンドル(つまみ)")]
    [SerializeField] private RectTransform handleRect;
    [Tooltip("ハンドルが基準点から動ける範囲(px)。これ以上は入力値が頭打ち")]
    [SerializeField] private float handleRange = 100f;

    [Header("待機中の目印")]
    [Tooltip("触れていない間も左下に薄いスティックを表示する")]
    [SerializeField] private bool showIdleGhost = true;
    [Tooltip("待機中の目印の不透明度")]
    [Range(0f, 1f)] [SerializeField] private float idleGhostAlpha = 0.4f;
    [Tooltip("待機中の目印の、ゾーン左下の角からの余白")]
    [SerializeField] private Vector2 idleGhostMargin = new Vector2(70f, 60f);

    private const float ActiveBaseAlpha = 0.7f;

    private RectTransform _backgroundRect;
    private Vector2 _value;
    private Vector2 _originLocalPoint;
    private bool _touchActive;

    // 台座の輪(実行時に生成)と、つまみの本来の色
    private RectTransform _baseRect;
    private UnityEngine.UI.Image _baseImage;
    private UnityEngine.UI.Image _handleImage;
    private Color _handleActiveColor = Color.white;

    /// <summary>-1〜1に正規化された入力値(x=左右、y=前後)</summary>
    public Vector2 Value => _value;

    private void Awake()
    {
        _backgroundRect = (RectTransform)transform;
        ApplyCircleSpritesIfUnset();
        // 起動時はハンドルを隠す
        if (handleRect != null) handleRect.gameObject.SetActive(false);

        if (handleRect != null)
        {
            _handleImage = handleRect.GetComponent<UnityEngine.UI.Image>();
            if (_handleImage != null)
            {
                _handleActiveColor = _handleImage.color;
                _handleImage.raycastTarget = false; // タッチを横取りしない
            }
        }
        BuildBaseRing();
    }

    private void OnEnable()
    {
        // 再表示時は待機中の見た目から始める
        ShowIdleState();
    }

    /// <summary>非表示になるとOnPointerUpが届かないため、入力値を強制的に0へ戻す</summary>
    private void OnDisable()
    {
        _touchActive = false;
        _value = Vector2.zero;
    }

    /// <summary>台座の輪を実行時に生成する(つまみの可動範囲を囲む大きさ)</summary>
    private void BuildBaseRing()
    {
        if (_baseRect != null) return;
        float handleSize = handleRect != null ? Mathf.Max(handleRect.sizeDelta.x, handleRect.sizeDelta.y) : 120f;
        float baseDiameter = handleRange * 2f + handleSize * 0.55f;

        var go = new GameObject("JoystickBase", typeof(RectTransform));
        go.transform.SetParent(transform, false);
        go.transform.SetAsFirstSibling(); // つまみより奥に描く
        _baseRect = (RectTransform)go.transform;
        _baseRect.anchorMin = new Vector2(0.5f, 0.5f);
        _baseRect.anchorMax = new Vector2(0.5f, 0.5f);
        _baseRect.pivot = new Vector2(0.5f, 0.5f);
        _baseRect.sizeDelta = new Vector2(baseDiameter, baseDiameter);

        _baseImage = go.AddComponent<UnityEngine.UI.Image>();
        _baseImage.sprite = TouchButton.BuildStyledButtonSprite();
        _baseImage.raycastTarget = false;
        Debug.Log($"[VirtualJoystick] 台座の輪を生成 diameter={baseDiameter:0} handleRange={handleRange} handleSize={handleSize:0}");
    }

    /// <summary>待機中の目印の位置(ゾーンのローカル座標、左下の角から台座+余白だけ内側)</summary>
    private Vector2 IdleGhostLocalPoint()
    {
        if (_backgroundRect == null) return Vector2.zero;
        Rect zone = _backgroundRect.rect;
        float baseRadius = _baseRect != null ? _baseRect.sizeDelta.x / 2f : handleRange;
        return new Vector2(zone.xMin + baseRadius + idleGhostMargin.x, zone.yMin + baseRadius + idleGhostMargin.y);
    }

    private void ShowIdleState()
    {
        if (!showIdleGhost)
        {
            if (_baseRect != null) _baseRect.gameObject.SetActive(false);
            if (handleRect != null) handleRect.gameObject.SetActive(false);
            return;
        }

        Vector2 idlePoint = IdleGhostLocalPoint();
        if (_baseRect != null)
        {
            _baseRect.gameObject.SetActive(true);
            _baseRect.anchoredPosition = idlePoint;
            if (_baseImage != null) _baseImage.color = new Color(1f, 1f, 1f, idleGhostAlpha);
        }
        if (handleRect != null)
        {
            handleRect.gameObject.SetActive(true);
            handleRect.anchoredPosition = idlePoint;
            if (_handleImage != null)
            {
                Color c = _handleActiveColor;
                c.a *= idleGhostAlpha;
                _handleImage.color = c;
            }
        }
    }

    /// <summary>ゾーンの大きさが変わっても待機中の目印が正しい位置にいるよう、触れていない間は位置を合わせ直す</summary>
    private void LateUpdate()
    {
        if (_touchActive || !showIdleGhost || _baseRect == null) return;
        Vector2 idlePoint = IdleGhostLocalPoint();
        if ((_baseRect.anchoredPosition - idlePoint).sqrMagnitude < 0.01f) return;
        _baseRect.anchoredPosition = idlePoint;
        if (handleRect != null) handleRect.anchoredPosition = idlePoint;
    }

    private void ShowActiveState(Vector2 originLocalPoint)
    {
        if (_baseRect != null)
        {
            _baseRect.gameObject.SetActive(true);
            _baseRect.anchoredPosition = originLocalPoint;
            if (_baseImage != null) _baseImage.color = new Color(1f, 1f, 1f, ActiveBaseAlpha);
        }
        if (handleRect != null)
        {
            handleRect.gameObject.SetActive(true);
            handleRect.anchoredPosition = originLocalPoint;
            if (_handleImage != null) _handleImage.color = _handleActiveColor;
        }
    }

    /// <summary>背景/ハンドルのスプライトが未設定なら円形スプライトを割り当てる</summary>
    private void ApplyCircleSpritesIfUnset()
    {
        var backgroundImage = GetComponent<UnityEngine.UI.Image>();
        if (backgroundImage != null && backgroundImage.sprite == null)
        {
            backgroundImage.sprite = TouchButton.BuildCircleSprite(128);
            backgroundImage.type = UnityEngine.UI.Image.Type.Simple;
        }

        if (handleRect != null)
        {
            var handleImage = handleRect.GetComponent<UnityEngine.UI.Image>();
            if (handleImage != null && handleImage.sprite == null)
            {
                handleImage.sprite = TouchButton.BuildCircleSprite(128);
                handleImage.type = UnityEngine.UI.Image.Type.Simple;
            }
        }
    }

    /// <summary>触れた位置を基準点として記録し、その位置にハンドルを出す(Valueは0のまま)</summary>
    public void OnPointerDown(PointerEventData eventData)
    {
        if (_backgroundRect == null) return;

        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _backgroundRect, eventData.position, eventData.pressEventCamera, out Vector2 localPoint))
        {
            return;
        }

        _originLocalPoint = localPoint;
        _touchActive = true;
        _value = Vector2.zero;

        ShowActiveState(_originLocalPoint);
    }

    /// <summary>基準点からの指のズレをhandleRangeでクランプし、ハンドル位置と入力値に反映する</summary>
    public void OnDrag(PointerEventData eventData)
    {
        if (!_touchActive || _backgroundRect == null) return;

        if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _backgroundRect, eventData.position, eventData.pressEventCamera, out Vector2 localPoint))
        {
            return;
        }

        Vector2 offset = localPoint - _originLocalPoint;
        Vector2 clamped = Vector2.ClampMagnitude(offset, handleRange);
        if (handleRect != null) handleRect.anchoredPosition = _originLocalPoint + clamped;
        _value = clamped / handleRange;
    }

    /// <summary>指を離したら入力値を0に戻し、待機中の目印へ戻す</summary>
    public void OnPointerUp(PointerEventData eventData)
    {
        _touchActive = false;
        _value = Vector2.zero;
        ShowIdleState();
    }
}
