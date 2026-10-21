using UnityEngine;

/// <summary>
/// PlayerControllerの移動・接地判定を担当する部分クラス。
/// CharacterController + SphereCast接地判定 + 地上/空中で別の加速度 + 詰まり脱出の保険で構成する。
/// 移動入力はカメラのヨー基準の方向に変換し、体は移動方向へ滑らかに回転する。
/// </summary>
public partial class PlayerController
{
    /// <summary>入力(h,v)を、カメラのヨー基準の水平方向ベクトルへ変換する(ピッチは無視)</summary>
    private Vector3 ComputeCameraRelativeWishDir(float h, float v)
    {
        Transform reference = cameraPivot != null ? cameraPivot : transform;
        Vector3 camForward = Vector3.ProjectOnPlane(reference.forward, Vector3.up).normalized;
        Vector3 camRight = Vector3.ProjectOnPlane(reference.right, Vector3.up).normalized;

        Vector3 wishDir = camRight * h + camForward * v;
        return wishDir.sqrMagnitude > 1f ? wishDir.normalized : wishDir;
    }

    /// <summary>体の向きを移動方向へ滑らかに回転させる。発砲直後のオーバーライド期間中は狙い方向を維持し、入力が無ければ何もしない</summary>
    private void RotateTowardsMovementDirection(Vector3 wishDir, float deltaTime)
    {
        if (Time.time < _aimFacingOverrideExpireTime)
        {
            transform.rotation = Quaternion.LookRotation(_aimFacingOverrideDirection, Vector3.up);
            return;
        }

        if (wishDir.sqrMagnitude < 0.0001f) return;

        Quaternion targetRotation = Quaternion.LookRotation(wishDir, Vector3.up);
        transform.rotation = Quaternion.RotateTowards(transform.rotation, targetRotation, turnSpeedDegreesPerSecond * deltaTime);
    }

    /// <summary>発砲/投擲の直前に呼ぶ。体をカメラのヨー方向へ即座に向け、durationSeconds秒間その向きを維持する。
    /// weaponHolderも同期的に追従させるので、直後に読む銃口位置/向きは最新になる</summary>
    public void TriggerAimFacingOverride(float durationSeconds = 0.5f)
    {
        Transform reference = cameraPivot != null ? cameraPivot : transform;
        Vector3 dir = Vector3.ProjectOnPlane(reference.forward, Vector3.up);
        if (dir.sqrMagnitude > 0.0001f)
        {
            _aimFacingOverrideDirection = dir.normalized;
            transform.rotation = Quaternion.LookRotation(_aimFacingOverrideDirection, Vector3.up);
        }
        _aimFacingOverrideExpireTime = Time.time + durationSeconds;

        Vector3 beforeSync = weaponHolder != null ? weaponHolder.forward : Vector3.zero;
        UpdateWeaponFollow();
        Debug.Log($"[PlayerController] TriggerAimFacingOverride: bodyYaw={transform.eulerAngles.y:F1} " +
            $"weaponHolder.forward {beforeSync}→{(weaponHolder != null ? weaponHolder.forward : Vector3.zero)}");
    }

    private void ApplyMovement(float h, float v, bool wantsJump, float deltaTime)
    {
        Vector3 wishDir = ComputeCameraRelativeWishDir(h, v);
        RotateTowardsMovementDirection(wishDir, deltaTime);

        _isGrounded = CheckGrounded();
        // 上昇中は接地扱いにしない(ジャンプ直後に張り付け力で潰されるのを防ぐ)
        bool treatAsGrounded = _isGrounded && _verticalVelocity <= 0f;

        // スロウ効果(_slowMultiplier)はここで掛ける
        Vector3 wishVelocity = wishDir * moveSpeed * _slowMultiplier.Value;

        if (treatAsGrounded)
        {
            if (wishDir.sqrMagnitude > 0.0001f)
            {
                _horizontalVelocity = Vector3.MoveTowards(_horizontalVelocity, wishVelocity, groundAcceleration * deltaTime);
            }
            else
            {
                // 入力が無ければ摩擦で減速して止まる
                _horizontalVelocity = Vector3.MoveTowards(_horizontalVelocity, Vector3.zero, groundFriction * deltaTime);
            }

            if (wantsJump)
            {
                // 到達高さ(スロウ倍率込み)から初速を逆算: v = sqrt(2 * |gravity| * height)
                _verticalVelocity = Mathf.Sqrt(2f * -gravity * jumpHeight * _slowMultiplier.Value);
                footsteps?.PlayJump();
                UpdateBodyLocomotionAnimation(treatAsGrounded, justJumped: true);
            }
            else
            {
                // 地面に張り付かせる(段差・坂道で浮かないように)
                _verticalVelocity = -groundedStickForce;
                UpdateBodyLocomotionAnimation(treatAsGrounded, justJumped: false);
            }
        }
        else
        {
            // 空中は方向転換のみ許可し、速度そのものは増やさない
            float speedBeforeSteer = _horizontalVelocity.magnitude;
            Vector3 steered = Vector3.MoveTowards(_horizontalVelocity, wishVelocity, airAcceleration * deltaTime);

            const float MinSteerCapSpeed = 0.01f;
            if (speedBeforeSteer > MinSteerCapSpeed && steered.magnitude > speedBeforeSteer)
            {
                steered = steered.normalized * speedBeforeSteer;
            }

            _horizontalVelocity = steered;
            _verticalVelocity += gravity * deltaTime;
        }

        // 水平と垂直を別のMove()に分けて引っかかりを減らす
        Vector3 horizontalMove = new Vector3(_horizontalVelocity.x, 0f, _horizontalVelocity.z) * deltaTime;
        Vector3 verticalMove = new Vector3(0f, _verticalVelocity, 0f) * deltaTime;

        _controller.Move(horizontalMove);
        _controller.Move(verticalMove);

        CheckForStuckAndEscape(h, v, wishDir, deltaTime);
    }

