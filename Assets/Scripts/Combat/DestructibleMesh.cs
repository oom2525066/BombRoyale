using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 爆発でボクセル単位に削れる遮蔽物。ボクセル化/メッシュ生成はVoxelVolumeに任せ、
/// このクラスはネットワーク同期とMeshFilter/MeshCollider/デブリ生成だけを担当する。
/// 爆発イベント(ローカル座標の中心+半径)をNetworkListで全クライアントへ配り、同じ順序で適用して状態を揃える。
/// 必須構成: NetworkObject / MeshFilter / MeshRenderer / MeshCollider / このスクリプト。
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
[RequireComponent(typeof(MeshCollider))]
[GenerateSerializationForType(typeof(ExplosionRecord))]
public class DestructibleMesh : NetworkBehaviour
{
    /// <summary>同期する爆発イベント1件(NetworkListの要素型。ポインタを含まない値型としてmemcpyシリアライズする)</summary>
    public struct ExplosionRecord : System.IEquatable<ExplosionRecord>, INetworkSerializeByMemcpy
    {
        public Vector3 LocalCenter;
        // 非一様スケールでも球状の爆発になるよう、軸ごとの半径(楕円体)で持つ
        public Vector3 LocalRadii;

        public bool Equals(ExplosionRecord other) =>
            LocalCenter.Equals(other.LocalCenter) && LocalRadii.Equals(other.LocalRadii);

        public override bool Equals(object obj) => obj is ExplosionRecord other && Equals(other);
        public override int GetHashCode() => LocalCenter.GetHashCode() ^ LocalRadii.GetHashCode();
    }

    [Header("ボクセル化設定")]
    [Tooltip("設定すると、このメッシュの外形をボクセル化して使う。未設定ならlocalScaleの直方体を使う")]
    [SerializeField] private Mesh sourceMeshOverride;
    [Tooltip("sourceMeshOverride使用時の固定解像度(X,Y,Z)")]
    [SerializeField] private Vector3Int voxelResolution = new Vector3Int(10, 6, 6);
    [Tooltip("sourceMeshOverride未設定時の、1ボクセルの目標サイズ(メートル)")]
    [SerializeField] private float targetVoxelSize = 0.35f;
    [Tooltip("自動計算する解像度の1軸あたりの上限")]
    [SerializeField] private int maxVoxelsPerAxis = 20;
    [Tooltip("自動計算する合計ボクセル数の上限")]
    [SerializeField] private int maxTotalVoxels = 900;
    [SerializeField] private Material material;

    [Header("耐久力")]
    [Tooltip("ボクセルの初期耐久値")]
    [SerializeField] private byte voxelHitPoints = 4;
    [Tooltip("爆発の中心で1回に減る耐久値")]
    [SerializeField] private int maxDamagePerHit = 4;
    [Tooltip("爆発の境界で1回に減る耐久値")]
    [SerializeField] private int minDamagePerHit = 1;

    [Header("破片(デブリ)")]
    [Tooltip("削れたボクセルのうち、物理デブリとして飛ばす割合(0〜1)")]
    [SerializeField, Range(0f, 1f)] private float debrisSpawnRatio = 0.35f;
    [Tooltip("デブリ1個あたりのボクセル数")]
    [SerializeField] private int debrisGroupSize = 1;
    [Tooltip("1回の爆発で生成するデブリの最大個数")]
    [SerializeField] private int maxDebrisPerExplosion = 10;
    [Tooltip("デブリの頂点をランダムにずらす量(メートル)")]
    [SerializeField] private float debrisJitterAmount = 0.1f;
    [Tooltip("デブリごとのランダムな非均等スケールの振れ幅")]
    [SerializeField] private float debrisRandomScaleVariance = 0.35f;
    [SerializeField] private float debrisLifetimeSeconds = 6f;
    [SerializeField] private float debrisForce = 4f;

