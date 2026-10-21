using Unity.Netcode;
using UnityEngine;

/// <summary>
/// プレイヤー未スポーン中(ロビー・読み込み中)に街を映す保険カメラ。起動時に自動生成され、
/// ローカルプレイヤーのスポーンで無効化、切断で再有効化される。位置は固定で視線だけ左右に振る。
/// </summary>
public class FallbackCamera : MonoBehaviour
{
    private static FallbackCamera _instance;

    [Header("背景カメラの視点(実際の街を映す)")]
    [Tooltip("街を見渡す固定カメラのワールド座標")]
    [SerializeField] private Vector3 vantagePosition = new Vector3(5f, 14f, -22f);
    [Tooltip("常にこの地点の方向を向く")]
    [SerializeField] private Vector3 lookAtPoint = new Vector3(-3f, 3f, 15f);
    [Tooltip("位置は固定したまま、視線だけをこの振幅(度)で左右にゆっくり振る")]
    [SerializeField] private float yawSwayAmplitudeDegrees = 14f;
    [Tooltip("視線振りの速さ。小さいほど動きが緩やかになる")]
    [SerializeField] private float yawSwayDegreesPerSecond = 2.5f;

    private Camera _camera;
    private AudioListener _listener;

    // 視線振りの位相。Time.timeを使うと長時間で精度が落ちるため、deltaTimeを積算してRepeatで小さく保つ
    private float _swayPhaseDegrees;

    // Steam Audioは起動時にAudioListenerが存在する前提のため、最初のシーン読み込み前に生成する
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Bootstrap()
    {
        if (_instance != null) return;

        var go = new GameObject("FallbackCamera(Auto)");
        DontDestroyOnLoad(go);
        go.AddComponent<FallbackCamera>();
    }

    private void Awake()
    {
        if (_instance != null && _instance != this)
        {
            Destroy(gameObject);
            return;
        }
        _instance = this;

        _camera = gameObject.AddComponent<Camera>();
        // 本物の街を映すため、プレイヤーカメラと同じ既定値(Skybox)にする。背景色はスカイボックス読み込み前の保険
        _camera.clearFlags = CameraClearFlags.Skybox;
        _camera.backgroundColor = new Color(0.53f, 0.81f, 0.92f);
        _camera.depth = -100; // プレイヤーカメラより奥に描画する
        _camera.farClipPlane = 400f;

        UpdateCameraTransform();

        _listener = gameObject.AddComponent<AudioListener>();

        Debug.Log("[FallbackCamera] 保険カメラを生成しました(未接続時の\"No cameras rendering\"対策。実際の街を映す固定視点)");
    }

    private void Update()
    {
        bool localPlayerHasCamera = NetworkManager.Singleton != null
            && NetworkManager.Singleton.LocalClient != null
            && NetworkManager.Singleton.LocalClient.PlayerObject != null;

        if (_camera.enabled == localPlayerHasCamera)
        {
            _camera.enabled = !localPlayerHasCamera;
            _listener.enabled = !localPlayerHasCamera;
        }

        if (_camera.enabled) UpdateCameraTransform();
    }

    /// <summary>位置をvantagePositionに固定し、視線だけを三角波(PingPong)で左右に振る</summary>
    private void UpdateCameraTransform()
    {
        transform.position = vantagePosition;

        Vector3 baseDirection = (lookAtPoint - vantagePosition).normalized;
        // PingPongの周期(amplitude*4)に合わせて積算値をラップする
        float period = yawSwayAmplitudeDegrees * 4f;
        _swayPhaseDegrees = Mathf.Repeat(_swayPhaseDegrees + yawSwayDegreesPerSecond * Time.deltaTime, period);
        float sway = Mathf.PingPong(_swayPhaseDegrees, yawSwayAmplitudeDegrees * 2f) - yawSwayAmplitudeDegrees;
        Quaternion swayRotation = Quaternion.AngleAxis(sway, Vector3.up);
        transform.rotation = Quaternion.LookRotation(swayRotation * baseDirection, Vector3.up);
    }
}
