using Unity.Netcode;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// マップに配置する武器の拾い物。拾得判定はPlayerWeapon.RequestPickupServerRpc()側が行い、
/// このクラスは武器種類・消費フラグ・見た目(上下運動/回転/「拾う」プロンプト)・銃撃/誘爆への反応を持つ。
/// </summary>
[RequireComponent(typeof(NetworkObject))]
public class WeaponPickup : NetworkBehaviour
{
    [SerializeField] private PlayerWeapon.WeaponType weaponType = PlayerWeapon.WeaponType.Blaster;
    [Tooltip("Bomb等、所持数として加算される武器の場合の付与数(通常武器は1固定)")]
    [SerializeField] private int grantAmount = 1;
    [SerializeField] private float promptShowDistance = 3f;
    [SerializeField] private Vector3 promptWorldOffset = new Vector3(0f, 1f, 0f);
    [Header("見た目(実アセット)")]
    [Tooltip("設定すると子として実体化して見た目に使う(未設定ならプレースホルダーのまま)")]
    [SerializeField] private GameObject visualPrefab;
    [SerializeField] private Vector3 visualLocalEulerAngles = Vector3.zero;
    [SerializeField] private Vector3 visualLocalScale = Vector3.one;
    [Header("遠くからの視認性")]
    [Tooltip("本体をゆっくり上下に揺らす+回転させて視認性を上げる(上下の振幅・速度・回転速度)")]
    [SerializeField] private float bobAmplitude = 0.15f;
    [SerializeField] private float bobSpeed = 2f;
    [SerializeField] private float spinDegreesPerSecond = 90f;
    [Header("誘爆")]
    [Tooltip("設定していれば、誘爆に巻き込まれた時にこのPrefabをこの位置に生成して即座に起爆させる(未設定なら誘爆しない)")]
    [SerializeField] private ExplosiveItem chainDetonateExplosivePrefab;
    [Header("銃撃を受けた時の反応")]
    [Tooltip("chainDetonateExplosivePrefabがある場合に、この回数被弾したら誘爆する")]
    [SerializeField] private int gunfireHitsToDetonate = 3;
    [Tooltip("被弾時に与える物理的な力積の大きさ")]
    [SerializeField] private float gunfireImpulseForce = 5f;
    [Header("効果音")]
    [Tooltip("拾った瞬間に鳴らす効果音(全クライアントで再生。未設定なら鳴らさない)")]
    [SerializeField] private AudioClip pickupClip;
    [Range(0f, 1f)] [SerializeField] private float pickupVolume = 0.6f;

    public PlayerWeapon.WeaponType WeaponType => weaponType;
    public int GrantAmount => grantAmount;
    public bool IsConsumed { get; private set; }

    private Rigidbody _rigidbody;
    private int _gunfireHitCount;

    /// <summary>手放した武器の実際の所持数をこの拾い物に引き継がせる(Instantiate直後、Spawn前に呼ぶ)</summary>
    public void ServerOverrideGrantAmount(int amount)
    {
        grantAmount = Mathf.Max(1, amount);
    }

    private GameObject _promptRoot;
    private Camera _localCamera;
    private Transform _visualPivot;
    private float _bobPhase;

    // 画質設定の描画距離より遠い拾い物は、見た目の描画と上下運動/回転/プロンプトだけを止める
    // (NetworkObject・Collider・誘爆・拾得判定には触れない)。判定は一定間隔ごと
    private const float CullCheckIntervalSeconds = 0.25f;
    private Renderer[] _cullableRenderers;
    private bool _distanceCulled;
    private float _nextCullCheckTime;
    private bool _countedActive;

