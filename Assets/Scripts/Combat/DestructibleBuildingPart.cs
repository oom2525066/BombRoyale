using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 街の設置オブジェクト(建物/道路/小物等)1個ぶんの被弾・破壊を三角形単位で管理する。
/// 爆心地に近い三角形ほど大ダメージで取り除き、穴の奥には灰色の裏打ち面を生成する。
/// 初期化は初被弾時に遅延実行し、全クライアントがClientRpc経由の同じ入力から同じ結果を再現する。
/// </summary>
public class DestructibleBuildingPart : MonoBehaviour
{
    private static readonly Dictionary<string, DestructibleBuildingPart> _registry = new Dictionary<string, DestructibleBuildingPart>();
    private static Transform _effectsRoot;

    [Tooltip("建物ID(空欄ならGameObject名を使う。同名を複製した場合は一意な名前を付ける)")]
    [SerializeField] private string buildingId;

    [Header("耐久・ダメージ(三角形単位)")]
    [Tooltip("三角形1個あたりの耐久値")]
    [SerializeField] private byte triangleHitPoints = 2;
    [Tooltip("爆心地に最も近い三角形に与えるダメージ(×power)")]
    [SerializeField] private int maxDamagePerHit = 2;
    [Tooltip("爆発半径の端の三角形に与えるダメージの下限(×power)")]
    [SerializeField] private int minDamagePerHit = 1;
    [Tooltip("ダメージ判定の対象にするRendererを探す範囲(爆発半径×この倍率)")]
    [SerializeField] private float rendererSearchMultiplier = 1.5f;
    [Tooltip("三角形の法線と爆心地方向の内積の下限。これを下回る(爆心地を向いていない)三角形は対象外")]
    [SerializeField, Range(-1f, 1f)] private float minFacingDot = 0f;
    [Tooltip("ワールドバウンズ体積がこれ以下のRendererは小物とみなし、命中で全体を即破壊する")]
    [SerializeField] private float smallObjectMaxVolume = 8f;
    [Tooltip("元バウンズをこの値だけ縮めた、破壊されない内部進入防止用BoxColliderのマージン")]
    [SerializeField] private float interiorBlockerMargin = 2.5f;
    [Tooltip("内部進入防止Colliderのマージンの、Rendererのワールドサイズに対する割合(大きい建物用)")]
    [SerializeField] private float interiorBlockerMarginFraction = 0.12f;
    [Tooltip("掘削の最大深さを内部進入防止Colliderの手前で止めるための余裕")]
    [SerializeField] private float interiorBlockerDepthBuffer = 0.5f;
    [Tooltip("名前にこれらの単語を含む有機的な対象(木・草など)は裏打ちを生成せず、命中で全体を破壊する")]
    [SerializeField] private string[] organicNoBackingNameContains = { "Tree", "Bush", "Plant", "Hedge", "Flower", "Grass" };

    [Header("破壊の細かさ(粒度)")]
    [Tooltip("trueで初期化時にメッシュを面積適応的に細分化し、削除の粒度を上げる")]
    [SerializeField] private bool enableFinerSubdivision = true;
    [Tooltip("この面積(ワールド)を超える三角形を中点分割する")]
    [SerializeField] private float targetTriangleArea = 1f;
    [Tooltip("分割の最大段階数(1段階で三角形1枚→4枚)")]
    [SerializeField] private int maxSubdivisionDepth = 4;
    [Tooltip("元の三角形数がこの値を超えるメッシュは、目標面積を比例して大きくし分割を粗くする")]
    [SerializeField] private int subdivisionSizeThreshold = 5000;
    [Tooltip("上記の目標面積の引き上げ倍率の上限")]
    [SerializeField] private float maxSubdivisionAreaMultiplier = 6f;

    [Header("爆発跡(表面の焦げ跡)")]
    [SerializeField] private int maxDecals = 12;
    [SerializeField] private float decalSize = 0.9f;

    [Header("破片(デブリ)")]
    [SerializeField, Range(0f, 1f)] private float debrisSpawnRatio = 0.35f;
    [SerializeField] private int maxDebrisPerExplosion = 12;
    [SerializeField] private float debrisLifetimeSeconds = 6f;
    [SerializeField] private float debrisForce = 4f;

    [Header("穴の裏打ち(内部が空に見えるのを防ぐ)")]
    [Tooltip("裏打ちの最大深さの、爆発半径に対する比率(深さ=半径×この値×power)")]
    [SerializeField] private float backingDepthRadiusRatio = 0.25f;
    [Tooltip("裏打ち面の色(ライティングを受ける灰色)")]
    [SerializeField] private Color backingColor = new Color(0.42f, 0.42f, 0.42f);
    [Tooltip("既に死んだ三角形への再命中時に積み増す裏打ち深さ(×power)")]
    [SerializeField] private float backingExtraDepthPerHit = 1.2f;
    [Tooltip("裏打ちが奥まってよい絶対的な最大深さ。到達した三角形は裏打ちを生成せず貫通する")]
    [SerializeField] private float maxBackingExtraDepth = 3f;
    [Tooltip("三角形の最長辺に対する、角ごとの深さの上限の比率")]
    [SerializeField] private float maxCornerDepthSlope = 0.75f;
    [Tooltip("Rendererバウンズ最短辺に対する、裏打ち深さの上限の比率(薄い対象の突き抜け防止)")]
    [SerializeField] private float boundsSafetyMargin = 0.85f;
    [Tooltip("深さの局所Raycast上限を取る際に、レイ始点を法線方向へずらす余白")]
    [SerializeField] private float raycastSkinOffset = 0.05f;

    [Header("破断の不規則さ(FractureNoise)")]
    [Tooltip("削除境界の半径の揺らぎ強度(0で完全な円)")]
    [SerializeField, Range(0f, 1f)] private float fractureRimNoiseStrength = 0.06f;
    [Tooltip("クレーター深さの揺らぎ強度(0で滑らかな弧)")]
    [SerializeField, Range(0f, 1f)] private float craterDepthNoiseStrength = 0.18f;
    [Tooltip("ノイズ基本セルの大きさ(爆発半径の倍率。小さいほど細かい凹凸)")]
    [SerializeField] private float fractureNoiseCellSizeRatio = 1f;

    [Header("デバッグ")]
    [Tooltip("裏打ちメッシュ更新時などに詳細ログを出力する")]
    [SerializeField] private bool verboseLogging = true;

    private static Material _backingMaterial;
    // 被弾した対象のマテリアルを両面描画(Cull Off)にするためのシェーダープロパティID
    private static readonly int CullPropertyId = Shader.PropertyToID("_Cull");

    /// <summary>32bit index bufferに対応した端末か(static初期化式でUnity APIを呼べないため遅延評価でキャッシュ)</summary>
    private static bool? _supports32BitIndicesCache;
    private static bool Supports32BitIndices => _supports32BitIndicesCache ??= SystemInfo.supports32bitsIndexBuffer;

    /// <summary>非対応端末(UInt16)で、裏打ちの頂点数がこの値に近づいたら追加を打ち切る閾値</summary>
    private const int Max16BitBackingVertexCount = 65000;

    private readonly List<GameObject> _decals = new List<GameObject>();
    private Renderer[] _originalRenderers;
    private bool _initialized;

    /// <summary>1サブメッシュ(=1マテリアル)ぶんの三角形状態。Alive/Hpだけが被弾で変化する</summary>
    private class SubmeshState
    {
        public int[] OriginalTriangles; // 3個ずつ、元の並びのまま不変
        public Vector3[] LocalCentroids; // 三角形ごとの重心(ローカル座標、キャッシュ)
        public Vector3[] LocalNormals; // 三角形ごとの面法線(ローカル座標、キャッシュ)
        public byte[] Hp;
        public bool[] Alive;
        public int AliveCount;
        // RebuildSubmeshTrianglesが使い回すスクラッチ領域
        public readonly List<int> ScratchTriangleList = new List<int>();
    }

    /// <summary>1個のRendererぶんの破壊状態(複製メッシュ・サブメッシュ状態・裏打ち用データ)</summary>
    private class RendererState
    {
        public Renderer Renderer;
        public Mesh InstanceMesh; // このRenderer専用の編集可能な複製メッシュ(細分化済み)
        public MeshCollider MeshCollider; // 同じGameObjectにあれば(無ければnull=当たり判定は変化しない)
        // MeshColliderが無いGameObject用に保持する任意のCollider(BoxCollider等)。全滅時にenabled=falseにする
        public Collider FallbackCollider;
        public SubmeshState[] Submeshes;
        // 頂点インデックス→位置ベースの溶接ID。UVシーム等で分かれた同位置の頂点を、辺の隣接判定で同一視するために使う
        public int[] WeldedIds;
        // 辺(溶接済み頂点ID2個の組)→その辺を共有する三角形((サブメッシュ番号, 三角形インデックス)の組)。
        // Renderer全体で1つ持ち、穴の縁の判定(スカート生成)にサブメッシュをまたいで使う
        public Dictionary<long, List<(int sub, int tri)>> EdgeTriangles;
        // 体積が小さい「小物」か。小物は命中でRenderer全体をワンパン破壊する(初期化時に判定)
        public bool IsSmallObject;
        // 内部進入防止Colliderまでの最短距離(ワールド単位)。掘削の貫通判定(objectDepthCeiling)の基準に使う
        public float EffectiveInteriorBlockerMargin;

        // 溶接済み頂点ID→その頂点を共有する全三角形の平均法線。裏打ちを奥まらせる方向を角をまたいでも一致させ、床が繋がるようにする
        public Dictionary<int, Vector3> WeldedVertexNormal;

