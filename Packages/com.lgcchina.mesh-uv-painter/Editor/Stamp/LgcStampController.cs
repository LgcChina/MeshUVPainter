#if UNITY_EDITOR
using UnityEditor;
// v2.13.1: Match selection corner buttons and use the same rectangles for drawing and input.
using UnityEngine;

/// <summary>
/// LGC - 盖印图案控制器 (v2.13.1)
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

    private const float HANDLE_SIZE = 20f;

    private int controlId;
    public bool IsEditing => currentMode != EditMode.None;

    public void PrepareInput() => controlId = GUIUtility.GetControlID("LgcStampDrag".GetHashCode(), FocusType.Passive);

    public void CancelInteraction()
    {
        if (controlId != 0 && GUIUtility.hotControl == controlId) GUIUtility.hotControl = 0;
        currentMode = EditMode.None;
    }

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
        if (!settings.ShowPreview || Event.current.type != EventType.Repaint) return;

        Matrix4x4 matrix = GUI.matrix;
        using (new Handles.DrawingScope(Handles.color, Handles.matrix))
        {
            GUI.BeginClip(previewRect);
            try
            {
                float pixelsPerUV = Mathf.Min(previewRect.width / viewRect.width, previewRect.height / viewRect.height);
                float scalePixels = settings.Scale * pixelsPerUV;
                float aspect = (float)settings.StampTexture.width / settings.StampTexture.height;
                float halfW = scalePixels * aspect * 0.5f;
                float halfH = scalePixels * 0.5f;
                Vector2 centerLocal = UVToLocal(settings.UV, previewRect, viewRect);
                DrawControlBox(centerLocal, halfW, halfH, settings.Rotation, new Rect(Vector2.zero, previewRect.size));
            }
            finally { GUI.EndClip(); GUI.matrix = matrix; }
        }
    }

    /// <summary>
    /// 处理鼠标交互（含光标反馈）
    /// </summary>
    public void HandleInput(Rect previewRect, Rect viewRect, LgcUVViewController viewController, LgcSymmetryGizmoController symmetryController)
    {
        Event e = Event.current;
        if (e == null) return;
        if (settings.StampTexture == null || !settings.ShowPreview) { CancelInteraction(); return; }
        if (IsEditing && GUIUtility.hotControl != controlId) { CancelInteraction(); return; }
        if (IsEditing && (e.rawType == EventType.MouseUp || (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)))
        {
            CancelInteraction();
            if (e.type != EventType.Used) e.Use();
            EditorWindow.focusedWindow?.Repaint();
            return;
        }
        if (!IsEditing && (!previewRect.Contains(e.mousePosition) || GUIUtility.hotControl != 0)) return;
        if (e.alt || (e.isMouse && e.button != 0)) return;

        Vector2 localMouse = e.mousePosition - previewRect.position;
        float pixelsPerUV = Mathf.Min(previewRect.width / viewRect.width, previewRect.height / viewRect.height);
        float scalePixels = settings.Scale * pixelsPerUV;
        float aspect = (float)settings.StampTexture.width / settings.StampTexture.height;
        float halfW = scalePixels * aspect * 0.5f;
        float halfH = scalePixels * 0.5f;
        Vector2 centerLocal = UVToLocal(settings.UV, previewRect, viewRect);

        int handle = HandleAt(localMouse, centerLocal, halfW, halfH, settings.Rotation, new Rect(Vector2.zero, previewRect.size));
        // 光标反馈
        MouseCursor cursor = MouseCursor.Arrow;
        if (handle == 3 || IsOnMoveHandle(localMouse, centerLocal, halfW, halfH, settings.Rotation))
            cursor = MouseCursor.MoveArrow;
        else if (handle == 0)
            cursor = MouseCursor.MoveArrow;
        else if (handle == 2)
            cursor = MouseCursor.ResizeUpLeft;
        else if (handle == 1)
            cursor = MouseCursor.Arrow;
        EditorGUIUtility.AddCursorRect(new Rect(e.mousePosition.x - 10, e.mousePosition.y - 10, 20, 20), cursor);

        // ---- 事件处理 ----
        if (e.type == EventType.MouseDown && e.button == 0)
        {
            if (handle == 1)
            {
                settings.StampTexture = null;
                settings.ResetTransform();
                CancelInteraction();
                e.Use();
                EditorWindow.focusedWindow?.Repaint();
                return;
            }

            if (handle == 0)
            {
                GUIUtility.hotControl = controlId;
                currentMode = EditMode.Rotate;
                dragStartMouse = localMouse;
                dragStartRotation = settings.Rotation;
                e.Use();
                return;
            }

            if (handle == 2)
            {
                GUIUtility.hotControl = controlId;
                currentMode = EditMode.Scale;
                dragStartMouse = localMouse;
                dragStartScale = settings.Scale;
                e.Use();
                return;
            }

            if (handle == 3 || IsOnMoveHandle(localMouse, centerLocal, halfW, halfH, settings.Rotation))
            {
                GUIUtility.hotControl = controlId;
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
                    settings.Rotation = dragStartRotation - (angle - startAngle) * Mathf.Rad2Deg;
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
            CancelInteraction();
            e.Use();
            EditorWindow.focusedWindow?.Repaint();
        }
    }

    #region 绘制辅助

    private Vector2[] ControlCorners(Vector2 center, float halfW, float halfH, float rotation)
    {
        Vector2[] corners = { new Vector2(-halfW,-halfH),new Vector2(halfW,-halfH),new Vector2(halfW,halfH),new Vector2(-halfW,halfH) };
        for(int i=0;i<4;i++) corners[i]=center+RotatePoint(corners[i],-rotation);
        return corners;
    }

    private Rect ControlHandleRect(Vector2 corner, Rect canvas)
    {
        return new Rect(Mathf.Clamp(corner.x-HANDLE_SIZE*.5f,canvas.xMin,canvas.xMax-HANDLE_SIZE),
            Mathf.Clamp(corner.y-HANDLE_SIZE*.5f,canvas.yMin,canvas.yMax-HANDLE_SIZE),HANDLE_SIZE,HANDLE_SIZE);
    }

    private void DrawControlBox(Vector2 center, float halfW, float halfH, float rotation, Rect canvas)
    {
        Vector2[] corners=ControlCorners(center,halfW,halfH,rotation);
        Handles.color=Color.cyan;
        Handles.DrawPolyLine(corners[0],corners[1],corners[2],corners[3],corners[0]);
        string[] icons={"↻","✕","↔","✥"};
        string[] keys={"SelectionRotate","StampRemoveHint","SelectionScale","SelectionMove"};
        for(int i=0;i<4;i++)
            GUI.Box(ControlHandleRect(corners[i],canvas),new GUIContent(icons[i],EditorLanguageManager.Instance.GetText(keys[i])),EditorStyles.miniButton);
    }

    #endregion

    #region 碰撞检测

    private int HandleAt(Vector2 mouse, Vector2 center, float halfW, float halfH, float rotation, Rect canvas)
    {
        Vector2[] corners=ControlCorners(center,halfW,halfH,rotation);
        foreach(int i in new[]{1,0,2,3}) if(ControlHandleRect(corners[i],canvas).Contains(mouse))return i;
        return -1;
    }

    private bool IsOnMoveHandle(Vector2 mouse, Vector2 center, float halfW, float halfH, float rotation)
    {
        Vector2 local=RotatePoint(mouse-center,rotation);
        return Mathf.Abs(local.x)<halfW*.6f && Mathf.Abs(local.y)<halfH*.6f;
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