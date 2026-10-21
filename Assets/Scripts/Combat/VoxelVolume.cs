using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// 直方体領域を格子状のボクセルとして持ち、楕円体範囲での削りと、面カリング方式
/// (空気に接する面だけを描画)のメッシュ生成を行うロジッククラス。ネットワーク同期は持たない。
/// コンストラクタで直方体を丸ごと、BuildFromMesh()で任意メッシュの外形からボクセル化できる。
/// </summary>
public class VoxelVolume
{
    public readonly int SizeX, SizeY, SizeZ;
    public readonly Vector3 VoxelSize;
    public readonly Vector3 Origin; // ボクセル(0,0,0)の最小コーナーのローカル座標

    private readonly bool[] _voxels; // フラット化した3次元配列。index = x + y*SizeX + z*SizeX*SizeY
    private readonly byte[] _hitPoints; // 各ボクセルの耐久値
    // 各ボクセルのマテリアル添字(パレット色番号)。既定は全て0
    private readonly byte[] _materialIndex;

    /// <summary>BuildMesh系が出力するサブメッシュ数(＝必要なマテリアル配列の要素数)</summary>
    public readonly int MaterialCount;

    /// <summary>
    /// hitPointsVariance&gt;0なら、各ボクセルの耐久値をindexの決定論的ハッシュでhitPoints±varianceにばらつかせる
    /// (全クライアントが同じ値を独立に再現できる)。0なら一律。
    /// </summary>
    public VoxelVolume(Vector3Int resolution, Vector3 boundsSize, Vector3 boundsCenter, byte hitPoints = 1,
        int materialCount = 1, byte hitPointsVariance = 0)
    {
        SizeX = Mathf.Max(1, resolution.x);
        SizeY = Mathf.Max(1, resolution.y);
        SizeZ = Mathf.Max(1, resolution.z);
        VoxelSize = new Vector3(
            Mathf.Max(0.001f, boundsSize.x) / SizeX,
            Mathf.Max(0.001f, boundsSize.y) / SizeY,
            Mathf.Max(0.001f, boundsSize.z) / SizeZ);
        Origin = boundsCenter - boundsSize * 0.5f;
        MaterialCount = Mathf.Max(1, materialCount);

        _voxels = new bool[SizeX * SizeY * SizeZ];
        _hitPoints = new byte[SizeX * SizeY * SizeZ];
        _materialIndex = new byte[SizeX * SizeY * SizeZ];
        byte hp = (byte)Mathf.Max(1, hitPoints);
        for (int i = 0; i < _voxels.Length; i++)
        {
            _voxels[i] = true; // デフォルトは中身の詰まった直方体
            _hitPoints[i] = hitPointsVariance > 0 ? JitterHitPoints(hp, hitPointsVariance, i) : hp;
        }
    }

    /// <summary>ボクセルindexの整数ハッシュで耐久値をhp±varianceにばらつかせる(Randomは使わず決定論的)</summary>
    private static byte JitterHitPoints(byte hp, byte variance, int voxelIndex)
    {
        uint h = (uint)voxelIndex;
        h = (h ^ 61u) ^ (h >> 16);
        h *= 9u;
        h ^= h >> 4;
        h *= 0x27d4eb2du;
        h ^= h >> 15;
        int range = variance * 2 + 1;
        int offset = (int)(h % (uint)range) - variance;
        return (byte)Mathf.Clamp(hp + offset, 1, 255);
    }

    public byte GetMaterialIndex(int x, int y, int z) => InBounds(x, y, z) ? _materialIndex[Index(x, y, z)] : (byte)0;

    public void SetMaterialIndex(int x, int y, int z, byte materialIndex)
    {
        if (!InBounds(x, y, z)) return;
        _materialIndex[Index(x, y, z)] = (byte)Mathf.Clamp(materialIndex, 0, MaterialCount - 1);
    }

    /// <summary>任意のメッシュの外形をボクセル化する。各ボクセル中心からのレイとの交差数の奇偶で内外を判定する(重いので一度きり向け)</summary>
    public static VoxelVolume BuildFromMesh(Mesh mesh, Vector3Int resolution, byte hitPoints = 1, int materialCount = 1)
    {
        Bounds bounds = mesh.bounds;
        var volume = new VoxelVolume(resolution, bounds.size, bounds.center, hitPoints, materialCount);

        Vector3[] verts = mesh.vertices;
        int[] tris = mesh.triangles;
        Vector3 rayDir = new Vector3(1f, 0.0001f, 0.0002f); // 軸/エッジ平行だと判定が不安定なため、わずかに傾ける

        for (int x = 0; x < volume.SizeX; x++)
        {
            for (int y = 0; y < volume.SizeY; y++)
            {
                for (int z = 0; z < volume.SizeZ; z++)
                {
                    Vector3 center = volume.VoxelCenterLocal(x, y, z);
                    volume.Set(x, y, z, IsPointInsideMesh(center, rayDir, verts, tris));
                }
            }
        }

        return volume;
    }

