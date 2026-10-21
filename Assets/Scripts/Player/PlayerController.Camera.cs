using UnityEngine;

/// <summary>
/// PlayerControllerの肩越しTPSカメラと視点(ピッチ/ヨー)を担当する部分クラス。
/// ヨーは体の向きと独立した_yawで管理し、cameraPivotのワールド回転をLateUpdateで上書きする。
/// SmoothDampによる追従、壁との衝突回避、速度に応じたFOV変化、カメラシェイクもここで扱う。
/// </summary>
public partial class PlayerController
{
    private Vector3 _cameraVelocity;
    private bool _cameraWasObstructed;
    private float _shakeTimeRemaining;
    private float _shakeDuration;
    private float _shakeMagnitude;

    /// <summary>ピッチ・ヨーの値だけを更新する(Transformへの反映はUpdateThirdPersonCamera())</summary>
    private void HandleLookLocally(float pitchDelta, float yawDelta)
    {
        _pitch = Mathf.Clamp(_pitch - pitchDelta, minPitch, maxPitch);
        _yaw += yawDelta;

        LookPitch.Value = _pitch;
    }

    /// <summary>_pitch/_yawから直接求めた最新の照準方向(Transformの反映は遅れるため、Update内の狙いにはこちらを使う)</summary>
    public Vector3 CurrentAimForward => Quaternion.Euler(_pitch, _yaw, 0f) * Vector3.forward;

    private void HandleLookPitchReplicated(float previousValue, float newValue)
    {
        if (IsOwner) return; // Ownerは既にローカルで反映済み

        _pitch = newValue;
        if (cameraPivot != null) cameraPivot.localRotation = Quaternion.Euler(_pitch, 0f, 0f);
    }

    /// <summary>近くで爆発が起きた時等、Owner本人のカメラを一時的に揺らす。他人からの呼び出しは無視する</summary>
    public void TriggerCameraShake(float magnitude, float duration)
    {
        if (!IsOwner) return;
        _shakeMagnitude = Mathf.Max(_shakeMagnitude, magnitude);
        _shakeDuration = Mathf.Max(_shakeDuration, duration);
        _shakeTimeRemaining = Mathf.Max(_shakeTimeRemaining, duration);
    }

    private void UpdateThirdPersonCamera()
    {
        if (cameraPivot == null || playerCamera == null) return;

        // ヨー・ピッチをワールド回転として直接上書きする(体の向きは無視)
        cameraPivot.rotation = Quaternion.Euler(_pitch, _yaw, 0f);

        Vector3 shoulderOrigin = cameraPivot.position + cameraPivot.right * shoulderOffset;

        Vector3 offset = -cameraPivot.forward * cameraDistance + cameraPivot.up * cameraHeight;
        float maxDistance = offset.magnitude;
        Vector3 castDir = offset.normalized;

        float distance = maxDistance;
        bool obstructed = false;
        if (Physics.SphereCast(shoulderOrigin, cameraCollisionRadius, castDir, out RaycastHit hit,
                maxDistance, cameraCollisionMask, QueryTriggerInteraction.Ignore))
        {
            distance = Mathf.Max(0.2f, hit.distance);
            obstructed = true;

            // 自分自身にヒットした場合はマスク設定ミスなので警告を出す
            if (hit.collider.transform.root == transform)
            {
                Debug.LogWarning($"[PlayerController] カメラの障害物判定が自分自身({hit.collider.name})に" +
                    "ヒットしました。cameraCollisionMaskがPlayerレイヤーを除外できているか確認してください");
            }
        }

        Vector3 targetPosition = shoulderOrigin + castDir * distance;

        // 障害物がある間はスムージングをほぼ無効にして壁へのめり込みを防ぐ
        float smoothTime = (obstructed || _cameraWasObstructed) ? 0.01f : cameraSmoothTime;
        _cameraWasObstructed = obstructed;

        Vector3 smoothedPosition = Vector3.SmoothDamp(playerCamera.transform.position, targetPosition, ref _cameraVelocity, smoothTime);

        if (_shakeTimeRemaining > 0f)
        {
            _shakeTimeRemaining -= Time.deltaTime;
            float ratio = _shakeDuration > 0f ? Mathf.Clamp01(_shakeTimeRemaining / _shakeDuration) : 0f;
            smoothedPosition += Random.insideUnitSphere * (_shakeMagnitude * ratio);
            if (_shakeTimeRemaining <= 0f) _shakeMagnitude = 0f;
        }

        playerCamera.transform.position = smoothedPosition;
        playerCamera.transform.rotation = cameraPivot.rotation;

        // 移動速度に応じて視野角を広げる
        float speedRatio = Mathf.Clamp01(new Vector2(_horizontalVelocity.x, _horizontalVelocity.z).magnitude / Mathf.Max(0.01f, moveSpeed));
        float targetFov = baseFieldOfView + speedRatio * sprintFovBoost;
        playerCamera.fieldOfView = Mathf.Lerp(playerCamera.fieldOfView, targetFov, Time.deltaTime * fovLerpSpeed);
    }
}
