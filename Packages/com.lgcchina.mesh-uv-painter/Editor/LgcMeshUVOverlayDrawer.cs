#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// LGC - Mesh UV Overlay Drawer (v2.7.0)
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
        public int[] cachedActiveTriangles;
        public int[] cachedInactiveTriangles;
        public Vector3[] activeLinePoints;    // 当前材质组
        public Vector3[] inactiveLinePoints;  // 全部子网格模式下弱化显示
        public bool valid = false;
    }

    private static UVOverlayCache cache = new UVOverlayCache();

    public static void DrawUVOverlay(
        Rect previewRect,
        Rect viewRect,
        Mesh mesh,
        int[] activeTriangles,
        int[] inactiveTriangles,
        Color lineColor,
        float alpha)
    {
        if (mesh == null) return;

        var uvs = mesh.uv;

        if (uvs == null || uvs.Length == 0 ||
            (activeTriangles == null || activeTriangles.Length == 0) &&
            (inactiveTriangles == null || inactiveTriangles.Length == 0))
            return;

        // ★ 检查缓存是否有效
        bool cacheValid = cache.valid &&
                         cache.cachedMesh == mesh &&
                         RectEquals(cache.cachedViewRect, viewRect) &&
                         RectEquals(cache.cachedPreviewRect, previewRect) &&
                         object.ReferenceEquals(cache.cachedActiveTriangles, activeTriangles) &&
                         object.ReferenceEquals(cache.cachedInactiveTriangles, inactiveTriangles);

        // ★ 如果缓存无效，重建
        if (!cacheValid)
        {
            RebuildCache(mesh, uvs, activeTriangles, inactiveTriangles, previewRect, viewRect);
        }

        // ★ 用 Handles.DrawLines 批量绘制
        DrawCachedLines(previewRect, lineColor, alpha);
    }

    /// <summary>
    /// 重建坐标缓存
    /// </summary>
    private static void RebuildCache(Mesh mesh, Vector2[] uvs,
                                     int[] activeTriangles,
                                     int[] inactiveTriangles,
                                     Rect previewRect, Rect viewRect)
    {
        float w = previewRect.width;
        float h = previewRect.height;

        Vector3[] points = new Vector3[uvs.Length];
        bool[] pointReady = new bool[uvs.Length];

        Vector3 GetPoint(int vertexIndex)
        {
            if (!pointReady[vertexIndex])
            {
                Vector2 uv = uvs[vertexIndex];
                float nx = (uv.x - viewRect.xMin) / Mathf.Max(1e-6f, viewRect.width);
                float ny = (uv.y - viewRect.yMin) / Mathf.Max(1e-6f, viewRect.height);
                points[vertexIndex] = new Vector3(nx * w, (1f - ny) * h, 0f);
                pointReady[vertexIndex] = true;
            }
            return points[vertexIndex];
        }

        Vector3[] BuildLines(int[] tris)
        {
            if (tris == null || tris.Length < 3) return System.Array.Empty<Vector3>();
            System.Collections.Generic.List<Vector3> lineVertices =
                new System.Collections.Generic.List<Vector3>((tris.Length / 3) * 6);

            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                int i0 = tris[i];
                int i1 = tris[i + 1];
                int i2 = tris[i + 2];
                if (i0 < 0 || i1 < 0 || i2 < 0 ||
                    i0 >= uvs.Length || i1 >= uvs.Length || i2 >= uvs.Length)
                    continue;

                Vector3 p0 = GetPoint(i0);
                Vector3 p1 = GetPoint(i1);
                Vector3 p2 = GetPoint(i2);
                lineVertices.Add(p0); lineVertices.Add(p1);
                lineVertices.Add(p1); lineVertices.Add(p2);
                lineVertices.Add(p2); lineVertices.Add(p0);
            }
            return lineVertices.ToArray();
        }

        // ★ 更新缓存
        cache.cachedMesh = mesh;
        cache.cachedViewRect = viewRect;
        cache.cachedPreviewRect = previewRect;
        cache.cachedActiveTriangles = activeTriangles;
        cache.cachedInactiveTriangles = inactiveTriangles;
        cache.activeLinePoints = BuildLines(activeTriangles);
        cache.inactiveLinePoints = BuildLines(inactiveTriangles);
        cache.valid = true;
    }

    /// <summary>
    /// 用 Handles.DrawLines 批量绘制（关键修复：不要重复添加previewRect偏移）
    /// </summary>
    private static void DrawCachedLines(Rect previewRect, Color lineColor, float alpha)
    {
        if (!cache.valid)
            return;

        Handles.BeginGUI();
        GUI.BeginClip(previewRect);

        // GUI.BeginClip已经做了坐标系变换，坐标原点现在是previewRect的左上角
        if (cache.inactiveLinePoints != null && cache.inactiveLinePoints.Length > 0)
        {
            Color inactive = lineColor;
            inactive.a = Mathf.Clamp01(alpha) * 0.22f;
            Handles.color = inactive;
            Handles.DrawLines(cache.inactiveLinePoints);
        }

        if (cache.activeLinePoints != null && cache.activeLinePoints.Length > 0)
        {
            Handles.color = new Color(lineColor.r, lineColor.g, lineColor.b, Mathf.Clamp01(alpha));
            Handles.DrawLines(cache.activeLinePoints);
        }

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
        cache.cachedMesh = null;
        cache.cachedActiveTriangles = null;
        cache.cachedInactiveTriangles = null;
        cache.activeLinePoints = null;
        cache.inactiveLinePoints = null;
    }
}
#endif
