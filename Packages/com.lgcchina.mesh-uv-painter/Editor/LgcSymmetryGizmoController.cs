#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// LGC - 对称 Gizmo 控制器
/// 负责对称线的绘制、拖拽，以及 UV 镜像计算
/// </summary>
internal class LgcSymmetryGizmoController
{
    public Vector2 SymA { get; private set; }
    public Vector2 SymB { get; private set; }

    private bool draggingA;
    private bool draggingB;
    private bool draggingLine;

    public LgcSymmetryGizmoController()
    {
        Reset();
    }

    public void Reset()
    {
        SymA = new Vector2(0.5f, 0f);
        SymB = new Vector2(0.5f, 1f);
    }

    /// <summary>
    /// 主调用入口：绘制并处理交互
    /// </summary>
    public void DrawAndHandle(
        Rect previewRect,
        Rect viewRect,
        bool locked,
        float zoom,
        bool repaintOnChange = true)
    {
        Event e = Event.current;
        if (e == null) return;

        Vector2 aGui = UVToGUI(SymA, previewRect, viewRect);
        Vector2 bGui = UVToGUI(SymB, previewRect, viewRect);

        float handleBase = 14f;
        float handleRadius = handleBase * Mathf.Clamp(Mathf.Sqrt(Mathf.Max(1f, zoom)), 1f, 2f);

        HandleCursorFeedback(e, aGui, bGui, handleRadius);

        if (!locked)
        {
            HandleMouseInput(
                e, previewRect, viewRect,
                aGui, bGui,
                handleRadius,
                repaintOnChange);
        }

        DrawGizmo(aGui, bGui, handleRadius, locked);
    }

    #region Draw

    private void DrawGizmo(Vector2 a, Vector2 b, float r, bool locked)
    {
        Handles.BeginGUI();
        Handles.color = new Color(0.2f, 0.8f, 1f, locked ? 0.6f : 0.9f);

        Handles.DrawAAPolyLine(3f, a, b);
        DrawHandle(a, r);
        DrawHandle(b, r);

        Handles.EndGUI();
    }

    private static void DrawHandle(Vector2 center, float r)
    {
        Color main = Handles.color;
        Color outline = new Color(main.r, main.g, main.b, 0.25f);

        Handles.color = outline;
        Handles.DrawSolidDisc(center, Vector3.forward, r * 0.95f);

        Handles.color = main;
        Handles.DrawSolidDisc(center, Vector3.forward, r * 0.6f);
    }

    #endregion

    #region Input

    private void HandleCursorFeedback(Event e, Vector2 a, Vector2 b, float r)
    {
        Vector2 m = e.mousePosition;

        bool nearA = (m - a).sqrMagnitude <= r * r;
        bool nearB = (m - b).sqrMagnitude <= r * r;
        bool nearLine = DistancePointToSegment(m, a, b) <= r;

        if (nearA || nearB)
        {
            EditorGUIUtility.AddCursorRect(
                new Rect(m.x - r, m.y - r, r * 2, r * 2),
                MouseCursor.ResizeUpLeft);
        }
        else if (nearLine)
        {
            EditorGUIUtility.AddCursorRect(
                new Rect(
                    Mathf.Min(a.x, b.x) - r,
                    Mathf.Min(a.y, b.y) - r,
                    Mathf.Abs(a.x - b.x) + r * 2,
                    Mathf.Abs(a.y - b.y) + r * 2),
                MouseCursor.MoveArrow);
        }
    }

    private void HandleMouseInput(
        Event e,
        Rect guiRect,
        Rect viewRect,
        Vector2 aGui,
        Vector2 bGui,
        float r,
        bool repaint)
    {
        if (!guiRect.Contains(e.mousePosition)) return;

        if (e.type == EventType.MouseDown && e.button == 0)
        {
            if ((e.mousePosition - aGui).sqrMagnitude <= r * r)
                draggingA = true;
            else if ((e.mousePosition - bGui).sqrMagnitude <= r * r)
                draggingB = true;
            else if (DistancePointToSegment(e.mousePosition, aGui, bGui) <= r)
                draggingLine = true;

            if (draggingA || draggingB || draggingLine)
                e.Use();
        }
        else if (e.type == EventType.MouseDrag && (draggingA || draggingB || draggingLine))
        {
            Vector2 uvDelta = GUIToUVDelta(e.delta, guiRect, viewRect);

            if (draggingA) SymA = Clamp01(SymA + uvDelta);
            else if (draggingB) SymB = Clamp01(SymB + uvDelta);
            else
            {
                SymA = Clamp01(SymA + uvDelta);
                SymB = Clamp01(SymB + uvDelta);
            }

            e.Use();
            if (repaint) EditorWindow.focusedWindow?.Repaint();
        }
        else if (e.type == EventType.MouseUp)
        {
            draggingA = draggingB = draggingLine = false;
        }
    }

    #endregion

    #region Utils

    public Vector2 MirrorUV(Vector2 uv)
    {
        Vector2 ab = SymB - SymA;
        float len2 = ab.sqrMagnitude;
        if (len2 < 1e-8f) return uv;

        float t = Vector2.Dot(uv - SymA, ab) / len2;
        Vector2 proj = SymA + t * ab;
        return proj + (proj - uv);
    }

    private static Vector2 UVToGUI(Vector2 uv, Rect rect, Rect viewRect)
    {
        float nx = (uv.x - viewRect.xMin) / viewRect.width;
        float ny = (uv.y - viewRect.yMin) / viewRect.height;

        return new Vector2(
            rect.x + nx * rect.width,
            rect.y + (1f - ny) * rect.height);
    }

    private static Vector2 GUIToUVDelta(Vector2 guiDelta, Rect rect, Rect viewRect)
    {
        float du = (guiDelta.x / rect.width) * viewRect.width;
        float dv = -(guiDelta.y / rect.height) * viewRect.height;
        return new Vector2(du, dv);
    }

    private static Vector2 Clamp01(Vector2 v)
    {
        v.x = Mathf.Clamp01(v.x);
        v.y = Mathf.Clamp01(v.y);
        return v;
    }

    private static float DistancePointToSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float t = Vector2.Dot(p - a, ab) / Mathf.Max(1e-6f, ab.sqrMagnitude);
        t = Mathf.Clamp01(t);
        return Vector2.Distance(p, a + t * ab);
    }

    #endregion
}
#endif