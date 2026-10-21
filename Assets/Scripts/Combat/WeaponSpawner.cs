using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 指定エリア内のランダムな位置へWeaponPickup(武器の拾い物)を配置するサーバー専用スポナー。
/// 試合開始時に一括配置し、試合中は一定間隔で補充する。Y座標は真上からの地面レイキャストで合わせる。
/// </summary>
public class WeaponSpawner : MonoBehaviour
{
    public static WeaponSpawner Instance { get; private set; }

    [Tooltip("この位置を中心にランダム配置する(未設定ならこのGameObject自身の位置)")]
    [SerializeField] private Transform areaCenter;
    [SerializeField] private Vector2 areaHalfExtents = new Vector2(20f, 20f);
    [SerializeField] private int spawnCount = 6;
    [Tooltip("配置位置を地面の高さへスナップするための下向きレイキャストに使うレイヤー")]
    [SerializeField] private LayerMask groundMask = ~0;
    [Tooltip("レイキャストの開始高さ(エリア中心のY座標からの相対値)。建物より確実に高くする")]
    [SerializeField] private float raycastStartHeight = 150f;
    [Tooltip("この高さ(エリア中心のY座標からの相対値)を超えた場所に着地する候補は無効にする(屋上を除外)")]
    [SerializeField] private float maxGroundHeightAboveCenter = 3f;
    [SerializeField] private WeaponPickup[] pickupPrefabs;
    [Tooltip("pickupPrefabsと同じ順番・要素数の抽選重み(大きいほど出やすい)。合わない/未設定なら等確率")]
    [SerializeField] private float[] pickupWeights;

    [Header("時間経過リスポーン")]
    [Tooltip("この間隔ごとに、現在の個数が上限未満なら1個だけ補充する")]
    [SerializeField] private float respawnIntervalSeconds = 8f;
    [Tooltip("試合中に同時に存在できる最大個数")]
    [SerializeField] private int maxActiveCount = 10;
    [Tooltip("この距離以内にプレイヤーがいる位置には出さない")]
    [SerializeField] private float minDistanceFromPlayers = 6f;
    [Tooltip("この距離以内に既存アイテムがある位置には出さない")]
    [SerializeField] private float minDistanceFromOtherPickups = 4f;
    [Tooltip("条件を満たす位置が見つかるまでの最大試行回数")]
    [SerializeField] private int maxPlacementAttempts = 30;

    private float _respawnTimer;

    private readonly List<NetworkObject> _spawned = new List<NetworkObject>();

    private void Awake()
    {
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
    }

    /// <summary>試合中のみ、サーバーが時間経過リスポーンを進める</summary>
    private void Update()
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
        if (MatchManager.Instance == null || MatchManager.Instance.Phase != MatchManager.GamePhase.Playing) return;

        _spawned.RemoveAll(n => n == null || !n.IsSpawned);

        _respawnTimer -= Time.deltaTime;
        if (_respawnTimer > 0f) return;
        _respawnTimer = respawnIntervalSeconds;

