using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// プレイヤーの武器所持(4枠ホットバー)・発砲・投擲・拾得・見た目(持ち手/クロスヘア/投擲予測線/ホットバーUI)を扱う。
/// 武器は種類だけをNetworkVariableで同期する軽量設計で、見た目は各クライアントがローカルに出し入れする。
/// 銃(Blaster/ShockwaveGun/RocketLauncher)はスロットごとに独立した弾数を持ち、スタックするのはグレネード系のみ。
/// 拾得は拾得ボタン押下時にサーバー側でOverlapSphereして判定する。
/// </summary>
[RequireComponent(typeof(Health))]
public class PlayerWeapon : NetworkBehaviour
{
    public enum WeaponType
    {
        None = 0, Blaster = 1, Bomb = 2, RocketLauncher = 3, ShockwaveGun = 4,
        // グレネード系(Bombと同じ投擲操作。種類ごとの差はExplosiveItemのBombDefinitionが持つ)
        HeavyBomb = 5, FireBomb = 6, FreezeBomb = 7, SplitBomb = 8,
    }

    /// <summary>Bomb以外のグレネード系統1種類ぶんの設定(構え方/ホットバー表示用)</summary>
    [System.Serializable]
    private class GrenadeTypeEntry
    {
        public WeaponType weaponType;
        [Tooltip("投げた後の実体")]
        public ExplosiveItem explosivePrefab;
        [Tooltip("構えている間、手元に表示する見た目。未設定ならBomb用プレースホルダー(球)にフォールバック")]
        public GameObject heldVisualPrefab;
        public Vector3 heldVisualLocalEulerAngles = Vector3.zero;
        public Vector3 heldVisualLocalScale = Vector3.one;
        public string displayName = "Grenade";
        [Tooltip("所持できる最大個数(BombのmaxBombStackに相当)")]
        public int maxStack = 5;
    }

    private const int HotbarSize = 4;

    [Header("参照")]
    [SerializeField] private Camera playerCamera;
    [Tooltip("持っている武器の見た目をぶら下げる基準点。PlayerController.weaponHolderと同じものを指す")]
    [SerializeField] private Transform weaponHolder;

    [Header("見た目(プレースホルダー。実アセット未設定時のフォールバック)")]
    [SerializeField] private Material blasterHeldMaterial;
    [SerializeField] private Material bombHeldMaterial;
    [SerializeField] private Material rocketHeldMaterial;
    [SerializeField] private Material shockwaveHeldMaterial;
    [SerializeField] private Material trajectoryLineMaterial;

    [Header("見た目(実アセット。設定した種類はプレースホルダーの代わりにこちらを使う)")]
    [Tooltip("Cosmic Retro Blasters Pack等のBlaster本体メッシュ。未設定ならプレースホルダーの箱で構え持ちする")]
    [SerializeField] private GameObject blasterHeldVisualPrefab;
    [SerializeField] private Vector3 blasterHeldVisualLocalEulerAngles = Vector3.zero;
    [SerializeField] private Vector3 blasterHeldVisualLocalScale = Vector3.one;
    [Tooltip("Cosmic Retro Grenades等の実メッシュ。未設定ならプレースホルダーの球で構え持ちする")]
    [SerializeField] private GameObject bombHeldVisualPrefab;
    [SerializeField] private Vector3 bombHeldVisualLocalEulerAngles = Vector3.zero;
    [SerializeField] private Vector3 bombHeldVisualLocalScale = Vector3.one;
    [Tooltip("BigRookGames Stylized Rocket Launcherのランチャー本体メッシュ。未設定ならプレースホルダーの筒で構え持ちする")]
    [SerializeField] private GameObject rocketHeldVisualPrefab;
    [SerializeField] private Vector3 rocketHeldVisualLocalEulerAngles = Vector3.zero;
    [SerializeField] private Vector3 rocketHeldVisualLocalScale = Vector3.one;
    [Tooltip("Cosmic Retro Blasters Pack等のショックウェーブガン本体メッシュ。未設定ならプレースホルダーの立方体で構え持ちする")]
    [SerializeField] private GameObject shockwaveHeldVisualPrefab;
    [SerializeField] private Vector3 shockwaveHeldVisualLocalEulerAngles = Vector3.zero;
    [SerializeField] private Vector3 shockwaveHeldVisualLocalScale = Vector3.one;

    [Header("Blaster(ヒットスキャン通常武器)")]
    [Tooltip("Blasterの1発あたりのダメージ")]
    [SerializeField] private float blasterDamage = 8f;
    [Tooltip("熱ゲージが0(冷えている)時の連射間隔(秒)")]
    [SerializeField] private float blasterFireRateSeconds = 0.12f;
    [Tooltip("熱ゲージが満タン近くの時の連射間隔(秒)。熱ゲージに応じてblasterFireRateSecondsとの間を補間する")]
    [SerializeField] private float blasterMinFireRateSeconds = 0.05f;
    [SerializeField] private float blasterRange = 100f;
    [SerializeField] private LayerMask blasterHitMask = ~0;
    [Tooltip("発射音。全クライアントに聞こえるようClientRpcで再生する")]
    [SerializeField] private AudioClip blasterFireClip;
    [Tooltip("Blasterの発射音の音量")]
    [Range(0f, 1f)] [SerializeField] private float blasterFireVolume = 0.6f;
    [Tooltip("見た目の発射点(銃口)。weaponHolderローカル空間のオフセット。命中判定はカメラ基準のまま")]
    [SerializeField] private Vector3 blasterMuzzleLocalOffset = new Vector3(0f, -0.05f, 0.6f);
    [Tooltip("Blasterの着弾でボクセル建物を削る半径(ごく小さく、約1ボクセル分)")]
    [SerializeField] private float blasterVoxelChipRadius = 0.35f;
    [Tooltip("1個体あたりの所持弾数の上限(拾うとフル装填の新しい個体になる)")]
    [SerializeField] private int maxBlasterAmmo = 90;
    [Tooltip("この発数を連続で撃つと熱ゲージが満タンになりオーバーヒートする")]
    [SerializeField] private int blasterOverheatShotThreshold = 30;
    [Tooltip("満タンの熱ゲージが0まで冷える秒数(=オーバーヒート中に撃てない時間)")]
    [SerializeField] private float blasterOverheatDurationSeconds = 5f;
    [Tooltip("最後に発射してからこの秒数は熱ゲージが冷えない")]
    [SerializeField] private float blasterHeatDecayGraceSeconds = 1f;
    [Tooltip("オーバーヒートした瞬間の警告音(Ownerのみ再生)")]
    [SerializeField] private AudioClip blasterOverheatClip;
    [Range(0f, 1f)] [SerializeField] private float blasterOverheatVolume = 0.6f;

    [Header("Bomb(投擲・誘爆する爆発物)")]
    [SerializeField] private ExplosiveItem bombPrefab;
    [SerializeField] private float bombThrowForce = 20f;
    [Tooltip("投擲武器なので連続投げすぎを防ぐクールダウン")]
    [SerializeField] private float bombThrowCooldownSeconds = 0.6f;
    [Tooltip("予測線がここまで伸びたら(壁に当たらなくても)打ち切る")]
    [SerializeField] private float bombMaxThrowDistance = 25f;
    [SerializeField] private LayerMask trajectoryObstacleMask = ~0;
    [Tooltip("所持できるBombの最大個数")]
    [SerializeField] private int maxBombStack = 10;
    [Tooltip("投擲の軌道を山なりにするため、狙い方向に足す上向き成分")]
    [SerializeField] private float throwArcUpwardBias = 0.35f;
    [Tooltip("Bomb以外のグレネード系統。投擲力/クールダウン/予測線はBombと共通")]
    [SerializeField] private GrenadeTypeEntry[] extraGrenadeTypes;

    [Header("RocketLauncher(着弾式・直進する爆発物。クロスヘアでそのまま狙って撃つのでBombのような" +
        "予測線は出さない。1発ごとにリロードが必要な弾数制限武器)")]
    [SerializeField] private ExplosiveItem rocketPrefab;
    [SerializeField] private float rocketLaunchForce = 45f;
    [Tooltip("所持できるRocketの最大数(スタックせず、拾うと常にこの数まで補充される)")]
    [SerializeField] private int rocketReserveCapacity = 5;
    [Tooltip("1発撃つごとに次弾を装填するまでの時間(秒)")]
    [SerializeField] private float rocketReloadDurationSeconds = 2.5f;
    [Tooltip("収束エイム用の距離。虚空を狙った場合のフォールバック距離でもある")]
    [SerializeField] private float rocketAimConvergenceDistance = 200f;
    [Tooltip("発射音。全クライアントに聞こえるようClientRpcで再生する")]
    [SerializeField] private AudioClip rocketFireClip;
    [Tooltip("装填音。Owner本人にだけその場で(サーバー往復を待たず)再生する")]
    [SerializeField] private AudioClip rocketReloadClip;
    [Tooltip("弾切れ/装填中に撃とうとした時の空撃ち音。Owner本人にだけ再生する")]
    [SerializeField] private AudioClip rocketDryFireClip;
    [Tooltip("マズルフラッシュ用のパーティクルマテリアル(Unlit推奨)。Blasterの発砲/着弾演出にも流用する")]
    [SerializeField] private Material rocketMuzzleFlashMaterial;

    [Header("ShockwaveGun(前方へ弾を飛ばし、着弾地点の敵を吹き飛ばす。建物は壊せない)")]
    [Tooltip("連射防止のクールダウン(秒)")]
    [SerializeField] private float shockwaveCooldownSeconds = 1.5f;
    [Tooltip("1個体あたりの所持弾数の上限(拾うとフル装填の新しい個体になる)")]
    [SerializeField] private int maxShockwaveStack = 6;
    [Tooltip("発射音。全クライアントに聞こえるようClientRpcで再生する")]
    [SerializeField] private AudioClip shockwaveFireClip;
    [Tooltip("ショックウェーブガンの発射音の音量")]
    [Range(0f, 1f)] [SerializeField] private float shockwaveFireVolume = 0.75f;
    [Tooltip("発射する飛翔体(ShockwaveProjectileを持つprefab)")]
    [SerializeField] private ShockwaveProjectile shockwaveProjectilePrefab;
    [Tooltip("飛翔体の初速(m/s)。重力の影響を受けず直進する")]
    [SerializeField] private float shockwaveProjectileSpeed = 50f;
    [Tooltip("発射位置を狙い方向へさらに前方へずらす距離(m)")]
    [SerializeField] private float shockwaveSpawnForwardOffset = 0.15f;
    [Tooltip("発射位置を上方向へずらす距離(m)。通常は0")]
    [SerializeField] private float shockwaveSpawnUpwardOffset = 0f;
    [Tooltip("RocketLauncherの飛翔体を、銃口(GetMuzzleOrigin)から狙い方向へさらに前方へずらす距離(m)")]
    [SerializeField] private float rocketSpawnForwardOffset = 0.15f;

    [Header("拾得")]
    [SerializeField] private float pickupRadius = 2.5f;
    [SerializeField] private LayerMask pickupMask = ~0;

    [Header("武器切り替え音")]
    [Tooltip("武器切り替え時の効果音(Ownerのみ再生)")]
    [SerializeField] private AudioClip weaponSwitchClip;
    [Range(0f, 1f)] [SerializeField] private float weaponSwitchVolume = 0.5f;

    // デバッグ用: 最初からBlasterを持たせるか(現在は無効)
    private const bool StartWithBlasterForDebug = false;

