using UnityEngine;

/// <summary>
/// 爆発時の追加効果(炎上/凍結/榴散弾など)の基底クラス(Strategyパターン)。
/// BombDefinition.detonationEffectsに設定すると、ExplosiveItem本体を変更せず特殊効果を追加できる。
/// </summary>
public abstract class DetonationEffect : ScriptableObject
{
    /// <summary>サーバー側でのみ呼ばれる起爆時の追加効果</summary>
    public abstract void ServerApply(ExplosiveItem source, Vector3 position, ulong instigatorClientId);
}