        // 裏打ち面: 取り除いた三角形ごとに、爆心地からの距離に応じた深さだけ奥まらせた灰色の面を作り、穴の奥が素通しに見えるのを防ぐ。
        // 深さは溶接済み頂点ID単位(BaseDepth=爆発クレーター、ExtraDepth=再命中の積み増し)で持ち、隣接三角形の床が隙間なく繋がるようにする
        public readonly Dictionary<int, float> VertexBaseDepth = new Dictionary<int, float>();
        public readonly Dictionary<int, float> VertexExtraDepth = new Dictionary<int, float>();
        public GameObject BackingGo;
        public Mesh BackingMesh;
        public MeshCollider BackingMeshCollider;
        public readonly List<Vector3> BackingVertices = new List<Vector3>();
        public readonly List<Vector3> BackingNormals = new List<Vector3>();
        public readonly List<int> BackingTriangles = new List<int>();
    }

    private readonly List<RendererState> _rendererStates = new List<RendererState>();

    // 木など有機的な対象は裏打ちを生成しない(名前から1回だけ判定してキャッシュ)
    private bool _skipBackingGeneration;

    private void Awake()
    {
        if (string.IsNullOrEmpty(buildingId)) buildingId = gameObject.name;

        _originalRenderers = GetComponentsInChildren<Renderer>(true);
        _skipBackingGeneration = MatchesAnyNameContains(gameObject.name, organicNoBackingNameContains);

        if (_registry.ContainsKey(buildingId))
        {
            Debug.LogWarning($"[DestructibleBuildingPart] buildingId \"{buildingId}\" が重複しています。" +
                "破壊/リセットの同期が混線するので、Inspectorで一意な名前を設定してください。");
        }
        _registry[buildingId] = this;
    }

    private static readonly char[] NameWordSeparators = { ' ', '_', '(', ')', '-' };

    /// <summary>名前を区切り文字で単語に分割し、patternsのいずれかと完全一致(大小無視)する単語があるか判定する(部分一致の誤判定を避ける)</summary>
    private static bool MatchesAnyNameContains(string name, string[] patterns)
    {
        if (patterns == null) return false;
        string[] words = name.Split(NameWordSeparators, System.StringSplitOptions.RemoveEmptyEntries);
        foreach (var pattern in patterns)
        {
            if (string.IsNullOrEmpty(pattern)) continue;
            foreach (var word in words)
            {
                if (string.Equals(word, pattern, System.StringComparison.OrdinalIgnoreCase)) return true;
            }
        }
        return false;
    }

    private void OnDestroy()
    {
        if (_registry.TryGetValue(buildingId, out var current) && current == this) _registry.Remove(buildingId);
    }

    /// <summary>着弾前に初期化を済ませるための公開ラッパー(飛翔中の爆発物から先読みで呼び、初被弾時のラグを避ける)</summary>
    public void PrewarmIfNeeded() => InitializeIfNeeded(viaPrewarm: true);

    /// <summary>初被弾時に1回だけ呼ぶ遅延初期化。Rendererのメッシュを編集可能な複製に差し替え、三角形ごとのHP・重心・法線をキャッシュする</summary>
    private void InitializeIfNeeded(bool viaPrewarm = false)
    {
        if (_initialized) return;
        _initialized = true;

        // 初期化にかかった時間を計測する(末尾のログに出力)
        var initStopwatch = System.Diagnostics.Stopwatch.StartNew();

        // 小物判定は対象全体(全Renderer合成)のバウンズ体積でも行う(個別Rendererだけだと小物の一部に内部進入防止Colliderが残るため)
        Bounds? combinedBounds = null;
        foreach (var r0 in _originalRenderers)
        {
            if (r0 == null) continue;
            if (combinedBounds == null) { combinedBounds = r0.bounds; continue; }
            Bounds b = combinedBounds.Value;
            b.Encapsulate(r0.bounds);
            combinedBounds = b;
        }
        bool wholeObjectIsSmall = combinedBounds.HasValue && BoundsVolume(combinedBounds.Value.size) <= smallObjectMaxVolume;

        foreach (var r in _originalRenderers)
        {
            if (r == null) continue;
            MeshFilter meshFilter = r.GetComponent<MeshFilter>();
            Mesh sourceMesh = meshFilter != null ? meshFilter.sharedMesh : null;
            if (sourceMesh == null || !sourceMesh.isReadable)
            {
                if (sourceMesh != null)
                {
                    Debug.LogWarning($"[DestructibleBuildingPart] \"{buildingId}\": \"{r.name}\"のメッシュが" +
                        "Read/Write Enabledでないため、この部分は破壊できません(実機ビルド時のインポート設定を確認してください)");
                }
                continue;
            }

            Mesh instanceMesh = Instantiate(sourceMesh);
            // indexFormatは、既にデータの入った複製には適用しない(三角形がクリアされるため)

            // 削除粒度を上げるため、メッシュを先に細分化しておく
            if (enableFinerSubdivision) SubdivideMeshInPlace(instanceMesh, r.transform);

            meshFilter.mesh = instanceMesh; // このRenderer専用の複製に差し替える(共有アセットは変更しない)

            // 被弾した対象のRendererだけマテリアルを両面描画にする(穴から見える反対側の壁を描画するため。共有アセットは変更しない)
            foreach (var mat in r.materials)
            {
                if (mat != null) mat.SetInt(CullPropertyId, (int)UnityEngine.Rendering.CullMode.Off);
            }

            MeshCollider meshCollider = r.GetComponent<MeshCollider>();
            Collider fallbackCollider = null;
            if (meshCollider != null)
            {
                // アセットは既にクリーンなので、毎回のメッシュクリーニング・頂点溶接を省く
                meshCollider.cookingOptions = MeshColliderCookingOptions.UseFastMidphase;
                meshCollider.sharedMesh = instanceMesh; // 分割済みのメッシュで1回だけCookする
            }
            else
            {
                // MeshColliderが無い場合は、その他のCollider(BoxCollider等)を保持して全滅時に無効化する
                fallbackCollider = r.GetComponent<Collider>();
            }

            Vector3[] verts = instanceMesh.vertices;
            int[] weldedIds = BuildWeldedVertexIds(verts);
            var submeshes = new SubmeshState[instanceMesh.subMeshCount];
            // Renderer全体(全サブメッシュ)で1つ構築する。サブメッシュをまたいだ隣接判定に必要
            var edgeTriangles = new Dictionary<long, List<(int sub, int tri)>>();
            // 溶接済み頂点ID→三角形法線の合計(後で正規化して頂点ごとの平均法線にする)
            var weldedNormalSum = new Dictionary<int, Vector3>();
            for (int sub = 0; sub < instanceMesh.subMeshCount; sub++)
            {
                int[] tris = instanceMesh.GetTriangles(sub);
                int triCount = tris.Length / 3;
                var centroids = new Vector3[triCount];
                var normals = new Vector3[triCount];
                var hp = new byte[triCount];
                var alive = new bool[triCount];
                for (int t = 0; t < triCount; t++)
                {
                    int a = tris[t * 3], b = tris[t * 3 + 1], c = tris[t * 3 + 2];
                    Vector3 v0 = verts[a];
                    Vector3 v1 = verts[b];
                    Vector3 v2 = verts[c];
                    centroids[t] = (v0 + v1 + v2) / 3f;
                    // 退化三角形の法線はゼロベクトルになるが、後段のdot判定で除外されるだけで問題ない
                    normals[t] = Vector3.Cross(v1 - v0, v2 - v0).normalized;
                    hp[t] = triangleHitPoints;
                    alive[t] = true;

                    AddEdgeTriangle(edgeTriangles, weldedIds[a], weldedIds[b], sub, t);
                    AddEdgeTriangle(edgeTriangles, weldedIds[b], weldedIds[c], sub, t);
                    AddEdgeTriangle(edgeTriangles, weldedIds[c], weldedIds[a], sub, t);

                    // 退化三角形(ゼロ法線)は頂点の平均法線に寄与させない
                    if (normals[t].sqrMagnitude > 0.0001f)
                    {
                        AccumulateWeldedNormal(weldedNormalSum, weldedIds[a], normals[t]);
                        AccumulateWeldedNormal(weldedNormalSum, weldedIds[b], normals[t]);
                        AccumulateWeldedNormal(weldedNormalSum, weldedIds[c], normals[t]);
                    }
                }

                submeshes[sub] = new SubmeshState
                {
                    OriginalTriangles = tris,
                    LocalCentroids = centroids,
                    LocalNormals = normals,
                    Hp = hp,
                    Alive = alive,
                    AliveCount = triCount,
                };
            }
            NormalizeWeldedNormals(weldedNormalSum);

            // 小物判定: バウンズ体積が閾値以下、または裏打ちを生成しない有機物(木など)は、1枚でも当たれば全体を一括破壊する
            bool isSmall = _skipBackingGeneration || BoundsVolume(r.bounds.size) <= smallObjectMaxVolume;

            // 対象全体が小物なら、個別のRendererの体積に関わらず小物として扱う(どちらかがtrueなら小物)
            if (isSmall != wholeObjectIsSmall)
            {
                Debug.LogWarning($"[DestructibleBuildingPart] \"{buildingId}\": \"{r.name}\"は単独では" +
                    $"isSmall={isSmall}(体積={BoundsVolume(r.bounds.size):F3})だが、対象全体では" +
                    $"wholeObjectIsSmall={wholeObjectIsSmall}(合成体積={(combinedBounds.HasValue ? BoundsVolume(combinedBounds.Value.size) : -1f):F3})。" +
                    "両方の論理和(どちらかがtrueなら小物)を採用する");
            }
            isSmall = isSmall || wholeObjectIsSmall;

            // 建物内部(モデル化されていない空洞)に入れないよう、元バウンズより一回り小さい破壊されない固定BoxColliderを追加する(小物は対象外)。
            // 掘削深さの基準になる内部進入防止Colliderまでの距離は、Colliderを作る場合に下で上書きする
            float effectiveInteriorBlockerMargin = interiorBlockerMargin;
            if (!isSmall)
            {
                // マージンはワールド単位なので、lossyScaleで軸ごとにローカル単位へ換算してからバウンズに適用する
                Bounds localBounds = instanceMesh.bounds;
                Vector3 lossyScale = r.transform.lossyScale;

                // 各軸のマージンは、固定値とワールドサイズ×割合の大きい方(大きな建物ほど外殻を歩ける範囲を広げる)
                Vector3 worldSize = new Vector3(
                    localBounds.size.x * Mathf.Abs(lossyScale.x),
                    localBounds.size.y * Mathf.Abs(lossyScale.y),
                    localBounds.size.z * Mathf.Abs(lossyScale.z));
                // マージンは各軸のワールドサイズの45%を上限にクランプし、薄い軸でもColliderが付く余白を残す
                Vector3 worldMargin = new Vector3(
                    Mathf.Min(Mathf.Max(interiorBlockerMargin, worldSize.x * interiorBlockerMarginFraction), worldSize.x * 0.45f),
                    Mathf.Min(Mathf.Max(interiorBlockerMargin, worldSize.y * interiorBlockerMarginFraction), worldSize.y * 0.45f),
                    Mathf.Min(Mathf.Max(interiorBlockerMargin, worldSize.z * interiorBlockerMarginFraction), worldSize.z * 0.45f));
                Vector3 localMargin = new Vector3(
                    worldMargin.x / Mathf.Max(0.0001f, Mathf.Abs(lossyScale.x)),
                    worldMargin.y / Mathf.Max(0.0001f, Mathf.Abs(lossyScale.y)),
                    worldMargin.z / Mathf.Max(0.0001f, Mathf.Abs(lossyScale.z)));
                Vector3 insetSize = localBounds.size - localMargin * 2f;
                // 余白の有無はスケール適用後のワールド単位で判定する
                Vector3 worldInsetSize = worldSize - worldMargin * 2f;
                if (worldInsetSize.x > 0.01f && worldInsetSize.y > 0.01f && worldInsetSize.z > 0.01f)
                {
                    BoxCollider blocker = r.gameObject.AddComponent<BoxCollider>();
                    blocker.center = localBounds.center;
                    blocker.size = insetSize;

                    // 掘削の貫通判定を、この恒久的な壁までの最短距離に揃える
                    effectiveInteriorBlockerMargin = Mathf.Min(worldMargin.x, Mathf.Min(worldMargin.y, worldMargin.z));
                }
                else
                {
                    Debug.LogWarning($"[DestructibleBuildingPart] \"{buildingId}\": \"{r.name}\"の内部進入防止用" +
                        $"Colliderを追加できませんでした(worldSize={worldSize} worldMargin={worldMargin} " +
                        $"worldInsetSize={worldInsetSize})。この対象は建物内部に入れてしまう可能性があります");
                }
            }

            _rendererStates.Add(new RendererState
            {
                Renderer = r,
                InstanceMesh = instanceMesh,
                MeshCollider = meshCollider,
                FallbackCollider = fallbackCollider,
                Submeshes = submeshes,
                WeldedIds = weldedIds,
                EdgeTriangles = edgeTriangles,
                WeldedVertexNormal = weldedNormalSum,
                IsSmallObject = isSmall,
                EffectiveInteriorBlockerMargin = effectiveInteriorBlockerMargin,
            });
        }

        initStopwatch.Stop();
        Debug.Log($"[DestructibleBuildingPart] \"{buildingId}\": 初被弾で初期化しました " +
            $"(破壊可能Renderer数={_rendererStates.Count}/{_originalRenderers.Length} " +
            $"elapsedMs={initStopwatch.Elapsed.TotalMilliseconds:F1} viaPrewarm={viaPrewarm})");
    }

