using UnityEngine;

/// <summary>
/// グレネード系爆弾1種類ぶんの性能データ(ScriptableObject)。ExplosiveItem.definitionへ差し込んで使い、
/// 未設定(null)ならExplosiveItem側のInspector値を使う。特殊効果はDetonationEffectの配列で持たせる。
/// </summary>
[CreateAssetMenu(fileName = "BombDefinition", menuName = "BombRoyale/Bomb Definition")]
public class BombDefinition : ScriptableObject
{
    [Header("見た目")]
    public GameObject visualPrefab;
    public Vector3 visualLocalEulerAngles = Vector3.zero;
    public Vector3 visualLocalScale = Vector3.one;

    [Header("爆発")]
    public float fuseSeconds = 3f;
    public float damage = 60f;
    public float damageRadius = 5f;
    public float power = 1f;
    public float minDamageFalloff = 0.35f;

    [Header("ノックバック/自爆")]
    public float knockbackForcePerDamage = 0.45f;
    public float knockbackUpwardBias = 0.7f;
    public float knockbackRadiusMultiplier = 1.3f;
    public float selfDamageMultiplier = 0.5f;

    [Header("特殊効果(Strategyパターン。0個以上を自由に組み合わせる)")]
    [Tooltip("爆発のダメージ適用後に順番に呼ばれる追加効果(炎上/凍結/榴散弾など)")]
    public DetonationEffect[] detonationEffects;

    [Header("爆発エフェクトの色")]
    [Tooltip("trueなら閃光/火花/衝撃波の色をexplosionColorベースに置き換える")]
    public bool overrideExplosionColor = false;
    [Tooltip("overrideExplosionColor=true時の基準色(閃光/火花/衝撃波の色はここから派生)")]
    public Color explosionColor = new Color(1f, 0.55f, 0.05f);
}