    private static readonly Vector3 DefaultInsideMeshRayDir = new Vector3(1f, 0.0001f, 0.0002f);

    /// <summary>1点だけの内外判定(レイと三角形の交差数の奇偶)</summary>
    public static bool IsPointInsideMesh(Vector3 point, Vector3[] verts, int[] tris)
        => IsPointInsideMesh(point, DefaultInsideMeshRayDir, verts, tris);

    private static bool IsPointInsideMesh(Vector3 point, Vector3 rayDir, Vector3[] verts, int[] tris)
    {
        int hitCount = 0;
        for (int i = 0; i < tris.Length; i += 3)
        {
            if (RayIntersectsTriangle(point, rayDir, verts[tris[i]], verts[tris[i + 1]], verts[tris[i + 2]]))
            {
                hitCount++;
            }
        }
        return (hitCount % 2) == 1;
    }

    /// <summary>Möller–Trumboreのレイ-三角形交差判定(前方向の交差のみtrue)</summary>
    private static bool RayIntersectsTriangle(Vector3 origin, Vector3 dir, Vector3 v0, Vector3 v1, Vector3 v2)
    {
        const float epsilon = 1e-6f;
        Vector3 edge1 = v1 - v0;
        Vector3 edge2 = v2 - v0;
        Vector3 h = Vector3.Cross(dir, edge2);
        float a = Vector3.Dot(edge1, h);
        if (Mathf.Abs(a) < epsilon) return false;

        float f = 1f / a;
        Vector3 s = origin - v0;
        float u = f * Vector3.Dot(s, h);
        if (u < 0f || u > 1f) return false;

        Vector3 q = Vector3.Cross(s, edge1);
        float v = f * Vector3.Dot(dir, q);
        if (v < 0f || u + v > 1f) return false;

        float t = f * Vector3.Dot(edge2, q);
        return t > epsilon;
    }

    private int Index(int x, int y, int z) => x + y * SizeX + z * SizeX * SizeY;

    public bool InBounds(int x, int y, int z) => x >= 0 && x < SizeX && y >= 0 && y < SizeY && z >= 0 && z < SizeZ;

    public bool Get(int x, int y, int z) => InBounds(x, y, z) && _voxels[Index(x, y, z)];

    public void Set(int x, int y, int z, bool value)
    {
        if (!InBounds(x, y, z)) return;
        _voxels[Index(x, y, z)] = value;
    }

    public Vector3 VoxelCenterLocal(int x, int y, int z)
    {
        return Origin + new Vector3((x + 0.5f) * VoxelSize.x, (y + 0.5f) * VoxelSize.y, (z + 0.5f) * VoxelSize.z);
    }

    /// <summary>
    /// ローカル座標の楕円体範囲内のボクセルの耐久値を、中心ほど大きく境界ほど小さい量(maxDamage〜minDamageの線形補間)だけ減らし、
    /// 0以下になったものを削る。新しく削れたボクセル座標のリストを返す。
    /// </summary>
    public List<Vector3Int> CarveEllipsoid(Vector3 localCenter, Vector3 localRadii, int maxDamage, int minDamage)
    {
        var newlyRemoved = new List<Vector3Int>();
        Vector3 safeRadii = new Vector3(
            Mathf.Max(0.0001f, Mathf.Abs(localRadii.x)),
            Mathf.Max(0.0001f, Mathf.Abs(localRadii.y)),
            Mathf.Max(0.0001f, Mathf.Abs(localRadii.z)));

        Vector3Int minIdx = WorldToIndexClamped(localCenter - safeRadii);
        Vector3Int maxIdx = WorldToIndexClamped(localCenter + safeRadii);

        for (int x = minIdx.x; x <= maxIdx.x; x++)
        {
            for (int y = minIdx.y; y <= maxIdx.y; y++)
            {
                for (int z = minIdx.z; z <= maxIdx.z; z++)
                {
                    if (!Get(x, y, z)) continue;
                    Vector3 d = VoxelCenterLocal(x, y, z) - localCenter;
                    float normalizedDistSqr =
                        (d.x * d.x) / (safeRadii.x * safeRadii.x) +
                        (d.y * d.y) / (safeRadii.y * safeRadii.y) +
                        (d.z * d.z) / (safeRadii.z * safeRadii.z);
                    if (normalizedDistSqr > 1f) continue;

                    float normalizedDist = Mathf.Sqrt(Mathf.Clamp01(normalizedDistSqr));
                    int damage = Mathf.Max(1, Mathf.RoundToInt(Mathf.Lerp(maxDamage, minDamage, normalizedDist)));

                    int idx = Index(x, y, z);
                    _hitPoints[idx] = (byte)Mathf.Max(0, _hitPoints[idx] - damage);
                    if (_hitPoints[idx] <= 0)
                    {
                        Set(x, y, z, false);
                        newlyRemoved.Add(new Vector3Int(x, y, z));
                    }
                }
            }
        }

        return newlyRemoved;
    }