    /// <summary>面積がtargetTriangleAreaを超える三角形を中点分割して削除の解像度を上げる。分割不要なら即座に戻る</summary>
    private void SubdivideMeshInPlace(Mesh mesh, Transform rendererTransform)
    {
        Vector3[] srcVerts = mesh.vertices;
        if (srcVerts.Length == 0) return;

        // 元の三角形数が閾値を超えるメッシュは目標面積を比例して大きくし、分割後の三角形総数(初期化コスト)を抑える
        int totalMeshTriCount = mesh.triangles.Length / 3;
        float sizeRatio = totalMeshTriCount > subdivisionSizeThreshold
            ? (float)totalMeshTriCount / subdivisionSizeThreshold
            : 1f;
        float effectiveTargetArea = targetTriangleArea * Mathf.Min(sizeRatio, maxSubdivisionAreaMultiplier);

        Vector3[] srcNormals = mesh.normals;
        Vector2[] srcUv0 = mesh.uv;
        Vector2[] srcUv2 = mesh.uv2;
        Color[] srcColors = mesh.colors;

        bool hasNormals = srcNormals.Length == srcVerts.Length;
        bool hasUv0 = srcUv0.Length == srcVerts.Length;
        bool hasUv2 = srcUv2.Length == srcVerts.Length;
        bool hasColors = srcColors.Length == srcVerts.Length;
        bool hasTangents = mesh.tangents.Length == srcVerts.Length;

        var verts = new List<Vector3>(srcVerts);
        var normals = hasNormals ? new List<Vector3>(srcNormals) : null;
        var uv0 = hasUv0 ? new List<Vector2>(srcUv0) : null;
        var uv2 = hasUv2 ? new List<Vector2>(srcUv2) : null;
        var colors = hasColors ? new List<Color>(srcColors) : null;

        int subCount = mesh.subMeshCount;
        var newTrianglesPerSub = new List<int>[subCount];
        bool anySubdivided = false;
        bool anyFallback = false;
        int totalSrcTriCount = 0, totalOutTriCount = 0;

        for (int sub = 0; sub < subCount; sub++)
        {
            int[] srcTris = mesh.GetTriangles(sub);
            var outTris = new List<int>(srcTris.Length);
            int beforeVertCount = verts.Count;

            SubdivideSubmeshForFinerRemoval(verts, normals, uv0, uv2, colors,
                hasNormals, hasUv0, hasUv2, hasColors,
                srcTris, outTris, effectiveTargetArea, maxSubdivisionDepth, rendererTransform);

            // 分割結果が空になった場合は、対象が消えるのを避けるため元の三角形リストにフォールバックする
            if (srcTris.Length > 0 && outTris.Count == 0)
            {
                Debug.LogWarning($"[DestructibleBuildingPart] \"{buildingId}\": サブメッシュ{sub}の分割結果が" +
                    $"空だったため、元の三角形リストにフォールバックしました(元三角形数={srcTris.Length / 3})");
                outTris = new List<int>(srcTris);
                verts.RemoveRange(beforeVertCount, verts.Count - beforeVertCount);
                if (hasNormals) normals.RemoveRange(beforeVertCount, normals.Count - beforeVertCount);
                if (hasUv0) uv0.RemoveRange(beforeVertCount, uv0.Count - beforeVertCount);
                if (hasUv2) uv2.RemoveRange(beforeVertCount, uv2.Count - beforeVertCount);
                if (hasColors) colors.RemoveRange(beforeVertCount, colors.Count - beforeVertCount);
                anyFallback = true;
            }
            else if (verts.Count > beforeVertCount)
            {
                anySubdivided = true;
            }

            newTrianglesPerSub[sub] = outTris;
            totalSrcTriCount += srcTris.Length / 3;
            totalOutTriCount += outTris.Count / 3;
        }

        Debug.Log($"[DestructibleBuildingPart] \"{buildingId}\": \"{rendererTransform.name}\"の破壊粒度分割 " +
            $"三角形数 {totalSrcTriCount}→{totalOutTriCount}{(anyFallback ? "(一部フォールバックあり)" : "")} " +
            $"effectiveTargetArea={effectiveTargetArea:F2}");

        if (!anySubdivided) return;

        mesh.SetVertices(verts);
        if (hasNormals) mesh.SetNormals(normals);
        if (hasUv0) mesh.SetUVs(0, uv0);
        if (hasUv2) mesh.SetUVs(1, uv2);
        if (hasColors) mesh.SetColors(colors);

        for (int sub = 0; sub < subCount; sub++) mesh.SetTriangles(newTrianglesPerSub[sub], sub, false);

        if (!hasNormals) mesh.RecalculateNormals();
        if (hasTangents) mesh.RecalculateTangents(); // タンジェントはUnity任せにする
        mesh.RecalculateBounds();
    }

