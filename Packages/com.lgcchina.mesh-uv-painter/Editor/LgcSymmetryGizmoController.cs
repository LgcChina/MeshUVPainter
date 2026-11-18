#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

/// <summary>
/// LGC - �Գ� Gizmo ������
/// ����Գ��ߵĻ��ơ���ק���Լ� UV �������
/// v2.7.0: ���� GUI.BeginClip(previewRect) �ü�����ֹ���Ƴ���Ԥ����
/// </summary>
internal class LgcSymmetryGizmoController
{
    public Vector2 SymA { get; private set; }
    public Vector2 SymB { get; private set; }

    private bool draggingA;
    private bool draggingB;
    private bool draggingLine;

    /// <summary>
    /// �Ƿ�������ֻ�������� StampController �жϣ�
    /// </summary>
    public bool IsLocked => _locked;
    private bool _locked;

    public LgcSymmetryGizmoController()
    {
        Reset();
    }

    public void Reset()
    {
        SymA = new Vector2(0.5f, 0f);
        SymB = new Vector2(0.5f, 1f);
        _locked = false;
    }

    /// <summary>
    /// ��������ڣ����Ʋ���������
    /// </summary>
    public void DrawAndHandle(
        Rect previewRect,
        Rect viewRect,
        bool locked,
        float zoom,
        bool repaintOnChange = true)
    {
        _locked = locked;

        Event e = Event.current;
        if (e == null) return;

        Vector2 aGui = UVToGUI(SymA, previewRect, viewRect);
        Vector2 bGui = UVToGUI(SymB, previewRect, viewRect);

        float handleBase = 14f;
        float handleRadius = handleBase * Mathf.Clamp(Mathf.Sqrt(Mathf.Max(1f, zoom)), 1f, 2f);

        // �� ��귴������Ԥ����������Ч��
        if (previewRect.Contains(e.mousePosition))
        {
            Vector2 localMouse = e.mousePosition - previewRect.position;
            Vector2 aLocal = aGui - previewRect.position;
            Vector2 bLocal = bGui - previewRect.position;

            bool nearA = (localMouse - aLocal).sqrMagnitude <= handleRadius * handleRadius;
            bool nearB = (localMouse - bLocal).sqrMagnitude <= handleRadius * handleRadius;
            bool nearLine = DistancePointToSegment(localMouse, aLocal, bLocal) <= handleRadius;

            if (nearA || nearB)
            {
                EditorGUIUtility.AddCursorRect(
                    new Rect(e.mousePosition.x - 10, e.mousePosition.y - 10, 20, 20),
                    MouseCursor.ResizeUpLeft);
            }
            else if (nearLine && !locked)
            {
                EditorGUIUtility.AddCursorRect(
                    new Rect(e.mousePosition.x - 10, e.mousePosition.y - 10, 20, 20),
                    MouseCursor.MoveArrow);
            }
        }

        if (!locked)
        {
            HandleMouseInput(
                e, previewRect, viewRect,
                aGui, bGui,
                handleRadius,
                repaintOnChange);
        }

        // �� �ü����ƣ����Գ��������� previewRect ��
        DrawGizmo(aGui, bGui, handleRadius, locked, previewRect);
    }

    #region Draw

    private void DrawGizmo(Vector2 a, Vector2 b, float r, bool locked, Rect previewRect)
    {
        Handles.BeginGUI();

        // �� �ؼ��޸����ü���Ԥ�����򣬷�ֹ���Ƴ���
        GUI.BeginClip(previewRect);

        // תΪ�ֲ����꣨����� previewRect ���Ͻǣ�
        Vector2 la = a - previewRect.position;
        Vector2 lb = b - previewRect.position;

        Handles.color = new Color(0.2f, 0.8f, 1f, locked ? 0.6f : 0.9f);

        Handles.DrawAAPolyLine(3f, la, lb);

        DrawHandle(la, r);
        DrawHandle(lb, r);

        GUI.EndClip();
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
            else if (DistancePointToSegment(e.mousePosition - guiRect.position, aGui - guiRect.position, bGui - guiRect.position) <= r)
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