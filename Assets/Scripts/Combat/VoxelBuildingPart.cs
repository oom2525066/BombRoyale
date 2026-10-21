using System.Collections;
using System.Collections.Generic;
using System.IO;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 事前に.voxから焼き込んだボクセルデータ(Resources)を読み込み、建物スケールで表示・破壊するコンポーネント。
/// ネットワーク同期/当たり判定/デブリ/崩落を担当し、ボクセル操作はVoxelVolumeに任せる(DestructibleMeshの建物版)。
/// 実行時のボクセル化を避けて焼き込み済みデータを読むだけにし、色は256色パレット+ボクセルごとのUV.xで再現する。
/// 焼き込み形式: "BVXL"(4byte) + バージョン(int32) + サイズX/Y/Z(int32×3) + ボクセル数N(int32) + N個の(x,y,z,colorIndex)。
/// コライダーは見た目のサーフェスメッシュをそのままMeshColliderに使う。
/// 必須構成: NetworkObject / MeshFilter / MeshRenderer / MeshCollider / このスクリプト。
/// </summary>
[RequireComponent(typeof(NetworkObject))]
[RequireComponent(typeof(MeshFilter))]
[RequireComponent(typeof(MeshRenderer))]
[RequireComponent(typeof(MeshCollider))]
[GenerateSerializationForType(typeof(VoxelBuildingPart.ExplosionRecordV))]
public class VoxelBuildingPart : NetworkBehaviour
{
    /// <summary>同期する爆発イベント1件(NetworkListの要素型。ポインタを含まない値型としてmemcpyシリアライズする)</summary>
    public struct ExplosionRecordV : System.IEquatable<ExplosionRecordV>, INetworkSerializeByMemcpy
    {
        public Vector3 LocalCenter;
        public Vector3 LocalRadii;
        // falseなら構造崩壊判定(孤立/支え無しの塊の除去)を行わず、その場の数ボクセルだけを削る(Blasterの小さなチップ用)
        public bool AllowStructuralCollapse;

        public bool Equals(ExplosionRecordV other) =>
            LocalCenter.Equals(other.LocalCenter) && LocalRadii.Equals(other.LocalRadii) &&
            AllowStructuralCollapse == other.AllowStructuralCollapse;

        public override bool Equals(object obj) => obj is ExplosionRecordV other && Equals(other);
        public override int GetHashCode() => LocalCenter.GetHashCode() ^ LocalRadii.GetHashCode() ^ AllowStructuralCollapse.GetHashCode();
    }

    [Header("焼き込みデータ")]
    [Tooltip("Resources.Loadで読み込む.bytesアセット名(拡張子無し)")]
    [SerializeField] private string bakedDataResourceName = "WacdonaldsBuilding";
    [Tooltip("同じくResourcesから読み込むパレットテクスチャ名(拡張子無し)")]
    [SerializeField] private string paletteResourceName = "WacdonaldsBuilding_Palette";
    [Tooltip("焼き込みデータのボクセル1個ぶんの実世界サイズ(メートル)。焼き込みツール側の値と一致させる")]
    [SerializeField] private float voxelScale = 0.2f;

    [Tooltip("trueなら爆発を一切受け付けない(地面/道路タイルなど常に残したい床用)")]
    [SerializeField] private bool isIndestructible = false;
    [Tooltip("falseにすると、孤立した連結成分のうち最大の1個以外を削除する処理(FindIsolatedIslands)を行わない。複数の木をまとめたオブジェクト用")]
    [SerializeField] private bool removeDisconnectedFragments = true;

    [Header("耐久力")]
    // 爆発1回の距離減衰込みダメージと固定の耐久値を比較して壊れるかを決める(累積HPは持たない)
    [SerializeField] private byte voxelHitPoints = 5;
    [Tooltip("各ボクセルの耐久値をvoxelHitPoints±この値の範囲でばらつかせる。0だと滑らかな断面になる")]
    [SerializeField] private byte hitPointsVariance = 0;
    [SerializeField] private int maxDamagePerHit = 10;
    [SerializeField] private int minDamagePerHit = 1;

