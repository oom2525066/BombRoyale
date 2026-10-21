using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// プレイヤーキャラクターの本体(partial class。移動はMovement、カメラはCamera、観戦はSpectate)。
/// Owner権威のCharacterController + ClientNetworkTransformで同期し、入力はPlayerInputFrame経由で受け取る。
/// Prefabには CharacterController / NetworkObject / ClientNetworkTransform と、子のCameraPivot→PlayerCameraが必要。
/// </summary>
[RequireComponent(typeof(CharacterController))]
public partial class PlayerController : NetworkBehaviour
{
    [Header("移動")]
    [SerializeField] private float moveSpeed = 6f;
    [SerializeField] private float gravity = -20f;
    [SerializeField] private float groundAcceleration = 20f; // 入力方向へ加速する速さ
    [SerializeField] private float groundFriction = 24f;     // 入力を離した時に速度0へ落とす速さ
    [Tooltip("空中での方向転換のしやすさ(速度そのものは増えない)")]
    [SerializeField] private float airAcceleration = 8f;
    [SerializeField] private float groundCheckExtraDistance = 0.15f;
    [SerializeField] private float groundedStickForce = 3f;
    [SerializeField] private LayerMask environmentMask = ~0;
    [Tooltip("体を移動方向へ向ける1秒あたりの最大回転角度(度)")]
    [SerializeField] private float turnSpeedDegreesPerSecond = 720f;
    [Tooltip("ジャンプの到達高さ(m)")]
    [SerializeField] private float jumpHeight = 1.2f;

    [Header("詰まり対策")]
    [Tooltip("この間隔ごとに、入力があるのにほとんど動けていないかをチェックする(秒)")]
    [SerializeField] private float stuckCheckIntervalSeconds = 1f;
    [SerializeField] private float stuckMinMoveDistance = 0.05f;
    [SerializeField] private float stuckEscapeUpwardVelocity = 4f;
    [SerializeField] private float stuckEscapeHorizontalSpeed = 3f;

    [Header("TPSカメラ")]
    [Tooltip("視点のピッチ(上下)だけを持つ空のTransform。Playerの子に配置する")]
    [SerializeField] private Transform cameraPivot;
    [Tooltip("実際に描画するカメラ。CameraPivotの子に配置する")]
    [SerializeField] private Camera playerCamera;
    [SerializeField] private float cameraDistance = 4.5f;
    [SerializeField] private float cameraHeight = 0.3f;
    [Tooltip("肩越し構図にするための、カメラ基準点の右方向オフセット(m)")]
    [SerializeField] private float shoulderOffset = 0.9f;
    [SerializeField] private float minPitch = -40f;
    [SerializeField] private float maxPitch = 75f;
    [SerializeField] private float cameraCollisionRadius = 0.25f;
    [SerializeField] private LayerMask cameraCollisionMask = ~0;
    [Tooltip("カメラ追従の重み(大きいほど遅れて追従する)")]
    [SerializeField] private float cameraSmoothTime = 0.06f;
    [SerializeField] private float baseFieldOfView = 60f;
    [Tooltip("全力移動時に視野角へ追加される量")]
    [SerializeField] private float sprintFovBoost = 6f;
    [SerializeField] private float fovLerpSpeed = 5f;

    [Header("戦闘")]
    [Tooltip("同じPlayerプレハブのPlayerWeapon。武器処理を委譲する")]
    [SerializeField] private PlayerWeapon weapon;
    [SerializeField] private Health health;
    [Tooltip("同じPlayerプレハブのFootsteps。未設定なら足音無し")]
    [SerializeField] private Footsteps footsteps;

    [Header("見た目(カラー)")]
    [Tooltip("OwnerClientId順にこの中から1色を割り当てる")]
    [SerializeField] private Color[] bodyColorPalette =
    {
        new Color(0.90f, 0.20f, 0.20f), // 赤
        new Color(0.20f, 0.45f, 0.95f), // 青
        new Color(0.25f, 0.80f, 0.30f), // 緑
        new Color(0.95f, 0.80f, 0.15f), // 黄
        new Color(0.75f, 0.25f, 0.85f), // 紫
        new Color(0.95f, 0.55f, 0.10f), // オレンジ
        new Color(0.15f, 0.80f, 0.80f), // シアン
        new Color(0.95f, 0.35f, 0.65f), // ピンク
    };

