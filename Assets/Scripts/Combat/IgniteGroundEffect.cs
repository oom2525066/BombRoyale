using Unity.Netcode;
using UnityEngine;

/// <summary>火炎爆弾用の起爆効果。着弾地点にBurningGroundAreaを生成し、留まったプレイヤーを継続ダメージで燃やす。</summary>
[CreateAssetMenu(fileName = "IgniteGroundEffect", menuName = "BombRoyale/Detonation Effects/Ignite Ground")]
public class IgniteGroundEffect : DetonationEffect
{
    [SerializeField] private BurningGroundArea burningGroundAreaPrefab;
    [SerializeField] private float radius = 4f;
    [SerializeField] private float durationSeconds = 5f;
    [SerializeField] private float damagePerSecond = 8f;

    public override void ServerApply(ExplosiveItem source, Vector3 position, ulong instigatorClientId)
    {
        if (burningGroundAreaPrefab == null)
        {
            Debug.LogWarning("[IgniteGroundEffect] burningGroundAreaPrefab未設定のため炎上効果をスキップしました");
            return;
        }

        BurningGroundArea area = Object.Instantiate(burningGroundAreaPrefab, position, Quaternion.identity);
        area.GetComponent<NetworkObject>().Spawn(true);
        area.ServerInitialize(radius, durationSeconds, damagePerSecond, instigatorClientId);
    }
}