    /// <summary>[Perf]ログ用: 存在している拾い物の数 / そのうち距離で描画を止めている数(このクライアント内の値)</summary>
    public static int ActiveCount { get; private set; }
    public static int DistanceCulledCount { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStaticCounters()
    {
        ActiveCount = 0;
        DistanceCulledCount = 0;
    }

    private void Awake()
    {
        // 物理で転がるのはグレネード系のみ(銃器は非対称形状で回転演出と組み合わさると見た目が破綻するため)
        if (PlayerWeapon.IsGrenadeWeaponType(weaponType)) SetupGunfirePhysics();
    }

    /// <summary>銃撃で転がせるよう、専用レイヤーでColliderを実体化しRigidbodyを付ける(プレイヤーとの物理衝突だけ無効にして素通りは維持)</summary>
    private void SetupGunfirePhysics()
    {
        int pickupLayer = LayerMask.NameToLayer("Pickup");
        if (pickupLayer >= 0)
        {
            gameObject.layer = pickupLayer;
            int playerLayer = LayerMask.NameToLayer("Player");
            if (playerLayer >= 0) Physics.IgnoreLayerCollision(pickupLayer, playerLayer, true);
        }

        var col = GetComponent<Collider>();
        if (col != null) col.isTrigger = false;

        _rigidbody = GetComponent<Rigidbody>();
        if (_rigidbody == null) _rigidbody = gameObject.AddComponent<Rigidbody>();
        _rigidbody.mass = 0.5f;
        _rigidbody.linearDamping = 0.3f;
        _rigidbody.angularDamping = 0.2f;
        // 高速な力積を受けた瞬間に地形をすり抜けないようにする
        _rigidbody.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
    }

    private void Start()
    {
        BuildVisualIfNeeded();
        // プロンプトを組む前に集めて、距離カリングの対象を拾い物の見た目だけに限定する
        CollectCullableRenderers();
        ActiveCount++;
        _countedActive = true;
        _nextCullCheckTime = Time.time + Random.Range(0f, CullCheckIntervalSeconds);
        BuildPrompt();
        _bobPhase = Random.Range(0f, Mathf.PI * 2f); // 複数のアイテムが同じ位相で揺れないようにする
    }

    /// <summary>visualPrefabを子として実体化し、ルート自身のプレースホルダーは無効化する</summary>
    private void BuildVisualIfNeeded()
    {
        if (visualPrefab == null) return;

        var placeholderRenderer = GetComponent<MeshRenderer>();
        if (placeholderRenderer != null) placeholderRenderer.enabled = false;

        var visual = Instantiate(visualPrefab, transform);
        visual.transform.localPosition = Vector3.zero;
        visual.transform.localRotation = Quaternion.Euler(visualLocalEulerAngles);
        visual.transform.localScale = visualLocalScale;
        VisualPrefabUtility.StripStrayVisualComponents(visual);
        _visualPivot = visual.transform; // Update()で上下運動+回転を加える対象
    }

    private void Update()
    {
        if (IsConsumed) return;

        if (Time.time >= _nextCullCheckTime)
        {
            _nextCullCheckTime = Time.time + CullCheckIntervalSeconds;
            UpdateDistanceCulling();
        }
        if (_distanceCulled)
        {
            // 遠くて見えない間は上下運動/回転/プロンプトの処理を省く
            if (_promptRoot != null && _promptRoot.activeSelf) _promptRoot.SetActive(false);
            return;
        }

        // 上下運動+回転で視認性を上げる
        if (_visualPivot != null)
        {
            _bobPhase += Time.deltaTime * bobSpeed;
            _visualPivot.localPosition = Vector3.up * (Mathf.Sin(_bobPhase) * bobAmplitude);
            // 親が傾いて静止していてもワールド鉛直軸まわりに回るようSpace.Worldにする
            _visualPivot.Rotate(Vector3.up, spinDegreesPerSecond * Time.deltaTime, Space.World);
        }

        if (_promptRoot == null) return;

        PlayerController local = PlayerController.LocalPlayer;
        if (local == null)
        {
            _promptRoot.SetActive(false);
            return;
        }

        float distance = Vector3.Distance(local.transform.position, transform.position);
        bool show = distance <= promptShowDistance;
        _promptRoot.SetActive(show);
        if (!show) return;

        if (_localCamera == null || !_localCamera.isActiveAndEnabled)
        {
            _localCamera = local.GetComponentInChildren<Camera>();
        }
        if (_localCamera == null) return;

        // ビルボード: カメラの回転をそのままコピーして常に正面向きにする
        _promptRoot.transform.rotation = _localCamera.transform.rotation;
    }

    /// <summary>距離カリングでON/OFFする対象のRendererを集める(既に無効なプレースホルダーは除く)</summary>
    private void CollectCullableRenderers()
    {
        var all = GetComponentsInChildren<Renderer>(true);
        var list = new System.Collections.Generic.List<Renderer>(all.Length);
        foreach (var r in all)
        {
            if (r != null && r.enabled) list.Add(r);
        }
        _cullableRenderers = list.ToArray();
    }

    private void UpdateDistanceCulling()
    {
        bool shouldCull = false;
        float maxDistance = GraphicsQualitySettings.PickupDrawDistance;
        if (maxDistance > 0f)
        {
            // 自分のプレイヤーがいない間は全部描画する
            PlayerController local = PlayerController.LocalPlayer;
            if (local != null)
            {
                shouldCull = (local.transform.position - transform.position).sqrMagnitude > maxDistance * maxDistance;
            }
        }

        if (shouldCull != _distanceCulled) SetDistanceCulled(shouldCull);
    }

    private void SetDistanceCulled(bool culled)
    {
        _distanceCulled = culled;
        DistanceCulledCount += culled ? 1 : -1;
        if (_cullableRenderers == null) return;
        foreach (var r in _cullableRenderers)
        {
            if (r != null) r.enabled = !culled;
        }
    }

    public override void OnDestroy()
    {
        if (_countedActive)
        {
            ActiveCount--;
            if (_distanceCulled) DistanceCulledCount--;
            _countedActive = false;
        }
        base.OnDestroy();
    }

    /// <summary>「拾う」プロンプトを、World Space CanvasとTextで実行時に組み立てる</summary>
    private void BuildPrompt()
    {
        var root = new GameObject("PickupPrompt");
        root.transform.SetParent(transform, false);
        root.transform.localPosition = promptWorldOffset;
        root.transform.localScale = Vector3.one * 0.01f; // World Space Canvasは1px=1unit相当なので縮小する

        var canvas = root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.WorldSpace;

        var rootRect = root.GetComponent<RectTransform>();
        rootRect.sizeDelta = new Vector2(200f, 50f);

        var textGo = new GameObject("Text");
        textGo.transform.SetParent(root.transform, false);
        var text = textGo.AddComponent<Text>();
        text.font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        if (text.font == null) text.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
        text.alignment = TextAnchor.MiddleCenter;
        text.fontSize = 28;
        text.color = Color.white;
        // モバイルにはEキーが無いため、キー表記を含めない
        text.text = Application.isMobilePlatform ? "拾う" : "[E] 拾う";
        Debug.Log($"[WeaponPickup] {gameObject.name}: プロンプト文言を決定 " +
            $"isMobilePlatform={Application.isMobilePlatform} text=\"{text.text}\"");

        var textRect = textGo.GetComponent<RectTransform>();
        textRect.anchorMin = Vector2.zero;
        textRect.anchorMax = Vector2.one;
        textRect.sizeDelta = Vector2.zero;

        _promptRoot = root;
        _promptRoot.SetActive(false);
    }

    /// <summary>拾得成立時にサーバーが呼ぶ。拾得音のRPCをDespawnより先に送る</summary>
    public void ServerConsume()
    {
        if (!IsServer || IsConsumed) return;
        IsConsumed = true;

        // Despawn前にRPCを送ることで、RPCが届く前にオブジェクトが消えるのを避ける
        PlayPickupSoundClientRpc(transform.position);

        if (IsSpawned) NetworkObject.Despawn(true);
        else Destroy(gameObject);
    }

    /// <summary>拾得成立時、全クライアントで共通の拾得音を鳴らす</summary>
    [ClientRpc]
    private void PlayPickupSoundClientRpc(Vector3 position)
    {
        if (pickupClip == null) return;
        float scale = SoundManager.Instance != null ? SoundManager.Instance.SfxVolumeScale : 1f;
        SfxPlayer.PlayAt(pickupClip, position, pickupVolume * scale);
    }

    /// <summary>落ちている拾い物が銃撃を受けた時の反応(サーバー専用)。力積で押し動かし、爆発物なら被弾回数が閾値に達すると誘爆する</summary>
    public void ServerApplyGunfireHit(Vector3 hitPoint, Vector3 hitDirection, ulong shooterClientId)
    {
        if (!IsServer || IsConsumed) return;

        if (_rigidbody != null && hitDirection.sqrMagnitude > 0.0001f)
        {
            _rigidbody.AddForceAtPosition(hitDirection.normalized * gunfireImpulseForce, hitPoint, ForceMode.Impulse);
        }

        if (chainDetonateExplosivePrefab == null) return; // 爆発しない武器の拾い物は物理反応だけで終わり

        _gunfireHitCount++;
        Debug.Log($"[WeaponPickup] {gameObject.name}: 銃撃を受けました hitCount={_gunfireHitCount}/{gunfireHitsToDetonate} " +
            $"shooter=client {shooterClientId}");

        if (_gunfireHitCount >= gunfireHitsToDetonate)
        {
            ServerChainDetonate(shooterClientId);
        }
    }

    /// <summary>誘爆に巻き込まれた時(サーバー専用)。対応するExplosiveItemをこの位置に生成して即起爆する</summary>
    public void ServerChainDetonate(ulong instigatorClientId)
    {
        if (!IsServer || IsConsumed || chainDetonateExplosivePrefab == null) return;
        if (!chainDetonateExplosivePrefab.ParticipatesInChainDetonation) return;
        IsConsumed = true;

        ExplosiveItem spawned = Instantiate(chainDetonateExplosivePrefab, transform.position, Quaternion.identity);
        spawned.NetworkObject.Spawn(true);
        spawned.ServerInitialize(instigatorClientId, Vector3.zero);
        spawned.ServerDetonate();

        Debug.Log($"[WeaponPickup] {weaponType}の拾い物が誘爆に巻き込まれました position={transform.position}");

        if (IsSpawned) NetworkObject.Despawn(true);
        else Destroy(gameObject);
    }
}
