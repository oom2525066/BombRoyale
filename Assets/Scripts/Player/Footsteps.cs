using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 足音・着地音・ジャンプ音を担当するコンポーネント。判定はOwnerだけが行い、Owner自身はローカル再生、
/// 他クライアントにはServerRpc→ClientRpc経由で伝える。他人の音は壁越しだとこもらせる。
/// Player prefabのルート(PlayerControllerと同じGameObject)に付ける。
/// </summary>
[RequireComponent(typeof(PlayerController))]
public class Footsteps : NetworkBehaviour
{
    [Header("足音")]
    [Tooltip("足音を鳴らすAudioSource。3D音源(Spatial Blend=1)にしておくこと")]
    [SerializeField] private AudioSource footstepAudioSource;
    [Tooltip("足音のAudioClip候補。再生の度にこの中からランダムに1つ選ぶ")]
    [SerializeField] private AudioClip[] footstepClips;

    [Tooltip("この距離(m)進むごとに1回足音を鳴らす")]
    [SerializeField] private float stepDistance = 2.2f;
    [Range(0f, 1f)] [Tooltip("足音の基本音量")]
    [SerializeField] private float stepVolume = 0.75f;

    [Tooltip("再生の度に音程へランダムなブレを加える範囲(x=下限, y=上限)。単調な連続再生を避ける")]
    [SerializeField] private Vector2 pitchRange = new Vector2(0.92f, 1.08f);

    [Header("壁越しの足音(オクルージョン)")]
    [Tooltip("音源と聞き手の間の遮蔽判定に使うレイヤー(地形・壁を含める)。自分の足音には適用しない")]
    [SerializeField] private LayerMask occlusionMask = ~0;
    [Tooltip("遮蔽されている時のローパスフィルタのカットオフ周波数(Hz)。低いほど籠って聞こえる")]
    [SerializeField] private float occludedLowPassCutoff = 900f;
    [Range(0f, 1f)]
    [Tooltip("遮蔽されている時、音量にさらに掛かる倍率")]
    [SerializeField] private float occludedVolumeMultiplier = 0.5f;

    private AudioLowPassFilter _lowPassFilter;

    [Header("着地音")]
    [Tooltip("着地音のAudioClip候補。未設定ならfootstepClipsを流用する")]
    [SerializeField] private AudioClip[] landingClips;
    [Range(0f, 1f)] [Tooltip("着地音の基本音量")]
    [SerializeField] private float landingVolume = 0.85f;

    [Header("ジャンプ音")]
    [Tooltip("ジャンプした瞬間に鳴らすAudioClip候補。未設定ならジャンプ音は鳴らさない")]
    [SerializeField] private AudioClip[] jumpClips;
    [Range(0f, 1f)] [Tooltip("ジャンプ音の基本音量")]
    [SerializeField] private float jumpVolume = 0.65f;

    private float _distanceSinceLastStep;
    // 直前フレームの接地状態。スポーン直後に着地音が鳴らないよう初期値はtrue
    private bool _wasGroundedLastTick = true;

    private PlayerController _cachedPlayerController;
    private PlayerController CachedPlayerController
    {
        get
        {
            if (_cachedPlayerController == null) _cachedPlayerController = GetComponent<PlayerController>();
            return _cachedPlayerController;
        }
    }

    /// <summary>Ownerの移動処理直後に毎フレーム呼ぶ。移動距離に応じた足音と着地音を鳴らす</summary>
    public void Tick(float deltaTime, Vector3 horizontalVelocity, bool isGrounded)
    {
        if (isGrounded && !_wasGroundedLastTick)
        {
            PlayLandingLocally();
            RequestLandingServerRpc();
        }
        _wasGroundedLastTick = isGrounded;

        if (!isGrounded)
        {
            // 空中では距離を積算しない
            _distanceSinceLastStep = 0f;
            return;
        }

        float speed = horizontalVelocity.magnitude;
        if (speed < 0.1f) return;

        _distanceSinceLastStep += speed * deltaTime;
        if (_distanceSinceLastStep < stepDistance) return;

        _distanceSinceLastStep = 0f;

        PlayFootstepLocally();
        RequestFootstepServerRpc();
    }

    /// <summary>リスポーン直後に呼ぶ。着地判定と距離積算をリセットする</summary>
    public void ResetForRespawn()
    {
        _wasGroundedLastTick = true;
        _distanceSinceLastStep = 0f;
    }

    /// <summary>ジャンプした瞬間に呼ぶ。ジャンプ音をローカル再生し他クライアントへも伝える</summary>
    public void PlayJump()
    {
        PlayJumpLocally();
        RequestJumpServerRpc();
    }

    private void PlayJumpLocally(float occlusionVolumeMultiplier = 1f)
    {
        if (footstepAudioSource == null || jumpClips == null || jumpClips.Length == 0) return;

        AudioClip clip = jumpClips[Random.Range(0, jumpClips.Length)];
        float scale = SoundManager.Instance != null ? SoundManager.Instance.SfxVolumeScale : 1f;

        footstepAudioSource.pitch = Random.Range(pitchRange.x, pitchRange.y);
        footstepAudioSource.PlayOneShot(clip, jumpVolume * occlusionVolumeMultiplier * scale);
    }

