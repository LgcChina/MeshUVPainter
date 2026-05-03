#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// LGC - Mesh UV Overlay Drawer
/// 仅负责在 GUI 中绘制 Mesh 的 UV 线条叠加
/// </summary>
internal static class LgcMeshUVOverlayDrawer
{
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

        if (uvs == null || uvs.Length == 0 ||
            tris == null || tris.Length == 0)
            return;

        Handles.BeginGUI();
        Handles.color = new Color(
            lineColor.r,
            lineColor.g,
            lineColor.b,
            Mathf.Clamp01(alpha));

        GUI.BeginClip(previewRect);

        float w = previewRect.width;
        float h = previewRect.height;

        Vector3 MapUVToGUI(Vector2 uv)
        {
            float nx = (uv.x - viewRect.xMin) / Mathf.Max(1e-6f, viewRect.width);
            float ny = (uv.y - viewRect.yMin) / Mathf.Max(1e-6f, viewRect.height);

            return new Vector3(
                nx * w,
                (1f - ny) * h,
                0f);
        }

        for (int i = 0; i < tris.Length; i += 3)
        {
            int i0 = tris[i];
            int i1 = tris[i + 1];
            int i2 = tris[i + 2];

            if (i0 < 0 || i1 < 0 || i2 < 0 ||
                i0 >= uvs.Length ||
                i1 >= uvs.Length ||
                i2 >= uvs.Length)
                continue;

            Vector3 a = MapUVToGUI(uvs[i0]);
            Vector3 b = MapUVToGUI(uvs[i1]);
            Vector3 c = MapUVToGUI(uvs[i2]);

            Handles.DrawLine(a, b);
            Handles.DrawLine(b, c);
            Handles.DrawLine(c, a);
        }

        GUI.EndClip();
        Handles.EndGUI();
    }
}
#endif