    private readonly NetworkVariable<int> _slot0 = new NetworkVariable<int>((int)WeaponType.None, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _slot1 = new NetworkVariable<int>((int)WeaponType.None, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _slot2 = new NetworkVariable<int>((int)WeaponType.None, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _slot3 = new NetworkVariable<int>((int)WeaponType.None, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);
    // 持ち替えは見た目だけなのでOwner権威にする。発砲/投擲はサーバー側でEquippedを検証する
    private readonly NetworkVariable<int> _activeSlot = new NetworkVariable<int>(0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);
    // グレネードの所持数。スロットごとに独立したスタック(GetGrenadeAmmoSlots/ServerTryGrantWeapon参照)
    private readonly NetworkVariable<int> _bombCount0 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _bombCount1 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _bombCount2 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _bombCount3 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _heavyBombCount0 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _heavyBombCount1 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _heavyBombCount2 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _heavyBombCount3 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _fireBombCount0 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _fireBombCount1 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _fireBombCount2 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _fireBombCount3 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _freezeBombCount0 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _freezeBombCount1 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _freezeBombCount2 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _freezeBombCount3 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _splitBombCount0 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _splitBombCount1 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _splitBombCount2 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _splitBombCount3 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private NetworkVariable<int>[] _bombCountSlots;
    private NetworkVariable<int>[] _heavyBombCountSlots;
    private NetworkVariable<int>[] _fireBombCountSlots;
    private NetworkVariable<int>[] _freezeBombCountSlots;
    private NetworkVariable<int>[] _splitBombCountSlots;

    // 銃はスタックせず、個体=スロットごとに弾数(Blasterはさらに熱ゲージ/オーバーヒート状態)を持つ
    private readonly NetworkVariable<int> _blasterAmmo0 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _blasterAmmo1 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _blasterAmmo2 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _blasterAmmo3 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<float> _blasterHeat0 = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<float> _blasterHeat1 = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<float> _blasterHeat2 = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<float> _blasterHeat3 = new NetworkVariable<float>(0f, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<bool> _blasterOverheated0 = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<bool> _blasterOverheated1 = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<bool> _blasterOverheated2 = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<bool> _blasterOverheated3 = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    // サーバー側のみ: スロットごとの最後の発射時刻(熱ゲージ減衰の猶予判定用)
    private readonly float[] _blasterLastFireTimers = { float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity };

    private readonly NetworkVariable<int> _shockwaveAmmo0 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _shockwaveAmmo1 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _shockwaveAmmo2 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _shockwaveAmmo3 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);

    // RocketLauncherもスロットごとに独立した弾数/リロード状態を持つ
    private readonly NetworkVariable<int> _rocketReserve0 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _rocketReserve1 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _rocketReserve2 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<int> _rocketReserve3 = new NetworkVariable<int>(0, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<bool> _rocketReloading0 = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<bool> _rocketReloading1 = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<bool> _rocketReloading2 = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    private readonly NetworkVariable<bool> _rocketReloading3 = new NetworkVariable<bool>(false, NetworkVariableReadPermission.Owner, NetworkVariableWritePermission.Server);
    // サーバー側のみ: スロットごとのリロード残り時間(Update()参照)
    private readonly float[] _rocketReloadTimers = new float[HotbarSize];

    public int ActiveSlotIndex => _activeSlot.Value;
    public WeaponType Equipped => GetSlot(_activeSlot.Value);
    /// <summary>Owner本人だけが見られる、現在構えているBombインスタンスの所持数(スロット固有)</summary>
    public int BombCount => GetGrenadeAmmoVar(WeaponType.Bomb, _activeSlot.Value)?.Value ?? 0;

    /// <summary>Bomb/HeavyBomb/FireBomb/FreezeBomb/SplitBombを「グレネード系統」として判定する</summary>
    public static bool IsGrenadeWeaponType(WeaponType type) =>
        type == WeaponType.Bomb || type == WeaponType.HeavyBomb || type == WeaponType.FireBomb ||
        type == WeaponType.FreezeBomb || type == WeaponType.SplitBomb;

    private GrenadeTypeEntry FindGrenadeTypeEntry(WeaponType type)
    {
        if (extraGrenadeTypes == null) return null;
        foreach (var entry in extraGrenadeTypes)
        {
            if (entry != null && entry.weaponType == type) return entry;
        }
        return null;
    }

    /// <summary>種類ごとの4スロットぶんの所持数NetworkVariableをまとめた配列を返す(遅延初期化)</summary>
    private NetworkVariable<int>[] GetGrenadeAmmoSlots(WeaponType type)
    {
        switch (type)
        {
            case WeaponType.Bomb:
                return _bombCountSlots ??= new[] { _bombCount0, _bombCount1, _bombCount2, _bombCount3 };
            case WeaponType.HeavyBomb:
                return _heavyBombCountSlots ??= new[] { _heavyBombCount0, _heavyBombCount1, _heavyBombCount2, _heavyBombCount3 };
            case WeaponType.FireBomb:
                return _fireBombCountSlots ??= new[] { _fireBombCount0, _fireBombCount1, _fireBombCount2, _fireBombCount3 };
            case WeaponType.FreezeBomb:
                return _freezeBombCountSlots ??= new[] { _freezeBombCount0, _freezeBombCount1, _freezeBombCount2, _freezeBombCount3 };
            case WeaponType.SplitBomb:
                return _splitBombCountSlots ??= new[] { _splitBombCount0, _splitBombCount1, _splitBombCount2, _splitBombCount3 };
            default:
                return null;
        }
    }

    /// <summary>特定スロットの所持数NetworkVariable</summary>
    private NetworkVariable<int> GetGrenadeAmmoVar(WeaponType type, int slot)
    {
        var slots = GetGrenadeAmmoSlots(type);
        return slots != null && slot >= 0 && slot < slots.Length ? slots[slot] : null;
    }

    private ExplosiveItem GetGrenadePrefab(WeaponType type) =>
        type == WeaponType.Bomb ? bombPrefab : FindGrenadeTypeEntry(type)?.explosivePrefab;

    private int GetGrenadeMaxStack(WeaponType type) =>
        type == WeaponType.Bomb ? maxBombStack : (FindGrenadeTypeEntry(type)?.maxStack ?? 5);
    /// <summary>Owner本人だけが見られる、構えているShockwaveGunの残り弾数(個体ごとに別々)</summary>
    public int ShockwaveCount => ShockwaveAmmoVar(_activeSlot.Value).Value;
    /// <summary>Owner本人だけが見られる、構えているBlasterの残り弾数(個体ごとに別々)</summary>
    public int BlasterAmmo => BlasterAmmoVar(_activeSlot.Value).Value;
    /// <summary>Owner本人だけが見られる、構えているBlasterの熱ゲージ(0〜1)</summary>
    public float BlasterHeat01 => BlasterHeatVar(_activeSlot.Value).Value;
    /// <summary>Owner本人だけが見られる、現在構えているBlasterインスタンスが現在オーバーヒート中かどうか</summary>
    public bool IsBlasterOverheated => BlasterOverheatedVar(_activeSlot.Value).Value;
    /// <summary>Owner本人だけが見られる、現在構えているRocketLauncherインスタンスの残り弾数</summary>
    public int RocketReserve => RocketReserveVar(_activeSlot.Value).Value;
    /// <summary>Owner本人だけが見られる、現在構えているRocketLauncherインスタンスが次弾装填中かどうか</summary>
    public bool IsRocketReloading => RocketReloadingVar(_activeSlot.Value).Value;

    private NetworkVariable<int> ShockwaveAmmoVar(int slot)
    {
        switch (slot)
        {
            case 0: return _shockwaveAmmo0;
            case 1: return _shockwaveAmmo1;
            case 2: return _shockwaveAmmo2;
            default: return _shockwaveAmmo3;
        }
    }

    private NetworkVariable<int> BlasterAmmoVar(int slot)
    {
        switch (slot)
        {
            case 0: return _blasterAmmo0;
            case 1: return _blasterAmmo1;
            case 2: return _blasterAmmo2;
            default: return _blasterAmmo3;
        }
    }

    private NetworkVariable<float> BlasterHeatVar(int slot)
    {
        switch (slot)
        {
            case 0: return _blasterHeat0;
            case 1: return _blasterHeat1;
            case 2: return _blasterHeat2;
            default: return _blasterHeat3;
        }
    }

    private NetworkVariable<bool> BlasterOverheatedVar(int slot)
    {
        switch (slot)
        {
            case 0: return _blasterOverheated0;
            case 1: return _blasterOverheated1;
            case 2: return _blasterOverheated2;
            default: return _blasterOverheated3;
        }
    }

    private NetworkVariable<int> RocketReserveVar(int slot)
    {
        switch (slot)
        {
            case 0: return _rocketReserve0;
            case 1: return _rocketReserve1;
            case 2: return _rocketReserve2;
            default: return _rocketReserve3;
        }
    }

    private NetworkVariable<bool> RocketReloadingVar(int slot)
    {
        switch (slot)
        {
            case 0: return _rocketReloading0;
            case 1: return _rocketReloading1;
            case 2: return _rocketReloading2;
            default: return _rocketReloading3;
        }
    }

    private float _nextBlasterFireTime;
    private float _nextBombThrowTime;
    private float _nextShockwaveFireTime;
    private bool _wasFireHeldLastFrame;
    private bool _bombThrowCancelled;

    private PlayerController _playerControllerCache;
    /// <summary>発砲/投擲の瞬間に体をカメラの向きへ向け、一定時間その向きを保つ</summary>
    private void TriggerAimFacingOverride()
    {
        if (_playerControllerCache == null) _playerControllerCache = GetComponent<PlayerController>();
        _playerControllerCache?.TriggerAimFacingOverride();
    }

    /// <summary>当フレームの入力に基づく狙い方向。camera.forwardはLateUpdateまで更新が遅れるため、Update内ではこちらを使う</summary>
    private Vector3 GetCurrentAimForward()
    {
        if (_playerControllerCache == null) _playerControllerCache = GetComponent<PlayerController>();
        return _playerControllerCache != null ? _playerControllerCache.CurrentAimForward : playerCamera.transform.forward;
    }


    private GameObject _heldVisual;
    private WeaponType _heldVisualType = WeaponType.None;
    private LineRenderer _trajectoryLine;

    [Header("死亡時に武器を手放す")]
    [Tooltip("死亡時、構えていた武器を独立した物理挙動の落し物として手放す")]
    [SerializeField] private float weaponDropForce = 3f;
    [SerializeField] private float weaponDropDestroyDelaySeconds = 8f;

    private Health _health;
    private bool _wasDead;

    private void Awake()
    {
        _health = GetComponent<Health>();
    }

    public override void OnNetworkSpawn()
    {
        _slot0.OnValueChanged += (_, __) => HandleEquippedMaybeChanged();
        _slot1.OnValueChanged += (_, __) => HandleEquippedMaybeChanged();
        _slot2.OnValueChanged += (_, __) => HandleEquippedMaybeChanged();
        _slot3.OnValueChanged += (_, __) => HandleEquippedMaybeChanged();
        _activeSlot.OnValueChanged += (previousSlot, _) =>
        {
            HandleEquippedMaybeChanged();
            if (IsServer) ServerResetReloadIfSwitchedAway(previousSlot);
        };
        HandleEquippedMaybeChanged();

        // 命中確認のヒットマーカー。staticイベントなのでOwnerのみ購読し、Despawn時に必ず解除する
        if (IsOwner) Health.LocalHitConfirmed += HandleLocalHitConfirmed;
    }

    public override void OnNetworkDespawn()
    {
        if (IsOwner) Health.LocalHitConfirmed -= HandleLocalHitConfirmed;
    }

    private float _hitMarkerExpireTime;
    private bool _hitMarkerIsKill;
    private const float HitMarkerDurationSeconds = 0.2f;
    private const float HitMarkerKillDurationSeconds = 0.4f;

    /// <summary>Health.LocalHitConfirmedの購読先。ヒットマーカーの表示を開始する</summary>
    private void HandleLocalHitConfirmed(Health victim, float amount, bool isKill, Health.DamageType damageType)
    {
        _hitMarkerIsKill = isKill;
        _hitMarkerExpireTime = Time.time + (isKill ? HitMarkerKillDurationSeconds : HitMarkerDurationSeconds);
        Debug.Log($"[PlayerWeapon] ヒットマーカーを表示開始 isKill={isKill} expire={_hitMarkerExpireTime}");
    }

    /// <summary>装備が変わりうるイベントを受け、持ち手の見た目を更新する(UIの更新はOwnerのみ)</summary>
    private void HandleEquippedMaybeChanged()
    {
        RefreshHeldVisualIfNeeded();
        if (IsOwner)
        {
            RefreshFireButtonIcon();
            NotifyWeaponNamePopupIfChanged();
        }
    }

    private WeaponType _lastNotifiedEquipped = WeaponType.None;
    private float _weaponNamePopupExpireTime;
    private const float WeaponNamePopupDurationSeconds = 2f;
    private const float WeaponNamePopupFadeSeconds = 0.4f;

    /// <summary>装備の実際の変化を追跡し、武器名ポップアップと切り替え音を出す(武器を手放した時は出さない)</summary>
    private void NotifyWeaponNamePopupIfChanged()
    {
        WeaponType current = Equipped;
        if (current == _lastNotifiedEquipped) return;
        _lastNotifiedEquipped = current;
        if (current == WeaponType.None) return;
        _weaponNamePopupExpireTime = Time.time + WeaponNamePopupDurationSeconds;
        PlayWeaponSwitchSound();
    }

    /// <summary>Owner本人にのみ、その場でローカル再生する</summary>
    private void PlayWeaponSwitchSound()
    {
        if (weaponSwitchClip == null) return;
        float scale = SoundManager.Instance != null ? SoundManager.Instance.SfxVolumeScale : 1f;
        SfxPlayer.PlayAt(weaponSwitchClip, transform.position, weaponSwitchVolume * scale);
    }

    private string GetWeaponDisplayName(WeaponType type) => type switch
    {
        WeaponType.Blaster => Localization.Get("weapon.blaster"),
        WeaponType.Bomb => Localization.Get("weapon.bomb"),
        WeaponType.RocketLauncher => Localization.Get("weapon.rocketLauncher"),
        WeaponType.ShockwaveGun => Localization.Get("weapon.shockwaveGun"),
        // 追加グレネード種はextraGrenadeTypesのdisplayNameを使う
        _ => FindGrenadeTypeEntry(type)?.displayName ?? string.Empty,
    };

    /// <summary>ホットバーの上に武器名を表示し、一定時間後にフェードアウトする</summary>
    private void DrawWeaponNamePopup(float hotbarY)
    {
        float remaining = _weaponNamePopupExpireTime - Time.time;
        if (remaining <= -WeaponNamePopupFadeSeconds) return;

        float alpha = remaining > 0f ? 1f : Mathf.Clamp01(1f + remaining / WeaponNamePopupFadeSeconds);
        if (alpha <= 0f) return;

        string text = GetWeaponDisplayName(_lastNotifiedEquipped);
        if (string.IsNullOrEmpty(text)) return;

        var style = new GUIStyle(GUI.skin.label) { fontSize = 24, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        var shadowStyle = new GUIStyle(style);
        shadowStyle.normal.textColor = new Color(0f, 0f, 0f, 0.6f * alpha);
        style.normal.textColor = new Color(PopUI.Yellow.r, PopUI.Yellow.g, PopUI.Yellow.b, alpha);

        const float width = 320f;
        const float height = 40f;
        var rect = new Rect(GUIScaler.VirtualWidth / 2f - width / 2f, hotbarY - height - 12f, width, height);
        GUI.Label(new Rect(rect.x + 2f, rect.y + 2f, rect.width, rect.height), text, shadowStyle);
        GUI.Label(rect, text, style);
    }

    private NetworkVariable<int> SlotVar(int index)
    {
        switch (index)
        {
            case 0: return _slot0;
            case 1: return _slot1;
            case 2: return _slot2;
            default: return _slot3;
        }
    }

    public WeaponType GetSlot(int index) => (WeaponType)SlotVar(Mathf.Clamp(index, 0, HotbarSize - 1)).Value;

    /// <summary>Owner側のPlayerController.Update()から毎フレーム呼ぶ。発砲/投擲は試合中だけ受け付ける</summary>
    public void HandleInput(PlayerInputFrame frame)
    {
        if (!IsOwner) return;

        if (frame.weaponSwitchIndexOrNegativeOne >= 0 && frame.weaponSwitchIndexOrNegativeOne < HotbarSize)
        {
            _activeSlot.Value = frame.weaponSwitchIndexOrNegativeOne;
        }

        if (frame.wantsPickupDown)
        {
            RequestPickupServerRpc();
        }

        if (frame.wantsDropDown)
        {
            RequestDropWeaponServerRpc(_activeSlot.Value);
        }

        UpdatePickupButtonVisibility();

        bool combatActive = MatchManager.Instance == null || MatchManager.Instance.IsMatchActiveForInput;
        UpdateFireAndAim(combatActive && frame.wantsFirePrimaryHeld);
    }

    private float _nextPickupProximityCheckTime;

    /// <summary>拾える物が範囲内にある時だけ拾得ボタンを表示する(UI用の軽い判定。実際の可否はサーバー側で検証)</summary>
    private void UpdatePickupButtonVisibility()
    {
        if (Time.time < _nextPickupProximityCheckTime) return;
        _nextPickupProximityCheckTime = Time.time + 0.2f;

        bool near = false;
        Collider[] hits = Physics.OverlapSphere(transform.position, pickupRadius, pickupMask, QueryTriggerInteraction.Collide);
        foreach (var col in hits)
        {
            var pickup = col.GetComponentInParent<WeaponPickup>();
            if (pickup == null || pickup.IsConsumed) continue;
            near = true;
            break;
        }

        TouchInputProvider.Instance?.SetPickupButtonVisible(near);
    }

    /// <summary>装備に応じた入力処理。Blaster/Rocket/Shockwaveは押している間発射、グレネード系は押して予測線表示・離して投擲</summary>
    private void UpdateFireAndAim(bool fireHeld)
    {
        switch (Equipped)
        {
            case WeaponType.Blaster:
                HideTrajectoryPreview();
                if (fireHeld) TryFireBlaster();
                break;

            case WeaponType.Bomb:
            case WeaponType.HeavyBomb:
            case WeaponType.FireBomb:
            case WeaponType.FreezeBomb:
            case WeaponType.SplitBomb:
                if (fireHeld)
                {
                    ShowTrajectoryPreview(GetThrowOrigin(), GetThrowDirection() * bombThrowForce, true, bombMaxThrowDistance);
                }
                else if (_wasFireHeldLastFrame)
                {
                    HideTrajectoryPreview();
                    // 構え中にキャンセルボタンが押されていたら投げない(次の構えのためフラグはリセットする)
                    if (!_bombThrowCancelled) TryThrowBomb();
                    _bombThrowCancelled = false;
                }
                break;

            case WeaponType.RocketLauncher:
                // 直進する弾をクロスヘアで狙うので予測線は不要。Blasterと同じ押している間の連射型
                HideTrajectoryPreview();
                if (fireHeld) TryFireRocket(justPressed: !_wasFireHeldLastFrame);
                break;

            case WeaponType.ShockwaveGun:
                // Blasterと同じ押している間の連射型(予測線は不要)
                HideTrajectoryPreview();
                if (fireHeld) TryFireShockwave();
                break;

            default:
                HideTrajectoryPreview();
                break;
        }

        _wasFireHeldLastFrame = fireHeld;
    }

    private void TryFireBlaster()
    {
        if (playerCamera == null) return;
        if (Time.time < _nextBlasterFireTime) return;
        int slot = _activeSlot.Value;
        if (BlasterOverheatedVar(slot).Value) return; // ローカルの早期リターン(最終判定はサーバー側)
        if (BlasterAmmoVar(slot).Value <= 0) return; // ローカルの早期リターン(最終判定はサーバー側)

        // 熱ゲージ(サーバー権威、スロットごと)が高いほど次弾までの間隔を短くする
        float rateSeconds = Mathf.Lerp(blasterFireRateSeconds, blasterMinFireRateSeconds, BlasterHeatVar(slot).Value);
        _nextBlasterFireTime = Time.time + rateSeconds;

        // カメラの位置/向きは、TriggerAimFacingOverride()による体の回転に引きずられる前に確保してRPCへ渡す
        Vector3 aimOrigin = playerCamera.transform.position;
        Vector3 aimForward = GetCurrentAimForward();

        TriggerAimFacingOverride();
        RequestBlasterFireServerRpc(aimOrigin, aimForward, GetBlasterMuzzleOrigin());
    }

    /// <summary>見た目(トレーサー/マズルフラッシュ)の起点となる仮想的な銃口位置(blasterMuzzleLocalOffset参照)</summary>
    private Vector3 GetBlasterMuzzleOrigin()
    {
        return weaponHolder != null ? weaponHolder.TransformPoint(blasterMuzzleLocalOffset) : GetThrowOrigin();
    }

    /// <summary>持っている武器の見た目(実メッシュ)の外接AABBを狙い方向へ投影した先端を銃口位置として返す。
    /// minForwardFromHandは手元からの最低距離(体に埋まらないための下限)で、見た目が取れない/異常値の場合のフォールバックにもなる</summary>
    private Vector3 GetMuzzleOrigin(float minForwardFromHand)
    {
        Vector3 hand = GetThrowOrigin();
        if (weaponHolder == null) return hand;

        Vector3 forward = weaponHolder.forward;
        Vector3 fallback = hand + forward * minForwardFromHand;
        if (!TryGetHeldVisualBounds(out Bounds bounds)) return fallback;

        // AABBを任意方向へ投影した時の半径(サポート関数): 各軸の半径×方向成分の絶対値の和
        float reach = bounds.extents.x * Mathf.Abs(forward.x)
            + bounds.extents.y * Mathf.Abs(forward.y)
            + bounds.extents.z * Mathf.Abs(forward.z);
        Vector3 tip = bounds.center + forward * reach;

        float forwardFromHand = Vector3.Dot(tip - hand, forward);
        if (forwardFromHand < minForwardFromHand) tip += forward * (minForwardFromHand - forwardFromHand);

        // 見た目メッシュに巨大な付属物が混ざっている等の異常値ガード
        const float maxMuzzleDistanceFromHand = 3f;
        if ((tip - hand).sqrMagnitude > maxMuzzleDistanceFromHand * maxMuzzleDistanceFromHand) return fallback;
        return tip;
    }

    /// <summary>持ち手の見た目配下の描画対象(MeshRenderer/SkinnedMeshRenderer)の外接AABBをワールド空間で返す</summary>
    private bool TryGetHeldVisualBounds(out Bounds bounds)
    {
        bounds = default;
        if (_heldVisual == null) return false;

        bool found = false;
        foreach (Renderer r in _heldVisual.GetComponentsInChildren<Renderer>())
        {
            if (!r.enabled) continue;
            if (!(r is MeshRenderer) && !(r is SkinnedMeshRenderer)) continue;
            if (!found)
            {
                bounds = r.bounds;
                found = true;
            }
            else
            {
                bounds.Encapsulate(r.bounds);
            }
        }
        return found;
    }

    /// <summary>手元→銃口の間に障害物がある場合、飛翔体が壁の中で即爆発しないよう障害物の手前で止める</summary>
    private Vector3 ClampSpawnAgainstObstacles(Vector3 from, Vector3 to)
    {
        Vector3 delta = to - from;
        float distance = delta.magnitude;
        if (distance < 0.01f) return to;

        Vector3 direction = delta / distance;
        RaycastHit[] hits = Physics.RaycastAll(from, direction, distance, trajectoryObstacleMask, QueryTriggerInteraction.Ignore);

        // 自分自身のコライダーは障害物として扱わない
        float nearest = float.MaxValue;
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider.transform.root == transform.root) continue;
            if (hit.distance < nearest) nearest = hit.distance;
        }

        return nearest < float.MaxValue ? from + direction * Mathf.Max(0f, nearest - 0.05f) : to;
    }

    private void TryThrowBomb()
    {
        WeaponType type = Equipped;
        if (!IsGrenadeWeaponType(type)) return;
        if (playerCamera == null) return;
        if (Time.time < _nextBombThrowTime) return;
        _nextBombThrowTime = Time.time + bombThrowCooldownSeconds;

        // カメラの向きはスナップ前に確保する(TryFireBlaster参照)
        Vector3 aimForward = GetCurrentAimForward();
        TriggerAimFacingOverride();
        RequestThrowBombServerRpc(type, GetThrowOrigin(), GetThrowDirection(aimForward));
    }

    /// <summary>justPressed=このフレームで初めて押されたか(空撃ち音は押した瞬間だけ鳴らす)</summary>
    private void TryFireRocket(bool justPressed)
    {
        if (playerCamera == null) return;

        // ローカルの早期リターン(最終判定はサーバー側)
        int slot = _activeSlot.Value;
        if (RocketReloadingVar(slot).Value || RocketReserveVar(slot).Value <= 0)
        {
            if (justPressed && rocketDryFireClip != null) SfxPlayer.PlayAt(rocketDryFireClip, transform.position, SfxPlayer.CurrentSfxScale);
            return;
        }

        // 装填音はサーバー往復を待たずOwner本人にその場で再生する
        if (rocketReloadClip != null) SfxPlayer.PlayAt(rocketReloadClip, transform.position, SfxPlayer.CurrentSfxScale);

        // 発射位置は武器の見た目の銃口。狙い方向も銃口からカメラの収束点へ向かうよう計算し直す
        // カメラの位置/向きはスナップ前に確保する(TryFireBlaster参照)
        Vector3 aimOrigin = playerCamera.transform.position;
        Vector3 aimForward = GetCurrentAimForward();

        TriggerAimFacingOverride();
        Vector3 rocketMuzzle = ClampSpawnAgainstObstacles(GetThrowOrigin(), GetMuzzleOrigin(0.6f));
        RequestFireRocketServerRpc(rocketMuzzle, GetAimDirection(rocketMuzzle, aimOrigin, aimForward));
    }

    private void TryFireShockwave()
    {
        if (playerCamera == null) return;
        if (Time.time < _nextShockwaveFireTime) return;
        if (ShockwaveAmmoVar(_activeSlot.Value).Value <= 0) return; // ローカルの早期リターン(最終判定はサーバー側)
        _nextShockwaveFireTime = Time.time + shockwaveCooldownSeconds;

        // カメラの向きはスナップ前に確保する(TryFireBlaster参照)
        Vector3 aimForward = GetCurrentAimForward();
        TriggerAimFacingOverride();
        Vector3 shockwaveMuzzle = ClampSpawnAgainstObstacles(GetThrowOrigin(), GetMuzzleOrigin(0.6f));
        RequestFireShockwaveServerRpc(shockwaveMuzzle, aimForward);
    }

    /// <summary>Bombの投擲/予測線の起点。狙いはカメラ基準のまま、起点だけ手元(weaponHolder)にする</summary>
    private Vector3 GetThrowOrigin()
    {
        return weaponHolder != null ? weaponHolder.position : playerCamera.transform.position;
    }

    /// <summary>throwArcUpwardBias参照。狙い方向に上向き成分を足して山なりにする(予測線の表示中はこちらで最新の向きを読む)</summary>
    private Vector3 GetThrowDirection() => GetThrowDirection(GetCurrentAimForward());

    /// <summary>実際に投げる瞬間用。スナップ前に確保したカメラ向きを渡す</summary>
    private Vector3 GetThrowDirection(Vector3 cameraForward)
    {
        Vector3 biased = cameraForward + Vector3.up * throwArcUpwardBias;
        return biased.normalized;
    }

    /// <summary>RocketLauncherの狙い方向。カメラの狙い先(収束点)へ向かう方向を発射位置から計算し、着弾点をクロスヘアに一致させる</summary>
    private Vector3 GetAimDirection(Vector3 fromOrigin, Vector3 cameraOrigin, Vector3 cameraForward)
    {
        bool hitSomething = Physics.Raycast(cameraOrigin, cameraForward, out RaycastHit hit,
            rocketAimConvergenceDistance, trajectoryObstacleMask, QueryTriggerInteraction.Ignore);
        Vector3 convergencePoint = hitSomething ? hit.point : cameraOrigin + cameraForward * rocketAimConvergenceDistance;

        Vector3 direction = convergencePoint - fromOrigin;
        return direction.sqrMagnitude > 0.0001f ? direction.normalized : cameraForward;
    }

    /// <summary>Blasterの発射(ヒットスキャン)。判定はカメラ位置基準、トレーサー/マズルフラッシュは銃口起点で命中の有無に関わらず表示する</summary>
    [ServerRpc]
    private void RequestBlasterFireServerRpc(Vector3 cameraOrigin, Vector3 direction, Vector3 muzzleOrigin, ServerRpcParams rpcParams = default)
    {
        if (Equipped != WeaponType.Blaster) return;

        // 弾数/熱ゲージ/オーバーヒートは構えているスロットの個体固有の状態を見る
        int slot = _activeSlot.Value;
        var ammoVar = BlasterAmmoVar(slot);
        var heatVar = BlasterHeatVar(slot);
        var overheatedVar = BlasterOverheatedVar(slot);

        // 弾数/熱ゲージの権威的な判定。熱ゲージは1発ごとに増え、満タンでオーバーヒートする(冷却はUpdate()側)
        if (overheatedVar.Value) return;
        if (ammoVar.Value <= 0) return;

        // float誤差で閾値ちょうどの発数で満タンにならないことがあるため、わずかな許容誤差を設ける
        const float overheatEpsilon = 0.0005f;
        heatVar.Value = Mathf.Min(1f, heatVar.Value + 1f / blasterOverheatShotThreshold);
        if (heatVar.Value >= 1f - overheatEpsilon)
        {
            heatVar.Value = 1f; // ゲージ表示を満タンに揃える
            overheatedVar.Value = true;

            // 警告音はOwner本人宛てのClientRpcで鳴らす
            var ownerOnlyParams = new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = new[] { rpcParams.Receive.SenderClientId } } };
            NotifyBlasterOverheatedClientRpc(ownerOnlyParams);
        }
        _blasterLastFireTimers[slot] = Time.time; // 撃った直後は熱ゲージを冷やさない猶予期間(Update()参照)

        ammoVar.Value -= 1;
        if (ammoVar.Value <= 0) ServerRemoveFromActiveSlot(); // 弾切れで自動的に手放す

        ulong shooterId = rpcParams.Receive.SenderClientId;

        // RaycastAllで距離順に調べ、自分自身のコライダーは読み飛ばして先の本当の命中/未命中を採用する
        // WeaponPickupのコライダーはトリガーなので、Collideにして命中候補に含める
        RaycastHit[] hits = Physics.RaycastAll(cameraOrigin, direction, blasterRange, blasterHitMask, QueryTriggerInteraction.Collide);
        System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

        bool didHit = false;
        RaycastHit hit = default;
        foreach (var candidate in hits)
        {
            var candidateHealth = candidate.collider.GetComponentInParent<Health>();
            if (candidateHealth != null && candidateHealth.OwnerClientId == shooterId) continue; // 自分自身は素通りする
            didHit = true;
            hit = candidate;
            break;
        }
        Vector3 endPoint = didHit ? hit.point : cameraOrigin + direction * blasterRange;

        if (didHit)
        {
            var health = hit.collider.GetComponentInParent<Health>();
            if (health != null)
            {
                health.ServerApplyDamage(blasterDamage, shooterId);
            }
            else
            {
                // 未起爆のグレネード本体に当たった場合はこちらを優先して処理する
                var explosive = hit.collider.GetComponentInParent<ExplosiveItem>();
                var pickup = hit.collider.GetComponentInParent<WeaponPickup>();
                if (explosive != null)
                {
                    explosive.ServerApplyGunfireHit(hit.point, direction, shooterId);
                }
                else if (pickup != null && !pickup.IsConsumed)
                {
                    // 落ちているグレネード(拾い物)への被弾は、物理反応と3発目での誘爆をWeaponPickup側に任せる
                    pickup.ServerApplyGunfireHit(hit.point, direction, shooterId);
                }
                else
                {
                    // プレイヤー以外に命中した場合、ボクセル建物ならごく小さい半径で削る
                    var voxelBuilding = hit.collider.GetComponentInParent<VoxelBuildingPart>();
                    if (voxelBuilding != null) voxelBuilding.ServerApplyExplosion(hit.point, blasterVoxelChipRadius, allowStructuralCollapse: false);
                }
            }
        }

        PlayBlasterFireEffectsClientRpc(muzzleOrigin, endPoint, didHit);
        Debug.Log($"[PlayerWeapon] client {shooterId} がBlasterを発射しました (命中={didHit})");
    }

    /// <summary>全クライアントでトレーサー+マズルフラッシュ(+命中時は着弾の火花)を再生する</summary>
    [ClientRpc]
    private void PlayBlasterFireEffectsClientRpc(Vector3 muzzleOrigin, Vector3 endPoint, bool didHit)
    {
        if (blasterFireClip != null)
        {
            float scale = SoundManager.Instance != null ? SoundManager.Instance.SfxVolumeScale : 1f;
            SfxPlayer.PlayAt(blasterFireClip, muzzleOrigin, blasterFireVolume * scale);
        }
        SpawnBlasterTracer(muzzleOrigin, endPoint);
        SpawnMuzzleFlashEffect(muzzleOrigin, endPoint - muzzleOrigin);
        if (didHit) SpawnMuzzleFlashEffect(endPoint, Vector3.up); // 着弾の火花として同じ演出を流用
    }

    /// <summary>オーバーヒートに入った瞬間、Owner本人にのみ警告音を鳴らすClientRpc</summary>
    [ClientRpc]
    private void NotifyBlasterOverheatedClientRpc(ClientRpcParams rpcParams = default)
    {
        if (blasterOverheatClip == null) return;
        float scale = SoundManager.Instance != null ? SoundManager.Instance.SfxVolumeScale : 1f;
        SfxPlayer.PlayAt(blasterOverheatClip, transform.position, blasterOverheatVolume * scale);
    }

    /// <summary>発射位置〜着弾位置を結ぶ線を一瞬表示して弾道を表現する</summary>
    private void SpawnBlasterTracer(Vector3 origin, Vector3 endPoint)
    {
        var go = new GameObject("BlasterTracer");
        var lr = go.AddComponent<LineRenderer>();
        if (trajectoryLineMaterial != null) lr.sharedMaterial = trajectoryLineMaterial;
        lr.positionCount = 2;
        lr.SetPosition(0, origin);
        lr.SetPosition(1, endPoint);
        lr.startWidth = lr.endWidth = 0.03f;
        lr.useWorldSpace = true;
        lr.receiveShadows = false;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        Destroy(go, 0.08f);
    }

    [ServerRpc]
    private void RequestThrowBombServerRpc(WeaponType type, Vector3 origin, Vector3 direction, ServerRpcParams rpcParams = default)
    {
        if (Equipped != type || !IsGrenadeWeaponType(type)) return;
        NetworkVariable<int> ammoVar = GetGrenadeAmmoVar(type, _activeSlot.Value);
        ExplosiveItem prefab = GetGrenadePrefab(type);
        if (ammoVar == null || ammoVar.Value <= 0 || prefab == null) return;

        ulong throwerId = rpcParams.Receive.SenderClientId;

        ammoVar.Value -= 1;
        if (ammoVar.Value <= 0) ServerRemoveFromActiveSlot();

        ExplosiveItem bomb = Instantiate(prefab, origin + direction * 0.6f, Quaternion.identity);
        bomb.NetworkObject.Spawn(true);
        bomb.ServerInitialize(throwerId, direction * bombThrowForce);

        Debug.Log($"[PlayerWeapon] client {throwerId} が{type}を投擲しました (残り所持数 {ammoVar.Value})");
    }

    [ServerRpc]
    private void RequestFireRocketServerRpc(Vector3 origin, Vector3 direction, ServerRpcParams rpcParams = default)
    {
        if (Equipped != WeaponType.RocketLauncher || rocketPrefab == null) return;

        int slot = _activeSlot.Value;
        var reserveVar = RocketReserveVar(slot);
        var reloadingVar = RocketReloadingVar(slot);
        if (reloadingVar.Value || reserveVar.Value <= 0) return;

        ulong shooterId = rpcParams.Receive.SenderClientId;

        reserveVar.Value -= 1;
        if (reserveVar.Value <= 0)
        {
            // 弾切れ。この個体は手放して枠を空ける
            ServerRemoveFromActiveSlot();
        }
        else
        {
            reloadingVar.Value = true;
            _rocketReloadTimers[slot] = rocketReloadDurationSeconds;
        }

        // detonateOnImpactなRocket.prefabで着弾即爆発させる。originは呼び出し側で銃口に計算済みのため、ここでは少し前へ出すだけ
        Vector3 rocketSpawnPosition = origin + direction * rocketSpawnForwardOffset;
        ExplosiveItem rocket = Instantiate(rocketPrefab, rocketSpawnPosition, Quaternion.LookRotation(direction));
        rocket.NetworkObject.Spawn(true);
        rocket.ServerInitialize(shooterId, direction * rocketLaunchForce);

        PlayRocketFireEffectsClientRpc(rocketSpawnPosition, direction);
        Debug.Log($"[PlayerWeapon] client {shooterId} がRocketLauncher(slot {slot})を発射しました (残弾 {reserveVar.Value}, リロード中={reloadingVar.Value})");
    }

    /// <summary>発射音/マズルフラッシュは周囲にも見える/聞こえるべきなので、サーバーから全クライアントへ配る</summary>
    [ClientRpc]
    private void PlayRocketFireEffectsClientRpc(Vector3 position, Vector3 direction)
    {
        if (rocketFireClip != null) SfxPlayer.PlayAt(rocketFireClip, position, SfxPlayer.CurrentSfxScale);
        SpawnMuzzleFlashEffect(position, direction);
    }

    /// <summary>ショックウェーブ弾(ShockwaveProjectile)の発射。ダメージ/ノックバックは飛翔体側が持ち、ここでは弾数の消費とSpawnだけ行う</summary>
    [ServerRpc]
    private void RequestFireShockwaveServerRpc(Vector3 origin, Vector3 direction, ServerRpcParams rpcParams = default)
    {
        if (Equipped != WeaponType.ShockwaveGun) return;
        if (shockwaveProjectilePrefab == null) return;

        // 構えているスロットの個体固有の弾数を見る(リロードは無く、クールダウンのみ)
        var ammoVar = ShockwaveAmmoVar(_activeSlot.Value);
        if (ammoVar.Value <= 0) return;

        ammoVar.Value -= 1;
        if (ammoVar.Value <= 0) ServerRemoveFromActiveSlot();

        ulong shooterId = rpcParams.Receive.SenderClientId;

        Vector3 spawnPosition = origin + direction * shockwaveSpawnForwardOffset + Vector3.up * shockwaveSpawnUpwardOffset;
        ShockwaveProjectile projectile = Instantiate(shockwaveProjectilePrefab, spawnPosition, Quaternion.LookRotation(direction));
        projectile.NetworkObject.Spawn(true);
        projectile.ServerInitialize(shooterId, direction * shockwaveProjectileSpeed);

        PlayShockwaveFireEffectsClientRpc(spawnPosition, direction);
        Debug.Log($"[PlayerWeapon] client {shooterId} がShockwaveGunを発射しました (残り弾数{ammoVar.Value})");
    }

    /// <summary>発射音/マズルフラッシュ(SpawnMuzzleFlashEffectを流用)。ダメージ等はShockwaveProjectile側が持つ</summary>
    [ClientRpc]
    private void PlayShockwaveFireEffectsClientRpc(Vector3 position, Vector3 direction)
    {
        if (shockwaveFireClip != null)
        {
            float scale = SoundManager.Instance != null ? SoundManager.Instance.SfxVolumeScale : 1f;
            SfxPlayer.PlayAt(shockwaveFireClip, position, shockwaveFireVolume * scale);
        }
        SpawnMuzzleFlashEffect(position, direction);
    }

    /// <summary>実行時にParticleSystemを組み立てて演出を出す(Unlit系固定でURP安全)。AddComponent直後は自動再生されるため設定前に停止する</summary>
    private void SpawnMuzzleFlashEffect(Vector3 position, Vector3 direction)
    {
        var go = new GameObject("RocketMuzzleFlash");
        go.transform.position = position;
        go.transform.rotation = direction.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(direction) : Quaternion.identity;

        var ps = go.AddComponent<ParticleSystem>();
        ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);

        var main = ps.main;
        main.playOnAwake = false;
        main.duration = 0.25f;
        main.loop = false;
        main.startLifetime = 0.15f;
        main.startSpeed = new ParticleSystem.MinMaxCurve(2f, 5f);
        main.startSize = new ParticleSystem.MinMaxCurve(0.3f, 0.6f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(1f, 0.9f, 0.5f), new Color(1f, 0.5f, 0.1f));
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.stopAction = ParticleSystemStopAction.Destroy;

        var emission = ps.emission;
        emission.rateOverTime = 0f;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 12) });

        var shape = ps.shape;
        shape.shapeType = ParticleSystemShapeType.Cone;
        shape.angle = 15f;
        shape.radius = 0.1f;

        var sizeOverLifetime = ps.sizeOverLifetime;
        sizeOverLifetime.enabled = true;
        sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 1f, 1f, 0f));

        var psRenderer = ps.GetComponent<ParticleSystemRenderer>();
        if (rocketMuzzleFlashMaterial != null)
        {
            ParticleVfxUtility.EnsureSoftRoundParticleTexture(rocketMuzzleFlashMaterial);
            psRenderer.sharedMaterial = rocketMuzzleFlashMaterial;
        }

        ps.Play();
    }

    /// <summary>毎フレーム呼ばれる。死亡時の武器放出は全クライアントで、リロード/熱ゲージの時間経過はサーバー側のみ行う</summary>
    private void Update()
    {
        UpdateWeaponDropOnDeath();

        if (IsOwner)
        {
            EnsureHotbarIconsGenerated();
            UpdateFireButtonIfNeeded();
        }

        if (!IsServer) return;

        int active = _activeSlot.Value;
        var reloading = RocketReloadingVar(active);
        if (reloading.Value)
        {
            _rocketReloadTimers[active] -= Time.deltaTime;
            if (_rocketReloadTimers[active] <= 0f)
            {
                reloading.Value = false;
            }
        }

        // 熱ゲージは満タンからblasterOverheatDurationSecondsで0になる速度で減衰させる(最後に撃ってからgrace秒は減衰しない)。
        // 4スロット全てを、装備の有無に関わらず毎フレーム処理する
        float decayPerSecond = 1f / Mathf.Max(0.01f, blasterOverheatDurationSeconds);
        for (int slot = 0; slot < HotbarSize; slot++)
        {
            var heatVar = BlasterHeatVar(slot);
            if (heatVar.Value <= 0f) continue;
            if (Time.time - _blasterLastFireTimers[slot] < blasterHeatDecayGraceSeconds) continue;

            heatVar.Value = Mathf.Max(0f, heatVar.Value - decayPerSecond * Time.deltaTime);
            var overheatedVar = BlasterOverheatedVar(slot);
            if (overheatedVar.Value && heatVar.Value <= 0f) overheatedVar.Value = false;
        }
    }

    /// <summary>装備を外した時にリロードタイマーをフル装填時間へ戻す(再装備すると最初からやり直し)</summary>
    private void ServerResetReloadIfSwitchedAway(int previousSlot)
    {
        if (previousSlot < 0 || previousSlot >= HotbarSize) return;
        if (!RocketReloadingVar(previousSlot).Value) return;
        _rocketReloadTimers[previousSlot] = rocketReloadDurationSeconds;
    }

    /// <summary>ホットバーのアイコンを、OnGUIより前のUpdateで1フレーム1個ずつ事前生成する(OnGUI内のRender衝突回避)</summary>
    private void EnsureHotbarIconsGenerated()
    {
        for (int i = 0; i < HotbarSize; i++)
        {
            WeaponType t = GetSlot(i);
            if (t == WeaponType.None) continue;
            WeaponIconRenderer.EnsureGenerated(GetHeldVisualPrefab(t));
        }
    }

    /// <summary>死亡した瞬間に一度だけ持ち手の見た目を手放す(本体のラグドールとは独立した物理挙動)</summary>
    private void UpdateWeaponDropOnDeath()
    {
        bool isDead = _health != null && _health.IsDead;

        if (!isDead)
        {
            _wasDead = false;
            return;
        }

        if (_wasDead) return;
        _wasDead = true;

        DropHeldVisual();
    }

    /// <summary>持ち手の見た目をCollider+Rigidbody付きの落し物にして手放す(拾えない演出用で一定時間で消える)</summary>
    private void DropHeldVisual()
    {
        if (_heldVisual == null) return;

        GameObject dropped = _heldVisual;
        _heldVisual = null;
        _heldVisualType = WeaponType.None;

        dropped.transform.SetParent(null, true);

        if (dropped.GetComponent<Collider>() == null)
        {
            dropped.AddComponent<BoxCollider>();
        }

        var rb = dropped.AddComponent<Rigidbody>();
        rb.mass = 0.3f;
        rb.linearDamping = 0.3f;
        rb.angularDamping = 0.1f;

        Vector3 randomDir = new Vector3(Random.Range(-1f, 1f), 0.6f, Random.Range(-1f, 1f)).normalized;
        rb.AddForce(randomDir * weaponDropForce, ForceMode.VelocityChange);
        Vector3 randomTorqueAxis = new Vector3(Random.Range(-1f, 1f), Random.Range(-1f, 1f), Random.Range(-1f, 1f));
        rb.AddTorque(randomTorqueAxis * weaponDropForce, ForceMode.VelocityChange);

        Destroy(dropped, weaponDropDestroyDelaySeconds);
    }

    [ServerRpc]
    private void RequestPickupServerRpc(ServerRpcParams rpcParams = default)
    {
        ulong requesterId = rpcParams.Receive.SenderClientId;

        Collider[] hits = Physics.OverlapSphere(transform.position, pickupRadius, pickupMask, QueryTriggerInteraction.Collide);
        WeaponPickup nearest = null;
        float nearestSqrDist = float.MaxValue;

        foreach (var col in hits)
        {
            var pickup = col.GetComponentInParent<WeaponPickup>();
            if (pickup == null || pickup.IsConsumed) continue;

            float sqrDist = (pickup.transform.position - transform.position).sqrMagnitude;
            if (sqrDist < nearestSqrDist)
            {
                nearestSqrDist = sqrDist;
                nearest = pickup;
            }
        }

        if (nearest == null)
        {
            Debug.Log($"[PlayerWeapon] client {requesterId} の拾得試行: 範囲内({pickupRadius}m)に拾える物が無い");
            return;
        }

        if (!ServerTryGrantWeapon(nearest.WeaponType, nearest.GrantAmount))
        {
            // 拾得失敗時は通知を出さずログのみ残す
            Debug.Log($"[PlayerWeapon] client {requesterId} が {nearest.WeaponType} の拾得に失敗(既に所持しているか、ホットバー/所持数が満杯)");
            return;
        }

        nearest.ServerConsume();
        Debug.Log($"[PlayerWeapon] client {requesterId} が {nearest.WeaponType} を拾いました");
    }

    /// <summary>指定スロットの武器を、残り所持数/残弾を引き継いだ拾い物(WeaponPickup)として前方へ生成して手放す</summary>
    [ServerRpc]
    private void RequestDropWeaponServerRpc(int slotIndex, ServerRpcParams rpcParams = default)
    {
        ulong requesterId = rpcParams.Receive.SenderClientId;
        if (slotIndex < 0 || slotIndex >= HotbarSize) return;
        WeaponType type = GetSlot(slotIndex);
        if (type == WeaponType.None) return;

        WeaponPickup prefab = WeaponSpawner.Instance != null ? WeaponSpawner.Instance.FindPickupPrefab(type) : null;
        if (prefab == null)
        {
            Debug.LogWarning($"[PlayerWeapon] client {requesterId}: {type}の拾い物プレハブが" +
                "WeaponSpawnerに登録されておらず、手放せませんでした");
            return;
        }

        int amount = type switch
        {
            WeaponType.ShockwaveGun => ShockwaveAmmoVar(slotIndex).Value,
            WeaponType.Blaster => BlasterAmmoVar(slotIndex).Value,
            WeaponType.RocketLauncher => RocketReserveVar(slotIndex).Value,
            _ when IsGrenadeWeaponType(type) => GetGrenadeAmmoVar(type, slotIndex)?.Value ?? 1,
            _ => 1,
        };
        if (amount <= 0) amount = 1; // 弾切れでも手放せる(空の拾い物になる)

        Vector3 dropPosition = transform.position + transform.forward * 1.2f + Vector3.up * 0.3f;
        WeaponPickup spawned = Instantiate(prefab, dropPosition, Quaternion.identity);
        spawned.ServerOverrideGrantAmount(amount);
        spawned.NetworkObject.Spawn(true);

        if (IsGrenadeWeaponType(type)) { var ammoVar = GetGrenadeAmmoVar(type, slotIndex); if (ammoVar != null) ammoVar.Value = 0; }
        if (type == WeaponType.ShockwaveGun) ShockwaveAmmoVar(slotIndex).Value = 0;
        if (type == WeaponType.Blaster)
        {
            BlasterAmmoVar(slotIndex).Value = 0;
            BlasterHeatVar(slotIndex).Value = 0f;
            BlasterOverheatedVar(slotIndex).Value = false;
        }
        if (type == WeaponType.RocketLauncher)
        {
            RocketReserveVar(slotIndex).Value = 0;
            RocketReloadingVar(slotIndex).Value = false;
        }
        ServerRemoveFromSlot(slotIndex);

        Debug.Log($"[PlayerWeapon] client {requesterId} が {type}(所持数/残弾{amount})をスロット{slotIndex}から手放しました");
    }

    /// <summary>2つのスロットの中身(武器タイプとスロット固有の弾数/熱ゲージ等)を入れ替える(DrawHotbar参照)</summary>
    [ServerRpc]
    private void RequestSwapSlotsServerRpc(int slotA, int slotB, ServerRpcParams rpcParams = default)
    {
        if (slotA < 0 || slotA >= HotbarSize || slotB < 0 || slotB >= HotbarSize || slotA == slotB) return;

        WeaponType typeA = GetSlot(slotA);
        WeaponType typeB = GetSlot(slotB);

        int reserveA = RocketReserveVar(slotA).Value;
        bool reloadingA = RocketReloadingVar(slotA).Value;
        float rocketTimerA = _rocketReloadTimers[slotA];
        int reserveB = RocketReserveVar(slotB).Value;
        bool reloadingB = RocketReloadingVar(slotB).Value;
        float rocketTimerB = _rocketReloadTimers[slotB];

        int blasterAmmoA = BlasterAmmoVar(slotA).Value;
        float blasterHeatA = BlasterHeatVar(slotA).Value;
        bool blasterOverheatedA = BlasterOverheatedVar(slotA).Value;
        float blasterTimerA = _blasterLastFireTimers[slotA];
        int blasterAmmoB = BlasterAmmoVar(slotB).Value;
        float blasterHeatB = BlasterHeatVar(slotB).Value;
        bool blasterOverheatedB = BlasterOverheatedVar(slotB).Value;
        float blasterTimerB = _blasterLastFireTimers[slotB];

        int shockwaveA = ShockwaveAmmoVar(slotA).Value;
        int shockwaveB = ShockwaveAmmoVar(slotB).Value;

        // グレネード系もスロット固有の所持数を一緒に入れ替える
        WeaponType[] grenadeTypes = { WeaponType.Bomb, WeaponType.HeavyBomb, WeaponType.FireBomb, WeaponType.FreezeBomb, WeaponType.SplitBomb };
        var grenadeAmmoA = new int[grenadeTypes.Length];
        var grenadeAmmoB = new int[grenadeTypes.Length];
        for (int i = 0; i < grenadeTypes.Length; i++)
        {
            grenadeAmmoA[i] = GetGrenadeAmmoVar(grenadeTypes[i], slotA).Value;
            grenadeAmmoB[i] = GetGrenadeAmmoVar(grenadeTypes[i], slotB).Value;
        }

        SlotVar(slotA).Value = (int)typeB;
        SlotVar(slotB).Value = (int)typeA;

        RocketReserveVar(slotA).Value = reserveB;
        RocketReloadingVar(slotA).Value = reloadingB;
        _rocketReloadTimers[slotA] = rocketTimerB;

        RocketReserveVar(slotB).Value = reserveA;
        RocketReloadingVar(slotB).Value = reloadingA;
        _rocketReloadTimers[slotB] = rocketTimerA;

        BlasterAmmoVar(slotA).Value = blasterAmmoB;
        BlasterHeatVar(slotA).Value = blasterHeatB;
        BlasterOverheatedVar(slotA).Value = blasterOverheatedB;
        _blasterLastFireTimers[slotA] = blasterTimerB;

        BlasterAmmoVar(slotB).Value = blasterAmmoA;
        BlasterHeatVar(slotB).Value = blasterHeatA;
        BlasterOverheatedVar(slotB).Value = blasterOverheatedA;
        _blasterLastFireTimers[slotB] = blasterTimerA;

        ShockwaveAmmoVar(slotA).Value = shockwaveB;
        ShockwaveAmmoVar(slotB).Value = shockwaveA;

        for (int i = 0; i < grenadeTypes.Length; i++)
        {
            GetGrenadeAmmoVar(grenadeTypes[i], slotA).Value = grenadeAmmoB[i];
            GetGrenadeAmmoVar(grenadeTypes[i], slotB).Value = grenadeAmmoA[i];
        }

        Debug.Log($"[PlayerWeapon] client {rpcParams.Receive.SenderClientId} がスロット{slotA}({typeA})と" +
            $"スロット{slotB}({typeB})を入れ替えました");
    }

    /// <summary>武器を空きスロットへ追加する。グレネード系は所持数を上限まで加算し、銃は常に新しいフル装填の個体として追加する。
    /// 空きが無ければfalse(呼び出し元は拾い物を消費しない)</summary>
    private bool ServerTryGrantWeapon(WeaponType type, int amount)
    {
        if (!IsServer) return false;

        // (1)同種で空きのあるスタックへ加算、(2)無ければ空きスロットへ新しいスタックとして追加
        if (IsGrenadeWeaponType(type))
        {
            int max = GetGrenadeMaxStack(type);
            NetworkVariable<int>[] slots = GetGrenadeAmmoSlots(type);
            if (slots == null) return false;

            for (int i = 0; i < HotbarSize; i++)
            {
                if (GetSlot(i) == type && slots[i].Value < max)
                {
                    slots[i].Value = Mathf.Min(slots[i].Value + amount, max);
                    return true;
                }
            }

            int emptySlot = ServerFindEmptySlot();
            if (emptySlot < 0) return false; // 同種の空きスタックも空き枠も無ければ拾えない

            SlotVar(emptySlot).Value = (int)type;
            slots[emptySlot].Value = Mathf.Min(amount, max);
            return true;
        }

        if (type == WeaponType.ShockwaveGun)
        {
            // 銃はスタックしない: 空き枠へフル装填の新しい個体として追加する
            int slot = ServerFindEmptySlot();
            if (slot < 0) return false;

            SlotVar(slot).Value = (int)type;
            ShockwaveAmmoVar(slot).Value = Mathf.Min(amount, maxShockwaveStack);
            return true;
        }

        if (type == WeaponType.Blaster)
        {
            // 銃はスタックしない。熱ゲージ/オーバーヒートもこの個体専用にゼロから始める
            int slot = ServerFindEmptySlot();
            if (slot < 0) return false;

            SlotVar(slot).Value = (int)type;
            BlasterAmmoVar(slot).Value = Mathf.Min(amount, maxBlasterAmmo);
            BlasterHeatVar(slot).Value = 0f;
            BlasterOverheatedVar(slot).Value = false;
            _blasterLastFireTimers[slot] = float.NegativeInfinity;
            return true;
        }

        if (type == WeaponType.RocketLauncher)
        {
            int slot = ServerFindEmptySlot();
            if (slot < 0) return false; // 空き枠が無い時だけ拾えない

            SlotVar(slot).Value = (int)type;
            RocketReserveVar(slot).Value = rocketReserveCapacity;
            RocketReloadingVar(slot).Value = false;
            _rocketReloadTimers[slot] = 0f;
            return true;
        }

        return ServerAssignToEmptySlot(type);
    }

    private bool HasSlotWithType(WeaponType type)
    {
        for (int i = 0; i < HotbarSize; i++)
        {
            if (GetSlot(i) == type) return true;
        }
        return false;
    }

    private int ServerFindEmptySlot()
    {
        for (int i = 0; i < HotbarSize; i++)
        {
            if (GetSlot(i) == WeaponType.None) return i;
        }
        return -1;
    }

    private bool ServerAssignToEmptySlot(WeaponType type)
    {
        int slot = ServerFindEmptySlot();
        if (slot < 0) return false; // 4枠とも埋まっていて拾えなかった
        SlotVar(slot).Value = (int)type;
        return true;
    }

    private void ServerRemoveFromActiveSlot()
    {
        ServerRemoveFromSlot(_activeSlot.Value);
    }

    private void ServerRemoveFromSlot(int slot)
    {
        SlotVar(slot).Value = (int)WeaponType.None;
    }

    /// <summary>試合開始時にPlayerController経由で呼ばれ、前の試合の武器を持ち越さずリセットする</summary>
    public void ServerResetWeapon()
    {
        if (!IsServer) return;

        _slot0.Value = (int)(StartWithBlasterForDebug ? WeaponType.Blaster : WeaponType.None);
        _slot1.Value = (int)WeaponType.None;
        _slot2.Value = (int)WeaponType.None;
        _slot3.Value = (int)WeaponType.None;
        for (int i = 0; i < HotbarSize; i++)
        {
            GetGrenadeAmmoVar(WeaponType.Bomb, i).Value = 0;
            GetGrenadeAmmoVar(WeaponType.HeavyBomb, i).Value = 0;
            GetGrenadeAmmoVar(WeaponType.FireBomb, i).Value = 0;
            GetGrenadeAmmoVar(WeaponType.FreezeBomb, i).Value = 0;
            GetGrenadeAmmoVar(WeaponType.SplitBomb, i).Value = 0;

            RocketReserveVar(i).Value = 0;
            RocketReloadingVar(i).Value = false;
            _rocketReloadTimers[i] = 0f;

            ShockwaveAmmoVar(i).Value = 0;

            BlasterAmmoVar(i).Value = 0;
            BlasterHeatVar(i).Value = 0f;
            BlasterOverheatedVar(i).Value = false;
            _blasterLastFireTimers[i] = float.NegativeInfinity;
        }
    }

    /// <summary>WeaponTypeごとの実機モデルprefab(持ち手の見た目とホットバーのアイコン生成で共通)</summary>
    private GameObject GetHeldVisualPrefab(WeaponType type) => type switch
    {
        WeaponType.Blaster => blasterHeldVisualPrefab,
        WeaponType.Bomb => bombHeldVisualPrefab,
        WeaponType.RocketLauncher => rocketHeldVisualPrefab,
        WeaponType.ShockwaveGun => shockwaveHeldVisualPrefab,
        _ => FindGrenadeTypeEntry(type)?.heldVisualPrefab,
    };

    // ---- 見た目: 持ち手(全クライアント共通) ----
    private void RefreshHeldVisualIfNeeded()
    {
        // 死亡中は持ち手を手放し済みのため見た目を作り直さない(復活時のリセットで再度呼ばれる)
        if (_health != null && _health.IsDead) return;

        WeaponType current = Equipped;
        if (current == _heldVisualType) return;
        _heldVisualType = current;

        if (_heldVisual != null) Destroy(_heldVisual);
        if (weaponHolder == null || current == WeaponType.None) return;

        GameObject realVisualPrefab = GetHeldVisualPrefab(current);

        if (realVisualPrefab != null)
        {
            BuildRealHeldVisual(current, realVisualPrefab);
        }
        else
        {
            BuildPlaceholderHeldVisual(current);
        }
    }

    /// <summary>アセットストアの実メッシュを手元に構える。見た目だけなのでColliderが付いていれば取り除く</summary>
    private void BuildRealHeldVisual(WeaponType current, GameObject visualPrefab)
    {
        _heldVisual = Instantiate(visualPrefab, weaponHolder);
        foreach (var col in _heldVisual.GetComponentsInChildren<Collider>()) Destroy(col);
        VisualPrefabUtility.StripStrayVisualComponents(_heldVisual);

        GrenadeTypeEntry grenadeEntry = current == WeaponType.Bomb ? null : FindGrenadeTypeEntry(current);
        Vector3 localEuler = current switch
        {
            WeaponType.Blaster => blasterHeldVisualLocalEulerAngles,
            WeaponType.Bomb => bombHeldVisualLocalEulerAngles,
            WeaponType.ShockwaveGun => shockwaveHeldVisualLocalEulerAngles,
            WeaponType.RocketLauncher => rocketHeldVisualLocalEulerAngles,
            _ => grenadeEntry != null ? grenadeEntry.heldVisualLocalEulerAngles : Vector3.zero,
        };
        Vector3 localScale = current switch
        {
            WeaponType.Blaster => blasterHeldVisualLocalScale,
            WeaponType.Bomb => bombHeldVisualLocalScale,
            WeaponType.ShockwaveGun => shockwaveHeldVisualLocalScale,
            WeaponType.RocketLauncher => rocketHeldVisualLocalScale,
            _ => grenadeEntry != null ? grenadeEntry.heldVisualLocalScale : Vector3.one,
        };

        _heldVisual.transform.localPosition = Vector3.zero;
        _heldVisual.transform.localRotation = Quaternion.Euler(localEuler);
        _heldVisual.transform.localScale = localScale;
    }

    /// <summary>実アセット未設定の武器種向けフォールバック(プリミティブ形状)</summary>
    private void BuildPlaceholderHeldVisual(WeaponType current)
    {
        PrimitiveType primitive = current switch
        {
            WeaponType.RocketLauncher => PrimitiveType.Cylinder,
            WeaponType.ShockwaveGun => PrimitiveType.Cube,
            _ when IsGrenadeWeaponType(current) => PrimitiveType.Sphere,
            _ => PrimitiveType.Cube,
        };
        _heldVisual = GameObject.CreatePrimitive(primitive);
        Destroy(_heldVisual.GetComponent<Collider>()); // 持ち手の見た目に当たり判定は不要(誤爆防止)
        _heldVisual.transform.SetParent(weaponHolder, false);
        _heldVisual.transform.localPosition = Vector3.zero;

        Vector3 localScale;
        Quaternion localRotation;
        switch (current)
        {
            case WeaponType.Bomb:
            case WeaponType.HeavyBomb:
            case WeaponType.FireBomb:
            case WeaponType.FreezeBomb:
            case WeaponType.SplitBomb:
                localScale = new Vector3(0.2f, 0.2f, 0.2f);
                localRotation = Quaternion.identity;
                break;
            case WeaponType.RocketLauncher:
                // Cylinderは既定でローカルY軸方向に伸びるため、前方(Z軸)を向くよう90度倒す
                localScale = new Vector3(0.12f, 0.5f, 0.12f);
                localRotation = Quaternion.Euler(90f, 0f, 0f);
                break;
            case WeaponType.ShockwaveGun:
                localScale = new Vector3(0.18f, 0.18f, 0.35f);
                localRotation = Quaternion.identity;
                break;
            default:
                localScale = new Vector3(0.1f, 0.1f, 0.4f);
                localRotation = Quaternion.identity;
                break;
        }
        _heldVisual.transform.localRotation = localRotation;
        _heldVisual.transform.localScale = localScale;

        var mat = current switch
        {
            WeaponType.RocketLauncher => rocketHeldMaterial,
            WeaponType.ShockwaveGun => shockwaveHeldMaterial,
            _ when IsGrenadeWeaponType(current) => bombHeldMaterial,
            _ => blasterHeldMaterial,
        };
        if (mat != null) _heldVisual.GetComponent<Renderer>().sharedMaterial = mat;
    }

    // ---- 見た目: 発砲ボタン中央のアイコン(Owner本人のみ) ----
    private static Sprite _bombIconSprite;
    private static Sprite _blasterIconSprite;
    private static Sprite _rocketIconSprite;
    private static Sprite _shockwaveIconSprite;

    private void RefreshFireButtonIcon()
    {
        // 装備が変わった。次のUpdateFireButtonIfNeeded()で必ず作り直させる
        _fireButtonShownType = null;
        UpdateFireButtonIfNeeded();
    }

    // 発砲ボタンのアイコンはホットバーと同じ武器の絵(WeaponIconRenderer)を使い、未生成の間は簡易シルエットを出す
    private WeaponType? _fireButtonShownType;
    private bool _fireButtonShowingFallback;
    private static readonly System.Collections.Generic.Dictionary<Texture2D, Sprite> _fireIconSpriteCache =
        new System.Collections.Generic.Dictionary<Texture2D, Sprite>();

    /// <summary>Owner本人のUpdateから毎フレーム呼ぶ。装備変更時と本物の絵に差し替え可能になった時だけ反映する</summary>
    private void UpdateFireButtonIfNeeded()
    {
        TouchInputProvider touch = TouchInputProvider.Instance;
        if (touch == null) return;

        WeaponType type = Equipped;
        bool sameType = _fireButtonShownType.HasValue && _fireButtonShownType.Value == type;
        if (sameType && !_fireButtonShowingFallback) return;

        Texture2D rendered = type != WeaponType.None ? WeaponIconRenderer.TryGetIcon(GetHeldVisualPrefab(type)) : null;
        if (sameType && rendered == null) return; // まだ本物の絵が無い(仮のシルエットのまま待つ)

        Sprite icon = rendered != null ? SpriteForRenderedIcon(rendered) : IconForWeapon(type);
        touch.SetFireButtonState(icon, IsGrenadeWeaponType(type), type != WeaponType.None);
        _fireButtonShownType = type;
        _fireButtonShowingFallback = type != WeaponType.None && rendered == null;
    }

    private static Sprite SpriteForRenderedIcon(Texture2D texture)
    {
        if (_fireIconSpriteCache.TryGetValue(texture, out Sprite cached) && cached != null) return cached;
        Sprite sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
        sprite.name = $"FireIcon_{texture.name}";
        _fireIconSpriteCache[texture] = sprite;
        return sprite;
    }

    private static Sprite IconForWeapon(WeaponType type)
    {
        switch (type)
        {
            case WeaponType.Bomb:
            case WeaponType.HeavyBomb:
            case WeaponType.FireBomb:
            case WeaponType.FreezeBomb:
            case WeaponType.SplitBomb:
                if (_bombIconSprite == null) _bombIconSprite = BuildBombIconSprite(64);
                return _bombIconSprite;
            case WeaponType.Blaster:
                if (_blasterIconSprite == null) _blasterIconSprite = BuildBlasterIconSprite(64);
                return _blasterIconSprite;
            case WeaponType.RocketLauncher:
                if (_rocketIconSprite == null) _rocketIconSprite = BuildRocketIconSprite(64);
                return _rocketIconSprite;
            case WeaponType.ShockwaveGun:
                if (_shockwaveIconSprite == null) _shockwaveIconSprite = BuildShockwaveIconSprite(64);
                return _shockwaveIconSprite;
            default:
                return null;
        }
    }

    private static Sprite BuildBombIconSprite(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float radius = size / 2f - 4f;
        Vector2 center = new Vector2(size / 2f, size / 2f);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool inside = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center) <= radius;
                tex.SetPixel(x, y, inside ? Color.white : new Color(1f, 1f, 1f, 0f));
            }
        }

        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
    }

    /// <summary>単純な銃のシルエット(横長のバレル+縦のグリップ)</summary>
    private static Sprite BuildBlasterIconSprite(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, 0f));
            }
        }

        int barrelY0 = size / 2 - size / 10;
        int barrelY1 = size / 2 + size / 10;
        for (int y = barrelY0; y < barrelY1; y++)
        {
            for (int x = size / 6; x < size - size / 10; x++)
            {
                tex.SetPixel(x, y, Color.white);
            }
        }

        int gripX0 = size / 6;
        int gripX1 = gripX0 + size / 5;
        for (int y = 0; y < barrelY0; y++)
        {
            for (int x = gripX0; x < gripX1; x++)
            {
                tex.SetPixel(x, y, Color.white);
            }
        }

        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
    }

    /// <summary>単純なロケットのシルエット(横長の筒+先端の三角)</summary>
    private static Sprite BuildRocketIconSprite(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                tex.SetPixel(x, y, new Color(1f, 1f, 1f, 0f));
            }
        }

        int bodyY0 = size / 2 - size / 8;
        int bodyY1 = size / 2 + size / 8;
        int bodyX0 = size / 8;
        int bodyX1 = size - size / 4;
        for (int y = bodyY0; y < bodyY1; y++)
        {
            for (int x = bodyX0; x < bodyX1; x++)
            {
                tex.SetPixel(x, y, Color.white);
            }
        }

        int noseX1 = size - size / 10;
        int cy = size / 2;
        for (int x = bodyX1; x < noseX1; x++)
        {
            float t = (float)(x - bodyX1) / Mathf.Max(1, noseX1 - bodyX1);
            int halfHeight = Mathf.RoundToInt((bodyY1 - bodyY0) / 2f * (1f - t));
            for (int y = cy - halfHeight; y < cy + halfHeight; y++)
            {
                if (y < 0 || y >= size) continue;
                tex.SetPixel(x, y, Color.white);
            }
        }

        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
    }

    /// <summary>中心の点+同心円のリング2本で「広がる衝撃波」を表す単純な記号</summary>
    private static Sprite BuildShockwaveIconSprite(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        Vector2 center = new Vector2(size / 2f, size / 2f);
        float coreRadius = size * 0.10f;
        float[] ringRadii = { size * 0.30f, size * 0.46f };
        float ringThickness = size * 0.06f;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                float dist = Vector2.Distance(new Vector2(x + 0.5f, y + 0.5f), center);
                bool onCore = dist <= coreRadius;
                bool onRing = false;
                foreach (float r in ringRadii)
                {
                    if (Mathf.Abs(dist - r) <= ringThickness * 0.5f) { onRing = true; break; }
                }
                tex.SetPixel(x, y, (onCore || onRing) ? Color.white : new Color(1f, 1f, 1f, 0f));
            }
        }

        tex.Apply();
        return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
    }

    // ---- 見た目: 投擲/照準の予測線(Owner本人のみ) ----
    /// <summary>初速/重力有無/最大距離をパラメータ化し、Bomb(山なり)とRocketLauncher(直進)の両方の予測線を描画する</summary>
    private void ShowTrajectoryPreview(Vector3 startPos, Vector3 initialVelocity, bool useGravity, float maxDistance)
    {
        if (playerCamera == null) return;
        if (_trajectoryLine == null) _trajectoryLine = CreateTrajectoryLineRenderer();

        Vector3 velocity = initialVelocity;
        Vector3 pos = startPos;

        const int maxSteps = 40;
        const float stepTime = 0.05f;
        var points = new System.Collections.Generic.List<Vector3>(maxSteps) { pos };

        for (int i = 1; i < maxSteps; i++)
        {
            if (useGravity) velocity += Physics.gravity * stepTime;
            Vector3 nextPos = pos + velocity * stepTime;
            Vector3 segment = nextPos - pos;

            if (Physics.Raycast(pos, segment.normalized, out RaycastHit hit, segment.magnitude, trajectoryObstacleMask, QueryTriggerInteraction.Ignore))
            {
                points.Add(hit.point);
                break;
            }

            points.Add(nextPos);
            pos = nextPos;

            if (Vector3.Distance(startPos, pos) >= maxDistance) break;
        }

        _trajectoryLine.positionCount = points.Count;
        _trajectoryLine.SetPositions(points.ToArray());
        _trajectoryLine.enabled = true;
    }

    private void HideTrajectoryPreview()
    {
        if (_trajectoryLine != null) _trajectoryLine.enabled = false;
    }

    private LineRenderer CreateTrajectoryLineRenderer()
    {
        var go = new GameObject("BombTrajectoryPreview");
        go.transform.SetParent(transform, false);
        var lr = go.AddComponent<LineRenderer>();
        if (trajectoryLineMaterial != null) lr.sharedMaterial = trajectoryLineMaterial;
        lr.startWidth = lr.endWidth = 0.04f;
        lr.useWorldSpace = true;
        lr.receiveShadows = false;
        lr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        return lr;
    }

    // ---- 見た目: クロスヘア+ホットバーHUD(Owner本人のみ。OnGUIベース) ----
    private void OnGUI()
    {
        if (!IsSpawned || !IsOwner) return;
        if (MatchManager.Instance != null && !MatchManager.Instance.IsMatchActiveForInput) return;

        PopUI.EnsureSkinFontApplied();
        GUIScaler.Begin();

        // Bombは予測線で狙うためクロスヘア不要。他の銃は画面中央のクロスヘアで狙うので表示する
        if (Equipped == WeaponType.Blaster || Equipped == WeaponType.RocketLauncher || Equipped == WeaponType.ShockwaveGun) DrawCrosshair();
        DrawHitMarker();
        DrawHotbar();
        DrawBombCancelButton();

        GUIScaler.End();
    }

    /// <summary>攻撃者本人にだけ見える命中確認の×印。通常は白・短時間、キル確定時は赤く長めに表示する</summary>
    private void DrawHitMarker()
    {
        if (!DamageDisplaySettings.Enabled) return;
        float remaining = _hitMarkerExpireTime - Time.time;
        if (remaining <= 0f) return;

        if (_crosshairTexture == null)
        {
            _crosshairTexture = new Texture2D(1, 1);
            _crosshairTexture.SetPixel(0, 0, Color.white);
            _crosshairTexture.Apply();
        }

        float duration = _hitMarkerIsKill ? HitMarkerKillDurationSeconds : HitMarkerDurationSeconds;
        float alpha = Mathf.Clamp01(remaining / duration);
        float size = _hitMarkerIsKill ? 22f : 14f;
        const float thickness = 3f;
        float cx = GUIScaler.VirtualWidth / 2f;
        float cy = GUIScaler.VirtualHeight / 2f;

        Color previous = GUI.color;
        GUI.color = (_hitMarkerIsKill ? new Color(1f, 0.25f, 0.2f) : Color.white) * new Color(1f, 1f, 1f, alpha);

        // ×印: 正方形の2本の対角線を45度回した矩形2本として描く。RotateAroundPivotにはGUIScalerのスケールを掛けた座標を渡す
        Vector2 pivot = new Vector2(cx, cy) * GUIScaler.Scale;
        Matrix4x4 savedMatrix = GUI.matrix;
        var rectA = new Rect(cx - size / 2f, cy - thickness / 2f, size, thickness);
        GUIUtility.RotateAroundPivot(45f, pivot);
        GUI.DrawTexture(rectA, _crosshairTexture);
        GUI.matrix = savedMatrix;

        GUIUtility.RotateAroundPivot(-45f, pivot);
        GUI.DrawTexture(rectA, _crosshairTexture);
        GUI.matrix = savedMatrix;

        GUI.color = previous;
    }

    private static Texture2D _crosshairTexture;

    // 小サイズでも潰れないよう、GUI.Boxではなく白1x1テクスチャをGUI.colorで着色して塗りつぶす
    private static void DrawCrosshair()
    {
        if (_crosshairTexture == null)
        {
            _crosshairTexture = new Texture2D(1, 1);
            _crosshairTexture.SetPixel(0, 0, Color.white);
            _crosshairTexture.Apply();
        }

        const float size = 12f;
        const float thickness = 2f;
        float cx = GUIScaler.VirtualWidth / 2f;
        float cy = GUIScaler.VirtualHeight / 2f;

        Color previous = GUI.color;
        GUI.color = Color.white;
        GUI.DrawTexture(new Rect(cx - size / 2f, cy - thickness / 2f, size, thickness), _crosshairTexture);
        GUI.DrawTexture(new Rect(cx - thickness / 2f, cy - size / 2f, thickness, size), _crosshairTexture);
        GUI.color = previous;
    }

    /// <summary>Bomb構えで狙っている間だけ表示するキャンセルボタン。押すと離した時の投擲をスキップする</summary>
    private void DrawBombCancelButton()
    {
        if (!IsGrenadeWeaponType(Equipped) || !_wasFireHeldLastFrame || _bombThrowCancelled) return;

        // 移動ジョイスティックの少し右上に出す
        float joystickCenterX = GUIScaler.SafeAreaLeft + 100f;
        float joystickCenterY = GUIScaler.VirtualHeight - GUIScaler.SafeAreaBottom - 100f;

        const float buttonSize = 56f;
        float x = joystickCenterX + 90f;
        float y = joystickCenterY - 150f;
        var rect = new Rect(x, y, buttonSize, buttonSize);

        // 他のタッチボタンに合わせて円形で描き、当たり判定だけGUIStyle.noneのGUI.Buttonに任せる
        if (_dotTexture == null) _dotTexture = BuildDotTexture(32);
        Color previousColor = GUI.color;
        GUI.color = new Color(0.85f, 0.2f, 0.15f, 0.92f);
        GUI.DrawTexture(rect, _dotTexture);
        GUI.color = previousColor;

        var labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        labelStyle.normal.textColor = Color.white;
        GUI.Label(rect, "x", labelStyle);

        bool pressed = GUI.Button(rect, GUIContent.none, GUIStyle.none);

        if (pressed)
        {
            _bombThrowCancelled = true;
            HideTrajectoryPreview();
            Debug.Log("[PlayerWeapon] グレネードの投擲をキャンセルしました");
        }
    }

    private int _hotbarDragSlot = -1;
    private Vector2 _hotbarDragCurrentPos;

    /// <summary>ホットバーを描画する。タップで切り替え、別スロット上へドラッグして離すと入れ替え、ホットバー外へ十分ドラッグすると手放す</summary>
    private void DrawHotbar()
    {
        const float slotSize = 92f;
        const float gap = 10f;
        const float dropDragThreshold = 46f;
        float totalWidth = HotbarSize * slotSize + (HotbarSize - 1) * gap;
        float startX = (GUIScaler.VirtualWidth - totalWidth) / 2f;
        // SafeArea(ノッチ等)を考慮する
        float y = GUIScaler.VirtualHeight - GUIScaler.SafeAreaBottom - slotSize - 16f;

        Event e = Event.current;
        var slotRects = new Rect[HotbarSize];

        for (int i = 0; i < HotbarSize; i++)
        {
            var rect = new Rect(startX + i * (slotSize + gap), y, slotSize, slotSize);
            slotRects[i] = rect;
            bool isActive = i == ActiveSlotIndex;
            WeaponType type = GetSlot(i);

            // アクティブなスロットだけアクセントカラーで目立たせる
            Color previousColor = GUI.color;
            GUI.color = isActive ? PopUI.Yellow : Color.white;
            GUI.DrawTexture(rect, PopUI.PanelTexture);
            GUI.color = previousColor;

            // ドラッグ中のスロットの中身は後段でドラッグ位置(最前面)に描くので、元の枠の中には描かない
            if (i != _hotbarDragSlot)
            {
                DrawHotbarSlotContent(type, rect, isActive);
                DrawSlotCountBadge(GetSlotCount(type, i), rect);
            }

            if (e.type == EventType.MouseDown && rect.Contains(e.mousePosition))
            {
                _hotbarDragSlot = i;
                _hotbarDragCurrentPos = rect.center;
                e.Use();
            }
        }

        if (_hotbarDragSlot >= 0 && e.type == EventType.MouseDrag)
        {
            _hotbarDragCurrentPos = e.mousePosition;
            e.Use();
        }

        if (_hotbarDragSlot >= 0 && e.type == EventType.MouseUp)
        {
            int sourceSlot = _hotbarDragSlot;
            int targetSlot = -1;
            for (int i = 0; i < HotbarSize; i++)
            {
                if (slotRects[i].Contains(e.mousePosition)) { targetSlot = i; break; }
            }

            if (targetSlot >= 0 && targetSlot != sourceSlot)
            {
                RequestSwapSlotsServerRpc(sourceSlot, targetSlot);
            }
            else if (targetSlot == sourceSlot)
            {
                _activeSlot.Value = sourceSlot; // 自分自身のスロット上で離した = ただのタップ = 切り替え
            }
            else
            {
                float dragDistance = Vector2.Distance(_hotbarDragCurrentPos, slotRects[sourceSlot].center);
                if (dragDistance >= dropDragThreshold && GetSlot(sourceSlot) != WeaponType.None)
                {
                    RequestDropWeaponServerRpc(sourceSlot);
                }
            }

            _hotbarDragSlot = -1;
            e.Use();
        }

        // ドラッグ中のスロットだけは最後にまとめて描く(ループ順で後ろのスロットの下に隠れないよう最前面にする)
        if (_hotbarDragSlot >= 0)
        {
            var dragRect = new Rect(_hotbarDragCurrentPos.x - slotSize / 2f, _hotbarDragCurrentPos.y - slotSize / 2f, slotSize, slotSize);
            WeaponType dragType = GetSlot(_hotbarDragSlot);
            DrawHotbarSlotContent(dragType, dragRect, true);
            DrawSlotCountBadge(GetSlotCount(dragType, _hotbarDragSlot), dragRect);
        }

        DrawEquippedAmmoReadout(startX + totalWidth + 40f, y, slotSize);
        DrawWeaponNamePopup(y);
    }

    /// <summary>スロット1枠分の中身(モデルアイコン、無ければ文字フォールバック)を指定Rectに描く。
    /// 通常描画とドラッグ中の最前面描画の両方から呼ぶ</summary>
    private void DrawHotbarSlotContent(WeaponType type, Rect rect, bool isActive)
    {
        if (type == WeaponType.None) return;

        Texture2D icon = WeaponIconRenderer.TryGetIcon(GetHeldVisualPrefab(type));
        if (icon != null)
        {
            const float pad = 8f;
            var iconRect = new Rect(rect.x + pad, rect.y + pad, rect.width - pad * 2f, rect.height - pad * 2f);
            GUI.DrawTexture(iconRect, icon, ScaleMode.ScaleToFit, true);
            return;
        }

        // アイコン未生成(1フレーム目)やプレハブ未設定の武器用に、表示名の文字でフォールバックする
        string label = GetWeaponDisplayName(type);
        if (string.IsNullOrEmpty(label)) label = "-";
        var style = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        style.normal.textColor = isActive ? PopUI.Yellow : Color.white;
        GUI.Label(rect, label, style);
    }

    /// <summary>指定スロットの所持数/残弾を表示用に返す(持っていない/数の概念が無い場合は-1)</summary>
    private int GetSlotCount(WeaponType type, int slotIndex)
    {
        return type switch
        {
            WeaponType.ShockwaveGun => ShockwaveAmmoVar(slotIndex).Value,
            WeaponType.Blaster => BlasterAmmoVar(slotIndex).Value,
            WeaponType.RocketLauncher => RocketReserveVar(slotIndex).Value,
            _ when IsGrenadeWeaponType(type) => GetGrenadeAmmoVar(type, slotIndex)?.Value ?? 0,
            _ => -1,
        };
    }

    /// <summary>ホットバーの各スロット右下に所持数を小さく添える</summary>
    private static void DrawSlotCountBadge(int count, Rect slotRect)
    {
        if (count < 0) return; // None等、数の概念が無い武器は表示しない

        const float badgeHeight = 24f;
        const float badgeMinWidth = 30f;
        var style = new GUIStyle(GUI.skin.label) { fontSize = 14, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        style.normal.textColor = count > 0 ? Color.white : new Color(1f, 0.4f, 0.4f); // 0発は赤みを帯びさせ、空であることも一目で伝える
        string text = count.ToString();
        float textWidth = style.CalcSize(new GUIContent(text)).x;
        float badgeWidth = Mathf.Max(badgeMinWidth, textWidth + 10f);

        var badgeRect = new Rect(slotRect.xMax - badgeWidth - 2f, slotRect.yMax - badgeHeight - 2f, badgeWidth, badgeHeight);
        GUI.DrawTexture(badgeRect, PopUI.PanelTexture);
        GUI.Label(badgeRect, text, style);
    }

    /// <summary>装備中武器の残弾/個数をホットバー右側に表示する。最大値の小さい武器はドット、Blasterは数字+熱ゲージで見せる</summary>
    private void DrawEquippedAmmoReadout(float x, float y, float height)
    {
        WeaponType equipped = Equipped;
        switch (equipped)
        {
            case WeaponType.Bomb:
                DrawStackDots(x, y, height, BombCount, maxBombStack, PopUI.Yellow);
                break;
            case WeaponType.ShockwaveGun:
                DrawStackDots(x, y, height, ShockwaveCount, maxShockwaveStack, new Color(0.5f, 0.85f, 1f));
                break;
            case WeaponType.RocketLauncher:
                DrawRocketPips(x, y, height);
                break;
            case WeaponType.Blaster:
                DrawBlasterAmmoReadout(x, y, height);
                break;
            case WeaponType.HeavyBomb:
            case WeaponType.FireBomb:
            case WeaponType.FreezeBomb:
            case WeaponType.SplitBomb:
                NetworkVariable<int> ammoVar = GetGrenadeAmmoVar(equipped, _activeSlot.Value);
                if (ammoVar != null) DrawStackDots(x, y, height, ammoVar.Value, GetGrenadeMaxStack(equipped), PopUI.Yellow);
                break;
        }
    }

    private static Texture2D _dotTexture;

    /// <summary>塗りつぶし円のテクスチャを実行時に生成してキャッシュする(着色はGUI.color)</summary>
    private static Texture2D BuildDotTexture(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
        float radius = size / 2f;
        var center = new Vector2(radius, radius);
        for (int py = 0; py < size; py++)
        {
            for (int px = 0; px < size; px++)
            {
                bool inside = Vector2.Distance(new Vector2(px + 0.5f, py + 0.5f), center) <= radius;
                tex.SetPixel(px, py, inside ? Color.white : new Color(1f, 1f, 1f, 0f));
            }
        }
        tex.Apply();
        return tex;
    }

    /// <summary>所持数が最大値まで積み上がる武器の残弾表示。満タンのドットを並べ、残数分だけ着色する</summary>
    private void DrawStackDots(float x, float y, float height, int count, int max, Color dotColor)
    {
        if (_dotTexture == null) _dotTexture = BuildDotTexture(32);

        const float dotSize = 16f;
        const float dotGap = 5f;
        const float numberWidth = 50f;
        float contentWidth = max * (dotSize + dotGap) + 6f + numberWidth;

        // 明るい地面でも読めるよう、ホットバーと同じPanelTextureで裏打ちする
        DrawAmmoReadoutBacking(x, y, contentWidth, height);

        float dotY = y + (height - dotSize) / 2f;
        Color previous = GUI.color;
        for (int i = 0; i < max; i++)
        {
            GUI.color = i < count ? dotColor : new Color(1f, 1f, 1f, 0.18f);
            GUI.DrawTexture(new Rect(x + i * (dotSize + dotGap), dotY, dotSize, dotSize), _dotTexture);
        }
        GUI.color = previous;

        float numberX = x + max * (dotSize + dotGap) + 6f;
        var style = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
        style.normal.textColor = new Color(1f, 1f, 1f, 0.8f);
        GUI.Label(new Rect(numberX, y, numberWidth, height), $"x{count}", style);
    }

    /// <summary>ドット/ピップ系の残弾表示の背景パネル(ホットバーのスロットと同じPopUI.PanelTexture)</summary>
    private static void DrawAmmoReadoutBacking(float x, float y, float contentWidth, float height)
    {
        const float padding = 10f;
        var backRect = new Rect(x - padding, y, contentWidth + padding * 2f, height);
        GUI.DrawTexture(backRect, PopUI.PanelTexture);
    }

    /// <summary>RocketLauncherの装填中(1発)+予備をドットで見せる。先頭は弾室ドット(装填済みは白、リロード中は暗く)</summary>
    private void DrawRocketPips(float x, float y, float height)
    {
        if (_dotTexture == null) _dotTexture = BuildDotTexture(32);

        int chambered = (!IsRocketReloading && RocketReserve > 0) ? 1 : 0;
        int reserve = IsRocketReloading ? RocketReserve : Mathf.Max(0, RocketReserve - chambered);

        const float chamberSize = 20f;
        const float dotSize = 16f;
        const float dotGap = 5f;
        const float numberWidth = 90f;
        float contentWidth = chamberSize + dotGap + 3f + rocketReserveCapacity * (dotSize + dotGap) + 6f + numberWidth;
        DrawAmmoReadoutBacking(x, y, contentWidth, height);

        float centerY = y + height / 2f;

        Color previous = GUI.color;
        GUI.color = chambered > 0 ? Color.white : new Color(1f, 0.3f, 0.3f, 0.5f);
        GUI.DrawTexture(new Rect(x, centerY - chamberSize / 2f, chamberSize, chamberSize), _dotTexture);

        float dotX = x + chamberSize + dotGap + 3f;
        float dotY = y + (height - dotSize) / 2f;
        for (int i = 0; i < rocketReserveCapacity; i++)
        {
            GUI.color = i < reserve ? new Color(1f, 0.55f, 0.2f) : new Color(1f, 1f, 1f, 0.18f);
            GUI.DrawTexture(new Rect(dotX + i * (dotSize + dotGap), dotY, dotSize, dotSize), _dotTexture);
        }
        GUI.color = previous;

        float numberX = dotX + rocketReserveCapacity * (dotSize + dotGap) + 6f;
        var style = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
        style.normal.textColor = new Color(1f, 1f, 1f, 0.8f);
        GUI.Label(new Rect(numberX, y, numberWidth, height), FormatRocketAmmo(), style);
    }

    /// <summary>Blasterは数字表示のままにし、その下にオーバーヒートゲージ(DrawBlasterHeatGauge)だけを出す</summary>
    private void DrawBlasterAmmoReadout(float x, float y, float height)
    {
        var style = new GUIStyle(GUI.skin.label) { fontSize = 22, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleLeft };
        style.normal.textColor = IsBlasterOverheated ? new Color(1f, 0.3f, 0.3f) : Color.white;
        GUI.Label(new Rect(x, y, 140f, height), $"x{BlasterAmmo}", style);

        const float barWidth = 120f;
        DrawBlasterHeatGauge(x, y + height + 4f, barWidth);
    }

    /// <summary>Blasterの熱ゲージ(0〜1)を細いバーで表示する。満タンに近づくほど黄色→赤に変化させ、
    /// オーバーヒート中は常に赤にすることで「今は撃てない」を視覚的に伝える</summary>
    private void DrawBlasterHeatGauge(float x, float y, float width)
    {
        const float barHeight = 10f;
        var backRect = new Rect(x, y, width, barHeight);
        GUI.color = new Color(0f, 0f, 0f, 0.5f);
        GUI.DrawTexture(backRect, PopUI.PanelTexture);

        float heat01 = BlasterHeat01;
        if (heat01 > 0f)
        {
            var fillRect = new Rect(x, y, width * heat01, barHeight);
            GUI.color = IsBlasterOverheated ? new Color(1f, 0.2f, 0.2f) : Color.Lerp(new Color(1f, 0.85f, 0.2f), new Color(1f, 0.3f, 0.1f), heat01);
            GUI.DrawTexture(fillRect, PopUI.PanelTexture);
        }
        GUI.color = Color.white;
    }

    /// <summary>装弾数(1発)/予備弾数の形式(例: "1/1 (3)")に整形する。リロード中は装填済み0・予備=RocketReserve</summary>
    private string FormatRocketAmmo()
    {
        int chambered = (!IsRocketReloading && RocketReserve > 0) ? 1 : 0;
        int reserve = IsRocketReloading ? RocketReserve : Mathf.Max(0, RocketReserve - chambered);
        return $"{chambered}/1 ({reserve})";
    }
}
