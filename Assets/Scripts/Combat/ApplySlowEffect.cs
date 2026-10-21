using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 凍結爆弾用の起爆効果。爆心地周辺の全プレイヤーを鈍足化し、範囲内の他の爆弾も凍結して自発的な起爆を止める
/// (誘爆/銃撃による強制起爆は凍結中でも通る)。
/// </summary>
[CreateAssetMenu(fileName = "ApplySlowEffect", menuName = "BombRoyale/Detonation Effects/Apply Slow")]
public class ApplySlowEffect : DetonationEffect
{
    [SerializeField, Range(0.05f, 1f)] private float slowMultiplier = 0.35f;
    [SerializeField] private float durationSeconds = 3f;
    [SerializeField] private float radius = 5f;
    [SerializeField] private LayerMask playerMask = ~0;

    public override void ServerApply(ExplosiveItem source, Vector3 position, ulong instigatorClientId)
    {
        Collider[] hits = Physics.OverlapSphere(position, radius, playerMask, QueryTriggerInteraction.Collide);
        var processedPlayers = new HashSet<PlayerController>();
        var processedExplosives = new HashSet<ExplosiveItem>();

        foreach (var col in hits)
        {
            var pc = col.GetComponentInParent<PlayerController>();
            if (pc != null && processedPlayers.Add(pc))
            {
                pc.ServerApplySlow(slowMultiplier, durationSeconds);
                continue;
            }

            var explosive = col.GetComponentInParent<ExplosiveItem>();
            if (explosive != null && explosive != source && processedExplosives.Add(explosive))
            {
                explosive.ServerSetFrozen(durationSeconds);
            }
        }
    }
}
