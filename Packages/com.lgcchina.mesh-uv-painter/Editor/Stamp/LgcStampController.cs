#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// LGC - 盖印图案控制器 (v2.6.0)
/// 支持：移动/旋转/缩放/删除，对称预览，图案镜像预览
/// </summary>
internal class LgcStampController
{
    private readonly LgcStampSettings settings;

    private enum EditMode { None, Move, Rotate, Scale }
    private EditMode currentMode = EditMode.None;
    private Vector2 dragStartMouse;
    private Vector2 dragStartUV;
    private float dragStartRotation;
    private float dragStartScale;

    private const float HANDLE_SIZE = 16f;
    private const float ROTATE_HANDLE_SIZE = 20f;
    private const float DELETE_HANDLE_SIZE = 18f;

    public bool IsEditing => currentMode != EditMode.None;

    public LgcStampController(LgcStampSettings settings)
    {
        this.settings = settings;
    }

    /// <summary>
    /// 绘制预览图案和控制框（含对称预览和镜像）
    /// </summary>
    public void DrawPreview(Rect previewRect, Rect viewRect, LgcUVViewController viewController, LgcSymmetryGizmoController symmetryController = null)
    {
        if (settings.StampTexture == null || settings.StampTexture.width <= 0 || settings.StampTexture.height <= 0) return;
        if (!settings.ShowPreview) return;

        Handles.BeginGUI();
        GUI.BeginClip(previewRect);

        float pixelsPerUV = Mathf.Min(previewRect.width / viewRect.width, previewRect.height / viewRect.height);
        float scalePixels = settings.Scale * pixelsPerUV;
        float aspect = (float)settings.StampTexture.width / settings.StampTexture.height;
        float halfW = scalePixels * aspect * 0.5f;
        float halfH = scalePixels * 0.5f;

        Vector2 centerLocal = UVToLocal(settings.UV, previewRect, viewRect);

        // 绘制原图案
        DrawStampAt(centerLocal, scalePixels, settings.Rotation, settings.Opacity * 0.8f, settings.StampTexture);
        DrawControlBox(centerLocal, halfW, halfH, settings.Rotation);

        // 对称预览
        if (settings.EnableSymmetry && symmetryController != null)
        {
            Vector2 mirroredUV = symmetryController.MirrorUV(settings.UV);
            Vector2 mirrorCenterLocal = UVToLocal(mirroredUV, previewRect, viewRect);
            float mirrorRotation = -settings.Rotation;

            if (settings.MirrorPattern)
            {
                DrawStampAtMirrored(mirrorCenterLocal, scalePixels, mirrorRotation, settings.Opacity * 0.5f, settings.StampTexture);
            }
            else
            {
                DrawStampAt(mirrorCenterLocal, scalePixels, mirrorRotation, settings.Opacity * 0.5f, settings.StampTexture);
            }
        }

        GUI.EndClip();
        Handles.EndGUI();
    }