    /// <summary>ノックバック速度を通常移動とは独立に適用して減衰させる。死亡中も毎フレーム呼ぶ</summary>
    private void ProcessKnockback(float deltaTime)
    {
        if (_knockbackVelocity.sqrMagnitude <= 0.0001f) return;

        _controller.Move(_knockbackVelocity * deltaTime);
        _knockbackVelocity = Vector3.MoveTowards(_knockbackVelocity, Vector3.zero, knockbackDecayPerSecond * deltaTime);
    }

    /// <summary>詰まり脱出の保険。入力があるのに一定時間ほぼ動いていない場合、小さく跳ねて抜け出しを試みる</summary>
    private void CheckForStuckAndEscape(float h, float v, Vector3 wishDir, float deltaTime)
    {
        _stuckCheckTimer += deltaTime;
        if (_stuckCheckTimer < stuckCheckIntervalSeconds) return;

        if (_hasStuckCheckStartPosition)
        {
            float distanceMoved = Vector3.Distance(transform.position, _stuckCheckStartPosition);
            float inputMagnitude = new Vector2(h, v).magnitude;

            if (_isGrounded && distanceMoved < stuckMinMoveDistance && inputMagnitude > 0.3f)
            {
                // 障害物から離れる方向を優先し、無ければwishDir/体の正面へフォールバックする
                Vector3 escapeDir = ComputeEscapeAwayFromObstacles(wishDir);
                if (escapeDir.sqrMagnitude < 0.0001f)
                {
                    escapeDir = wishDir.sqrMagnitude > 0.0001f ? wishDir.normalized : transform.forward;
                }
                _verticalVelocity = stuckEscapeUpwardVelocity;
                _horizontalVelocity = escapeDir * stuckEscapeHorizontalSpeed;
            }
        }

        _stuckCheckTimer = 0f;
        _stuckCheckStartPosition = transform.position;
        _hasStuckCheckStartPosition = true;
    }

    /// <summary>周囲の障害物から押し返される水平方向を返す(重なりが無ければzero)。
    /// 左右から挟まれて打ち消し合う場合は、隙間に沿った方向(wishDirに近い側)を返す</summary>
    private Vector3 ComputeEscapeAwayFromObstacles(Vector3 wishDir)
    {
        Vector3 center = transform.position + _controller.center;
        // カプセルよりわずかに広い半径で拾い、数値誤差による見逃しを防ぐ
        float probeRadius = _controller.radius + 0.05f;
        Collider[] overlaps = Physics.OverlapSphere(center, probeRadius, environmentMask, QueryTriggerInteraction.Ignore);

        Vector3 pushSum = Vector3.zero;
        Vector3 firstAway = Vector3.zero;
        int count = 0;
        foreach (Collider col in overlaps)
        {
            if (col.transform.root == transform.root) continue; // 自分自身は無視
            Vector3 closest = col.ClosestPoint(center);
            Vector3 away = center - closest;
            away.y = 0f; // 水平方向のみ(上方向はstuckEscapeUpwardVelocityが担当)
            if (away.sqrMagnitude < 0.0001f) continue; // 方向が定まらない場合は無視
            if (count == 0) firstAway = away.normalized;
            pushSum += away.normalized;
            count++;
        }

        if (count == 0) return Vector3.zero;

        // 挟まれ検知: 押し返しが打ち消し合っている場合は隙間に沿った方向を返す
        const float cancelThreshold = 0.35f; // これ未満なら打ち消し合っているとみなす
        if (count >= 2 && pushSum.magnitude < cancelThreshold)
        {
            Vector3 perpendicular = Vector3.Cross(Vector3.up, firstAway).normalized;
            if (perpendicular.sqrMagnitude < 0.0001f) return Vector3.zero;

            // 前後のうちwishDirに近い方を選ぶ
            if (wishDir.sqrMagnitude > 0.0001f && Vector3.Dot(perpendicular, wishDir.normalized) < 0f)
            {
                perpendicular = -perpendicular;
            }
            return perpendicular;
        }

        return pushSum.normalized;
    }

    /// <summary>足元へのSphereCastで接地判定を行う(CharacterController.isGroundedの代替)</summary>
    private bool CheckGrounded()
    {
        float radius = Mathf.Max(0.05f, _controller.radius * 0.9f);
        Vector3 origin = transform.position + _controller.center;
        float castDistance = _controller.height / 2f - radius + groundCheckExtraDistance;
        if (castDistance <= 0f) castDistance = groundCheckExtraDistance;

        return Physics.SphereCast(origin, radius, Vector3.down, out _, castDistance, environmentMask, QueryTriggerInteraction.Ignore);
    }
}
