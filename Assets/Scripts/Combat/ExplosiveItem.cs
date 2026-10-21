using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 投擲/発射される爆発物の本体。Bomb・RocketLauncher等の爆発物系武器で共通に使い、種類の違いはPrefab側の値で表現する。
/// 起爆するとダメージ・ノックバック・環境破壊を適用し、近くの未起爆Explosiveを連鎖的に誘爆させる。
/// 起爆判定・ダメージ・誘爆はサーバー権威で、クライアントは演出(DetonateClientRpc)だけを受け取る。
/// 必須構成: NetworkObject / Rigidbody / Collider / このスクリプト。
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class ExplosiveItem : NetworkBehaviour
{
    [SerializeField] private float fuseSeconds = 3f;
    [SerializeField] private float damage = 60f;
    [SerializeField] private float damageRadius = 5f;
    [Tooltip("建物の破壊表現に渡す威力。damageRadius(範囲)とは独立に、範囲をどれだけ深く壊すかを決める")]
    [SerializeField] private float power = 1f;
    [Tooltip("爆心地から範囲端までのダメージ減衰の下限(0〜1)。端でもこの割合のダメージを保証する")]
    [SerializeField] private float minDamageFalloff = 0.35f;
    [Tooltip("この半径内にある他の未起爆Explosiveも巻き込んで誘爆させる")]
    [SerializeField] private float chainDetonateRadius = 4f;
    [Tooltip("falseにすると誘爆システムに一切参加しない(周囲を誘爆させず、他の誘爆にも巻き込まれない)")]
    [SerializeField] private bool participatesInChainDetonation = true;
    [SerializeField] private LayerMask damageMask = ~0;
    [SerializeField] private LayerMask explosiveMask = ~0;
    [Tooltip("falseにすると建物/遮蔽物/ボクセルへの破壊を一切行わない")]
    [SerializeField] private bool damagesEnvironment = true;
    [Tooltip("起爆演出のパーティクルに使うマテリアル(Unlit推奨)")]
    [SerializeField] private Material explosionParticleMaterial;
    [Tooltip("接地中に毎秒どれだけ速度を落とすか(転がり続けるのを防ぐ)")]
    [SerializeField] private float groundRollingResistancePerSecond = 3.5f;
    [Tooltip("trueならfuse切れを待たず、最初に何かへ衝突した瞬間に起爆する(RocketLauncher用)")]
    [SerializeField] private bool detonateOnImpact = false;
    [Header("銃撃での誘爆(グレネード/ロケット用)")]
    [Tooltip("trueなら、Blaster等の命中回数がgunfireHitsToDetonateに達した時点で起爆する")]
    [SerializeField] private bool destructibleByGunfire = false;
    [Tooltip("destructibleByGunfire=trueの場合、何発被弾したら起爆するか")]
    [SerializeField] private int gunfireHitsToDetonate = 3;
    [Tooltip("被弾した瞬間にRigidbodyへ加える力積の大きさ")]
    [SerializeField] private float gunfireImpulseForce = 5f;
    [Header("見た目(実アセット)")]
    [Tooltip("設定するとAwake()で子として実体化し、爆発物の見た目に使う。未設定なら当たり判定のみ")]
    [SerializeField] private GameObject visualPrefab;
    [Tooltip("visualPrefabの向きが飛翔方向(ローカルZ+)と合わない場合の補正回転")]
    [SerializeField] private Vector3 visualLocalEulerAngles = Vector3.zero;
    [SerializeField] private Vector3 visualLocalScale = Vector3.one;
    [Header("演出")]
    [Tooltip("起爆音。設定していればDetonateClientRpcで全クライアントに再生する")]
    [SerializeField] private AudioClip detonateClip;
    [Tooltip("起爆音の音量(1を超える値も有効)")]
    [SerializeField] private float detonateVolume = 1.25f;
    [Tooltip("この距離以内にいるローカルプレイヤーのカメラを揺らす")]
    [SerializeField] private float cameraShakeRadius = 24f;
    [SerializeField] private float cameraShakeMagnitude = 0.25f;
    [SerializeField] private float cameraShakeDuration = 0.35f;
    [Header("ノックバック/自爆")]
    [Tooltip("damage 1あたりのノックバック力(水平方向)。ノックバック力=damage×この値×距離減衰。0以下でノックバック無し")]
    [SerializeField] private float knockbackForcePerDamage = 0.45f;
    [Tooltip("水平方向の吹き飛ばし量に対する上向き成分の比率")]
    [SerializeField] private float knockbackUpwardBias = 0.7f;
    [Tooltip("ノックバックが効く範囲をdamageRadiusの何倍まで広げるか(1以上)")]
    [SerializeField] private float knockbackRadiusMultiplier = 1.3f;
    [Tooltip("自分の爆発物で自分がダメージを受けた場合のダメージ倍率(1未満で軽減)")]
    [SerializeField] private float selfDamageMultiplier = 0.5f;
    [Header("先読み初期化(初回破壊時のラグ対策)")]
    [Tooltip("飛翔中にこの半径内の建物を先読み初期化して、初回被弾時の重い処理を前倒しする(ローカルのみの処理)")]
    [SerializeField] private float prewarmRadius = 15f;
    [SerializeField] private float prewarmIntervalSeconds = 0.25f;

    [Header("データ駆動構成(任意)")]
    [Tooltip("設定すると、Awake()で見た目/爆発/ノックバック系の値をこのアセットで上書きし、起爆時にdetonationEffectsを順に実行する")]
    [SerializeField] private BombDefinition definition;

    /// <summary>WeaponPickupが、拾い物の中身が誘爆に参加する武器かを判定するために参照する</summary>
    public bool ParticipatesInChainDetonation => participatesInChainDetonation;

    /// <summary>建物向けRaycastに使う、爆心地を中心とした周囲26方向</summary>
    private static readonly Vector3[] SurroundingRayDirections = BuildSurroundingRayDirections();

    private static Vector3[] BuildSurroundingRayDirections()
    {
        var list = new List<Vector3>(26);
        for (int x = -1; x <= 1; x++)
        {
            for (int y = -1; y <= 1; y++)
            {
                for (int z = -1; z <= 1; z++)
                {
                    if (x == 0 && y == 0 && z == 0) continue;
                    list.Add(new Vector3(x, y, z).normalized);
                }
            }
        }
        return list.ToArray();
    }

    private Rigidbody _rigidbody;
    private ulong _instigatorClientId;
    private bool _hasDetonated;
    private bool _isTouchingGround;
    private float _prewarmTimer;
    private int _gunfireHitCount;
    private bool _isFrozen;
    private float _unfreezeTime;

    /// <summary>FreezeBomb用。durationSeconds間、fuse切れ/着弾による自力の起爆だけを止める(誘爆/銃撃での起爆は可能)</summary>
    public void ServerSetFrozen(float durationSeconds)
    {
        if (!IsServer || _hasDetonated) return;

        _isFrozen = true;
        _unfreezeTime = Time.time + Mathf.Max(0f, durationSeconds);
    }

    private void Awake()
    {
        _rigidbody = GetComponent<Rigidbody>();

        // 薄い地面を高速で貫通しないよう、コード側でContinuousDynamicに固定する
        if (_rigidbody != null) _rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        ApplyDefinitionIfPresent();
        BuildVisualIfNeeded();
    }

    /// <summary>definitionが設定されていれば、その値でこのインスタンスの設定を上書きする(BuildVisualIfNeededより前に呼ぶ)</summary>
    private void ApplyDefinitionIfPresent()
    {
        if (definition == null) return;

        visualPrefab = definition.visualPrefab;
        visualLocalEulerAngles = definition.visualLocalEulerAngles;
        visualLocalScale = definition.visualLocalScale;
        fuseSeconds = definition.fuseSeconds;
        damage = definition.damage;
        damageRadius = definition.damageRadius;
        power = definition.power;
        minDamageFalloff = definition.minDamageFalloff;
        knockbackForcePerDamage = definition.knockbackForcePerDamage;
        knockbackUpwardBias = definition.knockbackUpwardBias;
        knockbackRadiusMultiplier = definition.knockbackRadiusMultiplier;
        selfDamageMultiplier = definition.selfDamageMultiplier;
    }

    /// <summary>見た目用Prefabを子として実体化する(全クライアントで毎回呼ぶ)</summary>
    private void BuildVisualIfNeeded()
    {
        if (visualPrefab == null) return;

        var visual = Instantiate(visualPrefab, transform);
        visual.transform.localPosition = Vector3.zero;
        visual.transform.localRotation = Quaternion.Euler(visualLocalEulerAngles);
        visual.transform.localScale = visualLocalScale;
        VisualPrefabUtility.StripStrayVisualComponents(visual);
    }

    /// <summary>PlayerWeaponが、NetworkObject.Spawn()の直後にサーバーで呼ぶ。発射者と初速を設定し、fuseを開始する</summary>
    public void ServerInitialize(ulong instigatorClientId, Vector3 initialVelocity)
    {
        if (!IsServer) return;

        _instigatorClientId = instigatorClientId;
        if (_rigidbody != null) _rigidbody.linearVelocity = initialVelocity;
        StartCoroutine(FuseCoroutine());
    }

    /// <summary>接地中だけ速度を追加で削り、球体が転がり続けないようにする</summary>
    private void FixedUpdate()
    {
        if (!IsServer || _rigidbody == null || !_isTouchingGround) return;

        float damp = Mathf.Clamp01(groundRollingResistancePerSecond * Time.fixedDeltaTime);
        _rigidbody.linearVelocity *= 1f - damp;
        _rigidbody.angularVelocity *= 1f - damp;
    }

    private void OnCollisionEnter(Collision collision)
    {
        _isTouchingGround = true;
        if (!IsServer || !detonateOnImpact || _isFrozen) return;

        // 発射直後の自分自身への衝突では起爆させない
        var hitHealth = collision.collider.GetComponentInParent<Health>();
        if (hitHealth != null && hitHealth.OwnerClientId == _instigatorClientId) return;

        ServerDetonate();
    }

    private void OnCollisionStay(Collision collision) => _isTouchingGround = true;
    private void OnCollisionExit(Collision collision) => _isTouchingGround = false;

    /// <summary>凍結の解除判定と、飛翔中の周囲の建物の先読み初期化(サーバー/クライアントそれぞれ独立にローカルで行う)</summary>
    private void Update()
    {
        if (_hasDetonated) return;

        if (_isFrozen && Time.time >= _unfreezeTime) _isFrozen = false;

        _prewarmTimer -= Time.deltaTime;
        if (_prewarmTimer > 0f) return;
        _prewarmTimer = prewarmIntervalSeconds;

        Collider[] nearby = Physics.OverlapSphere(transform.position, prewarmRadius, damageMask, QueryTriggerInteraction.Ignore);
        foreach (var col in nearby)
        {
            DestructibleBuildingPart building = col.GetComponentInParent<DestructibleBuildingPart>();
            if (building != null) building.PrewarmIfNeeded();
        }
    }

    /// <summary>fuseSeconds後に起爆する。凍結中は残り時間を減らさない</summary>
    private IEnumerator FuseCoroutine()
    {
        float remaining = fuseSeconds;
        while (remaining > 0f)
        {
            if (!_isFrozen) remaining -= Time.deltaTime;
            yield return null;
        }
        ServerDetonate();
    }

    /// <summary>銃撃がこの爆発物に命中した時にサーバーで呼ぶ。力積を与えて被弾数を進め、規定数で起爆する</summary>
    public void ServerApplyGunfireHit(Vector3 hitPoint, Vector3 hitDirection, ulong shooterClientId)
    {
        if (!IsServer || !destructibleByGunfire || _hasDetonated) return;

        if (_rigidbody != null && hitDirection.sqrMagnitude > 0.0001f)
        {
            _rigidbody.AddForceAtPosition(hitDirection.normalized * gunfireImpulseForce, hitPoint, ForceMode.Impulse);
        }

        _gunfireHitCount++;
        Debug.Log($"[ExplosiveItem] グレネードが銃撃を受けました position={transform.position} " +
            $"hitCount={_gunfireHitCount}/{gunfireHitsToDetonate} shooter=client {shooterClientId}");

        if (_gunfireHitCount >= gunfireHitsToDetonate)
        {
            _instigatorClientId = shooterClientId; // キルは撃って起爆させた側に帰属させる
            ServerDetonate();
        }
    }

    /// <summary>fuse切れ・着弾・誘爆・銃撃から呼ばれる起爆処理(二重起爆防止込み)</summary>
    public void ServerDetonate()
    {
        if (!IsServer || _hasDetonated) return;
        _hasDetonated = true;

        Debug.Log($"[ExplosiveItem] 起爆 position={transform.position} instigator=client {_instigatorClientId}");

        // 地面を貫通して落下した場合に気づけるよう、明らかに低い位置での起爆は警告する
        if (transform.position.y < -3f)
        {
            Debug.LogWarning($"[ExplosiveItem] 地面よりかなり低い位置で起爆しました(貫通の疑い): position={transform.position}");
        }

        ApplyExplosionDamage();
        if (participatesInChainDetonation) ChainDetonateNearby();
        ApplyDetonationEffectsIfAny();

        DetonateClientRpc(transform.position);

        if (IsSpawned) NetworkObject.Despawn(true);
    }

    /// <summary>範囲内のプレイヤーへのダメージ/ノックバックと、遮蔽物・建物への破壊を適用する</summary>
    private void ApplyExplosionDamage()
    {
        // OverlapSphereはノックバック半径(ダメージ半径以上)で行い、ダメージとノックバックは別々に減衰を計算する
        float knockbackRadius = Mathf.Max(damageRadius, damageRadius * knockbackRadiusMultiplier);
        Collider[] hits = Physics.OverlapSphere(transform.position, knockbackRadius, damageMask, QueryTriggerInteraction.Ignore);
        var alreadyProcessed = new HashSet<Health>();
        var alreadyDestructed = new HashSet<DestructibleMesh>();
        var alreadyDamagedBuildings = new HashSet<DestructibleBuildingPart>();
        var alreadyDamagedVoxelBuildings = new HashSet<VoxelBuildingPart>();

        foreach (var col in hits)
        {
            var health = col.GetComponentInParent<Health>();
            if (health == null)
            {
                if (!damagesEnvironment) continue;

                // 遮蔽物(DestructibleMesh)。破壊判定はノックバック半径ではなくdamageRadiusで行う
                var cover = col.GetComponentInParent<DestructibleMesh>();
                if (cover != null && alreadyDestructed.Add(cover))
                {
                    cover.ServerApplyExplosion(transform.position, damageRadius);
                    continue;
                }

                // ボクセル建物(VoxelBuildingPart)
                var voxelBuilding = col.GetComponentInParent<VoxelBuildingPart>();
                if (voxelBuilding != null && alreadyDamagedVoxelBuildings.Add(voxelBuilding))
                {
                    float colliderDistance = Mathf.Sqrt(col.bounds.SqrDistance(transform.position));
                    Debug.Log($"[ExplosiveItem] VoxelBuildingPart検出: {voxelBuilding.name}(id={voxelBuilding.GetInstanceID()}) " +
                        $"collider={col.name} colliderBounds={col.bounds} colliderDistanceFromExplosion={colliderDistance} " +
                        $"explosionWorldPos={transform.position} damageRadius={damageRadius} knockbackRadius={knockbackRadius}");
                    voxelBuilding.ServerApplyExplosion(transform.position, damageRadius);
                    continue;
                }

                // 街の建物(DestructibleBuildingPart)。裏打ちColliderに当たった場合はBackingSurfaceLink経由で元の建物を辿る
                var building = col.GetComponentInParent<DestructibleBuildingPart>();
                if (building == null)
                {
                    var backingLink = col.GetComponentInParent<DestructibleBuildingPart.BackingSurfaceLink>();
                    if (backingLink != null) building = backingLink.Owner;
                }
                if (building != null && alreadyDamagedBuildings.Add(building))
                {
                    // ダメージ判定の中心は起爆位置、表面点+法線は焦げ跡デカール専用
                    Vector3 hitPoint, hitNormal;
                    bool preciseHit = TryFindBuildingSurfacePoint(col, out hitPoint, out hitNormal);
                    if (!preciseHit)
                    {
                        // 特定できなくてもデカールが出ないだけなので、boundsの近似点で代用する
                        hitPoint = col.bounds.ClosestPoint(transform.position);
                        hitNormal = EstimateBoundsFaceNormal(col.bounds, hitPoint);
                        Debug.LogWarning($"[ExplosiveItem] 建物への命中面をRaycastで特定できず、" +
                            $"近似座標で代用しました position={transform.position} hitPoint={hitPoint}");
                    }
                    building.ServerApplyDamage(hitPoint, hitNormal, transform.position, damageRadius, power);
                }
                continue;
            }
            if (!alreadyProcessed.Add(health)) continue;

            float distance = Vector3.Distance(transform.position, health.transform.position);

            if (distance < damageRadius)
            {
                float damageLinearFalloff = Mathf.Clamp01(1f - distance / damageRadius);
                // ダメージはminDamageFalloffを下限にした緩やかな減衰(ノックバックは別カーブ)
                float damageFalloff = Mathf.Lerp(minDamageFalloff, 1f, damageLinearFalloff);

                // 自爆は無くさず、selfDamageMultiplierで軽減する
                bool isSelfDamage = health.OwnerClientId == _instigatorClientId;
                float appliedDamage = damage * damageFalloff * (isSelfDamage ? selfDamageMultiplier : 1f);
                health.ServerApplyDamage(appliedDamage, _instigatorClientId);
            }

            // ノックバックはknockbackRadius基準の独立した減衰で計算する
            float knockbackLinearFalloff = Mathf.Clamp01(1f - distance / knockbackRadius);
            if (knockbackLinearFalloff > 0f) ApplyKnockbackIfPlayer(health, knockbackLinearFalloff);
        }
    }

    /// <summary>建物のCollider表面上の点+法線を、そのColliderへ実際に命中したRaycastで求める。まず近似点の方向、駄目なら周囲26方向を試す</summary>
    private bool TryFindBuildingSurfacePoint(Collider col, out Vector3 hitPoint, out Vector3 hitNormal)
    {
        Vector3 approxPoint = col.bounds.ClosestPoint(transform.position);
        Vector3 towardApprox = approxPoint - transform.position;
        if (towardApprox.sqrMagnitude > 0.0001f &&
            TryRaycastAllForCollider(towardApprox.normalized, col, out hitPoint, out hitNormal))
        {
            return true;
        }

        foreach (var dir in SurroundingRayDirections)
        {
            if (TryRaycastAllForCollider(dir, col, out hitPoint, out hitNormal)) return true;
        }

        hitPoint = default;
        hitNormal = default;
        return false;
    }

    /// <summary>指定方向へRaycastAllし、目的のColliderへの最も近い命中を探す(手前に別のColliderがあっても貫通して検出する)</summary>
    private bool TryRaycastAllForCollider(Vector3 direction, Collider col, out Vector3 hitPoint, out Vector3 hitNormal)
    {
        RaycastHit[] results = Physics.RaycastAll(transform.position, direction, damageRadius + 1f, damageMask, QueryTriggerInteraction.Ignore);
        float bestDistance = float.MaxValue;
        bool found = false;
        hitPoint = default;
        hitNormal = default;

        foreach (var hit in results)
        {
            if (hit.collider == col && hit.distance < bestDistance)
            {
                bestDistance = hit.distance;
                hitPoint = hit.point;
                hitNormal = hit.normal;
                found = true;
            }
        }
        return found;
    }

    /// <summary>Raycast失敗時の保険。bounds上の点がどの面に最も近いかで大まかな法線を推定する</summary>
    private static Vector3 EstimateBoundsFaceNormal(Bounds bounds, Vector3 point)
    {
        Vector3 local = point - bounds.center;
        Vector3 extents = bounds.extents;

        float xRatio = extents.x > 0.0001f ? Mathf.Abs(local.x) / extents.x : 0f;
        float yRatio = extents.y > 0.0001f ? Mathf.Abs(local.y) / extents.y : 0f;
        float zRatio = extents.z > 0.0001f ? Mathf.Abs(local.z) / extents.z : 0f;

        if (xRatio >= yRatio && xRatio >= zRatio) return new Vector3(Mathf.Sign(local.x), 0f, 0f);
        if (yRatio >= xRatio && yRatio >= zRatio) return new Vector3(0f, Mathf.Sign(local.y), 0f);
        return new Vector3(0f, 0f, Mathf.Sign(local.z));
    }

    /// <summary>爆心地から遠ざかる方向へ吹き飛ばす(近いほど、damageが大きいほど強い)。Owner権威の移動のため、PlayerController経由でインパルスを届ける</summary>
    private void ApplyKnockbackIfPlayer(Health health, float falloff)
    {
        if (knockbackForcePerDamage <= 0f) return;

        var pc = health.GetComponent<PlayerController>();
        if (pc == null) return;

        Vector3 planar = health.transform.position - transform.position;
        planar.y = 0f;
        Vector3 horizontalDir = planar.sqrMagnitude > 0.0001f ? planar.normalized : Vector3.forward;

        float magnitude = damage * knockbackForcePerDamage * falloff;
        Vector3 impulse = horizontalDir * magnitude + Vector3.up * (magnitude * knockbackUpwardBias);
        pc.ServerApplyKnockback(impulse);
    }

    /// <summary>半径内の未起爆Explosiveと、マップ上の爆発物系WeaponPickupを連鎖的に誘爆させる。キルは最初の発射者に帰属させる</summary>
    private void ChainDetonateNearby()
    {
        Collider[] hits = Physics.OverlapSphere(transform.position, chainDetonateRadius, explosiveMask, QueryTriggerInteraction.Collide);

        foreach (var col in hits)
        {
            var other = col.GetComponentInParent<ExplosiveItem>();
            if (other != null && other != this && !other._hasDetonated)
            {
                if (!other.participatesInChainDetonation) continue;
                other._instigatorClientId = _instigatorClientId;
                other.ServerDetonate(); // 再帰: otherがさらに別のExplosive/Pickupを誘爆させる
                continue;
            }

            var pickup = col.GetComponentInParent<WeaponPickup>();
            if (pickup != null && !pickup.IsConsumed)
            {
                pickup.ServerChainDetonate(_instigatorClientId);
            }
        }
    }

    /// <summary>definition.detonationEffects(炎上/凍結/榴散弾等)を順に実行する。1つが例外を投げても爆発自体には影響させない</summary>
    private void ApplyDetonationEffectsIfAny()
    {
        if (definition == null || definition.detonationEffects == null) return;

        foreach (var effect in definition.detonationEffects)
        {
            if (effect == null) continue;
            try
            {
                effect.ServerApply(this, transform.position, _instigatorClientId);
            }
            catch (System.Exception e)
            {
                Debug.LogError($"[ExplosiveItem] DetonationEffect({effect.name})の実行中に例外: {e}");
            }
        }
    }

    /// <summary>全クライアントで爆発演出・カメラ揺れ・起爆音を再生する</summary>
    [ClientRpc]
    private void DetonateClientRpc(Vector3 position)
    {
        Debug.Log($"[ExplosiveItem] 爆発演出位置: {position}");
        SpawnExplosionEffect(position);
        ShakeLocalCameraIfNearby(position);
        if (detonateClip != null)
        {
            SfxPlayer.PlayAt(detonateClip, position, detonateVolume * SfxPlayer.CurrentSfxScale, SfxPlayer.ExplosionMinDistance);
        }

    }

    /// <summary>爆発が近ければローカルプレイヤーのカメラだけを揺らす(距離に応じて弱める)</summary>
    private void ShakeLocalCameraIfNearby(Vector3 position)
    {
        PlayerController local = PlayerController.LocalPlayer;
        if (local == null) return;

        float distance = Vector3.Distance(local.transform.position, position);
        if (distance >= cameraShakeRadius) return;

        float ratio = 1f - distance / cameraShakeRadius;
        local.TriggerCameraShake(cameraShakeMagnitude * ratio, cameraShakeDuration);
    }

    /// <summary>爆発の見た目を、閃光・火花・煙・衝撃波の4層のParticleSystemを実行時に組み立てて再生する</summary>
    private void SpawnExplosionEffect(Vector3 position)
    {
        SpawnFlashLayer(position);
        SpawnSparkLayer(position);
        SpawnSmokeLayer(position);
        SpawnShockwaveLayer(position);
    }

    /// <summary>閃光/火花/衝撃波の基準色。definitionで上書きされていればその色、無ければ既定のオレンジ</summary>
    private Color GetExplosionBaseColor()
    {
        if (definition != null && definition.overrideExplosionColor) return definition.explosionColor;
        return new Color(1f, 0.55f, 0.05f);
    }

    /// <summary>中心が一瞬だけ強く光る閃光</summary>
    private void SpawnFlashLayer(Vector3 position)
    {
        ParticleSystem ps = CreateBaseParticleSystem("ExplosionFlash", position, out var main, out var emission, out var shape);

        main.duration = 0.25f;
        main.startLifetime = 0.18f;
        main.startSpeed = 0f;
        main.startSize = new ParticleSystem.MinMaxCurve(1.6f, 2.4f);
        main.startColor = Color.Lerp(GetExplosionBaseColor(), Color.white, 0.75f);

        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 1) });
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.05f;

        var sizeOverLifetime = ps.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.3f, 1f, 1f));

        FinalizeParticleSystem(ps);
    }

    /// <summary>外向きに飛び散る火花</summary>
    private void SpawnSparkLayer(Vector3 position)
    {
        ParticleSystem ps = CreateBaseParticleSystem("ExplosionSparks", position, out var main, out var emission, out var shape);

        main.duration = 0.6f;
        main.startLifetime = 0.45f;
        main.startSpeed = new ParticleSystem.MinMaxCurve(4f, 10f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.4f, 0.9f);
        Color sparkBase = GetExplosionBaseColor();
        main.startColor = new ParticleSystem.MinMaxGradient(
            Color.Lerp(sparkBase, Color.white, 0.35f), Color.Lerp(sparkBase, Color.black, 0.3f));

        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 60) });
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.4f;

        var sizeOverLifetime = ps.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 1f, 1f, 0f));

        FinalizeParticleSystem(ps);
    }

    /// <summary>上にゆっくり漂う煙(色を上書きしている武器は基準色に寄せる)</summary>
    private void SpawnSmokeLayer(Vector3 position)
    {
        ParticleSystem ps = CreateBaseParticleSystem("ExplosionSmoke", position, out var main, out var emission, out var shape);

        main.duration = 1.2f;
        main.startLifetime = new ParticleSystem.MinMaxCurve(1f, 1.4f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(1f, 2.5f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.6f, 1.1f);

        Color smokeLight = new Color(0.35f, 0.32f, 0.3f);
        Color smokeDark = new Color(0.15f, 0.14f, 0.13f);
        if (definition != null && definition.overrideExplosionColor)
        {
            Color baseColor = GetExplosionBaseColor();
            smokeLight = Color.Lerp(baseColor, Color.white, 0.5f);
            smokeDark = Color.Lerp(baseColor, Color.black, 0.35f);
        }
        main.startColor = new ParticleSystem.MinMaxGradient(smokeLight, smokeDark);
        main.gravityModifier = -0.15f; // 負の重力=わずかに上向きの力として使い、煙が漂う感じにする

        emission.SetBursts(new[] { new ParticleSystem.Burst(0.05f, 14) });
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.5f;

        var sizeOverLifetime = ps.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.5f, 1f, 1.6f));

        var colorOverLifetime = ps.colorOverLifetime;
        colorOverLifetime.enabled = true;
        var alphaFade = new Gradient();
        alphaFade.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0.8f, 0f), new GradientAlphaKey(0f, 1f) });
        colorOverLifetime.color = alphaFade;

        FinalizeParticleSystem(ps);
    }

    /// <summary>地面に沿って水平に広がるリング状の衝撃波</summary>
    private void SpawnShockwaveLayer(Vector3 position)
    {
        ParticleSystem ps = CreateBaseParticleSystem("ExplosionShockwave", position, out var main, out var emission, out var shape);

        main.duration = 0.4f;
        main.startLifetime = 0.35f;
        main.startSpeed = new ParticleSystem.MinMaxCurve(6f, 8f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.3f, 0.5f);
        Color shockwaveColor = Color.Lerp(GetExplosionBaseColor(), Color.white, 0.6f);
        shockwaveColor.a = 0.6f;
        main.startColor = shockwaveColor;
        main.gravityModifier = 0f;

        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 24) });
        shape.shapeType = ParticleSystemShapeType.Circle;
        shape.radius = 0.2f;
        shape.radiusThickness = 0f; // 円周上からのみ放出することで「輪」の形を保つ
        shape.rotation = new Vector3(90f, 0f, 0f); // Circleの既定の向き(XY平面)を水平(XZ平面)に倒す

        var sizeOverLifetime = ps.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 1f, 1f, 2.5f));

        FinalizeParticleSystem(ps);
    }

    /// <summary>各レイヤー共通のセットアップ(GameObject生成・ParticleSystem追加・停止・基本モジュール取得)</summary>
    private static ParticleSystem CreateBaseParticleSystem(string name, Vector3 position,
        out ParticleSystem.MainModule main, out ParticleSystem.EmissionModule emission, out ParticleSystem.ShapeModule shape)
    {
        var go = new GameObject(name);
        go.transform.position = position;

        var ps = go.AddComponent<ParticleSystem>();

        // 既定のplayOnAwakeによる自動再生を止めてから設定する(再生中はdurationを変更できないため)
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        main = ps.main;
        main.playOnAwake = false;
        main.loop = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.stopAction = ParticleSystemStopAction.Destroy;

        emission = ps.emission;
        emission.rateOverTime = 0f;

        shape = ps.shape;

        return ps;
    }

    private static MaterialPropertyBlock _untintedPropertyBlock;

    private void FinalizeParticleSystem(ParticleSystem ps)
    {
        var psRenderer = ps.GetComponent<ParticleSystemRenderer>();
        if (explosionParticleMaterial != null)
        {
            ParticleVfxUtility.EnsureSoftRoundParticleTexture(explosionParticleMaterial);
            psRenderer.sharedMaterial = explosionParticleMaterial;
        }

        // 色を上書きする武器だけ、共有マテリアルのオレンジの乗算を無効にする
        if (definition != null && definition.overrideExplosionColor)
        {
            if (_untintedPropertyBlock == null)
            {
                _untintedPropertyBlock = new MaterialPropertyBlock();
                _untintedPropertyBlock.SetColor("_BaseColor", Color.white);
                _untintedPropertyBlock.SetColor("_Color", Color.white);
            }
            psRenderer.SetPropertyBlock(_untintedPropertyBlock);
        }

        ps.Play();
    }
}
