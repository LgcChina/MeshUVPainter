#if UNITY_EDITOR
// v2.10.0: Configurable right-button pan with pointer capture and cancellation.
using UnityEngine;
using UnityEditor;

/// <summary>
/// LGC - UV 视图控制器
/// 负责：缩放、平移、视口计算、GUI ↔ UV 坐标转换
/// 不涉及 GPU、Undo、绘制逻辑
/// </summary>
internal class LgcUVViewController
{
    public float Zoom { get; private set; } = 1f;
    public Vector2 ViewCenter { get; private set; } = new Vector2(0.5f, 0.5f);

    public void ResetView()
    {
        Zoom = 1f;
        ViewCenter = new Vector2(0.5f, 0.5f);
    }

    #region View Rect

    public Rect GetViewRect()
    {
        float size = 1f / Mathf.Max(1f, Zoom);
        float half = size * 0.5f;

        float cx = Mathf.Clamp(ViewCenter.x, half, 1f - half);
        float cy = Mathf.Clamp(ViewCenter.y, half, 1f - half);

        return new Rect(cx - half, cy - half, size, size);
    }

    public void SetViewFromRect(Rect viewRect)
    {
        float size = Mathf.Clamp(viewRect.width, 1e-6f, 1f);
        Zoom = 1f / size;
        ViewCenter = viewRect.center;
    }

    public void ClampView()
    {
        SetViewFromRect(GetViewRect());
    }

    #endregion

    #region Navigation

    private int panControlId;
    private int panButton = -1;

    public void HandleNavigation(Rect previewRect, bool rightButtonPan = false)
    {
        panControlId = GUIUtility.GetControlID("LgcCanvasPan".GetHashCode(), FocusType.Passive);
        Event e = Event.current;
        if (e == null) return;
        if (panButton >= 0 && (GUIUtility.hotControl != panControlId || e.rawType == EventType.MouseUp || (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)))
        {
            CancelNavigation();
            if (e.type != EventType.Used) e.Use();
            return;
        }
        bool inside = previewRect.Contains(e.mousePosition);
        if (inside && e.type == EventType.ScrollWheel && GUIUtility.hotControl == 0)
        {
            HandleZoom(e, previewRect); e.Use(); EditorWindow.focusedWindow?.Repaint(); return;
        }
        bool panGesture = e.button == 2 || (e.button == 0 && e.alt) || (e.button == 1 && rightButtonPan);
        if (inside && panGesture && e.type == EventType.MouseDown && GUIUtility.hotControl == 0)
        {
            panButton = e.button; GUIUtility.hotControl = panControlId; e.Use();
        }
        else if (panButton == e.button && GUIUtility.hotControl == panControlId && e.type == EventType.MouseDrag)
        {
            HandlePan(e, previewRect); e.Use(); EditorWindow.focusedWindow?.Repaint();
        }
    }

    public void CancelNavigation()
    {
        if (panControlId != 0 && GUIUtility.hotControl == panControlId) GUIUtility.hotControl = 0;
        panButton = -1;
    }

    private void HandleZoom(Event e, Rect previewRect)
    {
        Rect vr = GetViewRect();

        Vector2 local = e.mousePosition - previewRect.position;
        Vector2 local01 = new Vector2(
            local.x / Mathf.Max(1, previewRect.width),
            local.y / Mathf.Max(1, previewRect.height));

        Vector2 uvPivot = new Vector2(
            vr.xMin + local01.x * vr.width,
            vr.yMax - local01.y * vr.height);

        float factor = Mathf.Pow(1.1f, -e.delta.y);
        float newZoom = Mathf.Clamp(Zoom * factor, 1f, 64f);
        float newSize = 1f / newZoom;

        Rect newVR = new Rect(
            uvPivot.x - local01.x * newSize,
            uvPivot.y + local01.y * newSize - newSize,
            newSize,
            newSize);

        SetViewFromRect(newVR);
        ClampView();
    }

    private void HandlePan(Event e, Rect previewRect)
    {
        Rect vr = GetViewRect();

        float dxUv = e.delta.x / previewRect.width * vr.width;
        float dyUv = e.delta.y / previewRect.height * vr.height;

        ViewCenter = new Vector2(
            ViewCenter.x - dxUv,
            ViewCenter.y + dyUv);

        ClampView();
    }

    #endregion

    #region Coordinate Utils

    public Vector2 GUIToUV(Vector2 guiPos, Rect previewRect)
    {
        Rect vr = GetViewRect();

        Vector2 local = guiPos - previewRect.position;
        Vector2 local01 = new Vector2(
            Mathf.Clamp01(local.x / previewRect.width),
            Mathf.Clamp01(local.y / previewRect.height));

        return new Vector2(
            vr.xMin + local01.x * vr.width,
            vr.yMax - local01.y * vr.height);
    }

    public Vector2 GUIToUVDelta(Vector2 guiDelta, Rect previewRect)
    {
        Rect vr = GetViewRect();

        return new Vector2(
            (guiDelta.x / previewRect.width) * vr.width,
            -(guiDelta.y / previewRect.height) * vr.height);
    }

    public Vector2 UVToGUI(Vector2 uv, Rect previewRect)
    {
        Rect vr = GetViewRect();

        float nx = (uv.x - vr.xMin) / vr.width;
        float ny = (uv.y - vr.yMin) / vr.height;

        return new Vector2(
            previewRect.x + nx * previewRect.width,
            previewRect.y + (1f - ny) * previewRect.height);
    }

    #endregion
}
#endif