    /// <summary>
    /// 处理鼠标交互（含光标反馈）
    /// </summary>
    public void HandleInput(Rect previewRect, Rect viewRect, LgcUVViewController viewController, LgcSymmetryGizmoController symmetryController)
    {
        if (settings.StampTexture == null || settings.StampTexture.width <= 0 || settings.StampTexture.height <= 0) return;
        if (!settings.ShowPreview) return;

        Event e = Event.current;
        if (e == null) return;
        if (!previewRect.Contains(e.mousePosition)) return;

        Vector2 localMouse = e.mousePosition - previewRect.position;
        float pixelsPerUV = Mathf.Min(previewRect.width / viewRect.width, previewRect.height / viewRect.height);
        float scalePixels = settings.Scale * pixelsPerUV;
        float aspect = (float)settings.StampTexture.width / settings.StampTexture.height;
        float halfW = scalePixels * aspect * 0.5f;
        float halfH = scalePixels * 0.5f;
        Vector2 centerLocal = UVToLocal(settings.UV, previewRect, viewRect);

        // 光标反馈
        MouseCursor cursor = MouseCursor.Arrow;
        if (IsOnMoveHandle(localMouse, centerLocal, halfW, halfH, settings.Rotation))
            cursor = MouseCursor.MoveArrow;
        else if (IsOnRotateHandle(localMouse, centerLocal, halfW, halfH, settings.Rotation))
            cursor = MouseCursor.MoveArrow;
        else if (IsOnScaleHandle(localMouse, centerLocal, halfW, halfH, settings.Rotation))
            cursor = MouseCursor.ResizeUpLeft;
        else if (IsOnDeleteHandle(localMouse, centerLocal, halfW, halfH, settings.Rotation))
            cursor = MouseCursor.Arrow;
        EditorGUIUtility.AddCursorRect(new Rect(e.mousePosition.x - 10, e.mousePosition.y - 10, 20, 20), cursor);

        // ---- 事件处理 ----
        if (e.type == EventType.MouseDown && e.button == 0)
        {
            if (IsOnDeleteHandle(localMouse, centerLocal, halfW, halfH, settings.Rotation))
            {
                settings.StampTexture = null;
                settings.ResetTransform();
                currentMode = EditMode.None;
                e.Use();
                EditorWindow.focusedWindow?.Repaint();
                return;
            }

            if (IsOnRotateHandle(localMouse, centerLocal, halfW, halfH, settings.Rotation))
            {
                currentMode = EditMode.Rotate;
                dragStartMouse = localMouse;
                dragStartRotation = settings.Rotation;
                e.Use();
                return;
            }

            if (IsOnScaleHandle(localMouse, centerLocal, halfW, halfH, settings.Rotation))
            {
                currentMode = EditMode.Scale;
                dragStartMouse = localMouse;
                dragStartScale = settings.Scale;
                e.Use();
                return;
            }

            if (IsOnMoveHandle(localMouse, centerLocal, halfW, halfH, settings.Rotation))
            {
                currentMode = EditMode.Move;
                dragStartMouse = localMouse;
                dragStartUV = settings.UV;
                e.Use();
                return;
            }
        }

        if (e.type == EventType.MouseDrag && currentMode != EditMode.None)
        {
            Vector2 delta = localMouse - dragStartMouse;

            switch (currentMode)
            {
                case EditMode.Move:
                    Vector2 uvDelta = GUIToUVDelta(delta, previewRect, viewRect);
                    Vector2 newUV = ClampStampUV(dragStartUV + uvDelta, settings.Scale, settings.StampTexture);
                    settings.UV = newUV;
                    break;

                case EditMode.Rotate:
                    float angle = Mathf.Atan2(localMouse.y - centerLocal.y, localMouse.x - centerLocal.x);
                    float startAngle = Mathf.Atan2(dragStartMouse.y - centerLocal.y, dragStartMouse.x - centerLocal.x);
                    settings.Rotation = dragStartRotation + (angle - startAngle) * Mathf.Rad2Deg;
                    settings.UV = ClampStampUV(settings.UV, settings.Scale, settings.StampTexture);
                    break;

                case EditMode.Scale:
                    float startDist = Vector2.Distance(dragStartMouse, centerLocal);
                    float currentDist = Vector2.Distance(localMouse, centerLocal);
                    if (startDist > 1f)
                    {
                        float scaleFactor = currentDist / startDist;
                        float maxScale = CalculateMaxScale(settings.StampTexture);
                        settings.Scale = Mathf.Clamp(dragStartScale * scaleFactor, 0.01f, maxScale);
                        settings.UV = ClampStampUV(settings.UV, settings.Scale, settings.StampTexture);
                    }
                    break;
            }

            e.Use();
            EditorWindow.focusedWindow?.Repaint();
        }

        if (e.type == EventType.MouseUp && currentMode != EditMode.None)
        {
            currentMode = EditMode.None;
            e.Use();
            EditorWindow.focusedWindow?.Repaint();
        }
    }

    #region 绘制辅助