    /// <summary>1サブメッシュの三角形のうち、面積がtargetAreaを超えるものを再帰的に中点分割する。
    /// 中点頂点は辺ごとにキャッシュしてTジャンクションを防ぎ、法線・UV・頂点カラーも補間する</summary>
    private static void SubdivideSubmeshForFinerRemoval(
        List<Vector3> verts, List<Vector3> normals, List<Vector2> uv0, List<Vector2> uv2, List<Color> colors,
        bool hasNormals, bool hasUv0, bool hasUv2, bool hasColors,
        int[] sourceTriangles, List<int> outTriangles, float targetArea, int maxDepth, Transform t)
    {
        var edgeMidpointCache = new Dictionary<long, int>();

        int AddMidpoint(int ia, int ib)
        {
            long key = EdgeKey(ia, ib);
            if (edgeMidpointCache.TryGetValue(key, out int existing)) return existing;

            int newIndex = verts.Count;
            verts.Add((verts[ia] + verts[ib]) * 0.5f);
            if (hasNormals) normals.Add(((normals[ia] + normals[ib]) * 0.5f).normalized);
            if (hasUv0) uv0.Add((uv0[ia] + uv0[ib]) * 0.5f);
            if (hasUv2) uv2.Add((uv2[ia] + uv2[ib]) * 0.5f);
            if (hasColors) colors.Add(Color.Lerp(colors[ia], colors[ib], 0.5f));

            edgeMidpointCache[key] = newIndex;
            return newIndex;
        }

        float WorldArea(int ia, int ib, int ic)
        {
            Vector3 a = t.TransformPoint(verts[ia]);
            Vector3 b = t.TransformPoint(verts[ib]);
            Vector3 c = t.TransformPoint(verts[ic]);
            return Vector3.Cross(b - a, c - a).magnitude * 0.5f;
        }

        void SubdivideRecursive(int ia, int ib, int ic, int depth)
        {
            float area = WorldArea(ia, ib, ic);
            // 面積がほぼ0の退化三角形は出力しない(コライダーのCook失敗の原因になる)
            if (float.IsNaN(area) || area <= 1e-8f) return;

            if (depth >= maxDepth || area <= targetArea)
            {
                outTriangles.Add(ia);
                outTriangles.Add(ib);
                outTriangles.Add(ic);
                return;
            }

            int mab = AddMidpoint(ia, ib);
            int mbc = AddMidpoint(ib, ic);
            int mca = AddMidpoint(ic, ia);

            SubdivideRecursive(ia, mab, mca, depth + 1);
            SubdivideRecursive(mab, ib, mbc, depth + 1);
            SubdivideRecursive(mca, mbc, ic, depth + 1);
            SubdivideRecursive(mab, mbc, mca, depth + 1);
        }

        int triCount = sourceTriangles.Length / 3;
        for (int t2 = 0; t2 < triCount; t2++)
        {
            SubdivideRecursive(sourceTriangles[t2 * 3], sourceTriangles[t2 * 3 + 1], sourceTriangles[t2 * 3 + 2], 0);
        }
    }

    /// <summary>位置がほぼ同じ頂点を同一視するための溶接IDを頂点ごとに割り当てる(UVシーム等で分かれた頂点を辺の隣接判定で同一視するため)</summary>
    private static int[] BuildWeldedVertexIds(Vector3[] verts)
    {
        var idByQuantizedPos = new Dictionary<(int x, int y, int z), int>();
        var weldedIds = new int[verts.Length];
        const float weldCellSize = 0.0001f;

        for (int i = 0; i < verts.Length; i++)
        {
            var key = (Mathf.RoundToInt(verts[i].x / weldCellSize),
                       Mathf.RoundToInt(verts[i].y / weldCellSize),
                       Mathf.RoundToInt(verts[i].z / weldCellSize));
            if (!idByQuantizedPos.TryGetValue(key, out int id))
            {
                id = idByQuantizedPos.Count;
                idByQuantizedPos[key] = id;
            }
            weldedIds[i] = id;
        }
        return weldedIds;
    }

    /// <summary>頂点ID2個を順序に依存しない1個のキーにまとめる(辺(a,b)と(b,a)を同一視)</summary>
    private static long EdgeKey(int a, int b) => ((long)Mathf.Min(a, b) << 32) | (uint)Mathf.Max(a, b);

    /// <summary>バウンズサイズの体積。小物判定に使う</summary>
    private static float BoundsVolume(Vector3 size) => Mathf.Abs(size.x) * Mathf.Abs(size.y) * Mathf.Abs(size.z);

    /// <summary>溶接済み頂点IDごとの法線合計に三角形の法線を足し込む(使う前にNormalizeWeldedNormalsで正規化すること)</summary>
    private static void AccumulateWeldedNormal(Dictionary<int, Vector3> sums, int weldedId, Vector3 normal)
    {
        sums.TryGetValue(weldedId, out Vector3 existing);
        sums[weldedId] = existing + normal;
    }

    /// <summary>積み上げた法線合計を1回だけ正規化する(ほぼゼロの場合はそのまま残し、呼び出し側でフォールバックする)</summary>
    private static void NormalizeWeldedNormals(Dictionary<int, Vector3> sums)
    {
        var keys = new List<int>(sums.Keys);
        foreach (int key in keys)
        {
            Vector3 v = sums[key];
            if (v.sqrMagnitude > 0.0001f) sums[key] = v.normalized;
        }
    }

    /// <summary>辺に隣接する三角形((サブメッシュ番号, インデックス))を登録する</summary>
    private static void AddEdgeTriangle(Dictionary<long, List<(int sub, int tri)>> map, int a, int b, int sub, int triangleIndex)
    {
        long key = EdgeKey(a, b);
        if (!map.TryGetValue(key, out var list))
        {
            list = new List<(int sub, int tri)>(2);
            map[key] = list;
        }
        list.Add((sub, triangleIndex));
    }

    /// <summary>決定論的な値ノイズ(整数ハッシュ、2オクターブ)。爆発の削除境界・クレーター深さを不規則にするために使う。
    /// 全クライアントで同じ結果になるよう、浮動小数点に依存しない整数ハッシュのみを使う</summary>
    private static class FractureNoise
    {
        private static uint FMix32(uint h)
        {
            h ^= h >> 16; h *= 0x85EBCA6Bu;
            h ^= h >> 13; h *= 0xC2B2AE35u;
            h ^= h >> 16;
            return h;
        }

        private static uint Hash3(int x, int y, int z, uint seed)
        {
            uint h = seed ^ 0x9E3779B9u;
            h = FMix32(h ^ ((uint)x * 0x9E3779B1u));
            h = FMix32(h ^ ((uint)y * 0x85EBCA77u));
            h = FMix32(h ^ ((uint)z * 0xC2B2AE3Du));
            return h;
        }

        private static float Rand01(int x, int y, int z, uint seed) => Hash3(x, y, z, seed) / (float)uint.MaxValue;

        private static float Smooth(float x) => x * x * (3f - 2f * x);

        private static float ValueNoise(Vector3 p, float cellSize, uint seed)
        {
            Vector3 cellPos = p / cellSize;
            int x0 = Mathf.FloorToInt(cellPos.x), y0 = Mathf.FloorToInt(cellPos.y), z0 = Mathf.FloorToInt(cellPos.z);
            float fx = cellPos.x - x0, fy = cellPos.y - y0, fz = cellPos.z - z0;
            float ux = Smooth(fx), uy = Smooth(fy), uz = Smooth(fz);

            float c000 = Rand01(x0, y0, z0, seed), c100 = Rand01(x0 + 1, y0, z0, seed);
            float c010 = Rand01(x0, y0 + 1, z0, seed), c110 = Rand01(x0 + 1, y0 + 1, z0, seed);
            float c001 = Rand01(x0, y0, z0 + 1, seed), c101 = Rand01(x0 + 1, y0, z0 + 1, seed);
            float c011 = Rand01(x0, y0 + 1, z0 + 1, seed), c111 = Rand01(x0 + 1, y0 + 1, z0 + 1, seed);

            float x00 = Mathf.Lerp(c000, c100, ux), x10 = Mathf.Lerp(c010, c110, ux);
            float x01 = Mathf.Lerp(c001, c101, ux), x11 = Mathf.Lerp(c011, c111, ux);
            float y0v = Mathf.Lerp(x00, x10, uy), y1v = Mathf.Lerp(x01, x11, uy);
            return Mathf.Lerp(y0v, y1v, uz); // 0..1
        }

        /// <summary>-1..1の2オクターブ合成ノイズ(基本セルサイズ+その1/3サイズ)を返す</summary>
        public static float Sample(Vector3 point, float baseCellSize, uint seed)
        {
            float n1 = ValueNoise(point, baseCellSize, seed) * 2f - 1f;
            float n2 = ValueNoise(point, baseCellSize / 3f, seed + 101u) * 2f - 1f;
            return Mathf.Clamp(n1 * 0.75f + n2 * 0.25f, -1f, 1f);
        }

        /// <summary>Vector3のビット列から決定論的なシードを求める(全クライアントで一致)</summary>
        public static uint HashVector3ToSeed(Vector3 v)
        {
            unchecked
            {
                uint hx = (uint)System.BitConverter.SingleToInt32Bits(v.x);
                uint hy = (uint)System.BitConverter.SingleToInt32Bits(v.y);
                uint hz = (uint)System.BitConverter.SingleToInt32Bits(v.z);
                return FMix32(hx * 0x9E3779B1u ^ FMix32(hy * 0x85EBCA77u) ^ FMix32(hz * 0xC2B2AE3Du));
            }
        }
    }