    /// <summary>
    /// 累積ダメージを持たない版。爆発1回分の距離減衰込みダメージが耐久値以上のボクセルだけを即座に削る。
    /// 爆心に最も近いボクセルから26近傍で繋がっているものだけが対象(隙間を挟んだ別オブジェクトには飛び越えない)。
    /// </summary>
    public List<Vector3Int> CarveEllipsoidThreshold(Vector3 localCenter, Vector3 localRadii, int maxDamage, int minDamage)
    {
        var newlyRemoved = new List<Vector3Int>();
        Vector3 safeRadii = new Vector3(
            Mathf.Max(0.0001f, Mathf.Abs(localRadii.x)),
            Mathf.Max(0.0001f, Mathf.Abs(localRadii.y)),
            Mathf.Max(0.0001f, Mathf.Abs(localRadii.z)));

        Vector3Int minIdx = WorldToIndexClamped(localCenter - safeRadii);
        Vector3Int maxIdx = WorldToIndexClamped(localCenter + safeRadii);

        int debugAliveInBox = 0, debugInEllipsoid = 0;
        int debugFirstDamage = -1, debugFirstDurability = -1;

        // 楕円体内に実在するボクセルを候補として集め、爆心に最も近い候補を起点にflood fillする
        var candidateDamage = new Dictionary<Vector3Int, int>();
        Vector3Int seed = default;
        float seedDistSqr = float.MaxValue;
        bool hasSeed = false;

        for (int x = minIdx.x; x <= maxIdx.x; x++)
        {
            for (int y = minIdx.y; y <= maxIdx.y; y++)
            {
                for (int z = minIdx.z; z <= maxIdx.z; z++)
                {
                    if (!Get(x, y, z)) continue;
                    debugAliveInBox++;
                    Vector3 d = VoxelCenterLocal(x, y, z) - localCenter;
                    float normalizedDistSqr =
                        (d.x * d.x) / (safeRadii.x * safeRadii.x) +
                        (d.y * d.y) / (safeRadii.y * safeRadii.y) +
                        (d.z * d.z) / (safeRadii.z * safeRadii.z);
                    if (normalizedDistSqr > 1f) continue;
                    debugInEllipsoid++;

                    float normalizedDist = Mathf.Sqrt(Mathf.Clamp01(normalizedDistSqr));
                    int damage = Mathf.Max(1, Mathf.RoundToInt(Mathf.Lerp(maxDamage, minDamage, normalizedDist)));

                    var coord = new Vector3Int(x, y, z);
                    candidateDamage[coord] = damage;

                    if (debugFirstDamage < 0) { debugFirstDamage = damage; debugFirstDurability = _hitPoints[Index(x, y, z)]; }

                    float distSqrToCenter = d.sqrMagnitude;
                    if (!hasSeed || distSqrToCenter < seedDistSqr)
                    {
                        hasSeed = true;
                        seedDistSqr = distSqrToCenter;
                        seed = coord;
                    }
                }
            }
        }

        if (hasSeed)
        {
            var reachable = new HashSet<Vector3Int>{ seed };
            var queue = new Queue<Vector3Int>();
            queue.Enqueue(seed);

            while (queue.Count > 0)
            {
                Vector3Int c = queue.Dequeue();
                for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (dx == 0 && dy == 0 && dz == 0) continue;
                    var n = new Vector3Int(c.x + dx, c.y + dy, c.z + dz);
                    if (!candidateDamage.ContainsKey(n)) continue;
                    if (!reachable.Add(n)) continue;
                    queue.Enqueue(n);
                }
            }

            foreach (var coord in reachable)
            {
                int damage = candidateDamage[coord];
                int idx = Index(coord.x, coord.y, coord.z);
                if (damage >= _hitPoints[idx])
                {
                    Set(coord.x, coord.y, coord.z, false);
                    newlyRemoved.Add(coord);
                }
            }
        }

        UnityEngine.Debug.Log($"[VoxelVolume] CarveEllipsoidThreshold: box=({minIdx}..{maxIdx}) " +
            $"aliveInBox={debugAliveInBox} inEllipsoid={debugInEllipsoid} connectedCandidates={(hasSeed ? candidateDamage.Count : 0)} " +
            $"removed={newlyRemoved.Count} firstDamage={debugFirstDamage} firstDurability={debugFirstDurability}");