    [Header("破片(デブリ)")]
    [Tooltip("削れたボクセルのうち、デブリとして飛ばす割合(0〜1)")]
    [SerializeField, Range(0f, 1f)] private float debrisSpawnRatio = 0.55f;
    [SerializeField] private int maxDebrisPerExplosion = 22;
    [Tooltip("この個数のボクセルをまとめて1つのデブリ塊にする")]
    [SerializeField] private int debrisGroupSize = 3;
    [SerializeField] private float debrisJitterAmount = 0.03f;
    [SerializeField] private float debrisLifetimeSeconds = 8f;
    [SerializeField] private float debrisForce = 6.5f;

    [Header("支えを失った部分の崩壊")]
    [SerializeField] private float unsupportedCollapseDelaySeconds = 0.5f;

    private VoxelVolume _volume;
    private MeshFilter _meshFilter;
    private MeshCollider _meshCollider;
    private Material _material;
    private bool _volumeBuilt;
    /// <summary>焼き込み直後の無傷な状態から一度でも削られたか(セッションをまたぐ巻き戻しの要否判定用)</summary>
    private bool _hasLocalDamage;

    private readonly NetworkList<ExplosionRecordV> _explosions = new NetworkList<ExplosionRecordV>(
        default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    // 全インスタンスの静的登録(試合開始時の一括リセット用)
    private static readonly List<VoxelBuildingPart> _activeInstances = new List<VoxelBuildingPart>();

    private void Awake()
    {
        _meshFilter = GetComponent<MeshFilter>();
        _meshCollider = GetComponent<MeshCollider>();

        // 未接続(タイトル画面)でも街が見えるよう、ネットワーク状態に依存せずAwakeで無傷のメッシュを作っておく
        BuildVolumeIfNeeded();
        RebuildMainMesh();
    }

    public override void OnNetworkSpawn()
    {
        _activeInstances.Add(this);

        // シーン配置のNetworkObjectはShutdownしても破棄されないため、前セッションの破壊状態が残っていれば無傷へ戻してから再生する
        if (_hasLocalDamage)
        {
            Debug.Log($"[VoxelBuildingPart] {gameObject.name}: 前のセッションの破壊状態が残っていたため、無傷へ巻き戻してからスポーンします");
            ResetLocalVolume();
        }

        // NetworkListの中身もShutdownをまたいで残るため、新規ホストは前回の被弾記録を捨てる(購読前なので自分にはイベントが飛ばない)
        if (IsServer && _explosions.Count > 0)
        {
            Debug.Log($"[VoxelBuildingPart] {gameObject.name}: 前のセッションの被弾記録{_explosions.Count}件を破棄します");
            _explosions.Clear();
        }

        // 途中参加クライアントは、現在の被弾状態を無傷の状態に対して再生して追いつく
        _explosions.OnListChanged += HandleExplosionsChanged;

        bool anyChange = false;
        foreach (var record in _explosions)
        {
            anyChange |= ApplyExplosionLocally(record, spawnDebris: false);
        }
        if (anyChange) RebuildMainMesh();
    }

    /// <summary>MatchManager.StartMatch()がサーバーのみで呼ぶ。全建物の被弾記録をClearして全クライアントを無傷へ巻き戻す</summary>
    public static void ServerResetAll()
    {
        foreach (var part in _activeInstances)
        {
            if (part != null) part.ServerResetDamage();
        }
    }

    private void ServerResetDamage()
    {
        if (!IsServer) return;
        if (_explosions.Count == 0) return; // 無傷ならクリアする必要も無い
        _explosions.Clear();
    }

    public override void OnNetworkDespawn()
    {
        _activeInstances.Remove(this);
        _explosions.OnListChanged -= HandleExplosionsChanged;

        // 切断後のタイトル画面で無傷の街が映るよう巻き戻す(終了処理中の例外は握りつぶす)
        if (_hasLocalDamage)
        {
            try
            {
                ResetLocalVolume();
            }
            catch (System.Exception e)
            {
                Debug.LogWarning($"[VoxelBuildingPart] {gameObject.name}: 切断時の巻き戻しに失敗しました: {e.Message}");
            }
        }
    }

    /// <summary>ローカルのボクセル/メッシュ/コライダーだけを無傷の状態から作り直す(ネットワーク状態には触れない)</summary>
    private void ResetLocalVolume()
    {
        // 予約済みの崩落コルーチンが巻き戻し後に発火しないよう止める
        StopAllCoroutines();
        _volumeBuilt = false;
        BuildVolumeIfNeeded();
        RebuildMainMesh();
        _hasLocalDamage = false;
    }

    /// <summary>焼き込み済みバイナリ(BVXL形式)を読み込んでVoxelVolumeとパレットマテリアルを復元する</summary>
    private void BuildVolumeIfNeeded()
    {
        if (_volumeBuilt) return;
        _volumeBuilt = true;

        // 再構築時に前回のMaterialインスタンスがリークしないよう破棄する
        if (_material != null) Destroy(_material);

        TextAsset baked = Resources.Load<TextAsset>(bakedDataResourceName);
        if (baked == null)
        {
            Debug.LogError($"[VoxelBuildingPart] {gameObject.name}: 焼き込みデータ'{bakedDataResourceName}'が" +
                "Resourcesに見つかりません");
            return;
        }

        using var stream = new MemoryStream(baked.bytes);
        using var reader = new BinaryReader(stream);

        byte[] magic = reader.ReadBytes(4);
        if (magic[0] != 'B' || magic[1] != 'V' || magic[2] != 'X' || magic[3] != 'L')
        {
            Debug.LogError($"[VoxelBuildingPart] {gameObject.name}: 焼き込みデータの形式が不正です");
            return;
        }
        int formatVersion = reader.ReadInt32();

        int sizeX = reader.ReadInt32();
        int sizeY = reader.ReadInt32();
        int sizeZ = reader.ReadInt32();
        int voxelCount = reader.ReadInt32();

        var resolution = new Vector3Int(sizeX, sizeY, sizeZ);
        var boundsSize = new Vector3(sizeX * voxelScale, sizeY * voxelScale, sizeZ * voxelScale);
        var boundsCenter = boundsSize * 0.5f;
        // materialCount=256はパレット全色のインデックス範囲(サブメッシュは常に1つ)
        _volume = new VoxelVolume(resolution, boundsSize, boundsCenter, voxelHitPoints, materialCount: 256,
            hitPointsVariance: hitPointsVariance);

        // コンストラクタは全域を実在で初期化するため、いったん空にしてから焼き込み座標だけを実在させる
        for (int x = 0; x < sizeX; x++)
            for (int y = 0; y < sizeY; y++)
                for (int z = 0; z < sizeZ; z++)
                    _volume.Set(x, y, z, false);

        // フォーマットv2以降は座標が2byte
        for (int i = 0; i < voxelCount; i++)
        {
            int vx = formatVersion >= 2 ? reader.ReadUInt16() : reader.ReadByte();
            int vy = formatVersion >= 2 ? reader.ReadUInt16() : reader.ReadByte();
            int vz = formatVersion >= 2 ? reader.ReadUInt16() : reader.ReadByte();
            byte colorIndex = reader.ReadByte();
            _volume.Set(vx, vy, vz, true);
            _volume.SetMaterialIndex(vx, vy, vz, colorIndex);
        }

        Texture2D palette = Resources.Load<Texture2D>(paletteResourceName);
        if (palette == null)
        {
            Debug.LogError($"[VoxelBuildingPart] {gameObject.name}: パレットテクスチャ" +
                $"'{paletteResourceName}'がResourcesに見つかりません");
        }
        Shader shader = Shader.Find("Universal Render Pipeline/Lit");
        _material = new Material(shader != null ? shader : Shader.Find("Standard"));
        // URP/Litは_BaseMapを参照するため明示的に設定する(Standardフォールバック用にmainTextureも設定)
        if (palette != null)
        {
            _material.SetTexture("_BaseMap", palette);
            _material.mainTexture = palette;
        }
        GetComponent<MeshRenderer>().material = _material;

        Debug.Log($"[VoxelBuildingPart] {gameObject.name}: 焼き込みデータ読み込み完了 " +
            $"size=({sizeX},{sizeY},{sizeZ}) voxelCount={voxelCount}");
    }

    /// <summary>ワールド座標の爆発をローカルの楕円体に変換して記録し、全クライアントへ同期する(サーバー専用)</summary>
    public void ServerApplyExplosion(Vector3 explosionCenterWorld, float explosionRadius, bool allowStructuralCollapse = true)
    {
        if (!IsServer || _volume == null || isIndestructible) return;

        Vector3 localCenter = transform.InverseTransformPoint(explosionCenterWorld);
        Vector3 scale = transform.lossyScale;
        Vector3 localRadii = new Vector3(
            scale.x > 0.0001f ? explosionRadius / scale.x : explosionRadius,
            scale.y > 0.0001f ? explosionRadius / scale.y : explosionRadius,
            scale.z > 0.0001f ? explosionRadius / scale.z : explosionRadius);

        _explosions.Add(new ExplosionRecordV { LocalCenter = localCenter, LocalRadii = localRadii, AllowStructuralCollapse = allowStructuralCollapse });

        float distanceToThisObject = Vector3.Distance(explosionCenterWorld, transform.position);
        Debug.Log($"[VoxelBuildingPart] {gameObject.name}(id={GetInstanceID()}): ServerApplyExplosion " +
            $"explosionWorldPos={explosionCenterWorld} thisObjectWorldPos={transform.position} " +
            $"distanceToThisObject={distanceToThisObject} radius={explosionRadius} " +
            $"localCenter={localCenter} localRadii={localRadii} lossyScale={scale} " +
            $"gridOrigin={_volume.Origin} gridSize=({_volume.SizeX},{_volume.SizeY},{_volume.SizeZ}) voxelSize={_volume.VoxelSize} " +
            $"allowStructuralCollapse={allowStructuralCollapse}");
    }

    private void HandleExplosionsChanged(NetworkListEvent<ExplosionRecordV> ev)
    {
        // Clear(試合開始時のリセット)は演出なしで無傷へ作り直す
        if (ev.Type == NetworkListEvent<ExplosionRecordV>.EventType.Clear)
        {
            ResetLocalVolume();
            return;
        }

        if (ev.Type != NetworkListEvent<ExplosionRecordV>.EventType.Add) return;
        if (ApplyExplosionLocally(ev.Value, spawnDebris: true)) RebuildMainMesh();
    }

    /// <summary>爆発1件をボリュームに適用し、削れた分のデブリ化と(許可時のみ)構造崩壊を行う。変化があればtrue</summary>
    private bool ApplyExplosionLocally(ExplosionRecordV record, bool spawnDebris)
    {
        _hasLocalDamage = true;
        List<Vector3Int> newlyRemoved = _volume.CarveEllipsoidThreshold(record.LocalCenter, record.LocalRadii, maxDamagePerHit, minDamagePerHit);

        // 実際に壊れたボクセル群の実寸範囲(調査用ログ)
        if (newlyRemoved.Count > 0)
        {
            Vector3 minLocal = _volume.VoxelCenterLocal(newlyRemoved[0].x, newlyRemoved[0].y, newlyRemoved[0].z);
            Vector3 maxLocal = minLocal;
            foreach (var v in newlyRemoved)
            {
                Vector3 c = _volume.VoxelCenterLocal(v.x, v.y, v.z);
                minLocal = Vector3.Min(minLocal, c);
                maxLocal = Vector3.Max(maxLocal, c);
            }
            Vector3 worldSpanMeters = Vector3.Scale(maxLocal - minLocal, transform.lossyScale);
            Vector3 removedWorldCenter = transform.TransformPoint((minLocal + maxLocal) * 0.5f);
            Debug.Log($"[VoxelBuildingPart] {gameObject.name}: 今回実際に壊れたボクセル群の実寸範囲 " +
                $"worldSpanMeters={worldSpanMeters} removedWorldCenter={removedWorldCenter} count={newlyRemoved.Count}");
        }

        Debug.Log($"[VoxelBuildingPart] {gameObject.name}: ApplyExplosionLocally localCenter={record.LocalCenter} " +
            $"localRadii={record.LocalRadii} newlyRemoved={newlyRemoved.Count} allowStructuralCollapse={record.AllowStructuralCollapse}");
        if (newlyRemoved.Count == 0) return false;

        // デブリは実際の爆発中心から離れる向きに飛ばす
        Vector3 explosionCenterWorld = transform.TransformPoint(record.LocalCenter);

        if (spawnDebris) SpawnDebrisGroups(newlyRemoved, explosionCenterWorld);

        // 構造崩壊判定は本物の爆発物(AllowStructuralCollapse)の時だけ行う
        if (record.AllowStructuralCollapse)
        {
            // 孤立片の除去と、浮いた部分の崩落は別々に判定する
            List<List<Vector3Int>> islands = removeDisconnectedFragments ? _volume.FindIsolatedIslands() : null;
            List<List<Vector3Int>> unsupported = spawnDebris ? _volume.FindUnsupportedIslands() : null;

            int isolatedTotal = 0;
            if (islands != null) foreach (var isl in islands) isolatedTotal += isl.Count;
            int unsupportedTotal = 0;
            if (unsupported != null) foreach (var isl in unsupported) unsupportedTotal += isl.Count;
            if (isolatedTotal > 0 || unsupportedTotal > 0)
            {
                Debug.Log($"[VoxelBuildingPart] {gameObject.name}: 構造崩壊で追加に消える分 " +
                    $"isolatedIslands={(islands?.Count ?? 0)}(voxels={isolatedTotal}) " +
                    $"unsupportedIslands={(unsupported?.Count ?? 0)}(voxels={unsupportedTotal}) " +
                    $"※直接のnewlyRemoved={newlyRemoved.Count}とは別枠");
            }

            if (islands != null)
            {
                foreach (var island in islands)
                {
                    if (spawnDebris) SpawnDebrisGroups(island, explosionCenterWorld);
                    _volume.RemoveVoxels(island);
                }
            }

            if (unsupported != null)
            {
                foreach (var island in unsupported)
                {
                    StartCoroutine(CollapseUnsupportedAfterDelay(island, explosionCenterWorld));
                }
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
        Mesh newMesh = _volume.BuildMeshWithPaletteUV();

        _meshFilter.sharedMesh = newMesh;
        _meshCollider.sharedMesh = newMesh;

        if (oldMesh != null) Destroy(oldMesh);
    }

    /// <summary>削れたボクセルからdebrisSpawnRatio分をランダムに選び、debrisGroupSize個ずつの塊にしてデブリ化する</summary>
    private void SpawnDebrisGroups(List<Vector3Int> voxels, Vector3 explosionCenterWorld)
    {
        if (voxels.Count == 0) return;

        var shuffled = new List<Vector3Int>(voxels);
        for (int i = shuffled.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        int spawnTargetCount = Mathf.Clamp(Mathf.CeilToInt(shuffled.Count * debrisSpawnRatio), 1, shuffled.Count);
        var selected = shuffled.GetRange(0, spawnTargetCount);

        int groupSize = Mathf.Max(1, debrisGroupSize);
        int spawnedCount = 0;
        for (int start = 0; start < selected.Count; start += groupSize)
        {
            if (spawnedCount >= maxDebrisPerExplosion) break;
            int count = Mathf.Min(groupSize, selected.Count - start);
            SpawnSingleDebris(selected.GetRange(start, count), explosionCenterWorld);
            spawnedCount++;
        }
    }

    /// <summary>1グループ分のボクセルを、見た目だけのデブリ(Colliderなし)として実行時に組み立てて飛ばす</summary>
    private void SpawnSingleDebris(List<Vector3Int> voxels, Vector3 explosionCenterWorld)
    {
        Mesh debrisMesh = _volume.BuildJaggedMeshForVoxels(voxels, debrisJitterAmount);
        if (debrisMesh.vertexCount == 0) return;

        Vector3 localCenter = debrisMesh.bounds.center;
        Vector3[] verts = debrisMesh.vertices;
        for (int i = 0; i < verts.Length; i++) verts[i] -= localCenter;
        debrisMesh.vertices = verts;
        debrisMesh.RecalculateBounds();

        Vector3 debrisWorldCenter = transform.TransformPoint(localCenter);

        var go = new GameObject("VoxelBuildingDebris");
        go.transform.SetPositionAndRotation(debrisWorldCenter, transform.rotation);
        // 建物の子にしないため、親のスケールを明示的に引き継ぐ
        go.transform.localScale = transform.lossyScale;

        go.AddComponent<MeshFilter>().sharedMesh = debrisMesh;
        var renderer = go.AddComponent<MeshRenderer>();
        if (_material != null) renderer.sharedMaterial = _material;

        int debrisLayer = LayerMask.NameToLayer("Debris");
        if (debrisLayer >= 0) go.layer = debrisLayer;

        // 重力による自由落下と、爆発中心から離れる向きの一回だけの力積のみを与える
        var rb = go.AddComponent<Rigidbody>();
        Vector3 outward = debrisWorldCenter - explosionCenterWorld;
        if (outward.sqrMagnitude < 0.0001f) outward = Random.onUnitSphere;
        rb.AddForce(outward.normalized * debrisForce, ForceMode.VelocityChange);

        Destroy(go, debrisLifetimeSeconds);
    }
}
