using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 着弾地点に残る炎のダメージゾーン。Spawn直後にServerInitialize()を呼んで使う。
/// ダメージ判定はサーバーのみが一定間隔で行い、クライアントは炎パーティクル(BurnVisualClientRpc)だけを受け取る。
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class BurningGroundArea : NetworkBehaviour
{
    private const float TickIntervalSeconds = 0.5f;

    private float _radius;
    private float _durationSeconds;
    private float _damagePerSecond;
    private ulong _instigatorClientId;
    private bool _initialized;

    /// <summary>炎パーティクル用の透過マテリアル(初回生成してキャッシュ。テクスチャ無しで四角い粒のまま使う)</summary>
    private static Material _fireParticleMaterial;

    /// <summary>Instantiate+NetworkObject.Spawn(true)の直後にサーバーが呼ぶ初期化</summary>
    public void ServerInitialize(float radius, float durationSeconds, float damagePerSecond, ulong instigatorClientId)
    {
        if (!IsServer || _initialized) return;
        _initialized = true;

        _radius = radius;
        _durationSeconds = durationSeconds;
        _damagePerSecond = damagePerSecond;
        _instigatorClientId = instigatorClientId;

        Debug.Log($"[BurningGroundArea] 炎上エリア発生 position={transform.position} radius={radius} " +
            $"duration={durationSeconds} dps={damagePerSecond} instigator=client {instigatorClientId}");

        BurnVisualClientRpc(radius, durationSeconds);
        StartCoroutine(ServerDamageTickCoroutine());
    }

    /// <summary>一定間隔でダメージを与え、持続時間が過ぎたらDespawnする</summary>
    private IEnumerator ServerDamageTickCoroutine()
    {
        var wait = new WaitForSeconds(TickIntervalSeconds);
        float elapsed = 0f;

        while (elapsed < _durationSeconds)
        {
            yield return wait;
            elapsed += TickIntervalSeconds;
            ApplyTickDamage();
        }

        Debug.Log($"[BurningGroundArea] 炎上エリア終了 position={transform.position}");
        if (IsSpawned) NetworkObject.Despawn(true);
    }

    private void ApplyTickDamage()
    {
        Collider[] hits = Physics.OverlapSphere(transform.position, _radius);
        var alreadyProcessed = new HashSet<Health>();

        foreach (var col in hits)
        {
            var health = col.GetComponentInParent<Health>();
            if (health == null || !alreadyProcessed.Add(health)) continue;

            health.ServerApplyDamage(_damagePerSecond * TickIntervalSeconds, _instigatorClientId, Health.DamageType.Fire);
        }
    }

    [ClientRpc]
    private void BurnVisualClientRpc(float radius, float durationSeconds)
    {
        Debug.Log($"[BurningGroundArea] 炎上演出開始 position={transform.position} duration={durationSeconds}");
        SpawnFireVisual(transform.position, radius, durationSeconds);

    }

    /// <summary>炎と煙の2層のParticle Systemを、ネットワーク化しない独立GameObjectとして生成し、
    /// durationSeconds後に破棄する</summary>
    private void SpawnFireVisual(Vector3 position, float radius, float durationSeconds)
    {
        var root = new GameObject("BurningGroundAreaVisual");
        root.transform.position = position;
        Destroy(root, durationSeconds);

        SpawnFlameColumnLayer(root.transform, radius, durationSeconds);
        SpawnSmokeWispLayer(root.transform, radius, durationSeconds);
    }

    /// <summary>地面の円範囲から立ち上る炎のレイヤー</summary>
    private void SpawnFlameColumnLayer(Transform parent, float radius, float durationSeconds)
    {
        ParticleSystem ps = CreateBaseParticleSystem("BurningGroundFlame", parent, out var main, out var emission, out var shape);

        main.loop = true;
        main.duration = durationSeconds;
        main.startLifetime = new ParticleSystem.MinMaxCurve(0.7f, 1.2f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(1.0f, 2.0f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.9f, 1.8f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.95f, 0.3f), new Color(1f, 0.25f, 0.02f));
        main.startRotation = new ParticleSystem.MinMaxCurve(0f, Mathf.PI * 2f); // ランダム回転で四角ビルボードの向きを散らす
        main.gravityModifier = 0f;

        emission.rateOverTime = 55f;
        shape.shapeType = ParticleSystemShapeType.Circle;
        shape.radius = Mathf.Max(0.1f, radius);
        shape.radiusThickness = 1f; // 円の縁だけでなく内側全体から立ち上らせる
        shape.rotation = new Vector3(90f, 0f, 0f); // Circleの向き(XY平面)を水平に倒す
        shape.randomDirectionAmount = 0.35f;

        // 経路を揺らして炎らしい不規則さを足す
        var noise = ps.noise;
        noise.enabled = true;
        noise.strength = 1.2f;
        noise.frequency = 0.6f;
        noise.scrollSpeed = 0.8f;
        noise.damping = true;
        noise.quality = ParticleSystemNoiseQuality.Medium;

        var sizeOverLifetime = ps.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.9f, 1f, 0.6f));

        var colorOverLifetime = ps.colorOverLifetime;
        colorOverLifetime.enabled = true;
        var fade = new Gradient();
        fade.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            // 寿命の半分まで不透明を維持してから消す
            new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.5f), new GradientAlphaKey(0f, 1f) });
        colorOverLifetime.color = fade;

        FinalizeParticleSystem(ps);
    }

    /// <summary>たまに立ち上る暗い灰色の煙のレイヤー</summary>
    private void SpawnSmokeWispLayer(Transform parent, float radius, float durationSeconds)
    {
        ParticleSystem ps = CreateBaseParticleSystem("BurningGroundSmoke", parent, out var main, out var emission, out var shape);

        main.loop = true;
        main.duration = durationSeconds;
        main.startLifetime = new ParticleSystem.MinMaxCurve(1.2f, 1.8f);
        main.startSpeed = new ParticleSystem.MinMaxCurve(0.6f, 1.3f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.5f, 1f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(0.3f, 0.28f, 0.26f), new Color(0.12f, 0.11f, 0.1f));
        main.gravityModifier = -0.1f; // 負の重力でゆっくり漂わせる

        emission.rateOverTime = 2.5f;
        shape.shapeType = ParticleSystemShapeType.Circle;
        shape.radius = Mathf.Max(0.1f, radius);
        shape.radiusThickness = 1f;
        shape.rotation = new Vector3(90f, 0f, 0f); // 炎レイヤーと同じ向き補正

        var sizeOverLifetime = ps.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.5f, 1f, 1.4f));

        var colorOverLifetime = ps.colorOverLifetime;
        colorOverLifetime.enabled = true;
        var fade = new Gradient();
        fade.SetKeys(
            new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0.5f, 0f), new GradientAlphaKey(0f, 1f) });
        colorOverLifetime.color = fade;

        FinalizeParticleSystem(ps);
    }

    /// <summary>2層共通のセットアップ(GameObject生成・ParticleSystem追加・基本モジュール取得)。
    /// 根オブジェクトのDestroyで一括片付けするためstopActionはNone</summary>
    private static ParticleSystem CreateBaseParticleSystem(string name, Transform parent,
        out ParticleSystem.MainModule main, out ParticleSystem.EmissionModule emission, out ParticleSystem.ShapeModule shape)
    {
        var go = new GameObject(name);
        go.transform.SetParent(parent, false);
        go.transform.localPosition = Vector3.zero;

        var ps = go.AddComponent<ParticleSystem>();

        // 自動再生中にmain.durationを変えるとエラーになるため、設定前に必ず停止しておく
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        main = ps.main;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.stopAction = ParticleSystemStopAction.None;

        emission = ps.emission;
        emission.rateOverTime = 0f;

        shape = ps.shape;

        return ps;
    }

    private void FinalizeParticleSystem(ParticleSystem ps)
    {
        var psRenderer = ps.GetComponent<ParticleSystemRenderer>();
        psRenderer.sharedMaterial = GetOrCreateFireMaterial();
        ps.Play();
    }

    /// <summary>炎パーティクル専用の透過マテリアルを生成してキャッシュする(テクスチャは意図的に割り当てない)</summary>
    private static Material GetOrCreateFireMaterial()
    {
        if (_fireParticleMaterial != null) return _fireParticleMaterial;

        var mat = new Material(Shader.Find("Universal Render Pipeline/Particles/Unlit"));
        mat.SetFloat("_Surface", 1f); // 0=Opaque, 1=Transparent
        mat.SetFloat("_Blend", 0f);   // 0=Alpha blend
        mat.SetFloat("_SrcBlend", (float)UnityEngine.Rendering.BlendMode.SrcAlpha);
        mat.SetFloat("_DstBlend", (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        mat.SetFloat("_ZWrite", 0f);
        mat.SetShaderPassEnabled("Universal Forward", true);
        mat.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
        mat.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
        mat.SetOverrideTag("RenderType", "Transparent");

        _fireParticleMaterial = mat;
        return _fireParticleMaterial;
    }
}
