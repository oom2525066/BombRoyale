using UnityEngine;

/// <summary>
/// 死亡後のカメラ演出(死亡位置を見下ろす)と、生存中の他プレイヤーの観戦を担当する部分クラス。
/// 観戦対象の位置だけを基準点にして、自分のlook入力で肩越しオービットカメラを再現する。
/// </summary>
public partial class PlayerController
{
    [Header("観戦(死亡後)")]
    [Tooltip("死亡位置を見下ろす演出の時間(秒)")]
    [SerializeField] private float deathCamDurationSeconds = 1.5f;
    [Tooltip("死亡カメラの死亡位置からのローカルオフセット(x=右, y=上, z=前)")]
    [SerializeField] private Vector3 deathCamViewOffset = new Vector3(0f, 3.5f, -4f);
    [Tooltip("観戦対象の足元からの注視点の高さ")]
    [SerializeField] private float spectateTargetHeight = 1.5f;

    private enum SpectateState { None, DeathCam, Spectating }
    private SpectateState _spectateState = SpectateState.None;

    private float _deathCamTimer;
    private Vector3 _deathCamStartPos;
    private Quaternion _deathCamStartRot;
    private Vector3 _deathCamTargetPos;
    private Quaternion _deathCamTargetRot;

    private PlayerController _spectateTarget;
    private ulong _lastKillerClientId = ulong.MaxValue;

    /// <summary>キル通知の購読先。自分が被害者なら撃破者IDを覚えておく(観戦対象の優先選択用)</summary>
    private void HandleKillBroadcastForSpectate(ulong victimClientId, ulong killerClientId)
    {
        if (victimClientId == OwnerClientId) _lastKillerClientId = killerClientId;
    }

    /// <summary>死亡中(Owner)に毎フレーム呼ぶ。視点入力と観戦対象の切り替えだけを処理する</summary>
    private void TickSpectate()
    {
        if (_spectateState == SpectateState.None)
        {
            BeginDeathSequence();
        }

        PlayerInputFrame frame = PlayerInputFrame.Empty;
        if (TouchInputProvider.Instance != null)
        {
            frame = TouchInputProvider.Instance.BuildInputFrame();
        }
        // 観戦中も視点操作で観戦対象の周りを見回せる
        HandleLookLocally(frame.lookPitchDelta, frame.lookYawDelta);

        if (_spectateState == SpectateState.DeathCam)
        {
            _deathCamTimer += Time.deltaTime;
            if (_deathCamTimer >= deathCamDurationSeconds)
            {
                _spectateState = SpectateState.Spectating;
                FindNextSpectateTarget();
            }
            return;
        }

        // 拾得ボタンを観戦対象の切り替えに流用する
        if (frame.wantsPickupDown) CycleSpectateTarget();

        if (_spectateTarget == null || _spectateTarget.health == null || _spectateTarget.health.IsDead)
        {
            FindNextSpectateTarget();
        }
    }

    /// <summary>死亡直後に1回呼ばれ、死亡位置を見下ろすカメラ演出を開始する</summary>
    private void BeginDeathSequence()
    {
        _spectateState = SpectateState.DeathCam;
        _deathCamTimer = 0f;
        _spectateTarget = null;

        if (playerCamera == null) return;

        _deathCamStartPos = playerCamera.transform.position;
        _deathCamStartRot = playerCamera.transform.rotation;

        Vector3 deathPosition = transform.position;
        Vector3 offsetWorld = transform.right * deathCamViewOffset.x
            + Vector3.up * deathCamViewOffset.y
            + transform.forward * deathCamViewOffset.z;
        _deathCamTargetPos = deathPosition + offsetWorld;

        Vector3 lookDir = deathPosition - _deathCamTargetPos;
        _deathCamTargetRot = lookDir.sqrMagnitude > 0.0001f
            ? Quaternion.LookRotation(lookDir.normalized, Vector3.up)
            : _deathCamStartRot;
    }

    /// <summary>生存中の他プレイヤーから観戦対象を選ぶ。自分を倒した相手を優先し、居なければOwnerClientIdが最小の者</summary>
    private void FindNextSpectateTarget()
    {
        if (_lastKillerClientId != ulong.MaxValue)
        {
            foreach (var candidate in ActiveInstances)
            {
                if (candidate != null && candidate.OwnerClientId == _lastKillerClientId
                    && candidate.health != null && !candidate.health.IsDead)
                {
                    _spectateTarget = candidate;
                    return;
                }
            }
        }

        PlayerController best = null;
        foreach (var candidate in ActiveInstances)
        {
            if (candidate == null || candidate == this) continue;
            if (candidate.health == null || candidate.health.IsDead) continue;
            if (best == null || candidate.OwnerClientId < best.OwnerClientId) best = candidate;
        }
        _spectateTarget = best;
    }