    private void DrawStampAt(Vector2 center, float scale, float rotation, float alpha, Texture2D tex)
    {
        if (tex == null) return;
        float aspect = (float)tex.width / tex.height;
        float w = scale * aspect;
        float h = scale;
        float halfW = w * 0.5f;
        float halfH = h * 0.5f;

        Matrix4x4 originalMatrix = GUI.matrix;
        Vector2 pivot = center;
        GUIUtility.RotateAroundPivot(rotation, pivot);

        Rect uvRect = new Rect(center.x - halfW, center.y - halfH, w, h);
        Color oldColor = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, alpha);
        GUI.DrawTexture(uvRect, tex);
        GUI.color = oldColor;

        GUI.matrix = originalMatrix;
    }

    /// <summary>
    /// ★ 绘制水平翻转的图案（镜像）- 使用 DrawTextureWithTexCoords，稳定且与GPU一致
    /// </summary>
    private void DrawStampAtMirrored(Vector2 center, float scale, float rotation, float alpha, Texture2D tex)
    {
        if (tex == null) return;
        float aspect = (float)tex.width / tex.height;
        float w = scale * aspect;
        float h = scale;
        float halfW = w * 0.5f;
        float halfH = h * 0.5f;

        Rect rect = new Rect(center.x - halfW, center.y - halfH, w, h);

        Matrix4x4 oldMatrix = GUI.matrix;
        // 先旋转（绕中心）
        GUIUtility.RotateAroundPivot(rotation, center);

        Color oldColor = GUI.color;
        GUI.color = new Color(1f, 1f, 1f, alpha);

        // ★ 水平翻转：纹理坐标 U 从 1→0，V 保持 0→1
        GUI.DrawTextureWithTexCoords(rect, tex, new Rect(1f, 0f, -1f, 1f));

        GUI.color = oldColor;
        GUI.matrix = oldMatrix;
    }

    private void DrawControlBox(Vector2 center, float halfW, float halfH, float rotation)
    {
        Vector2[] corners = new Vector2[4];
        corners[0] = new Vector2(-halfW, -halfH);
        corners[1] = new Vector2(halfW, -halfH);
        corners[2] = new Vector2(halfW, halfH);
        corners[3] = new Vector2(-halfW, halfH);

        Matrix4x4 rotMat = Matrix4x4.Rotate(Quaternion.Euler(0, 0, rotation));
        for (int i = 0; i < 4; i++)
            corners[i] = center + (Vector2)(rotMat * (Vector3)corners[i]);

        Handles.color = new Color(0.2f, 0.8f, 1f, 0.5f);
        Handles.DrawPolyLine(corners[0], corners[1], corners[2], corners[3], corners[0]);

        float handleSize = Mathf.Clamp(Mathf.Min(halfW, halfH) * 0.3f, 8f, 24f);

        DrawHandle(corners[0], handleSize, "↻", Color.yellow);
        DrawHandle(corners[1], handleSize, "✕", Color.red);
        DrawHandle(corners[2], handleSize, "◢", Color.green);
    }

    private void DrawHandle(Vector2 position, float size, string symbol, Color color)
    {
        Handles.color = color;
        Handles.DrawSolidDisc(position, Vector3.forward, size * 0.5f);
        Handles.color = Color.white;
        GUI.Label(new Rect(position.x - size * 0.5f, position.y - size * 0.5f, size, size), symbol, EditorStyles.boldLabel);
    }

    #endregion

    #region 碰撞检测

    private bool IsOnMoveHandle(Vector2 mouse, Vector2 center, float halfW, float halfH, float rotation)
    {
        Vector2 local = RotatePoint(mouse - center, -rotation);
        float moveW = halfW * 0.6f;
        float moveH = halfH * 0.6f;
        return Mathf.Abs(local.x) < moveW && Mathf.Abs(local.y) < moveH;
    }

    private bool IsOnRotateHandle(Vector2 mouse, Vector2 center, float halfW, float halfH, float rotation)
    {
        Vector2 local = RotatePoint(mouse - center, -rotation);
        Vector2 handlePos = new Vector2(-halfW, -halfH);
        float threshold = Mathf.Max(20f, Mathf.Min(halfW, halfH) * 0.2f);
        return (local - handlePos).sqrMagnitude < threshold * threshold;
    }

    private bool IsOnScaleHandle(Vector2 mouse, Vector2 center, float halfW, float halfH, float rotation)
    {
        Vector2 local = RotatePoint(mouse - center, -rotation);
        Vector2 handlePos = new Vector2(halfW, halfH);
        float threshold = Mathf.Max(20f, Mathf.Min(halfW, halfH) * 0.2f);
        return (local - handlePos).sqrMagnitude < threshold * threshold;
    }

    private bool IsOnDeleteHandle(Vector2 mouse, Vector2 center, float halfW, float halfH, float rotation)
    {
        Vector2 local = RotatePoint(mouse - center, -rotation);
        Vector2 handlePos = new Vector2(halfW, -halfH);
        float threshold = Mathf.Max(20f, Mathf.Min(halfW, halfH) * 0.2f);
        return (local - handlePos).sqrMagnitude < threshold * threshold;
    }

    #endregion

    #region 工具函数

    private Vector2 UVToLocal(Vector2 uv, Rect previewRect, Rect viewRect)
    {
        float nx = (uv.x - viewRect.xMin) / viewRect.width;
        float ny = (uv.y - viewRect.yMin) / viewRect.height;
        return new Vector2(nx * previewRect.width, (1f - ny) * previewRect.height);
    }

    private Vector2 GUIToUVDelta(Vector2 guiDelta, Rect previewRect, Rect viewRect)
    {
        float du = (guiDelta.x / previewRect.width) * viewRect.width;
        float dv = -(guiDelta.y / previewRect.height) * viewRect.height;
        return new Vector2(du, dv);
    }

    private Vector2 ClampStampUV(Vector2 uv, float scale, Texture2D tex)
    {
        if (tex == null) return uv;

        float aspect = (float)tex.width / tex.height;
        float halfW = scale * aspect * 0.5f;
        float halfH = scale * 0.5f;

        Vector2[] corners = new Vector2[4];
        corners[0] = new Vector2(-halfW, -halfH);
        corners[1] = new Vector2(halfW, -halfH);
        corners[2] = new Vector2(halfW, halfH);
        corners[3] = new Vector2(-halfW, halfH);

        float rad = settings.Rotation * Mathf.Deg2Rad;
        float cos = Mathf.Cos(rad);
        float sin = Mathf.Sin(rad);
        for (int i = 0; i < 4; i++)
        {
            float x = corners[i].x;
            float y = corners[i].y;
            corners[i].x = x * cos - y * sin;
            corners[i].y = x * sin + y * cos;
        }

        float minX = Mathf.Min(corners[0].x, corners[1].x, corners[2].x, corners[3].x);
        float maxX = Mathf.Max(corners[0].x, corners[1].x, corners[2].x, corners[3].x);
        float minY = Mathf.Min(corners[0].y, corners[1].y, corners[2].y, corners[3].y);
        float maxY = Mathf.Max(corners[0].y, corners[1].y, corners[2].y, corners[3].y);

        float clampMinX = -minX;
        float clampMaxX = 1f - maxX;
        float clampMinY = -minY;
        float clampMaxY = 1f - maxY;

        if (clampMinX > clampMaxX)
            uv.x = 0.5f;
        else
            uv.x = Mathf.Clamp(uv.x, clampMinX, clampMaxX);

        if (clampMinY > clampMaxY)
            uv.y = 0.5f;
        else
            uv.y = Mathf.Clamp(uv.y, clampMinY, clampMaxY);

        return uv;
    }

    private float CalculateMaxScale(Texture2D tex)
    {
        if (tex == null) return 2f;
        float aspect = (float)tex.width / tex.height;
        float maxByAspect = 0.8f / Mathf.Max(aspect, 1f);
        float maxByHeight = 0.8f;
        return Mathf.Min(maxByAspect, maxByHeight);
    }

    private Vector2 RotatePoint(Vector2 point, float angleDeg)
    {
        float rad = angleDeg * Mathf.Deg2Rad;
        float cos = Mathf.Cos(rad);
        float sin = Mathf.Sin(rad);
        return new Vector2(point.x * cos - point.y * sin, point.x * sin + point.y * cos);
    }

    #endregion
}
#endif