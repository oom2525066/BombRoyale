using Unity.Netcode;
using UnityEngine;

/// <summary>炸裂爆弾用の起爆効果。起爆の瞬間に小型の子爆弾(fragmentPrefab)を周囲へ均等にばら撒く。</summary>
[CreateAssetMenu(fileName = "FragmentationEffect", menuName = "BombRoyale/Detonation Effects/Fragmentation")]
public class FragmentationEffect : DetonationEffect
{
    [SerializeField] private ExplosiveItem fragmentPrefab;
    [SerializeField] private int fragmentCount = 6;
    [SerializeField] private float fragmentForce = 9f;
    [Tooltip("水平方向の飛散力に対する上向き成分の比率")]
    [SerializeField] private float fragmentUpwardBias = 0.5f;

    public override void ServerApply(ExplosiveItem source, Vector3 position, ulong instigatorClientId)
    {
        if (fragmentPrefab == null || fragmentCount <= 0)
        {
            Debug.LogWarning("[FragmentationEffect] fragmentPrefab未設定のため榴散弾効果をスキップしました");
            return;
        }

        for (int i = 0; i < fragmentCount; i++)
        {
            float angle = i * (360f / fragmentCount);
            Vector3 horizontal = Quaternion.Euler(0f, angle, 0f) * Vector3.forward;
            Vector3 velocity = (horizontal + Vector3.up * fragmentUpwardBias).normalized * fragmentForce;

            // 本体や互いを巻き込まないよう、少し浮かせて生成する
            ExplosiveItem fragment = Object.Instantiate(fragmentPrefab, position + Vector3.up * 0.3f, Quaternion.identity);
            fragment.NetworkObject.Spawn(true);
            fragment.ServerInitialize(instigatorClientId, velocity);
        }
    }
}