    /// <summary>観戦対象を次の生存者へ順送りする(OwnerClientId順の輪っか)</summary>
    private void CycleSpectateTarget()
    {
        PlayerController first = null;
        PlayerController next = null;
        bool foundCurrent = false;

        foreach (var candidate in ActiveInstances)
        {
            if (candidate == null || candidate == this) continue;
            if (candidate.health == null || candidate.health.IsDead) continue;

            if (first == null || candidate.OwnerClientId < first.OwnerClientId) first = candidate;

            if (foundCurrent && next == null) next = candidate;
            if (_spectateTarget != null && candidate == _spectateTarget) foundCurrent = true;
        }

        _spectateTarget = next ?? first;
    }

    /// <summary>リスポーン直後に呼ぶ。観戦状態をリセットする</summary>
    private void ResetSpectate()
    {
        _spectateState = SpectateState.None;
        _spectateTarget = null;
        _lastKillerClientId = ulong.MaxValue;
    }

    /// <summary>死亡中、LateUpdate()からUpdateThirdPersonCamera()の代わりに呼ぶ</summary>
    private void UpdateSpectateCamera()
    {
        if (playerCamera == null) return;

        if (_spectateState == SpectateState.DeathCam)
        {
            float t = deathCamDurationSeconds > 0f ? Mathf.Clamp01(_deathCamTimer / deathCamDurationSeconds) : 1f;
            float smoothT = t * t * (3f - 2f * t); // smoothstep

            playerCamera.transform.SetPositionAndRotation(
                Vector3.Lerp(_deathCamStartPos, _deathCamTargetPos, smoothT),
                Quaternion.Slerp(_deathCamStartRot, _deathCamTargetRot, smoothT));
            return;
        }

        if (_spectateTarget == null) return;

        // 観戦対象の胸〜頭を基準点に、UpdateThirdPersonCamera()と同じ肩越しオービット計算を行う
        Vector3 targetBasePosition = _spectateTarget.transform.position + Vector3.up * spectateTargetHeight;
        Quaternion pivotRotation = Quaternion.Euler(_pitch, _yaw, 0f);

        Vector3 shoulderOrigin = targetBasePosition + pivotRotation * Vector3.right * shoulderOffset;
        Vector3 offset = -(pivotRotation * Vector3.forward) * cameraDistance + (pivotRotation * Vector3.up) * cameraHeight;
        float maxDistance = offset.magnitude;
        Vector3 castDir = offset.normalized;

        float distance = maxDistance;
        if (Physics.SphereCast(shoulderOrigin, cameraCollisionRadius, castDir, out RaycastHit hit,
                maxDistance, cameraCollisionMask, QueryTriggerInteraction.Ignore))
        {
            distance = Mathf.Max(0.2f, hit.distance);
        }

        Vector3 targetPosition = shoulderOrigin + castDir * distance;
        Vector3 smoothedPosition = Vector3.SmoothDamp(playerCamera.transform.position, targetPosition, ref _cameraVelocity, cameraSmoothTime);

        playerCamera.transform.position = smoothedPosition;
        playerCamera.transform.rotation = pivotRotation;
        playerCamera.fieldOfView = Mathf.Lerp(playerCamera.fieldOfView, baseFieldOfView, Time.deltaTime * fovLerpSpeed);
    }

    /// <summary>観戦中の対象名と切り替え方法を画面下寄りに表示する</summary>
    private void OnGUI()
    {
        if (!IsSpawned || !IsOwner) return;
        if (_spectateState != SpectateState.Spectating) return;
        if (MatchManager.Instance == null || MatchManager.Instance.Phase != MatchManager.GamePhase.Playing) return;

        PopUI.EnsureSkinFontApplied();
        GUIScaler.Begin();

        string targetName = _spectateTarget != null ? _spectateTarget.DisplayName : "-";
        string text = Localization.Format("spectate.watching", targetName);

        var style = new GUIStyle(GUI.skin.label) { fontSize = 16, fontStyle = FontStyle.Bold, alignment = TextAnchor.MiddleCenter };
        style.normal.textColor = Color.white;
        float width = 320f;
        var rect = new Rect(GUIScaler.VirtualWidth / 2f - width / 2f, GUIScaler.VirtualHeight - GUIScaler.SafeAreaBottom - 120f, width, 28f);
        GUI.DrawTexture(rect, PopUI.PanelTexture);
        GUI.Label(rect, text, style);

        var hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 12, alignment = TextAnchor.MiddleCenter };
        hintStyle.normal.textColor = new Color(1f, 1f, 1f, 0.75f);
        var hintRect = new Rect(rect.x, rect.yMax + 2f, width, 20f);
        GUI.Label(hintRect, Localization.Get("spectate.cycleHint"), hintStyle);

        GUIScaler.End();
    }
}