    /// <summary>サーバーのみが呼ぶ破壊要求(ExplosiveItemから)。worldHitPoint/worldNormalは焦げ跡の位置用、
    /// damageCenterは実際の爆心地(ダメージ判定の中心)、powerは威力</summary>
    public void ServerApplyDamage(Vector3 worldHitPoint, Vector3 worldNormal, Vector3 damageCenter, float explosionRadius, float power)
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
        MatchManager.Instance?.ServerShowBuildingDamage(buildingId, worldHitPoint, worldNormal, damageCenter, explosionRadius, power);
    }

    /// <summary>MatchManagerのClientRpcから、全クライアントで呼ばれる</summary>
    public static void ApplyDamageEffectById(string id, Vector3 worldHitPoint, Vector3 worldNormal, Vector3 damageCenter, float explosionRadius, float power)
    {
        if (_registry.TryGetValue(id, out var part) && part != null) part.ShowDamageAt(worldHitPoint, worldNormal, damageCenter, explosionRadius, power);
    }

    /// <summary>全クライアント共通の被弾処理(三角形の削除・裏打ちの再構築・焦げ跡の貼り付け)</summary>
    private void ShowDamageAt(Vector3 worldHitPoint, Vector3 worldNormal, Vector3 damageCenter, float explosionRadius, float power)
    {
        Vector3 normal = worldNormal.sqrMagnitude > 0.0001f ? worldNormal.normalized : Vector3.up;

        InitializeIfNeeded();
        if (_rendererStates.Count == 0)
        {
            // 破壊可能なRendererが無い対象は、元の命中点にそのままデカールを貼る
            ScorchDecal.SpawnAndTrim(GetEffectsRoot(), _decals, maxDecals, worldHitPoint, normal, decalSize);
            return;
        }

        if (verboseLogging)
        {
            Debug.Log($"[DestructibleBuildingPart] \"{buildingId}\": ShowDamageAt " +
                $"damageCenter={damageCenter} radius={explosionRadius} power={power}");
        }

        float searchRadius = explosionRadius * rendererSearchMultiplier;
        float searchRadiusSqr = searchRadius * searchRadius;
        float explosionRadiusSqr = explosionRadius * explosionRadius;

        foreach (var state in _rendererStates)
        {
            if (state.Renderer == null || !state.Renderer.enabled) continue;
            if (state.Renderer.bounds.SqrDistance(damageCenter) > searchRadiusSqr) continue;
            ApplyDamageToRenderer(state, damageCenter, explosionRadius, explosionRadiusSqr, power);
        }

        // 対象全体が消滅した場合は、残った焦げ跡デカールを片付ける
        bool wholeObjectFullyDestroyed = _rendererStates.Count > 0;
        foreach (var state in _rendererStates)
        {
            int aliveHere = 0;
            foreach (var sm in state.Submeshes) aliveHere += sm.AliveCount;
            if (aliveHere > 0) { wholeObjectFullyDestroyed = false; break; }
        }
        if (wholeObjectFullyDestroyed) ScorchDecal.ClearAll(_decals);

        // ダメージ適用後に改めてRaycastし、実際に残っている表面へデカールを貼る(削れた面の手前に浮かないように)。表面が無ければ貼らない
        Vector3 rayOrigin = worldHitPoint + normal * raycastSkinOffset;
        if (Physics.Raycast(rayOrigin, -normal, out RaycastHit postDamageHit,
            maxBackingExtraDepth + raycastSkinOffset * 2f, ~0, QueryTriggerInteraction.Ignore))
        {
            ScorchDecal.SpawnAndTrim(GetEffectsRoot(), _decals, maxDecals, postDamageHit.point, postDamageHit.normal, decalSize);
        }
    }

    /// <summary>1個のRendererに距離減衰ダメージを三角形単位で適用する(耐久が尽きた三角形を取り除きデブリを飛ばす)。
    /// 削除半径はFractureNoiseで揺らし、既に死んだ三角形への再命中は裏打ち深さを積み増す。小物はApplyDamageToSmallObjectへ分岐する</summary>
    private void ApplyDamageToRenderer(RendererState state, Vector3 worldHitPoint, float explosionRadius, float explosionRadiusSqr, float power)
    {
        if (state.IsSmallObject)
        {
            ApplyDamageToSmallObject(state, worldHitPoint, explosionRadius);
            return;
        }

        Transform t = state.Renderer.transform;
        List<(Vector3 v0, Vector3 v1, Vector3 v2, int submesh)> removedWorldTriangles = null;
        // Mesh.verticesは毎回コピーされるため、1回の被弾につき1回だけ取得する
        Vector3[] verts = state.InstanceMesh.vertices;
        uint seed = FractureNoise.HashVector3ToSeed(worldHitPoint);
        float worldRimNoiseCellSize = Mathf.Max(0.01f, explosionRadius * fractureNoiseCellSizeRatio);
        float rimNoiseCellSize = worldRimNoiseCellSize / LocalScaleFactor(t);
        float maxDepth = explosionRadius * backingDepthRadiusRatio * power;
        bool anyNewlyRemoved = false;
        bool anyExtraDepthChanged = false;
        // 同じ溶接済み頂点への深さ積み増しは1回の爆発につき1回に制限する(共有する三角形数でムラが出ないように)
        var extraDepthVertexSeen = new HashSet<int>();
        // verboseLogging用の集計
        int newlyRemovedTriCount = 0;
        int extraDepthGainedVertexCount = 0;

        for (int sub = 0; sub < state.Submeshes.Length; sub++)
        {
            SubmeshState sm = state.Submeshes[sub];
            List<int> toRemove = null;

            for (int i = 0; i < sm.LocalCentroids.Length; i++)
            {
                Vector3 worldCentroid = t.TransformPoint(sm.LocalCentroids[i]);
                float distSqr = (worldCentroid - worldHitPoint).sqrMagnitude;

                // 三角形が爆心地の方を向いているかも判定し、向いていない面は距離に関わらず除外する(生死で基準が違うため判定は各分岐で行う)
                Vector3 worldNormal = t.TransformDirection(sm.LocalNormals[i]);
                bool hasValidNormal = worldNormal.sqrMagnitude > 0.0001f;
                if (hasValidNormal) worldNormal.Normalize();

                // 三角形ごとに実効半径を揺らし、境界が完全な円にならないようにする(ローカル重心を入力にして座標誤差をシードに持ち込まない)
                float rimNoise = FractureNoise.Sample(sm.LocalCentroids[i], rimNoiseCellSize, seed);
                float effectiveRadius = Mathf.Max(0.01f, explosionRadius * (1f + fractureRimNoiseStrength * rimNoise));

                if (!sm.Alive[i])
                {
                    // 裏打ちを生成しない対象は、深さの積み増しが無駄なので早期に打ち切る
                    if (_skipBackingGeneration) continue;

                    // 死んだ三角形の床は奥まっているため、既存の深さぶん判定半径を広げて、同じ場所を撃ち続けても反応するようにする
                    int di0 = sm.OriginalTriangles[i * 3], di1 = sm.OriginalTriangles[i * 3 + 1], di2 = sm.OriginalTriangles[i * 3 + 2];
                    float existingDepth = Mathf.Max(GetVertexDepth(state, state.WeldedIds[di0]),
                        Mathf.Max(GetVertexDepth(state, state.WeldedIds[di1]), GetVertexDepth(state, state.WeldedIds[di2])));

                    // 死んだ三角形のfacing判定は、既存の深さ+掘り進められる余地(maxBackingExtraDepth)ぶん奥まった点を基準にする(元の面基準だと正当な再命中まで弾くため)
                    if (hasValidNormal)
                    {
                        float facingRecessDistance = existingDepth + maxBackingExtraDepth;
                        Vector3 recessedReference = worldCentroid - worldNormal * facingRecessDistance;
                        Vector3 towardRecessed = worldHitPoint - recessedReference;
                        if (towardRecessed.sqrMagnitude > 0.0001f &&
                            Vector3.Dot(worldNormal, towardRecessed.normalized) < minFacingDot)
                        {
                            continue;
                        }
                    }

                    float forgivingRadius = effectiveRadius + existingDepth;
                    if (distSqr > forgivingRadius * forgivingRadius) continue;

                    // 頂点(溶接済みID)単位で深さを積み増し、隣接三角形との隙間を作らない
                    bool g0 = extraDepthVertexSeen.Add(state.WeldedIds[di0]) &&
                        GainVertexExtraDepth(state, di0, t.TransformPoint(verts[di0]), worldHitPoint, effectiveRadius, power);
                    bool g1 = extraDepthVertexSeen.Add(state.WeldedIds[di1]) &&
                        GainVertexExtraDepth(state, di1, t.TransformPoint(verts[di1]), worldHitPoint, effectiveRadius, power);
                    bool g2 = extraDepthVertexSeen.Add(state.WeldedIds[di2]) &&
                        GainVertexExtraDepth(state, di2, t.TransformPoint(verts[di2]), worldHitPoint, effectiveRadius, power);
                    if (g0) extraDepthGainedVertexCount++;
                    if (g1) extraDepthGainedVertexCount++;
                    if (g2) extraDepthGainedVertexCount++;
                    if (g0 || g1 || g2) anyExtraDepthChanged = true;
                    continue;
                }

                // 生きている三角形は元の面そのものなので、重心→爆心地の向きでそのまま判定する
                if (hasValidNormal)
                {
                    Vector3 towardHit = worldHitPoint - worldCentroid;
                    if (towardHit.sqrMagnitude > 0.0001f &&
                        Vector3.Dot(worldNormal, towardHit.normalized) < minFacingDot)
                    {
                        continue;
                    }
                }

                if (distSqr > effectiveRadius * effectiveRadius) continue;

                float normalizedDist = Mathf.Sqrt(distSqr) / effectiveRadius;
                byte damage = (byte)Mathf.Clamp(Mathf.RoundToInt(power * Mathf.Lerp(maxDamagePerHit, minDamagePerHit, normalizedDist)), 1, 255);
                sm.Hp[i] = (byte)Mathf.Max(0, sm.Hp[i] - damage);
                if (sm.Hp[i] > 0) continue;

                toRemove ??= new List<int>();
                toRemove.Add(i);
            }

            if (toRemove == null) continue;
            anyNewlyRemoved = true;
            newlyRemovedTriCount += toRemove.Count;

            foreach (int i in toRemove)
            {
                sm.Alive[i] = false;
                sm.AliveCount--;

                int i0 = sm.OriginalTriangles[i * 3], i1 = sm.OriginalTriangles[i * 3 + 1], i2 = sm.OriginalTriangles[i * 3 + 2];
                Vector3 w0 = t.TransformPoint(verts[i0]);
                Vector3 w1 = t.TransformPoint(verts[i1]);
                Vector3 w2 = t.TransformPoint(verts[i2]);

                // 頂点(溶接済みID)ごとに深さを評価して保存する(既存値との大きい方を採用)
                SetVertexBaseDepthAtLeast(state, i0, w0, worldHitPoint, explosionRadius, maxDepth, seed);
                SetVertexBaseDepthAtLeast(state, i1, w1, worldHitPoint, explosionRadius, maxDepth, seed);
                SetVertexBaseDepthAtLeast(state, i2, w2, worldHitPoint, explosionRadius, maxDepth, seed);

                removedWorldTriangles ??= new List<(Vector3, Vector3, Vector3, int)>();
                removedWorldTriangles.Add((w0, w1, w2, sub));
            }

            RebuildSubmeshTriangles(state, sub);
        }

        if (verboseLogging)
        {
            Debug.Log($"[DestructibleBuildingPart] \"{buildingId}\": ApplyDamageToRenderer結果 " +
                $"新規破壊三角形数={newlyRemovedTriCount} 深さ積み増し頂点数={extraDepthGainedVertexCount}");
        }

        if (!anyNewlyRemoved && !anyExtraDepthChanged) return;

        if (anyNewlyRemoved)
        {
            RefreshMeshCollider(state);
            SpawnDebrisFromRemoved(state, removedWorldTriangles);
        }
        RebuildBackingMeshFromScratch(state);
    }

    /// <summary>Rendererのワールド→ローカル換算の代表スケール(lossyScaleの3軸平均)。ワールド単位の値をローカル座標で使う前にこれで割る</summary>
    private static float LocalScaleFactor(Transform t)
    {
        Vector3 s = t.lossyScale;
        return Mathf.Max(0.0001f, (Mathf.Abs(s.x) + Mathf.Abs(s.y) + Mathf.Abs(s.z)) / 3f);
    }

    /// <summary>小物専用の被弾処理。爆発半径内に生存三角形が1枚でもあれば、そのRendererの全三角形を一括で破壊する</summary>
    private void ApplyDamageToSmallObject(RendererState state, Vector3 worldHitPoint, float explosionRadius)
    {
        Transform t = state.Renderer.transform;
        uint seed = FractureNoise.HashVector3ToSeed(worldHitPoint);
        float rimNoiseCellSize = Mathf.Max(0.01f, explosionRadius * fractureNoiseCellSizeRatio) / LocalScaleFactor(t);

        bool anyTriangleInRange = false;
        foreach (var sm in state.Submeshes)
        {
            for (int i = 0; i < sm.LocalCentroids.Length; i++)
            {
                if (!sm.Alive[i]) continue;
                Vector3 worldCentroid = t.TransformPoint(sm.LocalCentroids[i]);
                float rimNoise = FractureNoise.Sample(sm.LocalCentroids[i], rimNoiseCellSize, seed);
                float effectiveRadius = Mathf.Max(0.01f, explosionRadius * (1f + fractureRimNoiseStrength * rimNoise));
                if ((worldCentroid - worldHitPoint).sqrMagnitude <= effectiveRadius * effectiveRadius)
                {
                    anyTriangleInRange = true;
                    break;
                }
            }
            if (anyTriangleInRange) break;
        }
        if (!anyTriangleInRange) return;

        Vector3[] verts = state.InstanceMesh.vertices;
        var removedWorldTriangles = new List<(Vector3, Vector3, Vector3, int)>();

        for (int sub = 0; sub < state.Submeshes.Length; sub++)
        {
            SubmeshState sm = state.Submeshes[sub];
            bool anyRemovedThisSub = false;

            for (int i = 0; i < sm.Alive.Length; i++)
            {
                if (!sm.Alive[i]) continue;
                sm.Alive[i] = false;
                anyRemovedThisSub = true;

                int i0 = sm.OriginalTriangles[i * 3], i1 = sm.OriginalTriangles[i * 3 + 1], i2 = sm.OriginalTriangles[i * 3 + 2];
                Vector3 w0 = t.TransformPoint(verts[i0]);
                Vector3 w1 = t.TransformPoint(verts[i1]);
                Vector3 w2 = t.TransformPoint(verts[i2]);
                removedWorldTriangles.Add((w0, w1, w2, sub));
            }

            if (!anyRemovedThisSub) continue;
            sm.AliveCount = 0;
            RebuildSubmeshTriangles(state, sub);
        }

        RefreshMeshCollider(state);
        SpawnDebrisFromRemoved(state, removedWorldTriangles);
        Debug.Log($"[DestructibleBuildingPart] \"{buildingId}\": \"{state.Renderer.name}\"をワンパンで一括破壊しました " +
            $"(破壊三角形数={removedWorldTriangles.Count})");
        // 全サブメッシュが全損するため、裏打ちは生成されず既存分が片付く
        RebuildBackingMeshFromScratch(state);
    }

    /// <summary>爆心地からの距離に応じた裏打ち面の奥まり量を返す。sqrt(r^2-d^2)のお椀状カーブで、中心でmaxDepth、半径の端で0になる。
    /// FractureNoiseで凹凸を付ける(0.2〜1.8倍にクランプして反転を防ぐ)</summary>
    private float ComputeCraterDepth(Vector3 worldPoint, Vector3 worldHitPoint, float explosionRadius, float maxDepth, uint seed)
    {
        float normalizedDist = Vector3.Distance(worldPoint, worldHitPoint) / Mathf.Max(0.0001f, explosionRadius);
        normalizedDist = Mathf.Clamp01(normalizedDist);
        float curve = Mathf.Sqrt(Mathf.Max(0f, 1f - normalizedDist * normalizedDist));

        float noiseCellSize = Mathf.Max(0.01f, explosionRadius * fractureNoiseCellSizeRatio);
        float depthNoise = FractureNoise.Sample(worldPoint, noiseCellSize, seed + 1u);
        float depthJitter = Mathf.Clamp(1f + craterDepthNoiseStrength * depthNoise, 0.2f, 1.8f);

        return maxDepth * curve * depthJitter;
    }

    /// <summary>溶接済み頂点IDの現在の裏打ち深さ(BaseDepth+ExtraDepth、maxBackingExtraDepthでクランプ)を返す。未設定なら0</summary>
    private float GetVertexDepth(RendererState state, int weldedId)
    {
        state.VertexBaseDepth.TryGetValue(weldedId, out float b);
        state.VertexExtraDepth.TryGetValue(weldedId, out float e);
        return Mathf.Min(b + e, maxBackingExtraDepth);
    }

    /// <summary>頂点を奥まらせる方向(頂点ごとの平均法線のワールド変換)を返す。角をまたいでも同じ方向になり床が繋がる。平均法線が無い場合はfallbackを返す</summary>
    private static Vector3 GetWeldedRecessDirection(RendererState state, Transform t, int weldedId, Vector3 fallbackWorldNormal)
    {
        if (state.WeldedVertexNormal != null && state.WeldedVertexNormal.TryGetValue(weldedId, out Vector3 localNormal)
            && localNormal.sqrMagnitude > 0.0001f)
        {
            Vector3 worldNormal = t.TransformDirection(localNormal);
            if (worldNormal.sqrMagnitude > 0.0001f) return worldNormal.normalized;
        }
        return fallbackWorldNormal;
    }

    /// <summary>三角形が死んだ瞬間に頂点の基準深さを計算し、既存値より大きければ更新する(より深い値を優先)</summary>
    private void SetVertexBaseDepthAtLeast(RendererState state, int vertexIndex, Vector3 worldVertex,
        Vector3 worldHitPoint, float explosionRadius, float maxDepth, uint seed)
    {
        int weldedId = state.WeldedIds[vertexIndex];
        float candidate = ComputeCraterDepth(worldVertex, worldHitPoint, explosionRadius, maxDepth, seed);
        state.VertexBaseDepth.TryGetValue(weldedId, out float existing);
        if (candidate > existing) state.VertexBaseDepth[weldedId] = candidate;
    }

    /// <summary>既に死んでいる三角形の頂点へ、距離減衰させた深さを上限付きで積み増す。実際に増えた場合のみtrueを返す</summary>
    private bool GainVertexExtraDepth(RendererState state, int vertexIndex, Vector3 worldVertex,
        Vector3 worldHitPoint, float effectiveRadius, float power)
    {
        int weldedId = state.WeldedIds[vertexIndex];
        float existingDepth = GetVertexDepth(state, weldedId);
        float adjustedDist = Mathf.Max(0f, Vector3.Distance(worldVertex, worldHitPoint) - existingDepth);
        float normalizedDist = Mathf.Clamp01(adjustedDist / Mathf.Max(0.0001f, effectiveRadius));
        float gain = power * Mathf.Lerp(backingExtraDepthPerHit, backingExtraDepthPerHit * 0.3f, normalizedDist);

        state.VertexExtraDepth.TryGetValue(weldedId, out float existing);
        float newExtra = Mathf.Min(existing + gain, maxBackingExtraDepth);
        if (newExtra <= existing) return false;
        state.VertexExtraDepth[weldedId] = newExtra;
        return true;
    }

    /// <summary>裏打ちメッシュ全体を、現在死んでいる三角形の集合から毎回作り直す(床+スカート)。
    /// 全損したサブメッシュや深さが上限に達した三角形は裏打ちを作らず穴が貫通する。有機物対象は裏打ちを生成しない</summary>
    private void RebuildBackingMeshFromScratch(RendererState state)
    {
        state.BackingVertices.Clear();
        state.BackingNormals.Clear();
        state.BackingTriangles.Clear();

        Transform t = state.Renderer.transform;
        Vector3[] verts = state.InstanceMesh.vertices;
        bool anyBacking = false;

        // 掘削の最大深さ(対象全体共通): maxBackingExtraDepth・バウンズ最短辺・内部進入防止Colliderの手前、の最小値。三角形サイズには依存させない
        float objectDepthCeiling = Mathf.Min(maxBackingExtraDepth,
            Mathf.Min(
                Mathf.Min(state.Renderer.bounds.size.x, Mathf.Min(state.Renderer.bounds.size.y, state.Renderer.bounds.size.z)) * boundsSafetyMargin,
                Mathf.Max(0.3f, state.EffectiveInteriorBlockerMargin - interiorBlockerDepthBuffer)));

        for (int sub = 0; sub < state.Submeshes.Length && !_skipBackingGeneration; sub++)
        {
            SubmeshState sm = state.Submeshes[sub];
            if (sm.AliveCount == 0) continue;

            for (int i = 0; i < sm.Alive.Length; i++)
            {
                if (sm.Alive[i]) continue;

                int i0 = sm.OriginalTriangles[i * 3], i1 = sm.OriginalTriangles[i * 3 + 1], i2 = sm.OriginalTriangles[i * 3 + 2];
                int w0Id = state.WeldedIds[i0], w1Id = state.WeldedIds[i1], w2Id = state.WeldedIds[i2];

                Vector3 w0 = t.TransformPoint(verts[i0]);
                Vector3 w1 = t.TransformPoint(verts[i1]);
                Vector3 w2 = t.TransformPoint(verts[i2]);
                Vector3 faceNormal = Vector3.Cross(w1 - w0, w2 - w0).normalized;
                if (faceNormal.sqrMagnitude < 0.0001f) continue;

                // 対象全体で共通の上限でクランプする(隣接三角形が同じ値になる)
                float depth0 = Mathf.Min(GetVertexDepth(state, w0Id), objectDepthCeiling);
                float depth1 = Mathf.Min(GetVertexDepth(state, w1Id), objectDepthCeiling);
                float depth2 = Mathf.Min(GetVertexDepth(state, w2Id), objectDepthCeiling);

                // 貫通済みでも床パッチを省くだけで、スカート判定は必ず行う(生きた三角形との境界に壁が必要なため)
                bool fullyPunchedThrough = depth0 >= objectDepthCeiling && depth1 >= objectDepthCeiling && depth2 >= objectDepthCeiling;

                // 三角形内の角同士の深さの差を、最長辺×maxCornerDepthSlopeまでに抑える(急勾配の防止)
                float longestEdge = Mathf.Max((w1 - w0).magnitude, Mathf.Max((w2 - w1).magnitude, (w2 - w0).magnitude));
                float spreadLimit = longestEdge * maxCornerDepthSlope;
                float minCorner = Mathf.Min(depth0, Mathf.Min(depth1, depth2));
                float spreadCap = minCorner + spreadLimit;
                depth0 = Mathf.Min(depth0, spreadCap);
                depth1 = Mathf.Min(depth1, spreadCap);
                depth2 = Mathf.Min(depth2, spreadCap);

                // この三角形の重心から奥まらせる方向へRaycastし、屋根の下など局所的に近くにある別ジオメトリがあれば、その距離を追加の上限にする。
                // 自分の元MeshColliderと裏打ちColliderは除外し、レイ始点は法線方向へずらして縁の誤検出を避ける
                Vector3 centroid = (w0 + w1 + w2) / 3f;
                Vector3 rayOrigin = centroid - faceNormal * raycastSkinOffset;
                RaycastHit[] thicknessHits = Physics.RaycastAll(rayOrigin, -faceNormal, objectDepthCeiling, ~0, QueryTriggerInteraction.Ignore);
                float nearestOtherHitDistance = float.MaxValue;
                for (int h = 0; h < thicknessHits.Length; h++)
                {
                    Collider hitCollider = thicknessHits[h].collider;
                    if (hitCollider == state.MeshCollider || hitCollider == state.BackingMeshCollider) continue;
                    if (thicknessHits[h].distance < nearestOtherHitDistance) nearestOtherHitDistance = thicknessHits[h].distance;
                }
                if (nearestOtherHitDistance < float.MaxValue)
                {
                    float localCap = Mathf.Max(0.05f, nearestOtherHitDistance - raycastSkinOffset) * boundsSafetyMargin;
                    depth0 = Mathf.Min(depth0, localCap);
                    depth1 = Mathf.Min(depth1, localCap);
                    depth2 = Mathf.Min(depth2, localCap);
                }

                // 奥まらせる方向は頂点ごとの平均法線(角をまたいでも一致)を使う
                Vector3 n0 = GetWeldedRecessDirection(state, t, w0Id, faceNormal);
                Vector3 n1 = GetWeldedRecessDirection(state, t, w1Id, faceNormal);
                Vector3 n2 = GetWeldedRecessDirection(state, t, w2Id, faceNormal);

                // 貫通済みの場合は床パッチを作らない(本物の穴のまま)
                if (!fullyPunchedThrough)
                {
                    Vector3 recessed0 = w0 - n0 * depth0;
                    Vector3 recessed1 = w1 - n1 * depth1;
                    Vector3 recessed2 = w2 - n2 * depth2;
                    // 実際に追加できた場合のみanyBackingをtrueにする(0三角形のメッシュをColliderへ設定して古い形状が残るのを防ぐ)
                    if (AddBackingTriangleDoubleSided(state, recessed0, recessed1, recessed2)) anyBacking = true;
                }

                if (AppendSkirtIfBoundaryFresh(state, sub, i, w0Id, w1Id, w0, w1, n0, n1, depth0, depth1, objectDepthCeiling)) anyBacking = true;
                if (AppendSkirtIfBoundaryFresh(state, sub, i, w1Id, w2Id, w1, w2, n1, n2, depth1, depth2, objectDepthCeiling)) anyBacking = true;
                if (AppendSkirtIfBoundaryFresh(state, sub, i, w2Id, w0Id, w2, w0, n2, n0, depth2, depth0, objectDepthCeiling)) anyBacking = true;
            }
        }

        if (!anyBacking)
        {
            // 裏打ちが無くなった場合は、既存のBackingGoごと片付ける
            if (state.BackingGo != null)
            {
                Destroy(state.BackingGo);
                state.BackingGo = null;
                state.BackingMesh = null;
                state.BackingMeshCollider = null;
            }
            return;
        }

        EnsureBackingObject(state);
        // 頂点数が減る場合、SetVerticesの検証が古い三角形配列で失敗するため、先にClear()してから設定する
        state.BackingMesh.Clear();
        state.BackingMesh.SetVertices(state.BackingVertices);
        state.BackingMesh.SetNormals(state.BackingNormals);
        state.BackingMesh.SetTriangles(state.BackingTriangles, 0, false);
        state.BackingMesh.RecalculateBounds();

        // 当たり判定は見た目(お椀状のくぼみ)と同じ形にする
        state.BackingMeshCollider.sharedMesh = null;
        state.BackingMeshCollider.sharedMesh = state.BackingMesh;

        if (!verboseLogging) return;
        Material backingMat = GetBackingMaterial();
        Debug.Log($"[DestructibleBuildingPart] \"{buildingId}\": 裏打ちメッシュ再構築 " +
            $"頂点数={state.BackingVertices.Count} 三角形数={state.BackingTriangles.Count / 3} " +
            $"shader={backingMat.shader.name} color={backingMat.color} " +
            $"boundsSize={state.Renderer.bounds.size} objectDepthCeiling={objectDepthCeiling}");
    }

    /// <summary>死んだ三角形の1辺が「生きている/完全に貫通した三角形との境界」なら、元の面から裏打ちの床まで繋ぐ側面(スカート)を追加する。
    /// 隣が死んでいても完全には貫通していなければ床同士が繋がるので不要。実際に追加できた場合のみtrueを返す</summary>
    private bool AppendSkirtIfBoundaryFresh(RendererState state, int subIndex, int deadTriIndex,
        int vertexIdA, int vertexIdB, Vector3 worldA, Vector3 worldB, Vector3 normalA, Vector3 normalB,
        float depthA, float depthB, float objectDepthCeiling)
    {
        if (!state.EdgeTriangles.TryGetValue(EdgeKey(vertexIdA, vertexIdB), out var sharingTriangles)) return false;

        bool needsSkirt = false;
        foreach (var (otherSub, otherTri) in sharingTriangles)
        {
            if (otherSub == subIndex && otherTri == deadTriIndex) continue;

            SubmeshState otherSm = state.Submeshes[otherSub];
            if (otherSm.Alive[otherTri]) { needsSkirt = true; break; }

            // 隣も死んでいるが、3頂点のどれかがまだ上限に達していなければ床を持つので壁は不要(全頂点が上限なら完全な穴なので壁が必要)
            int oa = otherSm.OriginalTriangles[otherTri * 3];
            int ob = otherSm.OriginalTriangles[otherTri * 3 + 1];
            int oc = otherSm.OriginalTriangles[otherTri * 3 + 2];
            float od0 = GetVertexDepth(state, state.WeldedIds[oa]);
            float od1 = GetVertexDepth(state, state.WeldedIds[ob]);
            float od2 = GetVertexDepth(state, state.WeldedIds[oc]);
            bool otherFullyPunchedThrough = od0 >= objectDepthCeiling && od1 >= objectDepthCeiling && od2 >= objectDepthCeiling;
            if (otherFullyPunchedThrough) { needsSkirt = true; break; }
        }
        if (!needsSkirt) return false;

        Vector3 aRecessed = worldA - normalA * depthA;
        Vector3 bRecessed = worldB - normalB * depthB;

        // 元の辺と、奥まらせた辺を繋ぐ四角形(穴の側面)
        return AddBackingQuad(state, worldA, worldB, bRecessed, aRecessed);
    }

    /// <summary>三角形を表裏両面で裏打ちメッシュへ追加する(表裏は別頂点にして法線の打ち消し合いを防ぐ)。
    /// 退化三角形や頂点数上限で何も追加しなかった場合はfalseを返す</summary>
    private static bool AddBackingTriangleDoubleSided(RendererState state, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 faceNormal = Vector3.Cross(b - a, c - a).normalized;
        if (faceNormal.sqrMagnitude < 0.0001f) return false; // 退化三角形(面積ほぼ0)は法線が定まらないのでスキップ

        // 32bit非対応端末では、頂点数が上限に近づいたら追加を打ち切る
        if (!Supports32BitIndices && state.BackingVertices.Count + 6 > Max16BitBackingVertexCount) return false;

        int frontBase = state.BackingVertices.Count;
        state.BackingVertices.Add(a);
        state.BackingVertices.Add(b);
        state.BackingVertices.Add(c);
        state.BackingNormals.Add(faceNormal);
        state.BackingNormals.Add(faceNormal);
        state.BackingNormals.Add(faceNormal);
        state.BackingTriangles.Add(frontBase);
        state.BackingTriangles.Add(frontBase + 1);
        state.BackingTriangles.Add(frontBase + 2);

        int backBase = state.BackingVertices.Count;
        state.BackingVertices.Add(a);
        state.BackingVertices.Add(c);
        state.BackingVertices.Add(b);
        state.BackingNormals.Add(-faceNormal);
        state.BackingNormals.Add(-faceNormal);
        state.BackingNormals.Add(-faceNormal);
        state.BackingTriangles.Add(backBase);
        state.BackingTriangles.Add(backBase + 1);
        state.BackingTriangles.Add(backBase + 2);
        return true;
    }

    /// <summary>四角形a,b,c,dを2枚の三角形として両面追加する。どちらか一方でも追加できればtrue</summary>
    private static bool AddBackingQuad(RendererState state, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        bool added1 = AddBackingTriangleDoubleSided(state, a, b, c);
        bool added2 = AddBackingTriangleDoubleSided(state, a, c, d);
        return added1 || added2;
    }

    /// <summary>裏打ち用GameObjectに付け、そのColliderへの命中を元のDestructibleBuildingPartへ辿れるようにするリンク
    /// (裏打ちは共有ルート配下にあるためGetComponentInParentでは辿れない)</summary>
    public class BackingSurfaceLink : MonoBehaviour
    {
        public DestructibleBuildingPart Owner;
    }

    /// <summary>裏打ちメッシュ用のGameObject・Mesh・MeshRenderer・MeshColliderを(未作成なら)生成する</summary>
    private void EnsureBackingObject(RendererState state)
    {
        if (state.BackingGo != null) return;

        state.BackingGo = new GameObject($"{state.Renderer.name}_Backing");
        state.BackingGo.transform.SetParent(GetEffectsRoot(), true);
        state.BackingGo.transform.position = Vector3.zero;
        state.BackingGo.transform.rotation = Quaternion.identity;
        state.BackingGo.transform.localScale = Vector3.one;
        state.BackingGo.AddComponent<BackingSurfaceLink>().Owner = this;

        state.BackingMesh = new Mesh();
        // 頂点数が65,535を超えないよう32bit indexにする(非対応端末ではUInt16のままAddBackingTriangleDoubleSided側で上限を設ける)
        state.BackingMesh.indexFormat = Supports32BitIndices
            ? UnityEngine.Rendering.IndexFormat.UInt32
            : UnityEngine.Rendering.IndexFormat.UInt16;
        state.BackingGo.AddComponent<MeshFilter>().sharedMesh = state.BackingMesh;
        var renderer = state.BackingGo.AddComponent<MeshRenderer>();
        renderer.sharedMaterial = GetBackingMaterial();
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        // Colliderは見た目と同じ形にする
        state.BackingMeshCollider = state.BackingGo.AddComponent<MeshCollider>();
    }

    /// <summary>裏打ち用の共有マテリアル(URP Lit、無ければUnlit)を返す</summary>
    private Material GetBackingMaterial()
    {
        if (_backingMaterial != null) return _backingMaterial;

        Shader litShader = Shader.Find("Universal Render Pipeline/Lit");
        _backingMaterial = litShader != null
            ? new Material(litShader) { color = backingColor }
            : ProceduralUnlitColor.CreateMaterial(backingColor);
        return _backingMaterial;
    }

    /// <summary>生きている三角形だけでサブメッシュのインデックスを組み直す</summary>
    private static void RebuildSubmeshTriangles(RendererState state, int submeshIndex)
    {
        SubmeshState sm = state.Submeshes[submeshIndex];
        List<int> list = sm.ScratchTriangleList;
        list.Clear();
        for (int i = 0; i < sm.Alive.Length; i++)
        {
            if (!sm.Alive[i]) continue;
            list.Add(sm.OriginalTriangles[i * 3]);
            list.Add(sm.OriginalTriangles[i * 3 + 1]);
            list.Add(sm.OriginalTriangles[i * 3 + 2]);
        }
        state.InstanceMesh.SetTriangles(list, submeshIndex, false);
    }

    /// <summary>MeshColliderを再Cookさせる(sharedMeshをnull経由で再設定)。全三角形が消えた場合は再Cookに失敗して古い形状が残るため、Colliderをenabled=falseにする。
    /// MeshColliderが無い場合のFallbackColliderにも同じ基準を適用する</summary>
    private static void RefreshMeshCollider(RendererState state)
    {
        int totalAlive = 0;
        foreach (var sm in state.Submeshes) totalAlive += sm.AliveCount;
        bool shouldBeEnabled = totalAlive != 0;

        if (state.FallbackCollider != null && state.FallbackCollider.enabled != shouldBeEnabled)
        {
            state.FallbackCollider.enabled = shouldBeEnabled;
        }

        if (state.MeshCollider == null) return;

        if (!shouldBeEnabled)
        {
            state.MeshCollider.enabled = false;
            return;
        }

        if (!state.MeshCollider.enabled) state.MeshCollider.enabled = true;
        state.MeshCollider.sharedMesh = null;
        state.MeshCollider.sharedMesh = state.InstanceMesh;
    }

    /// <summary>取り除かれた三角形のうちdebrisSpawnRatio割合を選び、その三角形の頂点・マテリアルでデブリを飛ばす</summary>
    private void SpawnDebrisFromRemoved(RendererState state, List<(Vector3 v0, Vector3 v1, Vector3 v2, int submesh)> removed)
    {
        if (removed.Count == 0) return;

        for (int i = removed.Count - 1; i > 0; i--)
        {
            int j = Random.Range(0, i + 1);
            (removed[i], removed[j]) = (removed[j], removed[i]);
        }

        int spawnTargetCount = Mathf.Clamp(Mathf.CeilToInt(removed.Count * debrisSpawnRatio), 1, removed.Count);
        spawnTargetCount = Mathf.Min(spawnTargetCount, maxDebrisPerExplosion);

        Material[] sharedMats = state.Renderer.sharedMaterials;

        for (int i = 0; i < spawnTargetCount; i++)
        {
            var (v0, v1, v2, submesh) = removed[i];
            Material mat = submesh >= 0 && submesh < sharedMats.Length ? sharedMats[submesh] : null;
            SpawnSingleTriangleDebris(v0, v1, v2, mat);
        }
    }

    /// <summary>三角形1個ぶんを、Colliderを持たない見た目だけの物理デブリとして飛ばす</summary>
    private void SpawnSingleTriangleDebris(Vector3 v0, Vector3 v1, Vector3 v2, Material material)
    {
        Vector3 center = (v0 + v1 + v2) / 3f;
        Vector3 normal = Vector3.Cross(v1 - v0, v2 - v0).normalized;
        if (normal.sqrMagnitude < 0.0001f) normal = Vector3.up;

        var mesh = new Mesh();
        mesh.SetVertices(new[] { v0 - center, v1 - center, v2 - center });
        mesh.SetNormals(new[] { normal, normal, normal });
        mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
        mesh.RecalculateBounds();

        var go = new GameObject("BuildingDebris");
        int debrisLayer = LayerMask.NameToLayer("Debris");
        if (debrisLayer >= 0) go.layer = debrisLayer;
        go.transform.SetParent(GetEffectsRoot(), true);
        go.transform.SetPositionAndRotation(center, Quaternion.identity);

        go.AddComponent<MeshFilter>().sharedMesh = mesh;
        var renderer = go.AddComponent<MeshRenderer>();
        if (material != null) renderer.sharedMaterial = material;

        // トルク・減衰は加えず、重力落下+面の法線方向への一回の力積のみにする
        var rb = go.AddComponent<Rigidbody>();
        Vector3 outward = normal;
        if (outward.sqrMagnitude < 0.0001f) outward = Random.onUnitSphere;
        rb.AddForce(outward.normalized * debrisForce, ForceMode.VelocityChange);

        Destroy(go, debrisLifetimeSeconds);
    }

    /// <summary>爆発跡・デブリの共通親(スケール1)。対象のスケールを継承して巨大化するのを防ぐ</summary>
    private static Transform GetEffectsRoot()
    {
        if (_effectsRoot == null)
        {
            var go = new GameObject("BuildingDamageEffectsRoot");
            Object.DontDestroyOnLoad(go);
            _effectsRoot = go.transform;
        }
        return _effectsRoot;
    }

    /// <summary>次の試合開始時に全クライアントで呼ばれ、爆発跡・欠けた三角形・裏打ちを全て元に戻す</summary>
    public static void ResetAll()
    {
        foreach (var part in _registry.Values)
        {
            if (part != null) part.ResetOwnState();
        }
    }

    private void ResetOwnState()
    {
        ScorchDecal.ClearAll(_decals);

        foreach (var state in _rendererStates)
        {
            bool anyChanged = false;
            foreach (var sm in state.Submeshes)
            {
                if (sm.AliveCount != sm.Alive.Length) anyChanged = true;
                for (int i = 0; i < sm.Alive.Length; i++)
                {
                    sm.Alive[i] = true;
                    sm.Hp[i] = triangleHitPoints;
                }
                sm.AliveCount = sm.Alive.Length;
            }
            // 次の試合に裏打ち深さを持ち越さない
            state.VertexBaseDepth.Clear();
            state.VertexExtraDepth.Clear();

            if (anyChanged)
            {
                for (int sub = 0; sub < state.Submeshes.Length; sub++) RebuildSubmeshTriangles(state, sub);
                RefreshMeshCollider(state);
            }

            if (state.BackingGo != null)
            {
                Destroy(state.BackingGo);
                state.BackingGo = null;
                state.BackingMesh = null;
                state.BackingMeshCollider = null;
                state.BackingVertices.Clear();
                state.BackingNormals.Clear();
                state.BackingTriangles.Clear();
            }
        }
    }
}