        ServerTryRespawnOne();
    }

    /// <summary>プレイヤー/既存アイテムから離れた位置に1個だけ補充する</summary>
    private void ServerTryRespawnOne()
    {
        if (pickupPrefabs == null || pickupPrefabs.Length == 0) return;
        if (_spawned.Count >= maxActiveCount) return;

        Vector3 center = areaCenter != null ? areaCenter.position : transform.position;

        for (int attempt = 0; attempt < maxPlacementAttempts; attempt++)
        {
            if (!TryGetRandomGroundPosition(center, out Vector3 pos)) continue;
            if (!IsPositionClear(pos)) continue;

            WeaponPickup prefab = PickRandomPrefab();
            if (prefab == null) continue;

            WeaponPickup instance = Instantiate(prefab, pos, Quaternion.identity);
            instance.NetworkObject.Spawn(true);
            _spawned.Add(instance.NetworkObject);
            Debug.Log($"[WeaponSpawner] 時間経過リスポーン: {prefab.name} を {pos} に配置しました(現在{_spawned.Count}/{maxActiveCount}個)");
            return;
        }

        Debug.Log($"[WeaponSpawner] リスポーン位置が{maxPlacementAttempts}回の試行でも見つかりませんでした" +
            "(プレイヤー/既存アイテムが密集している、または建物の屋上ばかり引いた可能性)");
    }

    /// <summary>pickupWeightsが有効なら重み付き抽選、そうでなければ等確率で選ぶ</summary>
    private WeaponPickup PickRandomPrefab()
    {
        if (pickupWeights == null || pickupWeights.Length != pickupPrefabs.Length)
        {
            return pickupPrefabs[Random.Range(0, pickupPrefabs.Length)];
        }

        float total = 0f;
        for (int i = 0; i < pickupWeights.Length; i++) total += Mathf.Max(0f, pickupWeights[i]);
        if (total <= 0f) return pickupPrefabs[Random.Range(0, pickupPrefabs.Length)];

        float roll = Random.Range(0f, total);
        float cumulative = 0f;
        for (int i = 0; i < pickupWeights.Length; i++)
        {
            cumulative += Mathf.Max(0f, pickupWeights[i]);
            if (roll <= cumulative) return pickupPrefabs[i];
        }
        return pickupPrefabs[pickupPrefabs.Length - 1];
    }

    /// <summary>指定した武器種類に対応する拾い物プレハブを返す(武器を手放す時の生成用)</summary>
    public WeaponPickup FindPickupPrefab(PlayerWeapon.WeaponType type)
    {
        if (pickupPrefabs == null) return null;
        foreach (var prefab in pickupPrefabs)
        {
            if (prefab != null && prefab.WeaponType == type) return prefab;
        }
        return null;
    }

    /// <summary>指定位置がプレイヤー/既存アイテムのどちらからも十分離れているかを判定する</summary>
    private bool IsPositionClear(Vector3 pos)
    {
        float playerSqr = minDistanceFromPlayers * minDistanceFromPlayers;
        foreach (var pc in PlayerController.ActiveInstances)
        {
            if (pc == null) continue;
            if ((pc.transform.position - pos).sqrMagnitude < playerSqr) return false;
        }

        float pickupSqr = minDistanceFromOtherPickups * minDistanceFromOtherPickups;
        foreach (var netObj in _spawned)
        {
            if (netObj == null) continue;
            if ((netObj.transform.position - pos).sqrMagnitude < pickupSqr) return false;
        }

        return true;
    }

    /// <summary>試合開始時にサーバーが呼ぶ。前回分を片付けてから一括配置する</summary>
    public void ServerSpawnAll()
    {
        ServerClearPrevious();
        _respawnTimer = respawnIntervalSeconds;

        if (pickupPrefabs == null || pickupPrefabs.Length == 0) return;

        Vector3 center = areaCenter != null ? areaCenter.position : transform.position;
        int placedCount = 0;

        for (int i = 0; i < spawnCount; i++)
        {
            WeaponPickup prefab = PickRandomPrefab();
            if (prefab == null) continue;

            // 重複配置を避けるため、リスポーンと同じIsPositionClearチェックを適用する
            Vector3 pos = Vector3.zero;
            bool found = false;
            for (int attempt = 0; attempt < maxPlacementAttempts; attempt++)
            {
                if (TryGetRandomGroundPosition(center, out pos) && IsPositionClear(pos))
                {
                    found = true;
                    break;
                }
            }
            if (!found)
            {
                Debug.LogWarning($"[WeaponSpawner] 初期配置{i}個目: 条件を満たす座標が{maxPlacementAttempts}回の試行でも" +
                    "見つかりませんでした(建物の屋上ばかり引いた/既存アイテムと近すぎた可能性)。この1個はスキップします");
                continue;
            }

            WeaponPickup instance = Instantiate(prefab, pos, Quaternion.identity);
            instance.NetworkObject.Spawn(true);
            _spawned.Add(instance.NetworkObject);
            placedCount++;
        }

        Debug.Log($"[WeaponSpawner] {placedCount}/{spawnCount}個の武器をランダム配置しました");
    }

    /// <summary>ランダムなXZから地面へレイキャストし、有効な配置座標(屋上や街の外を除く)が得られたらtrueを返す</summary>
    private bool TryGetRandomGroundPosition(Vector3 center, out Vector3 result)
    {
        // areaHalfExtentsのyはZ方向の半径として使う
        float minX = center.x - areaHalfExtents.x, maxX = center.x + areaHalfExtents.x;
        float minZ = center.z - areaHalfExtents.y, maxZ = center.z + areaHalfExtents.y;


        // 範囲が潰れた異常設定の場合は何も配置しない
        if (minX >= maxX || minZ >= maxZ)
        {
            result = default;
            return false;
        }

        float x = Random.Range(minX, maxX);
        float z = Random.Range(minZ, maxZ);

        Vector3 rayStart = new Vector3(x, center.y + raycastStartHeight, z);
        if (Physics.Raycast(rayStart, Vector3.down, out RaycastHit hit, raycastStartHeight * 2f, groundMask, QueryTriggerInteraction.Ignore))
        {
            if (hit.point.y - center.y <= maxGroundHeightAboveCenter)
            {
                result = new Vector3(x, hit.point.y + 0.5f, z);
                return true;
            }
        }

        result = default;
        return false;
    }

    private void ServerClearPrevious()
    {
        foreach (var netObj in _spawned)
        {
            if (netObj != null && netObj.IsSpawned) netObj.Despawn(true);
        }
        _spawned.Clear();
    }
}
