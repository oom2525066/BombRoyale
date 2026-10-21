using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// ショックウェーブガンの飛翔体。重力を受けず直進し、何かに衝突した地点を中心にプレイヤーへ
/// ダメージ+ノックバックを与える(建物には影響しない)。地面の拾い物/未起爆のグレネードも銃撃扱いで反応させる。
/// Prefabは NetworkObject / Rigidbody(useGravity:0) / Collider / NetworkTransform / 本スクリプト の構成。
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class ShockwaveProjectile : NetworkBehaviour
{
    [Tooltip("着弾地点の中心で与えるダメージ")]
    [SerializeField] private float damage = 5f;
    [Tooltip("着弾地点を中心に、この半径内のプレイヤー全員へダメージ+ノックバックを与える")]
    [SerializeField] private float impactRadius = 4.5f;
    [Tooltip("範囲の端でも保証するダメージ割合の下限(0〜1。中心に近いほど1に近づく)")]
    [SerializeField] private float minDamageFalloff = 0.35f;
    [Tooltip("ノックバックの強さ")]
    [SerializeField] private float knockbackForce = 24f;
    [Tooltip("水平方向の吹き飛ばし量に対する上向き成分の比率")]
    [SerializeField] private float knockbackUpwardBias = 1.4f;
    [Tooltip("ノックバック減衰の分母を広げる係数(1以上)。範囲の端でノックバックが急に0にならないようにする")]
    [SerializeField] private float knockbackRadiusMultiplier = 1.3f;
    [Tooltip("この距離を飛んでも何にも当たらなければ、何もせず消える")]
    [SerializeField] private float maxRange = 20f;
    [Tooltip("着弾演出のパーティクルに使うマテリアル(Unlit推奨。PlayerWeapon.shockwaveEffectMaterialと同じもの)")]
    [SerializeField] private Material effectMaterial;

    private Rigidbody _rigidbody;
    private ulong _instigatorClientId;
    private bool _hasImpacted;
    private Vector3 _startPosition;

    private void Awake()
    {
        _rigidbody = GetComponent<Rigidbody>();
        // 高速飛翔時のすり抜け防止
        if (_rigidbody != null) _rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;

        // 専用メッシュが無いため、飛跡と発光する球を実行時に組み立てる(全クライアントで見えるべき見た目)
        BuildFlightVisual();
    }

    /// <summary>飛翔中の見た目(TrailRendererと発光する小さな球のParticleSystem)を組み立てる</summary>
    private void BuildFlightVisual()
    {
        var trail = gameObject.AddComponent<TrailRenderer>();
        trail.time = 0.2f;
        trail.startWidth = 0.25f;
        trail.endWidth = 0.02f;
        trail.minVertexDistance = 0.05f;
        trail.receiveShadows = false;
        trail.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        trail.colorGradient = BuildTrailGradient();
        if (effectMaterial != null) trail.sharedMaterial = effectMaterial;

        var bodyGo = new GameObject("ShockwaveProjectileBody");
        bodyGo.transform.SetParent(transform, false);

        var ps = bodyGo.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.playOnAwake = true;
        main.loop = true;
        main.startLifetime = 0.2f;
        main.startSpeed = 0f;
        main.startSize = new ParticleSystem.MinMaxCurve(0.35f, 0.45f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.65f, 0.9f, 1f), new Color(0.2f, 0.55f, 1f));
        main.simulationSpace = ParticleSystemSimulationSpace.Local;

        var emission = ps.emission;
        emission.rateOverTime = 40f;

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.12f;

        var psRenderer = ps.GetComponent<ParticleSystemRenderer>();
        if (effectMaterial != null)
        {
            ParticleVfxUtility.EnsureSoftRoundParticleTexture(effectMaterial);
            psRenderer.sharedMaterial = effectMaterial;
        }

        ps.Play();
    }

    private static Gradient BuildTrailGradient()
    {
        var gradient = new Gradient();
        gradient.SetKeys(
            new[] { new GradientColorKey(new Color(0.65f, 0.9f, 1f), 0f), new GradientColorKey(new Color(0.2f, 0.55f, 1f), 1f) },
            new[] { new GradientAlphaKey(0.9f, 0f), new GradientAlphaKey(0f, 1f) });
        return gradient;
    }

    /// <summary>NetworkObject.Spawn()の直後にサーバーが呼ぶ初期化(発射者と初速を設定する)</summary>
    public void ServerInitialize(ulong instigatorClientId, Vector3 velocity)
    {
        if (!IsServer) return;

        _instigatorClientId = instigatorClientId;
        _startPosition = transform.position;
        if (_rigidbody != null) _rigidbody.linearVelocity = velocity;
    }

    private void Update()
    {
        if (!IsServer || _hasImpacted) return;

        if (Vector3.Distance(_startPosition, transform.position) >= maxRange)
        {
            _hasImpacted = true;
            if (IsSpawned) NetworkObject.Despawn(true);
        }
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (!IsServer || _hasImpacted) return;

        // 発射者自身には衝突しても起爆しない
        var hitHealth = collision.collider.GetComponentInParent<Health>();
        if (hitHealth != null && hitHealth.OwnerClientId == _instigatorClientId) return;

        ServerImpact(collision.GetContact(0).point);
    }

    /// <summary>着弾地点の範囲判定。プレイヤーにはダメージ+ノックバック、拾い物/グレネードには銃撃扱いの反応を与える</summary>
    private void ServerImpact(Vector3 point)
    {
        if (_hasImpacted) return;
        _hasImpacted = true;

        // isTrigger=trueのColliderも拾えるようCollideにする
        Collider[] hits = Physics.OverlapSphere(point, impactRadius, ~0, QueryTriggerInteraction.Collide);
        var alreadyProcessed = new HashSet<Health>();
        var alreadyProcessedPickups = new HashSet<WeaponPickup>();
        var alreadyProcessedExplosives = new HashSet<ExplosiveItem>();
        var hitPositions = new List<Vector3>();

        foreach (var col in hits)
        {
            var health = col.GetComponentInParent<Health>();
            if (health != null)
            {
                if (health.IsDead || !alreadyProcessed.Add(health)) continue;
                if (health.OwnerClientId == _instigatorClientId) continue; // 自分自身は対象外

                // 距離に応じた線形減衰(下限クランプ付き)をダメージとノックバックに適用する
                float distance = Vector3.Distance(point, health.transform.position);

                float damageLinearFalloff = Mathf.Clamp01(1f - distance / impactRadius);
                float damageFalloff = Mathf.Lerp(minDamageFalloff, 1f, damageLinearFalloff);
                health.ServerApplyDamage(damage * damageFalloff, _instigatorClientId);

                var pc = health.GetComponent<PlayerController>();
                if (pc != null)
                {
                    // ノックバックはダメージより緩やかな減衰カーブ(半径をknockbackRadiusMultiplier倍した分母)を使う
                    float knockbackRadius = impactRadius * knockbackRadiusMultiplier;
                    float knockbackFalloff = Mathf.Clamp01(1f - distance / knockbackRadius);
                    Vector3 planar = health.transform.position - point;
                    planar.y = 0f;
                    Vector3 horizontalDir = planar.sqrMagnitude > 0.0001f ? planar.normalized : Vector3.forward;
                    float magnitude = knockbackForce * knockbackFalloff;
                    Vector3 impulse = horizontalDir * magnitude + Vector3.up * (magnitude * knockbackUpwardBias);
                    pc.ServerApplyKnockback(impulse);
                }

                hitPositions.Add(health.transform.position);
                continue;
            }

            var pickup = col.GetComponentInParent<WeaponPickup>();
            if (pickup != null && !pickup.IsConsumed && alreadyProcessedPickups.Add(pickup))
            {
                Vector3 dir = pickup.transform.position - point;
                pickup.ServerApplyGunfireHit(pickup.transform.position, dir.normalized, _instigatorClientId);
                continue;
            }

            var explosive = col.GetComponentInParent<ExplosiveItem>();
            if (explosive != null && alreadyProcessedExplosives.Add(explosive))
            {
                Vector3 dir = explosive.transform.position - point;
                explosive.ServerApplyGunfireHit(explosive.transform.position, dir.normalized, _instigatorClientId);
            }
        }

        ImpactClientRpc(point, hitPositions.ToArray());
        Debug.Log($"[ShockwaveProjectile] {point}に着弾(命中{hitPositions.Count}人)");

        if (IsSpawned) NetworkObject.Despawn(true);
    }

    [ClientRpc]
    private void ImpactClientRpc(Vector3 point, Vector3[] hitPositions)
    {
        SpawnImpactBurst(point);
        foreach (var hitPos in hitPositions)
        {
            SpawnHitSpark(hitPos);
        }
    }

    /// <summary>着弾地点で再生する、膨らんで消える水色のバースト</summary>
    private void SpawnImpactBurst(Vector3 position)
    {
        var go = new GameObject("ShockwaveImpactBurst");
        go.transform.position = position;

        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        float lifetime = 0.6f;
        var main = ps.main;
        main.playOnAwake = false;
        main.duration = lifetime;
        main.loop = false;
        main.startLifetime = lifetime;
        main.startSpeed = new ParticleSystem.MinMaxCurve(3f, 6f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.8f, 1.6f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.65f, 0.9f, 1f), new Color(0.2f, 0.55f, 1f));
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.stopAction = ParticleSystemStopAction.Destroy;

        var emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 60) });

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.3f;

        var sizeOverLifetime = ps.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, new AnimationCurve(
            new Keyframe(0f, 0.3f), new Keyframe(0.3f, 1.4f), new Keyframe(1f, 0f)));

        var colorOverLifetime = ps.colorOverLifetime;
        colorOverLifetime.enabled = true;
        var fadeGradient = new Gradient();
        fadeGradient.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(new Color(0.3f, 0.6f, 1f), 1f) },
            new[] { new GradientAlphaKey(0.9f, 0f), new GradientAlphaKey(0.5f, 0.6f), new GradientAlphaKey(0f, 1f) });
        colorOverLifetime.color = fadeGradient;

        var psRenderer = ps.GetComponent<ParticleSystemRenderer>();
        if (effectMaterial != null)
        {
            ParticleVfxUtility.EnsureSoftRoundParticleTexture(effectMaterial);
            psRenderer.sharedMaterial = effectMaterial;
        }

        ps.Play();
    }

    /// <summary>命中したプレイヤーの位置で再生する、小さく短命な火花</summary>
    private void SpawnHitSpark(Vector3 position)
    {
        var go = new GameObject("ShockwaveHitSpark");
        go.transform.position = position;

        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.playOnAwake = false;
        main.duration = 0.25f;
        main.loop = false;
        main.startLifetime = 0.25f;
        main.startSpeed = new ParticleSystem.MinMaxCurve(1.5f, 3f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.3f, 0.6f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.8f, 0.95f, 1f), new Color(0.4f, 0.7f, 1f));
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.stopAction = ParticleSystemStopAction.Destroy;

        var emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 16) });

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Sphere;
        shape.radius = 0.3f;

        var sizeOverLifetime = ps.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 1f, 1f, 0f));

        var psRenderer = ps.GetComponent<ParticleSystemRenderer>();
        if (effectMaterial != null)
        {
            ParticleVfxUtility.EnsureSoftRoundParticleTexture(effectMaterial);
            psRenderer.sharedMaterial = effectMaterial;
        }

        ps.Play();
    }
}
