using UnityEngine;
using UnityEngine.EventSystems;

/// <summary>
/// 画面右側の視点操作エリア。ドラッグ量(ピクセル)を蓄積し、TouchInputProviderがConsumeDelta()で取り出す。
/// 透明なUI Image(Raycast Target=true)に付ける想定。
/// </summary>
public class DragLookArea : MonoBehaviour, IDragHandler
{
    private Vector2 _accumulatedDelta;

    public void OnDrag(PointerEventData eventData)
    {
        _accumulatedDelta += eventData.delta;
    }

    /// <summary>蓄積したドラッグ量を返し、蓄積をリセットする</summary>
    public Vector2 ConsumeDelta()
    {
        Vector2 result = _accumulatedDelta;
        _accumulatedDelta = Vector2.zero;
        return result;
    }
}