    [ServerRpc]
    private void RequestJumpServerRpc()
    {
        PlayJumpClientRpc(BuildAllExceptOwnerRpcParams());
    }

    [ClientRpc]
    private void PlayJumpClientRpc(ClientRpcParams rpcParams = default)
    {
        PlayJumpLocally(ComputeOcclusionVolumeMultiplier());
    }

    /// <summary>足音をローカル再生する。occlusionVolumeMultiplierは遮蔽による音量倍率(既定1=遮蔽なし)</summary>
    private void PlayFootstepLocally(float occlusionVolumeMultiplier = 1f)
    {
        if (footstepAudioSource == null || footstepClips == null || footstepClips.Length == 0) return;

        AudioClip clip = footstepClips[Random.Range(0, footstepClips.Length)];
        float scale = SoundManager.Instance != null ? SoundManager.Instance.SfxVolumeScale : 1f;

        footstepAudioSource.pitch = Random.Range(pitchRange.x, pitchRange.y);
        footstepAudioSource.PlayOneShot(clip, stepVolume * occlusionVolumeMultiplier * scale);
    }

    private void PlayLandingLocally(float occlusionVolumeMultiplier = 1f)
    {
        AudioClip[] pool = (landingClips != null && landingClips.Length > 0) ? landingClips : footstepClips;
        if (footstepAudioSource == null || pool == null || pool.Length == 0) return;

        AudioClip clip = pool[Random.Range(0, pool.Length)];
        float scale = SoundManager.Instance != null ? SoundManager.Instance.SfxVolumeScale : 1f;

        footstepAudioSource.pitch = Random.Range(pitchRange.x, pitchRange.y);
        footstepAudioSource.PlayOneShot(clip, landingVolume * occlusionVolumeMultiplier * scale);
    }

    // ClientRpcの送信先リストの使い回しバッファ(GC回避)
    private readonly List<ulong> _rpcTargetBuffer = new List<ulong>();

    /// <summary>発生元のOwnerを除く全クライアントを送信先にしたClientRpcParamsを作る</summary>
    private ClientRpcParams BuildAllExceptOwnerRpcParams()
    {
        _rpcTargetBuffer.Clear();
        if (NetworkManager.Singleton != null)
        {
            foreach (var id in NetworkManager.Singleton.ConnectedClientsIds)
            {
                if (id != OwnerClientId) _rpcTargetBuffer.Add(id);
            }
        }
        return new ClientRpcParams { Send = new ClientRpcSendParams { TargetClientIds = _rpcTargetBuffer } };
    }

    [ServerRpc]
    private void RequestFootstepServerRpc()
    {
        PlayFootstepClientRpc(BuildAllExceptOwnerRpcParams());
    }

    /// <summary>他人の足音を受け取って再生する。遮蔽の有無を判定して音量・こもりに反映する</summary>
    [ClientRpc]
    private void PlayFootstepClientRpc(ClientRpcParams rpcParams = default)
    {
        PlayFootstepLocally(ComputeOcclusionVolumeMultiplier());
    }

    [ServerRpc]
    private void RequestLandingServerRpc()
    {
        PlayLandingClientRpc(BuildAllExceptOwnerRpcParams());
    }

    [ClientRpc]
    private void PlayLandingClientRpc(ClientRpcParams rpcParams = default)
    {
        PlayLandingLocally(ComputeOcclusionVolumeMultiplier());
    }

    /// <summary>音源からローカルカメラまでに遮蔽物があるか判定し、ローパスフィルタを設定して音量倍率を返す</summary>
    private float ComputeOcclusionVolumeMultiplier()
    {
        if (footstepAudioSource == null) return 1f;

        if (_lowPassFilter == null)
        {
            _lowPassFilter = footstepAudioSource.GetComponent<AudioLowPassFilter>();
            if (_lowPassFilter == null) _lowPassFilter = footstepAudioSource.gameObject.AddComponent<AudioLowPassFilter>();
        }

        bool occluded = false;
        PlayerController localPc = PlayerController.LocalPlayer;
        if (localPc != null && localPc.PlayerCamera != null)
        {
            Vector3 sourcePos = footstepAudioSource.transform.position;
            Vector3 toListener = localPc.PlayerCamera.transform.position - sourcePos;
            float distance = toListener.magnitude;
            if (distance > 0.05f)
            {
                occluded = Physics.Raycast(sourcePos, toListener / distance, distance, occlusionMask, QueryTriggerInteraction.Ignore);
            }
        }

        _lowPassFilter.cutoffFrequency = occluded ? occludedLowPassCutoff : 22000f;
        return occluded ? occludedVolumeMultiplier : 1f;
    }
}
