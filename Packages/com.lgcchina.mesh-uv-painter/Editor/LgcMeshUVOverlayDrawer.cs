#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// LGC - Mesh UV Overlay Drawer
/// 修复版：正确处理坐标空间
/// </summary>
internal static class LgcMeshUVOverlayDrawer
{
    // ★ 缓存数据结构
    private class UVOverlayCache
    {
        public Mesh cachedMesh;
        public Rect cachedViewRect;
        public Rect cachedPreviewRect;
        public Vector3[] linePoints;          // 本地坐标（相对于previewRect的左上角）
        public bool valid = false;
    }

    private static UVOverlayCache cache = new UVOverlayCache();

    public static void DrawUVOverlay(
        Rect previewRect,
        Rect viewRect,
        Mesh mesh,
        Color lineColor,
        float alpha)
    {
        if (mesh == null) return;

        var uvs = mesh.uv;
        var tris = mesh.triangles;

        if (uvs == null || uvs.Length == 0 || tris == null || tris.Length == 0)
            return;

        // ★ 检查缓存是否有效
        bool cacheValid = cache.valid &&
                         cache.cachedMesh == mesh &&
                         RectEquals(cache.cachedViewRect, viewRect) &&
                         RectEquals(cache.cachedPreviewRect, previewRect);

        // ★ 如果缓存无效，重建
        if (!cacheValid)
        {
            RebuildCache(mesh, uvs, tris, previewRect, viewRect);
        }

        // ★ 用 Handles.DrawLines 批量绘制
        DrawCachedLines(previewRect, lineColor, alpha);
    }

    /// <summary>
    /// 重建坐标缓存
    /// </summary>
    private static void RebuildCache(Mesh mesh, Vector2[] uvs, int[] tris,
                                     Rect previewRect, Rect viewRect)
    {
        float w = previewRect.width;
        float h = previewRect.height;

        System.Collections.Generic.List<Vector3> points =
            new System.Collections.Generic.List<Vector3>(uvs.Length);
        System.Collections.Generic.Dictionary<int, int> vertexIndexMap =
            new System.Collections.Generic.Dictionary<int, int>();

        // ★ 去重：同一个UV顶点只计算一次
        for (int i = 0; i < tris.Length; i++)
        {
            int triIdx = tris[i];
            if (triIdx < 0 || triIdx >= uvs.Length)
                continue;

            if (!vertexIndexMap.ContainsKey(triIdx))
            {
                Vector2 uv = uvs[triIdx];
                float nx = (uv.x - viewRect.xMin) / Mathf.Max(1e-6f, viewRect.width);
                float ny = (uv.y - viewRect.yMin) / Mathf.Max(1e-6f, viewRect.height);

                // ★ 坐标计算在本地空间（相对于previewRect的左上角）
                points.Add(new Vector3(nx * w, (1f - ny) * h, 0f));
                vertexIndexMap[triIdx] = points.Count - 1;
            }
        }

        // ★ 构建线段顶点数组
        System.Collections.Generic.List<Vector3> lineVertices =
            new System.Collections.Generic.List<Vector3>();

        for (int i = 0; i < tris.Length; i += 3)
        {
            int i0 = tris[i];
            int i1 = tris[i + 1];
            int i2 = tris[i + 2];

            if (i0 < 0 || i1 < 0 || i2 < 0 ||
                i0 >= uvs.Length || i1 >= uvs.Length || i2 >= uvs.Length)
                continue;

            int pi0 = vertexIndexMap[i0];
            int pi1 = vertexIndexMap[i1];
            int pi2 = vertexIndexMap[i2];

            // 三条边
            lineVertices.Add(points[pi0]);
            lineVertices.Add(points[pi1]);

            lineVertices.Add(points[pi1]);
            lineVertices.Add(points[pi2]);

            lineVertices.Add(points[pi2]);
            lineVertices.Add(points[pi0]);
        }

        // ★ 更新缓存
        cache.cachedMesh = mesh;
        cache.cachedViewRect = viewRect;
        cache.cachedPreviewRect = previewRect;
        cache.linePoints = lineVertices.ToArray();
        cache.valid = true;
    }

    /// <summary>
    /// 用 Handles.DrawLines 批量绘制（关键修复：不要重复添加previewRect偏移）
    /// </summary>
    private static void DrawCachedLines(Rect previewRect, Color lineColor, float alpha)
    {
        if (!cache.valid || cache.linePoints == null || cache.linePoints.Length == 0)
            return;

        Handles.BeginGUI();
        Handles.color = new Color(lineColor.r, lineColor.g, lineColor.b, Mathf.Clamp01(alpha));

        GUI.BeginClip(previewRect);

        // GUI.BeginClip已经做了坐标系变换，坐标原点现在是previewRect的左上角
        Handles.DrawLines(cache.linePoints);

        GUI.EndClip();
        Handles.EndGUI();
    }

    /// <summary>
    /// Rect 相等性检查
    /// </summary>
    private static bool RectEquals(Rect a, Rect b)
    {
        return Mathf.Abs(a.x - b.x) < 0.001f &&
               Mathf.Abs(a.y - b.y) < 0.001f &&
               Mathf.Abs(a.width - b.width) < 0.001f &&
               Mathf.Abs(a.height - b.height) < 0.001f;
    }

    /// <summary>
    /// 手动清理缓存
    /// </summary>
    public static void ClearCache()
    {
        cache.valid = false;
        cache.linePoints = null;
    }
}
#endif