        return newlyRemoved;
    }

    private Vector3Int WorldToIndexClamped(Vector3 localPos)
    {
        Vector3 rel = localPos - Origin;
        int x = Mathf.Clamp(Mathf.FloorToInt(rel.x / VoxelSize.x), 0, SizeX - 1);
        int y = Mathf.Clamp(Mathf.FloorToInt(rel.y / VoxelSize.y), 0, SizeY - 1);
        int z = Mathf.Clamp(Mathf.FloorToInt(rel.z / VoxelSize.z), 0, SizeZ - 1);
        return new Vector3Int(x, y, z);
    }

    /// <summary>現在の状態から面カリング方式でメッシュを構築する。マテリアル添字ごとにサブメッシュを分ける</summary>
    public Mesh BuildMesh()
    {
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var submeshTriangles = CreateEmptySubmeshLists();

        for (int x = 0; x < SizeX; x++)
        {
            for (int y = 0; y < SizeY; y++)
            {
                for (int z = 0; z < SizeZ; z++)
                {
                    if (!Get(x, y, z)) continue;

                    List<int> triangles = submeshTriangles[GetMaterialIndex(x, y, z)];
                    Vector3 min = Origin + new Vector3(x * VoxelSize.x, y * VoxelSize.y, z * VoxelSize.z);
                    Vector3 max = min + VoxelSize;

                    if (!Get(x + 1, y, z)) AddFace(vertices, normals, uvs, triangles, min, max, Vector3.right);
                    if (!Get(x - 1, y, z)) AddFace(vertices, normals, uvs, triangles, min, max, Vector3.left);
                    if (!Get(x, y + 1, z)) AddFace(vertices, normals, uvs, triangles, min, max, Vector3.up);
                    if (!Get(x, y - 1, z)) AddFace(vertices, normals, uvs, triangles, min, max, Vector3.down);
                    if (!Get(x, y, z + 1)) AddFace(vertices, normals, uvs, triangles, min, max, Vector3.forward);
                    if (!Get(x, y, z - 1)) AddFace(vertices, normals, uvs, triangles, min, max, Vector3.back);
                }
            }
        }

        return BuildMeshFromGeometry(vertices, normals, uvs, submeshTriangles);
    }

    /// <summary>BuildMesh()と同じ面カリングで、UVをマテリアル添字から256色パレット上の位置として計算する単一サブメッシュ版</summary>
    public Mesh BuildMeshWithPaletteUV()
    {
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();

        for (int x = 0; x < SizeX; x++)
        {
            for (int y = 0; y < SizeY; y++)
            {
                for (int z = 0; z < SizeZ; z++)
                {
                    if (!Get(x, y, z)) continue;

                    float u = (GetMaterialIndex(x, y, z) + 0.5f) / 256f;
                    Vector2 paletteUv = new Vector2(u, 0.5f);
                    Vector3 min = Origin + new Vector3(x * VoxelSize.x, y * VoxelSize.y, z * VoxelSize.z);
                    Vector3 max = min + VoxelSize;

                    if (!Get(x + 1, y, z)) AddPaletteFace(vertices, normals, uvs, triangles, min, max, Vector3.right, paletteUv);
                    if (!Get(x - 1, y, z)) AddPaletteFace(vertices, normals, uvs, triangles, min, max, Vector3.left, paletteUv);
                    if (!Get(x, y + 1, z)) AddPaletteFace(vertices, normals, uvs, triangles, min, max, Vector3.up, paletteUv);
                    if (!Get(x, y - 1, z)) AddPaletteFace(vertices, normals, uvs, triangles, min, max, Vector3.down, paletteUv);
                    if (!Get(x, y, z + 1)) AddPaletteFace(vertices, normals, uvs, triangles, min, max, Vector3.forward, paletteUv);
                    if (!Get(x, y, z - 1)) AddPaletteFace(vertices, normals, uvs, triangles, min, max, Vector3.back, paletteUv);
                }
            }
        }

        return BuildMeshFromGeometry(vertices, normals, uvs, new[] { triangles });
    }

    private void AddPaletteFace(List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs, List<int> triangles, Vector3 min, Vector3 max, Vector3 dir, Vector2 uv)
    {
        GetFaceCorners(min, max, dir, out Vector3 a, out Vector3 b, out Vector3 c, out Vector3 d);
        int startIndex = vertices.Count;
        vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
        normals.Add(dir); normals.Add(dir); normals.Add(dir); normals.Add(dir);
        uvs.Add(uv); uvs.Add(uv); uvs.Add(uv); uvs.Add(uv);
        triangles.Add(startIndex + 0); triangles.Add(startIndex + 1); triangles.Add(startIndex + 2);
        triangles.Add(startIndex + 0); triangles.Add(startIndex + 2); triangles.Add(startIndex + 3);
    }

    private List<int>[] CreateEmptySubmeshLists()
    {
        var lists = new List<int>[MaterialCount];
        for (int i = 0; i < lists.Length; i++) lists[i] = new List<int>();
        return lists;
    }

    /// <summary>指定したボクセル座標群だけを含む独立した小さなメッシュを構築する</summary>
    public Mesh BuildMeshForVoxels(List<Vector3Int> voxelCoords)
    {
        var set = new HashSet<Vector3Int>(voxelCoords);
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var submeshTriangles = CreateEmptySubmeshLists();

        bool InSet(int x, int y, int z) => set.Contains(new Vector3Int(x, y, z));

        foreach (var v in voxelCoords)
        {
            List<int> triangles = submeshTriangles[GetMaterialIndex(v.x, v.y, v.z)];
            Vector3 min = Origin + new Vector3(v.x * VoxelSize.x, v.y * VoxelSize.y, v.z * VoxelSize.z);
            Vector3 max = min + VoxelSize;

            if (!InSet(v.x + 1, v.y, v.z)) AddFace(vertices, normals, uvs, triangles, min, max, Vector3.right);
            if (!InSet(v.x - 1, v.y, v.z)) AddFace(vertices, normals, uvs, triangles, min, max, Vector3.left);
            if (!InSet(v.x, v.y + 1, v.z)) AddFace(vertices, normals, uvs, triangles, min, max, Vector3.up);
            if (!InSet(v.x, v.y - 1, v.z)) AddFace(vertices, normals, uvs, triangles, min, max, Vector3.down);
            if (!InSet(v.x, v.y, v.z + 1)) AddFace(vertices, normals, uvs, triangles, min, max, Vector3.forward);
            if (!InSet(v.x, v.y, v.z - 1)) AddFace(vertices, normals, uvs, triangles, min, max, Vector3.back);
        }

        return BuildMeshFromGeometry(vertices, normals, uvs, submeshTriangles);
    }

    /// <summary>
    /// デブリ用メッシュ。立方体の8頂点を一度だけジッターして全面で共有し(隙間防止)、各面を中心頂点つきの4三角形に分割して
    /// 不規則に砕けた見た目にする。色は本体と同じパレットUVで、単一サブメッシュ。jitterAmountはメートル。
    /// </summary>
    public Mesh BuildJaggedMeshForVoxels(List<Vector3Int> voxelCoords, float jitterAmount)
    {
        var set = new HashSet<Vector3Int>(voxelCoords);
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var triangles = new List<int>();

        bool InSet(int x, int y, int z) => set.Contains(new Vector3Int(x, y, z));

        foreach (var v in voxelCoords)
        {
            Vector3 min = Origin + new Vector3(v.x * VoxelSize.x, v.y * VoxelSize.y, v.z * VoxelSize.z);
            Vector3 max = min + VoxelSize;
            Vector3[] corners = BuildJitteredCorners(min, max, jitterAmount);

            float u = (GetMaterialIndex(v.x, v.y, v.z) + 0.5f) / 256f;
            Vector2 paletteUv = new Vector2(u, 0.5f);

            if (!InSet(v.x + 1, v.y, v.z)) AddFaceFromCorners(vertices, normals, uvs, triangles, corners, Vector3.right, jitterAmount, paletteUv);
            if (!InSet(v.x - 1, v.y, v.z)) AddFaceFromCorners(vertices, normals, uvs, triangles, corners, Vector3.left, jitterAmount, paletteUv);
            if (!InSet(v.x, v.y + 1, v.z)) AddFaceFromCorners(vertices, normals, uvs, triangles, corners, Vector3.up, jitterAmount, paletteUv);
            if (!InSet(v.x, v.y - 1, v.z)) AddFaceFromCorners(vertices, normals, uvs, triangles, corners, Vector3.down, jitterAmount, paletteUv);
            if (!InSet(v.x, v.y, v.z + 1)) AddFaceFromCorners(vertices, normals, uvs, triangles, corners, Vector3.forward, jitterAmount, paletteUv);
            if (!InSet(v.x, v.y, v.z - 1)) AddFaceFromCorners(vertices, normals, uvs, triangles, corners, Vector3.back, jitterAmount, paletteUv);
        }

        return BuildMeshFromGeometry(vertices, normals, uvs, new[] { triangles });
    }

    /// <summary>立方体min/maxの8頂点(0:min,1:+x,2:+x+y,3:+y,4:+z,5:+x+z,6:+x+y+z,7:+y+z)をそれぞれ独立にジッターして返す</summary>
    private static Vector3[] BuildJitteredCorners(Vector3 min, Vector3 max, float jitterAmount)
    {
        return new[]
        {
            new Vector3(min.x, min.y, min.z) + RandomJitter(jitterAmount),
            new Vector3(max.x, min.y, min.z) + RandomJitter(jitterAmount),
            new Vector3(max.x, max.y, min.z) + RandomJitter(jitterAmount),
            new Vector3(min.x, max.y, min.z) + RandomJitter(jitterAmount),
            new Vector3(min.x, min.y, max.z) + RandomJitter(jitterAmount),
            new Vector3(max.x, min.y, max.z) + RandomJitter(jitterAmount),
            new Vector3(max.x, max.y, max.z) + RandomJitter(jitterAmount),
            new Vector3(min.x, max.y, max.z) + RandomJitter(jitterAmount),
        };
    }

    /// <summary>ジッター済み8頂点から指定方向の面を組み立てる。面の中心を法線方向にずらしたピラミッド状の4三角形に分割する</summary>
    private void AddFaceFromCorners(List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs, List<int> triangles, Vector3[] c, Vector3 dir, float centerJitterAmount, Vector2 paletteUv)
    {
        Vector3 a, b, cc, d;
        if (dir == Vector3.right) { a = c[1]; b = c[2]; cc = c[6]; d = c[5]; }
        else if (dir == Vector3.left) { a = c[4]; b = c[7]; cc = c[3]; d = c[0]; }
        else if (dir == Vector3.up) { a = c[3]; b = c[7]; cc = c[6]; d = c[2]; }
        else if (dir == Vector3.down) { a = c[4]; b = c[0]; cc = c[1]; d = c[5]; }
        else if (dir == Vector3.forward) { a = c[5]; b = c[6]; cc = c[7]; d = c[4]; }
        else { a = c[0]; b = c[3]; cc = c[2]; d = c[1]; } // back

        Vector3 baseNormal = Vector3.Cross(b - a, cc - a).normalized;
        if (baseNormal.sqrMagnitude < 0.0001f) baseNormal = dir;

        Vector3 center = (a + b + cc + d) * 0.25f + baseNormal * Random.Range(-centerJitterAmount, centerJitterAmount);

        AddTriangle(vertices, normals, uvs, triangles, a, b, center, paletteUv);
        AddTriangle(vertices, normals, uvs, triangles, b, cc, center, paletteUv);
        AddTriangle(vertices, normals, uvs, triangles, cc, d, center, paletteUv);
        AddTriangle(vertices, normals, uvs, triangles, d, a, center, paletteUv);
    }

    private void AddTriangle(List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs, List<int> triangles, Vector3 p0, Vector3 p1, Vector3 p2, Vector2 paletteUv)
    {
        Vector3 normal = Vector3.Cross(p1 - p0, p2 - p0).normalized;
        int startIndex = vertices.Count;
        vertices.Add(p0); vertices.Add(p1); vertices.Add(p2);
        normals.Add(normal); normals.Add(normal); normals.Add(normal);
        uvs.Add(paletteUv); uvs.Add(paletteUv); uvs.Add(paletteUv);
        triangles.Add(startIndex); triangles.Add(startIndex + 1); triangles.Add(startIndex + 2);
    }

    /// <summary>ローカル座標を、面の法線に垂直な2軸へbox投影してUVにする(物体全体のバウンディングボックスをUV0〜1に正規化)</summary>
    private Vector2 ProjectUV(Vector3 position, Vector3 faceDir)
    {
        Vector3 size = new Vector3(
            Mathf.Max(0.0001f, SizeX * VoxelSize.x),
            Mathf.Max(0.0001f, SizeY * VoxelSize.y),
            Mathf.Max(0.0001f, SizeZ * VoxelSize.z));
        Vector3 normalized = new Vector3(
            (position.x - Origin.x) / size.x,
            (position.y - Origin.y) / size.y,
            (position.z - Origin.z) / size.z);

        return faceDir == Vector3.up || faceDir == Vector3.down
            ? new Vector2(normalized.x, normalized.z)
            : (faceDir == Vector3.right || faceDir == Vector3.left
                ? new Vector2(normalized.z, normalized.y)
                : new Vector2(normalized.x, normalized.y));
    }

    /// <summary>submeshTriangles[i]をマテリアル添字iのサブメッシュとしてMeshを組み立てる</summary>
    private static Mesh BuildMeshFromGeometry(List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs, List<int>[] submeshTriangles)
    {
        var mesh = new Mesh
        {
            indexFormat = vertices.Count > 65000
                ? UnityEngine.Rendering.IndexFormat.UInt32
                : UnityEngine.Rendering.IndexFormat.UInt16,
        };
        mesh.SetVertices(vertices);
        mesh.SetNormals(normals);
        mesh.SetUVs(0, uvs);
        mesh.subMeshCount = submeshTriangles.Length;
        for (int i = 0; i < submeshTriangles.Length; i++)
        {
            mesh.SetTriangles(submeshTriangles[i], i);
        }
        mesh.RecalculateBounds();
        return mesh;
    }

    private static void GetFaceCorners(Vector3 min, Vector3 max, Vector3 dir, out Vector3 a, out Vector3 b, out Vector3 c, out Vector3 d)
    {
        if (dir == Vector3.right)
        {
            a = new Vector3(max.x, min.y, min.z); b = new Vector3(max.x, max.y, min.z);
            c = new Vector3(max.x, max.y, max.z); d = new Vector3(max.x, min.y, max.z);
        }
        else if (dir == Vector3.left)
        {
            a = new Vector3(min.x, min.y, max.z); b = new Vector3(min.x, max.y, max.z);
            c = new Vector3(min.x, max.y, min.z); d = new Vector3(min.x, min.y, min.z);
        }
        else if (dir == Vector3.up)
        {
            a = new Vector3(min.x, max.y, min.z); b = new Vector3(min.x, max.y, max.z);
            c = new Vector3(max.x, max.y, max.z); d = new Vector3(max.x, max.y, min.z);
        }
        else if (dir == Vector3.down)
        {
            a = new Vector3(min.x, min.y, max.z); b = new Vector3(min.x, min.y, min.z);
            c = new Vector3(max.x, min.y, min.z); d = new Vector3(max.x, min.y, max.z);
        }
        else if (dir == Vector3.forward)
        {
            a = new Vector3(max.x, min.y, max.z); b = new Vector3(max.x, max.y, max.z);
            c = new Vector3(min.x, max.y, max.z); d = new Vector3(min.x, min.y, max.z);
        }
        else // back
        {
            a = new Vector3(min.x, min.y, min.z); b = new Vector3(min.x, max.y, min.z);
            c = new Vector3(max.x, max.y, min.z); d = new Vector3(max.x, min.y, min.z);
        }
    }

    private void AddQuad(List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs, List<int> triangles, Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 normal)
    {
        int startIndex = vertices.Count;
        vertices.Add(a); vertices.Add(b); vertices.Add(c); vertices.Add(d);
        normals.Add(normal); normals.Add(normal); normals.Add(normal); normals.Add(normal);
        uvs.Add(ProjectUV(a, normal)); uvs.Add(ProjectUV(b, normal)); uvs.Add(ProjectUV(c, normal)); uvs.Add(ProjectUV(d, normal));

        triangles.Add(startIndex + 0); triangles.Add(startIndex + 1); triangles.Add(startIndex + 2);
        triangles.Add(startIndex + 0); triangles.Add(startIndex + 2); triangles.Add(startIndex + 3);
    }

    private void AddFace(List<Vector3> vertices, List<Vector3> normals, List<Vector2> uvs, List<int> triangles, Vector3 min, Vector3 max, Vector3 dir)
    {
        GetFaceCorners(min, max, dir, out Vector3 a, out Vector3 b, out Vector3 c, out Vector3 d);
        AddQuad(vertices, normals, uvs, triangles, a, b, c, d, dir);
    }

    private static Vector3 RandomJitter(float amount)
    {
        return new Vector3(Random.Range(-amount, amount), Random.Range(-amount, amount), Random.Range(-amount, amount));
    }

    /// <summary>Flood Fillで連結成分を求め、最大の塊(メインボディ)以外を孤立破片として返す(最大の塊が1個だけなら空リスト)</summary>
    public List<List<Vector3Int>> FindIsolatedIslands()
    {
        var visited = new bool[SizeX * SizeY * SizeZ];
        var islands = new List<List<Vector3Int>>();

        for (int x = 0; x < SizeX; x++)
        {
            for (int y = 0; y < SizeY; y++)
            {
                for (int z = 0; z < SizeZ; z++)
                {
                    if (!Get(x, y, z) || visited[Index(x, y, z)]) continue;
                    islands.Add(FloodFill(x, y, z, visited));
                }
            }
        }

        if (islands.Count <= 1) return new List<List<Vector3Int>>();

        int mainIndex = 0;
        for (int i = 1; i < islands.Count; i++)
        {
            if (islands[i].Count > islands[mainIndex].Count) mainIndex = i;
        }
        islands.RemoveAt(mainIndex);
        return islands;
    }

    /// <summary>最下層(y=0)から連結して届くボクセルを「支えられている」とみなし、届かない連結成分を支えを失った塊として返す</summary>
    public List<List<Vector3Int>> FindUnsupportedIslands()
    {
        var supported = new bool[SizeX * SizeY * SizeZ];
        var queue = new Queue<Vector3Int>();

        for (int x = 0; x < SizeX; x++)
        {
            for (int z = 0; z < SizeZ; z++)
            {
                if (!Get(x, 0, z)) continue;
                int idx = Index(x, 0, z);
                if (supported[idx]) continue;
                supported[idx] = true;
                queue.Enqueue(new Vector3Int(x, 0, z));
            }
        }

        while (queue.Count > 0)
        {
            Vector3Int p = queue.Dequeue();
            TryEnqueueNeighbor(p.x + 1, p.y, p.z, supported, queue);
            TryEnqueueNeighbor(p.x - 1, p.y, p.z, supported, queue);
            TryEnqueueNeighbor(p.x, p.y + 1, p.z, supported, queue);
            TryEnqueueNeighbor(p.x, p.y - 1, p.z, supported, queue);
            TryEnqueueNeighbor(p.x, p.y, p.z + 1, supported, queue);
            TryEnqueueNeighbor(p.x, p.y, p.z - 1, supported, queue);
        }

        var visited = new bool[SizeX * SizeY * SizeZ];
        var islands = new List<List<Vector3Int>>();
        for (int x = 0; x < SizeX; x++)
        {
            for (int y = 0; y < SizeY; y++)
            {
                for (int z = 0; z < SizeZ; z++)
                {
                    int idx = Index(x, y, z);
                    if (!Get(x, y, z) || supported[idx] || visited[idx]) continue;
                    islands.Add(FloodFillMasked(x, y, z, visited, supported));
                }
            }
        }

        return islands;
    }

    private List<Vector3Int> FloodFill(int startX, int startY, int startZ, bool[] visited)
    {
        var island = new List<Vector3Int>();
        var queue = new Queue<Vector3Int>();
        queue.Enqueue(new Vector3Int(startX, startY, startZ));
        visited[Index(startX, startY, startZ)] = true;

        while (queue.Count > 0)
        {
            Vector3Int p = queue.Dequeue();
            island.Add(p);

            TryEnqueueNeighbor(p.x + 1, p.y, p.z, visited, queue);
            TryEnqueueNeighbor(p.x - 1, p.y, p.z, visited, queue);
            TryEnqueueNeighbor(p.x, p.y + 1, p.z, visited, queue);
            TryEnqueueNeighbor(p.x, p.y - 1, p.z, visited, queue);
            TryEnqueueNeighbor(p.x, p.y, p.z + 1, visited, queue);
            TryEnqueueNeighbor(p.x, p.y, p.z - 1, visited, queue);
        }

        return island;
    }

    /// <summary>FloodFillと同じだが、excludeMaskで除外済みのボクセルには侵入しない(FindUnsupportedIslands用)</summary>
    private List<Vector3Int> FloodFillMasked(int startX, int startY, int startZ, bool[] visited, bool[] excludeMask)
    {
        var island = new List<Vector3Int>();
        var queue = new Queue<Vector3Int>();
        queue.Enqueue(new Vector3Int(startX, startY, startZ));
        visited[Index(startX, startY, startZ)] = true;

        while (queue.Count > 0)
        {
            Vector3Int p = queue.Dequeue();
            island.Add(p);

            TryEnqueueNeighborMasked(p.x + 1, p.y, p.z, visited, excludeMask, queue);
            TryEnqueueNeighborMasked(p.x - 1, p.y, p.z, visited, excludeMask, queue);
            TryEnqueueNeighborMasked(p.x, p.y + 1, p.z, visited, excludeMask, queue);
            TryEnqueueNeighborMasked(p.x, p.y - 1, p.z, visited, excludeMask, queue);
            TryEnqueueNeighborMasked(p.x, p.y, p.z + 1, visited, excludeMask, queue);
            TryEnqueueNeighborMasked(p.x, p.y, p.z - 1, visited, excludeMask, queue);
        }

        return island;
    }

    private void TryEnqueueNeighbor(int x, int y, int z, bool[] visited, Queue<Vector3Int> queue)
    {
        if (!Get(x, y, z)) return;
        int idx = Index(x, y, z);
        if (visited[idx]) return;
        visited[idx] = true;
        queue.Enqueue(new Vector3Int(x, y, z));
    }

    private void TryEnqueueNeighborMasked(int x, int y, int z, bool[] visited, bool[] excludeMask, Queue<Vector3Int> queue)
    {
        if (!Get(x, y, z)) return;
        int idx = Index(x, y, z);
        if (excludeMask[idx] || visited[idx]) return;
        visited[idx] = true;
        queue.Enqueue(new Vector3Int(x, y, z));
    }

    /// <summary>指定したボクセル座標群を全て削除する</summary>
    public void RemoveVoxels(List<Vector3Int> voxelCoords)
    {
        foreach (var v in voxelCoords) Set(v.x, v.y, v.z, false);
    }
}