    [Header("支えを失った部分の崩壊")]
    [Tooltip("地面への接続を失ってから実際に崩れ落ちるまでの遅延(秒)")]
    [SerializeField] private float unsupportedCollapseDelaySeconds = 0.5f;

    private VoxelVolume _volume;
    private MeshFilter _meshFilter;
    private MeshCollider _meshCollider;
    private bool _volumeBuilt;

    // 爆発イベント列(サーバー権威)。ボクセル配列そのものではなく再現に必要な最小限の情報だけを同期する
    private readonly NetworkList<ExplosionRecord> _explosions = new NetworkList<ExplosionRecord>(
        default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>Cover本来の大きさ。Transform同期はSpawn時点で未反映のことがあるため、NetworkVariableで確実に配る</summary>
    private readonly NetworkVariable<Vector3> _serverAssignedScale = new NetworkVariable<Vector3>(
        Vector3.one, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>CoverSpawnerがInstantiate直後・Spawn前に呼んで元の大きさを設定する</summary>
    public void ServerSetInitialScale(Vector3 scale)
    {
        _serverAssignedScale.Value = scale;
    }

    private void Awake()
    {
        _meshFilter = GetComponent<MeshFilter>();
        _meshCollider = GetComponent<MeshCollider>();
    }

    public override void OnNetworkSpawn()
    {
        BuildVolumeIfNeeded();
        RebuildMainMesh();

        _explosions.OnListChanged += HandleExplosionsChanged;

        // 遅延接続クライアント向けに、保存済みの全爆発を演出なしで再生して現在の状態へ追いつく
        bool anyChange = false;
        foreach (var record in _explosions)
        {
            anyChange |= ApplyExplosionLocally(record, spawnDebris: false);
        }
        if (anyChange) RebuildMainMesh();
    }

    public override void OnNetworkDespawn()
    {
        _explosions.OnListChanged -= HandleExplosionsChanged;
    }

    /// <summary>同期された元サイズからVoxelVolumeを一度だけ構築する。メッシュ側が実サイズを持つのでlocalScaleは1にする</summary>
    private void BuildVolumeIfNeeded()
    {
        if (_volumeBuilt) return;
        _volumeBuilt = true;

        if (sourceMeshOverride != null)
        {
            _volume = VoxelVolume.BuildFromMesh(sourceMeshOverride, voxelResolution, voxelHitPoints);
            return;
        }

        Vector3 originalScale = _serverAssignedScale.Value;
        transform.localScale = Vector3.one;

        Vector3Int resolution = ComputeAutoResolution(originalScale);
        _volume = new VoxelVolume(resolution, originalScale, Vector3.zero, voxelHitPoints);

        Debug.Log($"[DestructibleMesh] {gameObject.name}: 自動ボクセル化 scale={originalScale} " +
            $"resolution={resolution.x}x{resolution.y}x{resolution.z}={resolution.x * resolution.y * resolution.z}個");
    }

    /// <summary>targetVoxelSizeを目安に、合計maxTotalVoxels以内に収まるX/Y/Z解像度を決める</summary>
    private Vector3Int ComputeAutoResolution(Vector3 scale)
    {
        int gx = Mathf.Clamp(Mathf.RoundToInt(scale.x / targetVoxelSize), 1, maxVoxelsPerAxis);
        int gy = Mathf.Clamp(Mathf.RoundToInt(scale.y / targetVoxelSize), 1, maxVoxelsPerAxis);
        int gz = Mathf.Clamp(Mathf.RoundToInt(scale.z / targetVoxelSize), 1, maxVoxelsPerAxis);

        int guard = 0;
        while (gx * gy * gz > maxTotalVoxels && guard++ < 200)
        {
            if (gx >= gy && gx >= gz) gx = Mathf.Max(1, gx - 1);
            else if (gy >= gx && gy >= gz) gy = Mathf.Max(1, gy - 1);
            else gz = Mathf.Max(1, gz - 1);
        }

        return new Vector3Int(gx, gy, gz);
    }

    /// <summary>爆発のワールド中心/半径をローカル座標系の楕円体に変換して記録し、全クライアントへ同期する(サーバー専用)</summary>
    public void ServerApplyExplosion(Vector3 explosionCenterWorld, float explosionRadius)
    {
        if (!IsServer || _volume == null) return;

        Vector3 localCenter = transform.InverseTransformPoint(explosionCenterWorld);
        // 軸ごとのスケールで半径を変換し、非一様スケールでも爆発範囲が歪まないようにする
        Vector3 scale = transform.lossyScale;
        Vector3 localRadii = new Vector3(
            scale.x > 0.0001f ? explosionRadius / scale.x : explosionRadius,
            scale.y > 0.0001f ? explosionRadius / scale.y : explosionRadius,
            scale.z > 0.0001f ? explosionRadius / scale.z : explosionRadius);

        _explosions.Add(new ExplosionRecord { LocalCenter = localCenter, LocalRadii = localRadii });
    }

    private void HandleExplosionsChanged(NetworkListEvent<ExplosionRecord> ev)
    {
        if (ev.Type != NetworkListEvent<ExplosionRecord>.EventType.Add) return;

        if (ApplyExplosionLocally(ev.Value, spawnDebris: true)) RebuildMainMesh();
    }

    /// <summary>
    /// 爆発1件をVoxelVolumeに適用し、削れた分と孤立した塊をデブリ化、地面から浮いた塊は遅れて崩壊させる。
    /// spawnDebris=falseは状態の復元のみ(デブリ/崩壊なし)。変化があればtrueを返す。
    /// </summary>
    private bool ApplyExplosionLocally(ExplosionRecord record, bool spawnDebris)
    {
        List<Vector3Int> newlyRemoved = _volume.CarveEllipsoid(record.LocalCenter, record.LocalRadii, maxDamagePerHit, minDamagePerHit);
        if (newlyRemoved.Count == 0) return false;

        // デブリは実際の爆発中心から離れる向きに飛ばす
        Vector3 explosionCenterWorld = transform.TransformPoint(record.LocalCenter);

        List<List<Vector3Int>> islands = _volume.FindIsolatedIslands();
        List<List<Vector3Int>> unsupported = spawnDebris ? _volume.FindUnsupportedIslands() : null;
        Debug.Log($"[DestructibleMesh] {gameObject.name}: 削り適用 localCenter={record.LocalCenter} " +
            $"localRadii={record.LocalRadii} 新規削除={newlyRemoved.Count}個 孤立破片={islands.Count}個 " +
            $"浮遊破片={unsupported?.Count ?? 0}個 spawnDebris={spawnDebris}");

        if (spawnDebris) SpawnDebrisGroups(newlyRemoved, explosionCenterWorld);

        foreach (var island in islands)
        {
            if (spawnDebris) SpawnDebrisGroups(island, explosionCenterWorld);
            _volume.RemoveVoxels(island);
        }

        if (unsupported != null)
        {
            foreach (var island in unsupported)
            {
                StartCoroutine(CollapseUnsupportedAfterDelay(island, explosionCenterWorld));
            }
        }

        return true;
    }

    /// <summary>支えを失ったボクセル塊を遅延後に崩す。遅延中に別の爆発で消えた分は除外する</summary>
    private IEnumerator CollapseUnsupportedAfterDelay(List<Vector3Int> voxels, Vector3 explosionCenterWorld)
    {
        yield return new WaitForSeconds(unsupportedCollapseDelaySeconds);

        var stillPresent = new List<Vector3Int>();
        foreach (var v in voxels)
        {
            if (_volume.Get(v.x, v.y, v.z)) stillPresent.Add(v);
        }
        if (stillPresent.Count == 0) yield break;

        SpawnDebrisGroups(stillPresent, explosionCenterWorld);
        _volume.RemoveVoxels(stillPresent);
        RebuildMainMesh();
    }

    private void RebuildMainMesh()
    {
        Mesh oldMesh = _meshFilter.sharedMesh;
        Mesh newMesh = _volume.BuildMesh();

        _meshFilter.sharedMesh = newMesh;
        _meshCollider.sharedMesh = newMesh;

        if (material != null) GetComponent<MeshRenderer>().sharedMaterial = material;

        // 実行時生成メッシュは自動GCされないため、差し替えた古い方は明示的に破棄する
        if (oldMesh != null) Destroy(oldMesh);
    }

    /// <summary>削れたボクセルからdebrisSpawnRatio分をランダムに選び、小グループに分けてデブリ化する</summary>
    private void SpawnDebrisGroups(List<Vector3Int> voxels, Vector3 explosionCenterWorld)
    {
        if (voxels.Count == 0) return;

        var shuffled = new List<Vector3Int>(voxels);
        for (int i = shuffled.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        // 最低1個は出す
        int spawnTargetCount = Mathf.Clamp(Mathf.CeilToInt(shuffled.Count * debrisSpawnRatio), 1, shuffled.Count);
        var selected = shuffled.GetRange(0, spawnTargetCount);

        int groupSize = Mathf.Max(1, debrisGroupSize);
        int spawnedCount = 0;
        for (int start = 0; start < selected.Count && spawnedCount < maxDebrisPerExplosion; start += groupSize)
        {
            int count = Mathf.Min(groupSize, selected.Count - start);
            SpawnSingleDebris(selected.GetRange(start, count), explosionCenterWorld);
            spawnedCount++;
        }
    }

    /// <summary>1グループ分のボクセルを、実行時に組み立てた見た目だけのデブリ(Colliderなし)として飛ばす</summary>
    private void SpawnSingleDebris(List<Vector3Int> voxels, Vector3 explosionCenterWorld)
    {
        Mesh debrisMesh = _volume.BuildJaggedMeshForVoxels(voxels, debrisJitterAmount);
        if (debrisMesh.vertexCount == 0) return;

        // メッシュ中心を原点に再配置し、ランダムスケールが破片の中心を軸に効くようにする
        Vector3 localCenter = debrisMesh.bounds.center;
        Vector3[] verts = debrisMesh.vertices;
        for (int i = 0; i < verts.Length; i++) verts[i] -= localCenter;
        debrisMesh.vertices = verts;
        debrisMesh.RecalculateBounds();

        Vector3 debrisWorldCenter = transform.TransformPoint(localCenter);

        var go = new GameObject("DestructibleMeshDebris");
        int debrisLayer = LayerMask.NameToLayer("Debris");
        if (debrisLayer >= 0) go.layer = debrisLayer;
        go.transform.SetPositionAndRotation(debrisWorldCenter, transform.rotation);

        // 破片ごとにランダムな非均等スケールを掛けて形にバリエーションを持たせる
        Vector3 randomScale = new Vector3(
            1f + Random.Range(-debrisRandomScaleVariance, debrisRandomScaleVariance),
            1f + Random.Range(-debrisRandomScaleVariance, debrisRandomScaleVariance),
            1f + Random.Range(-debrisRandomScaleVariance, debrisRandomScaleVariance));
        go.transform.localScale = Vector3.Scale(transform.localScale, randomScale);

        go.AddComponent<MeshFilter>().sharedMesh = debrisMesh;
        var renderer = go.AddComponent<MeshRenderer>();
        if (material != null) renderer.sharedMaterial = material;

        // 重力による自由落下と、爆発中心から離れる向きの一回だけの力積のみを与える
        var rb = go.AddComponent<Rigidbody>();
        Vector3 outward = debrisWorldCenter - explosionCenterWorld;
        if (outward.sqrMagnitude < 0.0001f) outward = Random.onUnitSphere;
        rb.AddForce(outward.normalized * debrisForce, ForceMode.VelocityChange);

        Destroy(go, debrisLifetimeSeconds);
    }
}