    private readonly NetworkVariable<Color> _bodyColor = new NetworkVariable<Color>(
        Color.white, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    private static MaterialPropertyBlock _colorPropertyBlock;

    /// <summary>今試合中のキル数(サーバー権威、自滅は除く)。勝者決定とリザルト表示に使う</summary>
    public readonly NetworkVariable<int> KillCount = new NetworkVariable<int>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>今試合中に他プレイヤーへ与えた累計ダメージ(サーバー権威、自爆は除く)</summary>
    public readonly NetworkVariable<float> TotalDamageDealt = new NetworkVariable<float>(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    [Header("見た目(実アセット)")]
    [Tooltip("設定するとAwake()でPlayerの子として実体化し、bodyVisualとして使うキャラクターモデル")]
    [SerializeField] private GameObject bodyVisualPrefab;
    [Tooltip("PlayerWeapon.weaponHolderと同じTransform(武器の見た目の基準点)")]
    [SerializeField] private Transform weaponHolder;
    [Tooltip("bodyVisualPrefab内から名前で検索する右手ボーン名。見つからなければ追従しない")]
    [SerializeField] private string rightHandBoneName = "RightHand";
    [Tooltip("weaponHolderが右手ボーンの位置へ近づく速さ(大きいほど素早く追従)")]
    [SerializeField] private float weaponFollowSpeed = 10f;
    [Tooltip("右手ボーンのローカル空間で見た、武器を持たせる位置のずらし量")]
    [SerializeField] private Vector3 weaponHandLocalOffset = new Vector3(0f, 0f, 0.05f);

    private Transform _rightHandBone;
    private Vector3 _weaponLocalOffsetPos;

    [Header("死亡演出")]
    [Tooltip("死亡時に複製して物理挙動させる見た目の対象。bodyVisualPrefab設定時は自動で割り当てられる")]
    [SerializeField] private Transform bodyVisual;
    [Tooltip("死亡時に加える吹き飛ばし力")]
    [SerializeField] private float deathRagdollForce = 6f;
    [SerializeField] private float deathRagdollTorque = 25f;
    [Tooltip("死亡後、ラグドールを消すまでの秒数")]
    [SerializeField] private float deathRagdollDestroyDelaySeconds = 2f;

    private GameObject _ragdollInstance;
    private Animator _bodyAnimator;
    private string _currentBodyAnimState;

    /// <summary>他プレイヤーからも見える視点ピッチ(上下)。武器の向きに使う</summary>
    public NetworkVariable<float> LookPitch = new NetworkVariable<float>(
        0f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    /// <summary>アニメ状態の同期用(Owner書き込み)。0=Idle/1=Walk/2=Run/3=Jumping Up</summary>
    private readonly NetworkVariable<byte> _bodyAnimStateNet = new NetworkVariable<byte>(
        0, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    /// <summary>PlayerPrefsに保存したニックネームをOwner権威で全員に同期する</summary>
    private readonly NetworkVariable<FixedString32Bytes> _displayName = new NetworkVariable<FixedString32Bytes>(
        default, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Owner);

    public string DisplayName => _displayName.Value.IsEmpty ? $"client {OwnerClientId}" : _displayName.Value.ToString();

    /// <summary>clientIdから表示名を引く。見つからなければ"client {id}"を返す</summary>
    public static string ResolveDisplayName(ulong clientId)
    {
        foreach (var pc in _activeInstances)
        {
            if (pc.OwnerClientId == clientId) return pc.DisplayName;
        }
        return $"client {clientId}";
    }

    private static readonly List<PlayerController> _activeInstances = new List<PlayerController>();
    /// <summary>スポーン中の全プレイヤー</summary>
    public static IReadOnlyList<PlayerController> ActiveInstances => _activeInstances;

    /// <summary>このクライアント自身のプレイヤー</summary>
    public static PlayerController LocalPlayer { get; private set; }

    /// <summary>自分のカメラ(ワールド→スクリーン座標変換用)</summary>
    public Camera PlayerCamera => playerCamera;

    private CharacterController _controller;
    private ClientNetworkTransform _networkTransform;

    private Vector3 _horizontalVelocity;
    private float _verticalVelocity;
    private bool _isGrounded;
    private float _pitch;
    // カメラのヨー(体の向きとは独立)
    private float _yaw;

    private float _stuckCheckTimer;
    private Vector3 _stuckCheckStartPosition;
    private bool _hasStuckCheckStartPosition;

    // 発砲/投擲の瞬間に体をカメラ方向へ向け、一定時間その向きを保つための状態
    private float _aimFacingOverrideExpireTime;
    private Vector3 _aimFacingOverrideDirection;

    private bool _wasDead;

    // 爆発等からの吹き飛ばし専用の速度(通常移動の速度とは独立して適用する)
    [Header("ノックバック")]
    [SerializeField] private float knockbackDecayPerSecond = 18f;
    private Vector3 _knockbackVelocity;
    // 死亡後にノックバックを消化し終えたらtrue(リスポーンでfalseに戻す)
    private bool _knockbackStoppedAfterDeath;

    [Header("場外転落")]
    [Tooltip("この高さを下回ったら場外転落とみなして即死させる(x,zは問わない)")]
    [SerializeField] private float fallDeathY = -4f;
    // サーバー側の位置は落下中に遅れるため、Ownerからの申告はこの分だけ甘く受け付ける
    private const float FallDeathServerTolerance = 3f;
    private bool _fallDeathRequested;

    /// <summary>サーバー権威の移動速度倍率(1=通常)。ServerApplySlow()で一時的に下げる</summary>
    [Header("ステータス効果(スロウ)")]
    private readonly NetworkVariable<float> _slowMultiplier = new NetworkVariable<float>(
        1f, NetworkVariableReadPermission.Everyone, NetworkVariableWritePermission.Server);

    /// <summary>スロウを解除すべきTime.time(サーバーのみ)</summary>
    private float _slowExpireTime;

    private void Awake()
    {
        _controller = GetComponent<CharacterController>();
        _networkTransform = GetComponent<ClientNetworkTransform>();
        BuildBodyVisualIfNeeded();
    }

    /// <summary>bodyVisualPrefabがあればPlayerの子として実体化し、Animatorと右手ボーン(武器追従用)を取得する。Root Motionは無効化する</summary>
    private void BuildBodyVisualIfNeeded()
    {
        if (bodyVisualPrefab == null) return;

        GameObject instance = Instantiate(bodyVisualPrefab, transform);
        instance.transform.localPosition = Vector3.zero;
        instance.transform.localRotation = Quaternion.identity;
        instance.transform.localScale = Vector3.one;

        bodyVisual = instance.transform;
        _bodyAnimator = instance.GetComponentInChildren<Animator>();
        if (_bodyAnimator != null) _bodyAnimator.applyRootMotion = false;

        if (weaponHolder != null && !string.IsNullOrEmpty(rightHandBoneName))
        {
            // Awake時点ではバインドポーズのままなので、1回Updateして既定ステートのポーズを反映させる
            if (_bodyAnimator != null) _bodyAnimator.Update(0f);

            Transform rightHand = FindDeepChild(instance.transform, rightHandBoneName);
            if (rightHand != null)
            {
                // 位置だけ右手ボーンへ補間追従させる(追従はUpdateWeaponFollow()。向きは別途計算)
                _rightHandBone = rightHand;
                _weaponLocalOffsetPos = weaponHandLocalOffset;
            }
            else
            {
                Debug.LogWarning($"[PlayerController] bodyVisualPrefabの中に右手ボーン\"{rightHandBoneName}\"が" +
                    "見つかりませんでした。武器の見た目はPlayer本体基準の固定位置のままになります。");
            }
        }
    }

    /// <summary>weaponHolderの位置を右手ボーンへ補間で追従させ、向きを体のyaw+視点pitchから直接設定する(全クライアント共通)</summary>
    private void UpdateWeaponFollow()
    {
        if (_rightHandBone == null || weaponHolder == null) return;

        float t = 1f - Mathf.Exp(-weaponFollowSpeed * Time.deltaTime);

        Vector3 targetPos = _rightHandBone.TransformPoint(_weaponLocalOffsetPos);
        weaponHolder.position = Vector3.Lerp(weaponHolder.position, targetPos, t);

        // 回転は補間せず即座に反映する
        weaponHolder.rotation = Quaternion.Euler(_pitch, transform.eulerAngles.y, 0f);
    }

    /// <summary>子孫を再帰的に名前で探す(ボーン探索用)</summary>
    private static Transform FindDeepChild(Transform parent, string name)
    {
        if (parent.name == name) return parent;
        foreach (Transform child in parent)
        {
            Transform found = FindDeepChild(child, name);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>Animatorのステートを直接CrossFadeで切り替える(状態が変わった時だけ呼ぶ)</summary>
    private void PlayBodyAnimation(string stateName, float crossFadeSeconds = 0.15f)
    {
        if (_bodyAnimator == null) return;
        if (_currentBodyAnimState == stateName) return;

        _bodyAnimator.CrossFade(stateName, crossFadeSeconds);
        _currentBodyAnimState = stateName;
    }

    private const byte BodyAnimIdle = 0;
    private const byte BodyAnimWalk = 1;
    private const byte BodyAnimRun = 2;
    private const byte BodyAnimJumpingUp = 3;

    /// <summary>全クライアントで、同期された状態コードをAnimatorステートへ変換して再生する</summary>
    private void HandleBodyAnimStateChanged(byte previous, byte current)
    {
        switch (current)
        {
            case BodyAnimJumpingUp: PlayBodyAnimation("Jumping Up", 0.05f); break;
            case BodyAnimWalk: PlayBodyAnimation("Walk"); break;
            case BodyAnimRun: PlayBodyAnimation("Run"); break;
            default: PlayBodyAnimation("Idle"); break;
        }
    }

    /// <summary>移動速度からIdle/Walk/Run、ジャンプ時はJumping Upを選んで状態コードを書き込む(Ownerのみ。空中では上書きしない)</summary>
    private void UpdateBodyLocomotionAnimation(bool grounded, bool justJumped)
    {
        if (_bodyAnimator == null) return;

        if (justJumped)
        {
            _bodyAnimStateNet.Value = BodyAnimJumpingUp;
            return;
        }

        if (!grounded) return;

        float speedRatio = new Vector2(_horizontalVelocity.x, _horizontalVelocity.z).magnitude / Mathf.Max(0.01f, moveSpeed);

        if (speedRatio < 0.05f) _bodyAnimStateNet.Value = BodyAnimIdle;
        else if (speedRatio < 0.6f) _bodyAnimStateNet.Value = BodyAnimWalk;
        else _bodyAnimStateNet.Value = BodyAnimRun;
    }

    public override void OnNetworkSpawn()
    {
        _activeInstances.Add(this);
        if (IsOwner) LocalPlayer = this;

        if (playerCamera != null)
        {
            playerCamera.gameObject.SetActive(IsOwner);
        }

        if (IsOwner) _displayName.Value = DisplayNameUI.GetSavedName();

        if (IsServer && bodyColorPalette != null && bodyColorPalette.Length > 0)
        {
            _bodyColor.Value = bodyColorPalette[(int)(OwnerClientId % (ulong)bodyColorPalette.Length)];
        }
        _bodyColor.OnValueChanged += HandleBodyColorChanged;
        ApplyBodyColor(_bodyColor.Value);

        LookPitch.OnValueChanged += HandleLookPitchReplicated;

        _bodyAnimStateNet.OnValueChanged += HandleBodyAnimStateChanged;

        SetupOutlineIfNeeded();

        // 観戦用に撃破者IDを覚えるため、Ownerだけが購読する
        if (IsOwner) MatchManager.KillBroadcast += HandleKillBroadcastForSpectate;
    }

    public override void OnNetworkDespawn()
    {
        _activeInstances.Remove(this);
        if (LocalPlayer == this) LocalPlayer = null;
        _bodyColor.OnValueChanged -= HandleBodyColorChanged;
        LookPitch.OnValueChanged -= HandleLookPitchReplicated;
        _bodyAnimStateNet.OnValueChanged -= HandleBodyAnimStateChanged;

        OutlineSettings.Changed -= ApplyOutlineSettings;
        if (_outlineMaterialInstance != null) Destroy(_outlineMaterialInstance);

        if (IsOwner) MatchManager.KillBroadcast -= HandleKillBroadcastForSpectate;
    }

    private SkinnedMeshRenderer _outlineRenderer;
    private Material _outlineMaterialInstance;

    /// <summary>自分以外のプレイヤーにアウトライン用マテリアルを追加する(ローカル表示のみ)</summary>
    private void SetupOutlineIfNeeded()
    {
        if (IsOwner) return;
        if (bodyVisual == null) return;

        _outlineRenderer = bodyVisual.GetComponentInChildren<SkinnedMeshRenderer>();
        if (_outlineRenderer == null) return;

        Shader outlineShader = Shader.Find("BombRoyale/PlayerOutline");
        if (outlineShader == null)
        {
            Debug.LogWarning("[PlayerController] PlayerOutlineシェーダーが見つかりませんでした。アウトラインは無効のままになります。");
            return;
        }

        _outlineMaterialInstance = new Material(outlineShader) { name = "PlayerOutline (Instance)" };
        _outlineMaterialInstance.SetFloat("_OutlineWidth", 0.02f);

        OutlineSettings.Changed += ApplyOutlineSettings;
        ApplyOutlineSettings();
    }

    /// <summary>アウトラインの色とON/OFFを反映する(OFFはマテリアルを外して消す)</summary>
    private void ApplyOutlineSettings()
    {
        if (_outlineRenderer == null || _outlineMaterialInstance == null) return;

        _outlineMaterialInstance.SetColor("_OutlineColor", OutlineSettings.CurrentColor);

        var materials = new List<Material>(_outlineRenderer.sharedMaterials);
        bool hasOutline = materials.Contains(_outlineMaterialInstance);

        if (OutlineSettings.Enabled && !hasOutline)
        {
            materials.Add(_outlineMaterialInstance);
            _outlineRenderer.materials = materials.ToArray();
        }
        else if (!OutlineSettings.Enabled && hasOutline)
        {
            materials.Remove(_outlineMaterialInstance);
            _outlineRenderer.materials = materials.ToArray();
        }
    }

    private void HandleBodyColorChanged(Color previousValue, Color newValue)
    {
        ApplyBodyColor(newValue);
    }

    /// <summary>bodyVisualのRendererにMaterialPropertyBlockでカラーを適用する(マテリアルは共有のまま)</summary>
    private void ApplyBodyColor(Color color)
    {
        if (bodyVisual == null) return;
        var renderer = bodyVisual.GetComponentInChildren<Renderer>();
        if (renderer == null) return;

        _colorPropertyBlock ??= new MaterialPropertyBlock();
        renderer.GetPropertyBlock(_colorPropertyBlock);
        _colorPropertyBlock.SetColor("_BaseColor", color);
        _colorPropertyBlock.SetColor("_Color", color);
        renderer.SetPropertyBlock(_colorPropertyBlock);
    }

    private void Update()
    {
        if (!IsSpawned) return;

        // 死亡演出は全クライアントで再生する
        UpdateDeathVisual();

        // スロウ解除はサーバーが判定する
        if (IsServer) TickSlowEffect();

        // 場外転落はサーバーも全員分を毎フレーム判定する
        if (IsServer) ServerCheckFallDeath();

        if (!IsOwner) return;

        bool isDead = health != null && health.IsDead;

        // ノックバックは死亡した瞬間の1回だけ消化し、以後は速度をゼロに固定する
        if (!isDead || !_knockbackStoppedAfterDeath)
        {
            ProcessKnockback(Time.deltaTime);
        }

        if (isDead)
        {
            if (!_knockbackStoppedAfterDeath)
            {
                _knockbackVelocity = Vector3.zero;
                _knockbackStoppedAfterDeath = true;
            }
            // 死亡中は観戦処理のみ(移動/発砲/拾得は受け付けない)
            TickSpectate();
            return;
        }

        _knockbackStoppedAfterDeath = false;
        if (_spectateState != SpectateState.None) ResetSpectate();

        // MatchManager未配置なら常に操作可能。発砲/拾得のフェーズ制限はweapon側で行う
        bool canMove = MatchManager.Instance == null || MatchManager.Instance.IsMovementActiveForInput;

        PlayerInputFrame frame = PlayerInputFrame.Empty;
        if (canMove && TouchInputProvider.Instance != null)
        {
            frame = TouchInputProvider.Instance.BuildInputFrame();
        }

        HandleLookLocally(frame.lookPitchDelta, frame.lookYawDelta);
        ApplyMovement(frame.horizontal, frame.vertical, frame.wantsJumpDown, Time.deltaTime);
        footsteps?.Tick(Time.deltaTime, _horizontalVelocity, _isGrounded);
        weapon?.HandleInput(frame);

        // 場外転落: Ownerが閾値を下回ったらサーバーへ申告する(サーバー側で再検証)
        if (!_fallDeathRequested && MatchManager.Instance != null && MatchManager.Instance.IsMatchActiveForInput
            && transform.position.y < fallDeathY)
        {
            _fallDeathRequested = true;
            RequestFallDeathServerRpc();
        }
    }

    /// <summary>死亡/リスポーンの切り替わりを検知し、ラグドールの生成/後片付けを行う(全クライアント)</summary>
    private void UpdateDeathVisual()
    {
        bool isDead = health != null && health.IsDead;

        if (!isDead)
        {
            if (_wasDead) DestroyRagdoll();
            _wasDead = false;
            return;
        }

        if (!_wasDead)
        {
            SpawnRagdoll();
            _wasDead = true;
        }
    }

    /// <summary>bodyVisualを複製してCollider+Rigidbodyを付けワールドへ切り離し、ランダムな力とトルクで物理に任せて転がす</summary>
    private void SpawnRagdoll()
    {
        if (bodyVisual == null) return;

        bodyVisual.gameObject.SetActive(false);

        _ragdollInstance = Instantiate(bodyVisual.gameObject, bodyVisual.position, bodyVisual.rotation);
        _ragdollInstance.name = "DeathRagdoll";
        _ragdollInstance.SetActive(true);
        _ragdollInstance.transform.SetParent(null, true); // プレイヤー本体の以後の移動に引きずられないよう切り離す

        // MaterialPropertyBlockは複製されないため、ラグドール側にも色を適用し直す
        var ragdollRenderer = _ragdollInstance.GetComponentInChildren<Renderer>();
        if (ragdollRenderer != null)
        {
            _colorPropertyBlock ??= new MaterialPropertyBlock();
            ragdollRenderer.GetPropertyBlock(_colorPropertyBlock);
            _colorPropertyBlock.SetColor("_BaseColor", _bodyColor.Value);
            _colorPropertyBlock.SetColor("_Color", _bodyColor.Value);
            ragdollRenderer.SetPropertyBlock(_colorPropertyBlock);
        }

        // Animatorを止めて死亡時点のポーズで凍結させる
        var ragdollAnimator = _ragdollInstance.GetComponentInChildren<Animator>();
        if (ragdollAnimator != null) ragdollAnimator.enabled = false;

        if (_ragdollInstance.GetComponentInChildren<Collider>() == null)
        {
            var capsule = _ragdollInstance.AddComponent<CapsuleCollider>();
            capsule.height = 1.6f;
            capsule.radius = 0.4f;
        }

        var rb = _ragdollInstance.AddComponent<Rigidbody>();
        rb.mass = 1f;
        rb.linearDamping = 0.3f;
        rb.angularDamping = 0.15f;

        Vector3 randomDir = new Vector3(Random.Range(-1f, 1f), 0.5f, Random.Range(-1f, 1f)).normalized;
        rb.AddForce(randomDir * deathRagdollForce, ForceMode.VelocityChange);
        Vector3 randomTorqueAxis = new Vector3(Random.Range(-1f, 1f), Random.Range(-1f, 1f), Random.Range(-1f, 1f)).normalized;
        rb.AddTorque(randomTorqueAxis * deathRagdollTorque, ForceMode.VelocityChange);

        Destroy(_ragdollInstance, deathRagdollDestroyDelaySeconds);
    }

    private void DestroyRagdoll()
    {
        if (_ragdollInstance != null) Destroy(_ragdollInstance);
        _ragdollInstance = null;
        if (bodyVisual != null) bodyVisual.gameObject.SetActive(true);
    }

    private void LateUpdate()
    {
        if (!IsSpawned) return;

        // 武器の追従は全クライアント共通なので、IsOwnerガードの前に行う
        UpdateWeaponFollow();

        if (!IsOwner) return;

        bool isDeadForCamera = health != null && health.IsDead;
        if (isDeadForCamera) UpdateSpectateCamera();
        else UpdateThirdPersonCamera();
    }

    /// <summary>試合開始時にサーバーが呼ぶ。体力・武器をリセットし、Ownerをスポーン位置へテレポートさせる</summary>
    public void ServerResetForMatchStart(Vector3 spawnPosition, Quaternion spawnRotation)
    {
        if (!IsServer) return;

        health?.ServerResetHealth();
        weapon?.ServerResetWeapon();

        var rpcParams = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } }
        };
        TeleportToSpawnClientRpc(spawnPosition, spawnRotation, rpcParams);
    }

    /// <summary>爆発等で吹き飛ばす。移動はOwner権威のため、Ownerへ ClientRpc で速度を伝える</summary>
    public void ServerApplyKnockback(Vector3 impulse)
    {
        if (!IsServer) return;

        var rpcParams = new ClientRpcParams
        {
            Send = new ClientRpcSendParams { TargetClientIds = new[] { OwnerClientId } }
        };
        ApplyKnockbackClientRpc(impulse, rpcParams);
    }

    [ClientRpc]
    private void ApplyKnockbackClientRpc(Vector3 impulse, ClientRpcParams rpcParams = default)
    {
        _knockbackVelocity += impulse;
    }

    /// <summary>スロウを掛ける(サーバー)。倍率を0.05〜1に制限し、durationSeconds後に解除する。再度呼ばれたら上書きする</summary>
    public void ServerApplySlow(float multiplier, float durationSeconds)
    {
        if (!IsServer) return;

        _slowMultiplier.Value = Mathf.Clamp(multiplier, 0.05f, 1f);
        _slowExpireTime = Time.time + Mathf.Max(0f, durationSeconds);
    }

    /// <summary>サーバーで毎フレーム呼ぶ。期限が来たらスロウを解除する</summary>
    private void TickSlowEffect()
    {
        if (_slowMultiplier.Value >= 1f) return;
        if (Time.time < _slowExpireTime) return;

        _slowMultiplier.Value = 1f;
    }

    /// <summary>Ownerの場外転落申告を、サーバー側の位置で再検証してから即死させる</summary>
    [ServerRpc]
    private void RequestFallDeathServerRpc()
    {
        if (health == null || health.IsDead) return;
        if (transform.position.y >= fallDeathY + FallDeathServerTolerance)
        {
            Debug.LogWarning($"[PlayerController] {gameObject.name}(client {OwnerClientId}) の場外転落申告を保留 " +
                $"(サーバー側y={transform.position.y:F1}、閾値{fallDeathY})。サーバー側の毎フレーム判定に任せる");
            return;
        }

        Debug.Log($"[PlayerController] {gameObject.name}(client {OwnerClientId}) が場外転落 (申告, y={transform.position.y:F1})");
        health.ServerApplyDamage(health.CurrentHealth.Value, OwnerClientId);
    }

    /// <summary>サーバーが毎フレーム、このプレイヤーの高さだけを見て場外転落を判定する(x,zは見ない)</summary>
    private void ServerCheckFallDeath()
    {
        if (health == null || health.IsDead) return;
        if (MatchManager.Instance == null || !MatchManager.Instance.IsMatchActiveForInput) return;
        if (transform.position.y >= fallDeathY) return;

        Debug.Log($"[PlayerController] {gameObject.name}(client {OwnerClientId}) が場外転落 (サーバー判定, y={transform.position.y:F1})");
        health.ServerApplyDamage(health.CurrentHealth.Value, OwnerClientId);
    }

    [ClientRpc]
    private void TeleportToSpawnClientRpc(Vector3 spawnPosition, Quaternion spawnRotation, ClientRpcParams rpcParams = default)
    {
        if (!IsOwner) return;

        // Owner権威のため、テレポートは本人に実行してもらう
        _controller.enabled = false;
        _networkTransform.Teleport(spawnPosition, spawnRotation, transform.localScale);
        _horizontalVelocity = Vector3.zero;
        _verticalVelocity = 0f;

        _pitch = 0f;
        // カメラのヨーをスポーン向きに合わせる
        _yaw = spawnRotation.eulerAngles.y;
        if (cameraPivot != null) cameraPivot.rotation = Quaternion.Euler(0f, _yaw, 0f);
        LookPitch.Value = 0f;

        _stuckCheckTimer = 0f;
        _hasStuckCheckStartPosition = false;

        // ラグドール等の死亡演出を即座に後片付けする
        DestroyRagdoll();
        _wasDead = false;
        if (bodyVisual != null) bodyVisual.localRotation = Quaternion.identity;

        // ノックバックの持ち越しを防ぐ
        _knockbackVelocity = Vector3.zero;
        _knockbackStoppedAfterDeath = false;
        _fallDeathRequested = false;
        ResetSpectate();

        footsteps?.ResetForRespawn();

        _controller.enabled = true;
    }
}
