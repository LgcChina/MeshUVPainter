#if UNITY_EDITOR
// LGC网格UV绘画工具（GPU版） - Unity 2022+
// LgcMeshUVColorPainter_GPU.cs (v2.4.1)
//
// 本版（v2.4.1）更新：
// 1) 对称模式启用但未锁定时，暂时禁用绘画（避免误画）。
// 2) 对称线条锚点放大，并随 zoom 自适应；线段命中阈值放宽；锚点外加浅色描边；提供 Move/Resize 鼠标指针反馈。
// 3) “UV 线条叠加” Foldout 使用多语言 L.GetText("UVOverlayFoldout").
//
// 说明：已在 DrawAndHandleSymmetryGizmo/HandlePaintingEvents/DrawLeftPanel 中进行局部修改，其余逻辑保持不变。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

public class LgcMeshUVColorPainter : EditorWindow
{
    internal const string VERSION = "v2.4.1"; // ★ 每次修改请更新版本号
    public static string VersionString => VERSION;
    private const string LOG_PREFIX = "[LGC网格UV绘画工具(GPU)] ";

    // ====== 工具区宽度控制 ======
    private const float LEFT_PANEL_MIN_WIDTH = 360f;
    private const float LEFT_PANEL_MAX_WIDTH = 520f;
    private float _leftPanelWidth;

    // ====== 选择对象/网格/渲染器/材质 ======
    [SerializeField] private GameObject targetObject;
    private Mesh targetMesh;
    private Renderer targetRenderer;
    private int selectedMatIndex = 0;
    private Material[] sharedMats;
    private string[] matSlotNames;
    private Material sourceMaterial;            // 当前使用中的材质（可能为实例）
    private Material originalMaterialAsset;     // 源材质资产（用于恢复）
    private string mainTexPropName = null;

    // 原始主纹理（用于橡皮擦过渡）
    private Texture originalTexture;
    private string originalAssetPath;

    // ====== GPU 绘制资源 ======
    private RenderTexture baseRT;   // 原始基底（从 originalTexture 复制一次）
    private RenderTexture paintRT;  // 工作绘制目标（GPU实时修改/合成）
    private const RenderTextureFormat RTFormat = RenderTextureFormat.ARGB32;
    private const GraphicsFormat GFormat = GraphicsFormat.R8G8B8A8_UNorm;

    // 计算着色器与内核
    private ComputeShader csPaint; // 任意包含 KBrush / KFillIsland 的 compute
    private int kBrush;            // 笔刷/橡皮
    private int kFillIsland;       // 孤岛填充/擦除（整岛）
    private bool computeReady = false;

    // UV 覆盖掩码 / 孤岛 ID（CPU 生成，一次性）
    private int maskW, maskH;
    private int[] uvMask;       // 0/1
    private int[] islandIdMask; // -1/0..N
    private int islandsCount = 0;
    private int activeIslandId = -1;
    private bool uvMaskDirty = true;
    private bool islandsDirty = true;

    // GPU 侧共享缓冲
    private ComputeBuffer uvMaskBuffer;   // int per pixel
    private ComputeBuffer islandIdBuffer; // int per pixel

    // 右侧视图与交互
    private Rect rightPanelRect;
    private Rect previewRect;
    private float zoom = 1f;
    private Vector2 viewCenter = new Vector2(0.5f, 0.5f);
    private Vector2 lastUV = new Vector2(-1, -1);
    private bool isPainting = false;
    private bool _strokeActive = false; // ★ 笔划合并护栏

    // UV 叠加显示（默认折叠）
    private bool foldUVPanel = false;
    private bool showUVOverlay = true;
    private Color uvColor = new Color(0.65f, 0.65f, 0.65f, 1f);
    private float uvAlpha = 0.85f;

    // 模式
    private enum PaintMode { Brush, Erase, Fill, IslandErase }
    private PaintMode mode = PaintMode.Brush;

    // 笔刷参数
    private Color brushColor = Color.yellow;
    private int brushSize = 32;
    private float brushOpacity = 1.0f; // 0..1
    private float brushHardness = 1.0f; // 0..1

    // 边界/隔离（默认开，但当无掩码可用时自动降级为关闭）
    private bool uvBoundaryLimitEnabled = true; // UV 边界限制（默认开）
    private bool islandIsolationEnabled = true; // 孤岛隔离（默认开）

    // 实时预览（移动到“边界与隔离”组）
    private bool enableRealTimePreview = false;

    // ====== 对称绘画（新增） ======
    private bool symmetryEnabled = false;
    private bool symmetryLocked = false;
    private Vector2 symA = new Vector2(0.5f, 0.0f);
    private Vector2 symB = new Vector2(0.5f, 1.0f);
    private bool draggingSymA = false, draggingSymB = false, draggingSymLine = false;

    // ====== 撤销/重做（工具内纯内存快照） ======
    private Stack<RenderTexture> undoStack = new Stack<RenderTexture>();
    private Stack<RenderTexture> redoStack = new Stack<RenderTexture>();
    private const int UNDO_LIMIT = 50; // 可在此调大/调小

    private class RTPool
    {
        private readonly Dictionary<(int, int), Stack<RenderTexture>> _dict = new();
        private readonly Func<int, int, RenderTexture> _factory;
        public RTPool(Func<int, int, RenderTexture> factory) { _factory = factory; }
        public RenderTexture Get(int w, int h)
        {
            if (_dict.TryGetValue((w, h), out var s) && s.Count > 0)
            {
                var rt = s.Pop();
                if (rt != null) return rt;
            }
            return _factory(w, h);
        }
        public void Return(RenderTexture rt)
        {
            if (rt == null) return;
            var key = (rt.width, rt.height);
            if (!_dict.TryGetValue(key, out var s)) _dict[key] = s = new Stack<RenderTexture>();
            s.Push(rt);
        }
        public void DisposeAll()
        {
            foreach (var kv in _dict)
            {
                var s = kv.Value;
                while (s.Count > 0)
                {
                    var rt = s.Pop();
                    if (rt) rt.Release();
                }
            }
            _dict.Clear();
        }
    }
    private RTPool rtPool;

    private static Texture2D checkerTex;

    private string outputAssetPath;
    private bool appliedToOutput = false;
    private bool outputCreatedInSession = false;

    private string infoMessage;
    private MessageType infoType = MessageType.Info;

    private Vector2 leftScroll;

    private string _targetResourceDir = "Assets/LGC/Tools/UV绘画/输出图片";

    [MenuItem("LGC/LGC网格UV绘画工具（GPU）")]
    public static void ShowWindow()
    {
        var window = GetWindow<LgcMeshUVColorPainter>("LGC网格UV绘画工具（GPU）");
        window.minSize = new Vector2(LEFT_PANEL_MIN_WIDTH + 600f, 600f);
        window.Show();
    }

    private void OnEnable()
    {
        EnsureCheckerTexture();
        mode = PaintMode.Brush;
        var L = EditorLanguageManager.Instance; L.InitLanguageData();
        infoMessage = L.GetText("InfoWelcome");
        LoadComputeIfNeeded();
        rtPool = new RTPool((w, h) => NewRT(w, h));
        wantsMouseMove = true;
    }

    private void OnDestroy()
    {
        RestoreOriginalMaterial();
        CleanupUnappliedOutputIfAny();
        ReleaseGPUResources();
        if (rtPool != null) rtPool.DisposeAll();
    }

    private void OnGUI()
    {
        var L = EditorLanguageManager.Instance; L.InitLanguageData();
        HandleGlobalShortcuts(Event.current);

        using (new GUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            GUILayout.FlexibleSpace();
            var cur = L.CurrentLang;
            var next = (EditorLanguage)EditorGUILayout.EnumPopup(new GUIContent("", L.GetText("LangDropdownTooltip")), cur, GUILayout.Width(120));
            if (next != cur) { L.CurrentLang = next; Repaint(); }
        }

        float targetLeft = position.width * 0.45f;
        _leftPanelWidth = Mathf.Clamp(targetLeft, LEFT_PANEL_MIN_WIDTH, LEFT_PANEL_MAX_WIDTH);
        minSize = new Vector2(LEFT_PANEL_MIN_WIDTH + 600f, 560f);
        EditorGUIUtility.labelWidth = Mathf.Max(160f, _leftPanelWidth - 160f);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.BeginVertical(GUILayout.MinWidth(LEFT_PANEL_MIN_WIDTH), GUILayout.Width(_leftPanelWidth));
        GUILayout.Label(L.GetText("LeftPanelTitle"), EditorStyles.boldLabel);
        leftScroll = EditorGUILayout.BeginScrollView(leftScroll);
        DrawLeftPanel();
        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndVertical();

        EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        GUILayout.Label(L.GetText("RightPanelTitle"), EditorStyles.boldLabel);
        rightPanelRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        DrawRightPanel(rightPanelRect);
        EditorGUILayout.EndVertical();
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space();
        var labelStyle = new GUIStyle(EditorStyles.centeredGreyMiniLabel)
        {
            alignment = TextAnchor.MiddleCenter,
            wordWrap = true
        };
        using (new GUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            var authorTxt = L.GetText("BottomAuthorInfo");
            var verTxt = string.Format(L.GetText("BottomVersionInfo"), VERSION);
            var content = new GUIContent($"{authorTxt} {verTxt}", L.GetText("ClickToSeeMore"));
            var btnStyle = new GUIStyle(labelStyle);
            Rect r = GUILayoutUtility.GetRect(new GUIContent(content), btnStyle, GUILayout.Height(18));
            EditorGUIUtility.AddCursorRect(r, MouseCursor.Link);
            if (GUI.Button(r, content, btnStyle))
            {
                LgcAuthorInfoWindow.OpenWindow();
            }
            GUILayout.FlexibleSpace();
        }
    }

    private void HandleGlobalShortcuts(Event e)
    {
        if (e == null) return;
        if (e.type != EventType.KeyDown) return;
        if (focusedWindow != this) return;
        bool ctrl = e.control || e.command;
        if (!ctrl) return;

        if (e.keyCode == KeyCode.Z)
        {
            if (e.shift) DoRedo_Internal(); else DoUndo_Internal();
            e.Use();
        }
        else if (e.keyCode == KeyCode.Y)
        {
            DoRedo_Internal(); e.Use();
        }
    }

    private void DrawLeftPanel()
    {
        var L = EditorLanguageManager.Instance;
        var wrapHelp = new GUIStyle(EditorStyles.helpBox) { wordWrap = true };
        EditorGUILayout.LabelField(infoMessage, wrapHelp);

        EditorGUI.BeginChangeCheck();
        targetObject = (GameObject)EditorGUILayout.ObjectField(L.GetText("TargetObject"), targetObject, typeof(GameObject), true);
        if (EditorGUI.EndChangeCheck())
        {
            TryBindTarget(targetObject);
        }

        using (new EditorGUI.DisabledScope(targetObject == null || targetMesh == null || targetRenderer == null))
        {
            if (sharedMats != null && sharedMats.Length > 0)
            {
                int newIndex = EditorGUILayout.Popup(L.GetText("MatSlot"), selectedMatIndex, matSlotNames);
                if (newIndex != selectedMatIndex)
                {
                    selectedMatIndex = newIndex;
                    BindMaterialSlot();
                }
            }
            else
            {
                EditorGUILayout.LabelField(L.GetText("MatSlot"), L.GetText("MatSlotNone"));
            }
            if (GUILayout.Button(L.GetText("RefreshSlot")))
            {
                BindMaterialSlot();
            }
        }

        EditorGUILayout.Space(8);
        GUILayout.Label(L.GetText("ModeTitle"), EditorStyles.boldLabel);
        DrawModeButtons();

        EditorGUILayout.Space(8);
        GUILayout.Label(L.GetText("BrushAndFill"), EditorStyles.boldLabel);
        brushColor = EditorGUILayout.ColorField(L.GetText("BrushOrFillColor"), brushColor);
        brushSize = EditorGUILayout.IntSlider(L.GetText("BrushRadius"), brushSize, 1, 256);
        brushOpacity = EditorGUILayout.Slider(L.GetText("Opacity"), Mathf.Clamp01(brushOpacity), 0f, 1f);
        brushHardness = EditorGUILayout.Slider(L.GetText("Hardness"), Mathf.Clamp01(brushHardness), 0f, 1f);

        EditorGUILayout.Space(8);
        GUILayout.Label(L.GetText("BoundaryAndIsolationTitle"), EditorStyles.boldLabel);
        uvBoundaryLimitEnabled = EditorGUILayout.ToggleLeft(L.GetText("ToggleUVBoundaryLimit"), uvBoundaryLimitEnabled);
        islandIsolationEnabled = EditorGUILayout.ToggleLeft(L.GetText("ToggleIslandIsolation"), islandIsolationEnabled);
        using (new EditorGUI.DisabledScope(paintRT == null || targetRenderer == null))
        {
            bool newRealtime = EditorGUILayout.Toggle(L.GetText("RealtimePreviewToggle"), enableRealTimePreview);
            if (newRealtime != enableRealTimePreview)
            {
                enableRealTimePreview = newRealtime;
                if (enableRealTimePreview && paintRT != null)
                {
                    ApplyMaterialInstanceWithTexture(paintRT);
                    infoMessage = L.GetText("RealtimeOn"); infoType = MessageType.Info;
                }
                else
                {
                    RestoreOriginalMaterial();
                    infoMessage = L.GetText("RealtimeOff"); infoType = MessageType.Info;
                }
            }
        }

        EditorGUILayout.Space(8);
        GUILayout.Label(L.GetText("SymmetryTitle"), EditorStyles.boldLabel);
        symmetryEnabled = EditorGUILayout.ToggleLeft(L.GetText("SymmetryEnable"), symmetryEnabled);
        using (new EditorGUI.DisabledScope(!symmetryEnabled))
        {
            symmetryLocked = EditorGUILayout.ToggleLeft(L.GetText("SymmetryLock"), symmetryLocked);
            using (new GUILayout.HorizontalScope())
            {
                if (GUILayout.Button(L.GetText("SymmetryReset"), GUILayout.Width(200)))
                {
                    symA = new Vector2(0.5f, 0.0f);
                    symB = new Vector2(0.5f, 1.0f);
                    Repaint();
                }
            }
            var help = new GUIStyle(EditorStyles.helpBox) { wordWrap = true };
            EditorGUILayout.LabelField(L.GetText("SymmetryHelp"), help);
        }

        EditorGUILayout.Space(4);
        foldUVPanel = EditorGUILayout.Foldout(foldUVPanel, L.GetText("UVOverlayFoldout"), true);
        if (foldUVPanel)
        {
            EditorGUI.indentLevel++;
            showUVOverlay = EditorGUILayout.Toggle(L.GetText("ShowUVOverlay"), showUVOverlay);
            uvColor = EditorGUILayout.ColorField(L.GetText("UVLineColor"), uvColor);
            uvAlpha = EditorGUILayout.Slider(L.GetText("UVOverlayStrength"), uvAlpha, 0f, 1f);
            EditorGUI.indentLevel--;
        }

        EditorGUILayout.Space(8);
        GUILayout.Label(L.GetText("GroupApplySaveLocate"), EditorStyles.boldLabel);
        using (new EditorGUI.DisabledScope(paintRT == null || targetRenderer == null))
        {
            if (GUILayout.Button(L.GetText("SaveOutputFull")))
            {
                SaveOutputOnly();
            }
            if (GUILayout.Button(L.GetText("ExportPaintLayer")))
            {
                ExportPaintLayerOnly();
            }
            EditorGUILayout.Space(4);
            if (GUILayout.Button("清空本次修改"))
            {
                ClearCurrentEdits();
            }
        }
        if (GUILayout.Button(L.GetText("LocateProjectResBtn")))
        {
            LocateAndCreateResourceDirectory();
        }
    }

    private void DrawModeButtons()
    {
        var L = EditorLanguageManager.Instance;
        bool isBrush = mode == PaintMode.Brush;
        bool isErase = mode == PaintMode.Erase;
        bool isFill = mode == PaintMode.Fill;
        bool isIslandErase = mode == PaintMode.IslandErase;

        var defaultColor = GUI.color;

        GUI.color = isBrush ? new Color(0.85f, 1f, 0.85f, 1f) : defaultColor;
        if (GUILayout.Button(L.GetText("ModeBrush"))) { if (mode != PaintMode.Brush) { mode = PaintMode.Brush; Repaint(); } }

        GUI.color = isErase ? new Color(1f, 0.9f, 0.8f, 1f) : defaultColor;
        if (GUILayout.Button(L.GetText("ModeErase"))) { if (mode != PaintMode.Erase) { mode = PaintMode.Erase; Repaint(); } }

        GUI.color = isFill ? new Color(0.85f, 0.9f, 1f, 1f) : defaultColor;
        if (GUILayout.Button(L.GetText("ModeFill"))) { if (mode != PaintMode.Fill) { mode = PaintMode.Fill; Repaint(); } }

        GUI.color = isIslandErase ? new Color(1f, 0.95f, 0.6f, 1f) : defaultColor;
        if (GUILayout.Button(EditorLanguageManager.Instance.GetText("ModeIslandErase")))
        { if (mode != PaintMode.IslandErase) { mode = PaintMode.IslandErase; Repaint(); } }

        GUI.color = defaultColor;
    }

    private void DrawRightPanel(Rect containerRect)
    {
        var L = EditorLanguageManager.Instance;

        const float topBarH = 28f;
        Rect topBar = new Rect(containerRect.x, containerRect.y, containerRect.width, topBarH);
        Rect btnRect = new Rect(topBar.x + (topBar.width - 100f) * 0.5f, topBar.y + 4f, 100f, 20f);
        using (new EditorGUI.DisabledScope(paintRT == null))
        {
            if (GUI.Button(btnRect, L.GetText("ResetViewBtn"))) { ResetView(); Repaint(); }
        }

        Rect areaRect = new Rect(containerRect.x, containerRect.y + topBarH + 2f,
                                 containerRect.width, containerRect.height - topBarH - 2f);

        if (paintRT == null)
        {
            GUI.Label(new Rect(areaRect.x + 8, areaRect.y + 8, areaRect.width - 16, 20),
                "暂无绘制目标。请在左侧绑定对象并选择材质槽。", EditorStyles.boldLabel);
            return;
        }

        float cw = Mathf.Max(50f, areaRect.width - 8f);
        float ch = Mathf.Max(50f, areaRect.height - 8f);
        float aspect = (float)paintRT.width / Mathf.Max(1, paintRT.height);
        float previewW = cw, previewH = previewW / aspect;
        if (previewH > ch) { previewH = ch; previewW = previewH * aspect; }
        previewRect = new Rect(areaRect.x + (areaRect.width - previewW) * 0.5f,
                               areaRect.y + (areaRect.height - previewH) * 0.5f,
                               previewW, previewH);

        var e = Event.current; if (e != null)
        {
            bool hover = previewRect.Contains(e.mousePosition);
            if (e.type == EventType.MouseMove || e.type == EventType.MouseEnterWindow || e.type == EventType.MouseLeaveWindow)
            {
                if (hover || e.type != EventType.MouseMove) Repaint();
            }
        }

        if (Event.current.type == EventType.Repaint && checkerTex != null)
            DrawTiledTexture(previewRect, checkerTex, 16);

        Rect viewRect = GetViewRect();
        GUI.DrawTextureWithTexCoords(previewRect, paintRT,
            new Rect(viewRect.xMin, viewRect.yMin, viewRect.width, viewRect.height));

        if (showUVOverlay && targetMesh != null && targetMesh.uv != null && targetMesh.uv.Length > 0)
            DrawUVImmediate(previewRect, viewRect, targetMesh, uvColor, uvAlpha);

        if (symmetryEnabled)
            DrawAndHandleSymmetryGizmo(previewRect, viewRect);

        HandleViewNavigation(previewRect);
        HandlePaintingEvents(viewRect);
        DrawBrushPreview(previewRect, viewRect);
    }

    private Rect GetViewRect()
    {
        float sz = 1f / Mathf.Max(1f, zoom);
        float half = sz * 0.5f;
        float cx = Mathf.Clamp(viewCenter.x, half, 1f - half);
        float cy = Mathf.Clamp(viewCenter.y, half, 1f - half);
        return new Rect(cx - half, cy - half, sz, sz);
    }
    private void SetViewFromRect(Rect vr)
    {
        float sz = Mathf.Clamp(vr.width, 1e-6f, 1f);
        zoom = 1f / sz; viewCenter = new Vector2(vr.center.x, vr.center.y);
    }
    private void ClampView() { Rect vr = GetViewRect(); SetViewFromRect(vr); }

    private void HandleViewNavigation(Rect rect)
    {
        Event e = Event.current; if (e == null) return;
        if (!rect.Contains(e.mousePosition)) return;

        if (e.type == EventType.ScrollWheel)
        {
            Rect vr = GetViewRect();
            Vector2 local = e.mousePosition - rect.position;
            Vector2 local01 = new Vector2(Mathf.Clamp01(local.x / Mathf.Max(1, rect.width)), Mathf.Clamp01(local.y / Mathf.Max(1, rect.height)));
            Vector2 uvPivot = new Vector2(vr.xMin + local01.x * vr.width, vr.yMax - local01.y * vr.height);
            float factor = Mathf.Pow(1.1f, -e.delta.y);
            float newZoom = Mathf.Clamp(zoom * factor, 1f, 64f);
            float newSize = 1f / newZoom;
            Rect newVR = new Rect(uvPivot.x - local01.x * newSize, uvPivot.y + local01.y * newSize - newSize, newSize, newSize);
            SetViewFromRect(newVR); ClampView();
            e.Use(); Repaint();
        }

        bool isMiddleDrag = (e.button == 2 && e.type == EventType.MouseDrag);
        bool isAltLeftDrag = (e.button == 0 && e.type == EventType.MouseDrag && e.alt);
        if (isMiddleDrag || isAltLeftDrag)
        {
            Rect vr = GetViewRect();
            float dxUv = e.delta.x / Mathf.Max(1, rect.width) * vr.width;
            float dyUv = e.delta.y / Mathf.Max(1, rect.height) * vr.height;
            viewCenter.x -= dxUv; viewCenter.y += dyUv;
            ClampView(); e.Use(); Repaint();
        }
    }

    private void DrawBrushPreview(Rect rect, Rect viewRect)
    {
        if (paintRT == null) return;
        Vector2 mouse = Event.current.mousePosition; if (!rect.Contains(mouse)) return;
        float scaleX = rect.width / Mathf.Max(1e-6f, viewRect.width * paintRT.width);
        float scaleY = rect.height / Mathf.Max(1e-6f, viewRect.height * paintRT.height);
        float guiRadius = brushSize * (scaleX + scaleY) * 0.5f;
        Handles.BeginGUI();
        Handles.color = new Color(brushColor.r, brushColor.g, brushColor.b, 0.9f);
        Handles.DrawWireDisc(mouse, Vector3.forward, guiRadius);
        Handles.DrawWireDisc(mouse, Vector3.forward, Mathf.Max(1f, guiRadius - 1.5f));
        Handles.EndGUI();
    }

    private void DrawUVImmediate(Rect rect, Rect viewRect, Mesh mesh, Color lineColor, float alpha)
    {
        var uvs = mesh.uv; var tris = mesh.triangles;
        if (uvs == null || uvs.Length == 0 || tris == null || tris.Length == 0) return;

        Handles.BeginGUI();
        Handles.color = new Color(lineColor.r, lineColor.g, lineColor.b, Mathf.Clamp01(alpha));
        GUI.BeginClip(rect);
        float w = rect.width, h = rect.height;

        Vector3 Map(Vector2 u)
        {
            float nx = (u.x - viewRect.xMin) / Mathf.Max(1e-6f, viewRect.width);
            float ny = (u.y - viewRect.yMin) / Mathf.Max(1e-6f, viewRect.height);
            return new Vector3(nx * w, (1f - ny) * h, 0);
        }

        for (int i = 0; i < tris.Length; i += 3)
        {
            int i0 = tris[i], i1 = tris[i + 1], i2 = tris[i + 2];
            if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= uvs.Length || i1 >= uvs.Length || i2 >= uvs.Length) continue;
            Vector3 a = Map(uvs[i0]), b = Map(uvs[i1]), c = Map(uvs[i2]);
            Handles.DrawLine(a, b); Handles.DrawLine(b, c); Handles.DrawLine(c, a);
        }
        GUI.EndClip();
        Handles.EndGUI();
    }

    private void DrawAndHandleSymmetryGizmo(Rect rect, Rect viewRect)
    {
        Event e = Event.current; bool mouseIn = rect.Contains(e.mousePosition);

        float handleBase = 14f; // 基础半径（像素）
        float handleR = handleBase * Mathf.Clamp(Mathf.Sqrt(Mathf.Max(1f, zoom)), 1f, 2f); // 随 zoom 自适应

        Vector2 MapUV(Vector2 uv)
        {
            float nx = (uv.x - viewRect.xMin) / Mathf.Max(1e-6f, viewRect.width);
            float ny = (uv.y - viewRect.yMin) / Mathf.Max(1e-6f, viewRect.height);
            return new Vector2(rect.x + nx * rect.width, rect.y + (1f - ny) * rect.height);
        }

        Vector2 aG = MapUV(symA), bG = MapUV(symB);

        // 鼠标指针反馈：靠近端点 -> Resize，靠近线段 -> Move
        {
            Vector2 m = e.mousePosition;
            float dLine = DistancePointToSegment(m, aG, bG);
            bool nearA = (m - aG).sqrMagnitude <= handleR * handleR;
            bool nearB = (m - bG).sqrMagnitude <= handleR * handleR;
            bool nearLine = dLine <= handleR * 1.0f;
            if (nearA || nearB)
            {
                var rA = new Rect(aG.x - handleR, aG.y - handleR, handleR * 2f, handleR * 2f);
                var rB = new Rect(bG.x - handleR, bG.y - handleR, handleR * 2f, handleR * 2f);
                EditorGUIUtility.AddCursorRect(rA, MouseCursor.ResizeUpLeft);
                EditorGUIUtility.AddCursorRect(rB, MouseCursor.ResizeUpLeft);
            }
            else if (nearLine)
            {
                float minX = Mathf.Min(aG.x, bG.x) - handleR;
                float minY = Mathf.Min(aG.y, bG.y) - handleR;
                float maxX = Mathf.Max(aG.x, bG.x) + handleR;
                float maxY = Mathf.Max(aG.y, bG.y) + handleR;
                EditorGUIUtility.AddCursorRect(new Rect(minX, minY, maxX - minX, maxY - minY), MouseCursor.MoveArrow);
            }
        }

        bool canDrag = !symmetryLocked;
        if (canDrag && mouseIn)
        {
            if (e.type == EventType.MouseDown && e.button == 0)
            {
                if ((e.mousePosition - aG).sqrMagnitude <= handleR * handleR) { draggingSymA = true; e.Use(); }
                else if ((e.mousePosition - bG).sqrMagnitude <= handleR * handleR) { draggingSymB = true; e.Use(); }
                else
                {
                    float d = DistancePointToSegment(e.mousePosition, aG, bG);
                    if (d <= handleR * 1.0f) { draggingSymLine = true; e.Use(); }
                }
            }
            else if (e.type == EventType.MouseDrag && (draggingSymA || draggingSymB || draggingSymLine))
            {
                Vector2 uvDelta = GUIToUVDirectionDelta(e.delta, rect, viewRect);
                if (draggingSymA) { symA += uvDelta; ClampUV(ref symA); }
                else if (draggingSymB) { symB += uvDelta; ClampUV(ref symB); }
                else if (draggingSymLine) { symA += uvDelta; symB += uvDelta; ClampUV(ref symA); ClampUV(ref symB); }
                e.Use(); Repaint();
            }
            else if (e.type == EventType.MouseUp)
            {
                draggingSymA = draggingSymB = draggingSymLine = false;
            }
        }

        Handles.BeginGUI();
        Handles.color = new Color(0.2f, 0.8f, 1f, 0.9f);
        Handles.DrawAAPolyLine(3f, aG, bG);
        Color hc = symmetryLocked ? new Color(0.2f, 0.8f, 1f, 0.6f) : new Color(0.2f, 0.8f, 1f, 1f);
        DrawHandleDisc(aG, handleR, hc);
        DrawHandleDisc(bG, handleR, hc);
        Handles.EndGUI();

        static float DistancePointToSegment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a; float t = Vector2.Dot(p - a, ab) / Mathf.Max(1e-6f, ab.sqrMagnitude);
            t = Mathf.Clamp01(t); Vector2 proj = a + t * ab; return (p - proj).magnitude;
        }
        static void DrawHandleDisc(Vector2 center, float r, Color c)
        {
            var old = Handles.color;
            var outline = new Color(c.r, c.g, c.b, 0.25f);
            Handles.color = outline; Handles.DrawSolidDisc(center, Vector3.forward, r * 0.95f);
            Handles.color = c; Handles.DrawSolidDisc(center, Vector3.forward, r * 0.60f);
            Handles.color = old;
        }
    }

    private static Vector2 GUIToUVDirectionDelta(Vector2 guiDelta, Rect rect, Rect viewRect)
    {
        float du = (guiDelta.x / Mathf.Max(1e-6f, rect.width)) * viewRect.width;
        float dv = -(guiDelta.y / Mathf.Max(1e-6f, rect.height)) * viewRect.height;
        return new Vector2(du, dv);
    }

    private static void ClampUV(ref Vector2 uv)
    {
        uv.x = Mathf.Clamp01(uv.x);
        uv.y = Mathf.Clamp01(uv.y);
    }

    private void HandlePaintingEvents(Rect viewRect)
    {
        if (!computeReady || paintRT == null) return;
        Event e = Event.current; if (e == null) return;

        // ★ 对称模式启用但未锁定：暂时禁用绘画（避免误画）
        if (symmetryEnabled && !symmetryLocked)
        {
            if (e.button == 0 && (e.type == EventType.MouseDown || e.type == EventType.MouseDrag || e.type == EventType.MouseUp))
            {
                var L = EditorLanguageManager.Instance;
                infoMessage = L.GetText("SymmetryEditingBlockPaint");
                infoType = MessageType.Info;
                e.Use();
                Repaint();
                return;
            }
        }

        bool inside = previewRect.Contains(e.mousePosition);

        if (inside && e.type == EventType.KeyDown)
        {
            if (e.keyCode == KeyCode.LeftBracket || e.character == '[') { brushSize = Mathf.Max(1, brushSize - 1); e.Use(); Repaint(); }
            else if (e.keyCode == KeyCode.RightBracket || e.character == ']') { brushSize = Mathf.Min(256, brushSize + 1); e.Use(); Repaint(); }
        }

        if (!inside)
        {
            if (e.type == EventType.MouseUp)
            {
                if (_strokeActive) { EndStrokeApply(); _strokeActive = false; }
                isPainting = false; lastUV = new Vector2(-1, -1); activeIslandId = -1;
            }
            return;
        }

        Vector2 local = e.mousePosition - previewRect.position;
        Vector2 local01 = new Vector2(Mathf.Clamp01(local.x / Mathf.Max(1, previewRect.width)), Mathf.Clamp01(local.y / Mathf.Max(1, previewRect.height)));
        Vector2 uv = new Vector2(viewRect.xMin + local01.x * viewRect.width, viewRect.yMax - local01.y * viewRect.height);

        if (e.button == 0)
        {
            if (mode == PaintMode.Fill || mode == PaintMode.IslandErase)
            {
                if (e.type == EventType.MouseDown)
                {
                    BeginStrokeSnapshotIfNeeded();
                    int island = GetIslandIdAtUV_CPU(uv);
                    if (island >= 0 && islandIdBuffer != null) { GPU_FillIsland(island, mode == PaintMode.Fill); }
                    else { infoMessage = EditorLanguageManager.Instance.GetText("InfoNoIslandData"); infoType = MessageType.Info; }
                    if (symmetryEnabled)
                    {
                        Vector2 muv = MirrorUVAcrossAxis(uv);
                        if (IsUVInside01(muv))
                        {
                            int islandM = GetIslandIdAtUV_CPU(muv);
                            if (islandM >= 0 && islandIdBuffer != null) GPU_FillIsland(islandM, mode == PaintMode.Fill);
                        }
                    }
                    EndStrokeApply(); _strokeActive = false;
                    e.Use(); Repaint();
                }
                return;
            }

            if (e.type == EventType.MouseDown)
            {
                BeginStrokeSnapshotIfNeeded();
                isPainting = true; lastUV = uv; _strokeActive = true;
                activeIslandId = islandIsolationEnabled ? GetIslandIdAtUV_CPU(uv) : -1;
                GPU_PaintAtUV(uv, mode == PaintMode.Brush);
                if (symmetryEnabled)
                {
                    Vector2 muv = MirrorUVAcrossAxis(uv);
                    if (IsUVInside01(muv))
                    {
                        int prev = activeIslandId;
                        int iso = islandIsolationEnabled ? GetIslandIdAtUV_CPU(muv) : -1;
                        activeIslandId = iso; GPU_PaintAtUV(muv, mode == PaintMode.Brush); activeIslandId = prev;
                    }
                }
                e.Use(); Repaint();
            }
            else if (e.type == EventType.MouseDrag && isPainting)
            {
                GPU_DrawStrokeBetween(lastUV, uv, mode == PaintMode.Brush);
                if (symmetryEnabled)
                {
                    Vector2 uvA = MirrorUVAcrossAxis(lastUV);
                    Vector2 uvB = MirrorUVAcrossAxis(uv);
                    if (IsUVInside01(uvA) || IsUVInside01(uvB))
                    {
                        int prev = activeIslandId;
                        int iso = islandIsolationEnabled ? GetIslandIdAtUV_CPU(uvB) : -1;
                        activeIslandId = iso;
                        GPU_DrawStrokeBetween(uvA, uvB, mode == PaintMode.Brush);
                        activeIslandId = prev;
                    }
                }
                lastUV = uv;
                e.Use(); Repaint();
            }
            else if (e.type == EventType.MouseUp && isPainting)
            {
                isPainting = false;
                EndStrokeApply(); _strokeActive = false;
                lastUV = new Vector2(-1, -1);
                activeIslandId = -1;
                e.Use(); Repaint();
            }
        }
    }

    private void BeginStrokeSnapshotIfNeeded()
    {
        if (_strokeActive) return; BeginStrokeSnapshot(); _strokeActive = true;
    }

    private void GPU_PaintAtUV(Vector2 uv, bool isBrushMode)
    {
        if (!computeReady || paintRT == null || baseRT == null) return;
        if (!IsUVInside01(uv)) return;

        int cx = Mathf.RoundToInt(uv.x * (paintRT.width - 1));
        int cy = Mathf.RoundToInt(uv.y * (paintRT.height - 1));
        int r = Mathf.Max(1, brushSize);
        int x0 = Mathf.Clamp(cx - r, 0, paintRT.width - 1);
        int y0 = Mathf.Clamp(cy - r, 0, paintRT.height - 1);
        int x1 = Mathf.Clamp(cx + r, 0, paintRT.width - 1);
        int y1 = Mathf.Clamp(cy + r, 0, paintRT.height - 1);
        int rw = x1 - x0 + 1;
        int rh = y1 - y0 + 1;

        csPaint.SetTexture(kBrush, "_PaintRT", paintRT);
        csPaint.SetTexture(kBrush, "_BaseRT", baseRT);

        var rect = new RectInt(x0, y0, rw, rh);
        var center = new Vector2Int(cx, cy);
        SetupCommonBrushParams(kBrush, rect, center, r, isBrushMode);
        SetupCommonMasks(kBrush);

        int tgx = (rw + 7) / 8;
        int tgy = (rh + 7) / 8;
        csPaint.Dispatch(kBrush, Mathf.Max(1, tgx), Mathf.Max(1, tgy), 1);
    }

    private void GPU_DrawStrokeBetween(Vector2 uvA, Vector2 uvB, bool isBrushMode)
    {
        if (!IsUVInside01(uvA) && !IsUVInside01(uvB)) return;
        Vector2 pA = new Vector2(uvA.x * (paintRT.width - 1), uvA.y * (paintRT.height - 1));
        Vector2 pB = new Vector2(uvB.x * (paintRT.width - 1), uvB.y * (paintRT.height - 1));
        float distPixels = Vector2.Distance(pA, pB);
        float spacing = Mathf.Max(1f, brushSize * (0.7f - 0.4f * brushHardness));
        int steps = Mathf.Max(1, Mathf.CeilToInt(distPixels / spacing));
        for (int i = 0; i <= steps; i++)
        {
            float t = steps == 0 ? 1f : i / (float)steps;
            Vector2 uv = Vector2.Lerp(uvA, uvB, t);
            GPU_PaintAtUV(uv, isBrushMode);
        }
    }

    private void GPU_FillIsland(int islandId, bool isFill)
    {
        if (!computeReady || paintRT == null || baseRT == null) return;
        if (islandIdBuffer == null || uvMaskBuffer == null) return;

        csPaint.SetTexture(kFillIsland, "_PaintRT", paintRT);
        csPaint.SetTexture(kFillIsland, "_BaseRT", baseRT);

        SetupCommonFillParams(kFillIsland, islandId, isFill);
        SetupCommonMasks(kFillIsland);

        int tgx = (paintRT.width + 15) / 16;
        int tgy = (paintRT.height + 15) / 16;
        csPaint.Dispatch(kFillIsland, Mathf.Max(1, tgx), Mathf.Max(1, tgy), 1);
    }

    private static bool IsUVInside01(Vector2 uv) => uv.x >= 0f && uv.x <= 1f && uv.y >= 0f && uv.y <= 1f;

    private Vector2 MirrorUVAcrossAxis(Vector2 p)
    {
        Vector2 a = symA; Vector2 b = symB; Vector2 ab = b - a; float len2 = ab.sqrMagnitude;
        if (len2 < 1e-8f) return p;
        float t = Vector2.Dot(p - a, ab) / len2; Vector2 proj = a + t * ab;
        Vector2 mirrored = proj + (proj - p);
        return mirrored;
    }

    private bool EnsureMaskBuffersUpToDate()
    {
        if (paintRT == null) return false;
        if (targetMesh == null)
        {
            ReleaseMaskBuffers();
            return false;
        }
        RebuildUVCoverageMaskIfNeeded();
        RebuildIslandIdsIfNeeded();

        if (uvMask == null || islandIdMask == null ||
            uvMask.Length != paintRT.width * paintRT.height ||
            islandIdMask.Length != paintRT.width * paintRT.height)
        {
            ReleaseMaskBuffers();
            return false;
        }

        if (uvMaskBuffer == null ||
            islandIdBuffer == null ||
            uvMaskBuffer.count != paintRT.width * paintRT.height ||
            islandIdBuffer.count != paintRT.width * paintRT.height)
        {
            UploadMaskBuffers();
        }
        return (uvMaskBuffer != null && islandIdBuffer != null);
    }

    private void RebuildUVCoverageMaskIfNeeded()
    {
        if (paintRT == null || targetMesh == null)
        {
            uvMask = null; islandIdMask = null; islandsCount = 0; return;
        }
        if (!uvMaskDirty && uvMask != null && maskW == paintRT.width && maskH == paintRT.height) return;

        maskW = paintRT.width; maskH = paintRT.height;
        uvMask = new int[maskW * maskH];

        var uvs = targetMesh.uv; var tris = targetMesh.triangles;
        if (uvs == null || uvs.Length == 0 || tris == null || tris.Length == 0)
        { islandIdMask = null; islandsCount = 0; uvMaskDirty = false; islandsDirty = false; return; }

        for (int i = 0; i < tris.Length; i += 3)
        {
            int i0 = tris[i], i1 = tris[i + 1], i2 = tris[i + 2];
            if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= uvs.Length || i1 >= uvs.Length || i2 >= uvs.Length) continue;
            RasterizeTriangleToMask(uvs[i0], uvs[i1], uvs[i2]);
        }
        uvMaskDirty = false;
        islandsDirty = true;
        RebuildIslandIdsIfNeeded();
    }

    private void RasterizeTriangleToMask(Vector2 a, Vector2 b, Vector2 c)
    {
        int w = maskW, h = maskH;
        float ax = a.x * (w - 1), ay = a.y * (h - 1);
        float bx = b.x * (w - 1), by = b.y * (h - 1);
        float cx = c.x * (w - 1), cy = c.y * (h - 1);
        int minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(ax, Mathf.Min(bx, cx))), 0, w - 1);
        int maxX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(ax, Mathf.Max(bx, cx))), 0, w - 1);
        int minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(ay, Mathf.Min(by, cy))), 0, h - 1);
        int maxY = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(ay, Mathf.Max(by, cy))), 0, h - 1);

        float Area(Vector2 p1, Vector2 p2, Vector2 p3) =>
            (p1.x * (p2.y - p3.y) + p2.x * (p3.y - p1.y) + p3.x * (p1.y - p2.y));
        float area = Area(new Vector2(ax, ay), new Vector2(bx, by), new Vector2(cx, cy));
        if (Mathf.Abs(area) < 1e-6f) return;

        for (int y = minY; y <= maxY; y++)
            for (int x = minX; x <= maxX; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                float a1 = Area(new Vector2(px, py), new Vector2(bx, by), new Vector2(cx, cy));
                float a2 = Area(new Vector2(ax, ay), new Vector2(px, py), new Vector2(cx, cy));
                float a3 = Area(new Vector2(ax, ay), new Vector2(bx, by), new Vector2(px, py));
                bool hasNeg = (a1 < 0) || (a2 < 0) || (a3 < 0);
                bool hasPos = (a1 > 0) || (a2 > 0) || (a3 > 0);
                if (hasNeg && hasPos) continue;
                int idx = y * w + x; uvMask[idx] = 1;
            }
    }

    private void RebuildIslandIdsIfNeeded()
    {
        if (!islandsDirty) return;
        islandsDirty = false;

        islandIdMask = new int[maskW * maskH];
        for (int i = 0; i < islandIdMask.Length; i++) islandIdMask[i] = -1;
        islandsCount = 0;
        if (uvMask == null) return;

        Queue<int> q = new Queue<int>();
        for (int i = 0; i < uvMask.Length; i++)
        {
            if (uvMask[i] == 1 && islandIdMask[i] < 0)
            {
                int id = islandsCount++;
                q.Enqueue(i);
                islandIdMask[i] = id;
                while (q.Count > 0)
                {
                    int idx = q.Dequeue();
                    int x = idx % maskW, y = idx / maskW;
                    Try(x - 1, y); Try(x + 1, y); Try(x, y - 1); Try(x, y + 1);
                    void Try(int nx, int ny)
                    {
                        if (nx < 0 || ny < 0 || nx >= maskW || ny >= maskH) return;
                        int nidx = ny * maskW + nx;
                        if (uvMask[nidx] == 1 && islandIdMask[nidx] < 0)
                        {
                            islandIdMask[nidx] = id; q.Enqueue(nidx);
                        }
                    }
                }
            }
        }
    }

    private int GetIslandIdAtUV_CPU(Vector2 uv)
    {
        if (paintRT == null || islandIdMask == null) return -1;
        int w = paintRT.width, h = paintRT.height;
        int sx = Mathf.Clamp(Mathf.RoundToInt(uv.x * (w - 1)), 0, w - 1);
        int sy = Mathf.Clamp(Mathf.RoundToInt(uv.y * (h - 1)), 0, h - 1);
        int idx = sy * w + sx;
        if (uvMask == null || idx < 0 || idx >= uvMask.Length || uvMask[idx] == 0) return -1;
        if (islandIdMask == null || islandIdMask.Length != w * h) return -1;
        return islandIdMask[idx];
    }

    private void UploadMaskBuffers()
    {
        ReleaseMaskBuffers();
        if (paintRT == null || uvMask == null || islandIdMask == null) return;
        int count = paintRT.width * paintRT.height;
        if (uvMask.Length != count || islandIdMask.Length != count) return;
        uvMaskBuffer = new ComputeBuffer(count, sizeof(int), ComputeBufferType.Default);
        islandIdBuffer = new ComputeBuffer(count, sizeof(int), ComputeBufferType.Default);
        uvMaskBuffer.SetData(uvMask);
        islandIdBuffer.SetData(islandIdMask);
    }
    private void ReleaseMaskBuffers()
    {
        if (uvMaskBuffer != null) { uvMaskBuffer.Release(); uvMaskBuffer = null; }
        if (islandIdBuffer != null) { islandIdBuffer.Release(); islandIdBuffer = null; }
    }

    private void ApplyMaterialInstanceWithTexture(Texture tex)
    {
        if (targetRenderer == null || originalMaterialAsset == null) return;
        var mats = targetRenderer.sharedMaterials;
        if (mats == null || mats.Length == 0 || selectedMatIndex < 0 || selectedMatIndex >= mats.Length) return;

        Material currentMat = mats[selectedMatIndex];
        Material instanceMat;
        if (currentMat != null && !EditorUtility.IsPersistent(currentMat) && currentMat.shader == originalMaterialAsset.shader)
        {
            instanceMat = currentMat;
        }
        else
        {
            instanceMat = new Material(originalMaterialAsset);
            Undo.RegisterCreatedObjectUndo(instanceMat, "Create Material Instance");
            mats[selectedMatIndex] = instanceMat;
            targetRenderer.sharedMaterials = mats;
        }
        AssignTextureToAllLikelyMainProps(instanceMat, tex);
#if UNITY_2021_3_OR_NEWER
        PrefabUtility.RecordPrefabInstancePropertyModifications(targetRenderer);
#endif
        AssetDatabase.SaveAssets();
        SceneView.RepaintAll();
        Repaint();
        sourceMaterial = instanceMat;
    }

    private string FindMainTexturePropertyName(Material mat)
    {
        if (mat == null) return null;
        var names = mat.GetTexturePropertyNames();
        if (names == null || names.Length == 0) return null;
        int Score(string n)
        {
            string s = n.ToLowerInvariant();
            int score = 0;
            if (s == "_basemap") score += 100;
            if (s == "_maintex") score += 90;
            if (s == "_basecolormap") score += 85;
            if (s.Contains("albedo")) score += 80;
            if (s.Contains("diffuse")) score += 70;
            if (s.Contains("color") && s.Contains("map")) score += 60;
            if (s.Contains("base")) score += 50;
            if (s.Contains("main")) score += 40;
            if (s.Contains("tex")) score += 10;
            if (mat.GetTexture(n) != null) score += 200;
            return score;
        }
        string best = names.OrderByDescending(n => Score(n)).FirstOrDefault();
        if (string.IsNullOrEmpty(best) || mat.GetTexture(best) == null)
        {
            string[] fallbacks = { "_BaseMap", "_MainTex", "_BaseColorMap", "_BaseColorTexture", "_Albedo", "_AlbedoMap", "MainTex" };
            foreach (var fb in fallbacks) { if (mat.HasProperty(fb) && mat.GetTexture(fb) != null) return fb; }
        }
        return best;
    }

    private void AssignTextureToAllLikelyMainProps(Material mat, Texture tex)
    {
        if (mat == null || tex == null) return;
        if (string.IsNullOrEmpty(mainTexPropName)) mainTexPropName = FindMainTexturePropertyName(mat);
        if (!string.IsNullOrEmpty(mainTexPropName) && mat.HasProperty(mainTexPropName))
        { Undo.RecordObject(mat, "Assign MainTex (Primary)"); mat.SetTexture(mainTexPropName, tex); }
        string[] candidates = { "_BaseMap", "_MainTex", "_BaseColorMap", "_BaseColorTexture", "_Albedo", "_AlbedoMap", "MainTex" };
        foreach (var n in candidates) { if (mat.HasProperty(n)) { Undo.RecordObject(mat, $"Assign {n}"); mat.SetTexture(n, tex); } }
        Undo.RecordObject(mat, "Assign mainTexture"); mat.mainTexture = tex;
        EditorUtility.SetDirty(mat);
    }

    private void RestoreOriginalMaterial()
    {
        if (targetRenderer == null || originalMaterialAsset == null) return;
        var mats = targetRenderer.sharedMaterials;
        if (mats == null || mats.Length == 0 || selectedMatIndex < 0 || selectedMatIndex >= mats.Length) return;
        if (mats[selectedMatIndex] == originalMaterialAsset) return;

        mats[selectedMatIndex] = originalMaterialAsset;
        targetRenderer.sharedMaterials = mats;
#if UNITY_2021_3_OR_NEWER
        PrefabUtility.RecordPrefabInstancePropertyModifications(targetRenderer);
#endif
        AssetDatabase.SaveAssets();
        SceneView.RepaintAll();
        Repaint();
        sourceMaterial = originalMaterialAsset;
    }

    private void SaveOutputOnly()
    {
        var L = EditorLanguageManager.Instance;
        if (paintRT == null) { infoMessage = L.GetText("InfoNoMesh"); infoType = MessageType.Warning; return; }
        if (string.IsNullOrEmpty(outputAssetPath)) UpdateDefaultOutputPath();

        WriteRTToPng_ColorCorrect(paintRT, outputAssetPath);
        AssetDatabase.ImportAsset(outputAssetPath, ImportAssetOptions.ForceUpdate);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();

        appliedToOutput = true;
        infoMessage = string.Format(L.GetText("SaveOk"), outputAssetPath);
        infoType = MessageType.Info;
    }

    private Texture2D ReadRT_Linear(RenderTexture rt)
    {
        var prev = RenderTexture.active;
        Texture2D texLinear = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, true);
        RenderTexture.active = rt;
        texLinear.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        texLinear.Apply(false, false);
        RenderTexture.active = prev;
        return texLinear;
    }

    private Texture2D ToSRGBIfNeeded(Texture2D linearTex)
    {
        bool needGamma = QualitySettings.activeColorSpace == ColorSpace.Linear;
        if (!needGamma) return linearTex;

        var pixels = linearTex.GetPixels();
        for (int i = 0; i < pixels.Length; i++)
        {
            var c = pixels[i];
            c.r = Mathf.LinearToGammaSpace(c.r);
            c.g = Mathf.LinearToGammaSpace(c.g);
            c.b = Mathf.LinearToGammaSpace(c.b);
            pixels[i] = c;
        }
        Texture2D srgbTex = new Texture2D(linearTex.width, linearTex.height, TextureFormat.RGBA32, false, false);
        srgbTex.SetPixels(pixels);
        srgbTex.Apply(false, false);
        Object.DestroyImmediate(linearTex);
        return srgbTex;
    }

    private void WriteRTToPng_ColorCorrect(RenderTexture rt, string assetPath)
    {
        if (rt == null || string.IsNullOrEmpty(assetPath)) return;
        string dir = Path.GetDirectoryName(assetPath).Replace('\\', '/');
        if (!AssetDatabase.IsValidFolder(dir)) CreateFoldersRecursively(dir);

        Texture2D texLinear = ReadRT_Linear(rt);
        Texture2D texSRGB = ToSRGBIfNeeded(texLinear);

        string assetsFull = Application.dataPath;
        string sub = assetPath.Substring("Assets".Length).TrimStart('/', '\\');
        string fullPath = Path.Combine(assetsFull, sub);
        File.WriteAllBytes(fullPath, texSRGB.EncodeToPNG());
        Object.DestroyImmediate(texSRGB);
    }

    private void ExportPaintLayerOnly()
    {
        var L = EditorLanguageManager.Instance;
        if (paintRT == null) { infoMessage = L.GetText("InfoNoMesh"); infoType = MessageType.Warning; return; }

        string baseName = (originalTexture != null) ? originalTexture.name : "未命名";
        foreach (char ch in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(ch, '_');
        string folder = _targetResourceDir;
        if (!AssetDatabase.IsValidFolder(folder)) CreateFoldersRecursively(folder);
        string layerPath = $"{folder}/{baseName}_绘制层.png";

        if (baseRT == null)
        {
            WriteRTToPng_ColorCorrect(paintRT, layerPath);
        }
        else
        {
            Texture2D texBaseLin = ReadRT_Linear(baseRT);
            Texture2D texPaintLin = ReadRT_Linear(paintRT);
            int w = texPaintLin.width, h = texPaintLin.height;
            Texture2D outLin = new Texture2D(w, h, TextureFormat.RGBA32, false, true);
            const float TH = 1f / 255f;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    Color pb = texBaseLin.GetPixel(x, y);
                    Color pp = texPaintLin.GetPixel(x, y);
                    float dr = Mathf.Abs(pp.r - pb.r);
                    float dg = Mathf.Abs(pp.g - pb.g);
                    float db = Mathf.Abs(pp.b - pb.b);
                    float a = Mathf.Max(dr, Mathf.Max(dg, db));
                    if (a <= TH) outLin.SetPixel(x, y, new Color(0, 0, 0, 0));
                    else outLin.SetPixel(x, y, new Color(pp.r, pp.g, pp.b, Mathf.Clamp01(a)));
                }
            }
            outLin.Apply(false, false);
            Texture2D outSRGB = ToSRGBIfNeeded(outLin);

            string assetsFull = Application.dataPath;
            string sub = layerPath.Substring("Assets".Length).TrimStart('/', '\\');
            string fullPath = Path.Combine(assetsFull, sub);
            File.WriteAllBytes(fullPath, outSRGB.EncodeToPNG());

            Object.DestroyImmediate(texBaseLin);
            Object.DestroyImmediate(texPaintLin);
            Object.DestroyImmediate(outSRGB);
        }

        AssetDatabase.ImportAsset(layerPath, ImportAssetOptions.ForceUpdate);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();

        infoMessage = string.Format(L.GetText("ExportLayerOk"), layerPath);
        infoType = MessageType.Info;
        Repaint();
    }

    private void LocateAndCreateResourceDirectory()
    {
        var L = EditorLanguageManager.Instance;
        string dir = _targetResourceDir;
        if (!AssetDatabase.IsValidFolder(dir))
        {
            CreateFoldersRecursively(dir);
            AssetDatabase.Refresh();
            Debug.Log(LOG_PREFIX + L.GetText("LocateCreateOk") + dir);
        }
        else
        {
            Debug.Log(LOG_PREFIX + L.GetText("LocateExists") + dir);
        }
        var obj = AssetDatabase.LoadAssetAtPath<Object>(dir);
        if (obj != null)
        {
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = obj;
            EditorGUIUtility.PingObject(obj);
            AssetDatabase.OpenAsset(obj);
            Debug.Log(LOG_PREFIX + L.GetText("LocateOpenOk") + dir);
        }
    }

    private void UpdateDefaultOutputPath()
    {
        string baseName = (originalTexture != null) ? originalTexture.name : "未命名";
        foreach (char ch in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(ch, '_');
        string folder = _targetResourceDir;
        if (!AssetDatabase.IsValidFolder(folder)) CreateFoldersRecursively(folder);
        outputAssetPath = $"{folder}/{baseName}.png";
    }

    private void CleanupUnappliedOutputIfAny()
    {
        if (!appliedToOutput && outputCreatedInSession && !string.IsNullOrEmpty(outputAssetPath))
        {
            var obj = AssetDatabase.LoadAssetAtPath<Object>(outputAssetPath);
            if (obj != null)
            {
                AssetDatabase.DeleteAsset(outputAssetPath);
                AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
            }
            outputCreatedInSession = false;
        }
    }

    private void ClearCurrentEdits()
    {
        var L = EditorLanguageManager.Instance;
        if (paintRT == null || baseRT == null) { infoMessage = L.GetText("InfoNoMesh"); infoType = MessageType.Warning; return; }
        BeginStrokeSnapshot();
        Graphics.Blit(baseRT, paintRT);
        EndStrokeApply(); _strokeActive = false;
        infoMessage = "已清空改动";
        infoType = MessageType.Info;
        Repaint();
    }

    private RenderTexture NewRT(int w, int h)
    {
        var rt = new RenderTexture(w, h, 0, RTFormat, 0)
        {
            enableRandomWrite = true,
            filterMode = FilterMode.Bilinear,
            wrapMode = TextureWrapMode.Clamp
        };
#if UNITY_2022_1_OR_NEWER
        rt.graphicsFormat = GFormat;
#endif
        rt.Create();
        return rt;
    }
    private RenderTexture NewRTLike(RenderTexture src) => NewRT(src.width, src.height);

    private void ClearRT(RenderTexture rt, Color c)
    {
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(true, true, c);
        RenderTexture.active = prev;
    }

    private void CreateFoldersRecursively(string path)
    {
        string[] parts = path.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
    }

    private RenderTexture GetSnapshotTarget()
    {
        return rtPool.Get(paintRT.width, paintRT.height);
    }

    private void ClearUndoStacks()
    {
        while (undoStack.Count > 0) { var t = undoStack.Pop(); if (t) rtPool.Return(t); }
        while (redoStack.Count > 0) { var t = redoStack.Pop(); if (t) rtPool.Return(t); }
    }

    private void ReleaseGPUResources()
    {
        ReleaseMaskBuffers();
        if (paintRT) { paintRT.Release(); paintRT = null; }
        if (baseRT) { baseRT.Release(); baseRT = null; }
        ClearUndoStacks();
    }

    private void EnsureCheckerTexture()
    {
        if (checkerTex != null) return;
        int s = 16;
        checkerTex = new Texture2D(s, s, TextureFormat.RGBA32, false, true);
        checkerTex.wrapMode = TextureWrapMode.Repeat;
        checkerTex.filterMode = FilterMode.Point;
        Color c0 = new Color(0.85f, 0.85f, 0.85f, 1f);
        Color c1 = new Color(0.95f, 0.95f, 0.95f, 1f);
        for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
                checkerTex.SetPixel(x, y, ((x < s / 2) ^ (y < s / 2)) ? c0 : c1);
        checkerTex.Apply(false, true);
        checkerTex.hideFlags = HideFlags.HideAndDontSave;
    }

    private void DrawTiledTexture(Rect rect, Texture2D tex, float tileSize)
    {
        if (tex == null) return;
        float u1 = rect.width / tileSize;
        float v1 = rect.height / tileSize;
        GUI.DrawTextureWithTexCoords(rect, tex, new Rect(0, 0, u1, v1));
    }

    private void ResetView() { zoom = 1f; viewCenter = new Vector2(0.5f, 0.5f); }

    private void LoadComputeIfNeeded()
    {
        if (computeReady && csPaint != null) return;
        csPaint = null; computeReady = false;

        string[] guids = AssetDatabase.FindAssets("t:ComputeShader");
        ComputeShader candidate = null;
        foreach (var guid in guids)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var c = AssetDatabase.LoadAssetAtPath<ComputeShader>(path);
            if (c == null) continue;
            int kb = -1, kf = -1;
            try { kb = c.FindKernel("KBrush"); kf = c.FindKernel("KFillIsland"); }
            catch { kb = -1; kf = -1; }
            if (kb >= 0 && kf >= 0)
            {
                if (candidate == null) candidate = c;
                if (Path.GetFileNameWithoutExtension(path).ToLowerInvariant().Contains("lgcpaint"))
                { candidate = c; break; }
            }
        }
        if (candidate != null)
        {
            csPaint = candidate;
            kBrush = csPaint.FindKernel("KBrush");
            kFillIsland = csPaint.FindKernel("KFillIsland");
            computeReady = (kBrush >= 0 && kFillIsland >= 0);
        }
        else computeReady = false;
    }

    private void TryBindTarget(GameObject obj)
    {
        var L = EditorLanguageManager.Instance;
        RestoreOriginalMaterial();
        ReleaseGPUResources();
        targetRenderer = null; targetMesh = null;
        sharedMats = null; matSlotNames = null;
        sourceMaterial = null; mainTexPropName = null;
        originalMaterialAsset = null;
        originalTexture = null; originalAssetPath = null;
        outputAssetPath = null; outputCreatedInSession = false;
        appliedToOutput = false;
        ResetView();
        uvMaskDirty = true; islandsDirty = true; activeIslandId = -1;
        ClearUndoStacks();

        if (obj == null) { infoMessage = L.GetText("InfoWelcome"); infoType = MessageType.Info; return; }

        targetRenderer = obj.GetComponent<SkinnedMeshRenderer>();
        if (!targetRenderer) targetRenderer = obj.GetComponent<MeshRenderer>();
        if (!targetRenderer) { infoMessage = L.GetText("InfoNoRenderer"); infoType = MessageType.Warning; return; }

        MeshFilter mf = obj.GetComponent<MeshFilter>();
        if (mf && mf.sharedMesh) targetMesh = mf.sharedMesh;
        else if (targetRenderer is SkinnedMeshRenderer sk && sk.sharedMesh) targetMesh = sk.sharedMesh;
        if (!targetMesh || targetMesh.vertexCount == 0) { infoMessage = L.GetText("InfoNoMesh"); infoType = MessageType.Warning; return; }

        sharedMats = targetRenderer.sharedMaterials;
        if (sharedMats == null || sharedMats.Length == 0)
        {
            infoMessage = L.GetText("InfoNoMats"); infoType = MessageType.Info;
            SetupGPUTargets(null, 1024, 1024);
            UpdateDefaultOutputPath();
            return;
        }

        matSlotNames = sharedMats.Select((m, i) => $"{i}: {(m ? m.name : "<null>")}").ToArray();
        selectedMatIndex = Mathf.Clamp(selectedMatIndex, 0, sharedMats.Length - 1);
        BindMaterialSlot();
    }

    private void BindMaterialSlot()
    {
        var L = EditorLanguageManager.Instance;
        ResetView();
        appliedToOutput = false;
        sourceMaterial = null; mainTexPropName = null;
        originalMaterialAsset = null;
        originalTexture = null; originalAssetPath = null;
        outputAssetPath = null; outputCreatedInSession = false;
        uvMaskDirty = true; islandsDirty = true; activeIslandId = -1;
        ClearUndoStacks();

        if (sharedMats == null || sharedMats.Length == 0 || selectedMatIndex < 0 || selectedMatIndex >= sharedMats.Length)
        {
            infoMessage = L.GetText("InfoSlotInvalid"); infoType = MessageType.Warning;
            UpdateDefaultOutputPath();
            return;
        }

        originalMaterialAsset = sharedMats[selectedMatIndex];
        if (!originalMaterialAsset)
        {
            infoMessage = string.Format(L.GetText("InfoSlotEmpty"), selectedMatIndex);
            infoType = MessageType.Warning;
            UpdateDefaultOutputPath();
            SetupGPUTargets(null, 1024, 1024);
            return;
        }

        sourceMaterial = originalMaterialAsset;
        mainTexPropName = FindMainTexturePropertyName(sourceMaterial);

        Texture tex = null;
        if (!string.IsNullOrEmpty(mainTexPropName)) tex = sourceMaterial.GetTexture(mainTexPropName);
        if (tex != null)
        {
            originalTexture = tex;
            originalAssetPath = AssetDatabase.GetAssetPath(tex);
            int w = Mathf.Max(32, tex.width);
            int h = Mathf.Max(32, tex.height);
            SetupGPUTargets(originalTexture, w, h);
            UpdateDefaultOutputPath();

            bool existedBefore = AssetDatabase.LoadAssetAtPath<Object>(outputAssetPath) != null;
            WriteRTToPng_ColorCorrect(paintRT, outputAssetPath);
            outputCreatedInSession = !existedBefore;

            infoMessage = string.Format(L.GetText("InfoReadMainTexOk"), tex.name, outputAssetPath);
            infoType = MessageType.Info;
        }
        else
        {
            SetupGPUTargets(null, 1024, 1024);
            UpdateDefaultOutputPath();
            bool existedBefore = AssetDatabase.LoadAssetAtPath<Object>(outputAssetPath) != null;
            WriteRTToPng_ColorCorrect(paintRT, outputAssetPath);
            outputCreatedInSession = !existedBefore;

            infoMessage = string.Format(L.GetText("InfoNoMainTex"), outputAssetPath);
            infoType = MessageType.Info;
        }
    }

    private void SetupGPUTargets(Texture srcTex, int w, int h)
    {
        ReleaseGPUResources();
        paintRT = NewRT(w, h);
        baseRT = NewRT(w, h);
        if (srcTex != null)
        {
            var prev = RenderTexture.active;
            Graphics.Blit(srcTex, baseRT);
            Graphics.Blit(srcTex, paintRT);
            RenderTexture.active = prev;
        }
        else
        {
            ClearRT(baseRT, Color.clear);
            ClearRT(paintRT, Color.clear);
        }
        if (!baseRT.enableRandomWrite) baseRT.enableRandomWrite = true; baseRT.Create();
        if (!paintRT.enableRandomWrite) paintRT.enableRandomWrite = true; paintRT.Create();
        RebuildUVCoverageMaskIfNeeded();
        RebuildIslandIdsIfNeeded();
        UploadMaskBuffers();
        LoadComputeIfNeeded();
    }

    private void BeginStrokeSnapshot()
    {
        if (paintRT == null) return;
        RenderTexture shot = GetSnapshotTarget();
        Graphics.Blit(paintRT, shot);
        undoStack.Push(shot);
        while (undoStack.Count > UNDO_LIMIT)
        {
            var old = undoStack.ToArray().Last();
            if (old != null) { undoStack = new Stack<RenderTexture>(undoStack.Where(t => t != old)); rtPool.Return(old); break; }
        }
        while (redoStack.Count > 0) { var r = redoStack.Pop(); if (r) rtPool.Return(r); }
    }

    private void EndStrokeApply()
    {
        if (enableRealTimePreview && paintRT != null) ApplyMaterialInstanceWithTexture(paintRT);
    }

    private void DoUndo_Internal()
    {
        if (undoStack.Count == 0 || paintRT == null) return;
        RenderTexture cur = GetSnapshotTarget();
        Graphics.Blit(paintRT, cur);
        redoStack.Push(cur);
        var prev = undoStack.Pop();
        Graphics.Blit(prev, paintRT);
        rtPool.Return(prev);
        if (enableRealTimePreview) ApplyMaterialInstanceWithTexture(paintRT);
        Repaint();
    }

    private void DoRedo_Internal()
    {
        if (redoStack.Count == 0 || paintRT == null) return;
        RenderTexture cur = GetSnapshotTarget();
        Graphics.Blit(paintRT, cur);
        undoStack.Push(cur);
        var next = redoStack.Pop();
        Graphics.Blit(next, paintRT);
        rtPool.Return(next);
        if (enableRealTimePreview) ApplyMaterialInstanceWithTexture(paintRT);
        Repaint();
    }

    private void SetupCommonMasks(int kernel)
    {
        bool maskAvailable = EnsureMaskBuffersUpToDate();
        if (maskAvailable)
        {
            csPaint.SetBuffer(kernel, "_UvMask", uvMaskBuffer);
            csPaint.SetBuffer(kernel, "_IslandId", islandIdBuffer);
        }
        int enableBoundary = (uvBoundaryLimitEnabled && maskAvailable) ? 1 : 0;
        bool canIso = islandIsolationEnabled && maskAvailable && activeIslandId >= 0;
        int enableIsolation = canIso ? 1 : 0;
        int activeId = canIso ? activeIslandId : -1;
        csPaint.SetInt("_EnableBoundary", enableBoundary);
        csPaint.SetInt("_EnableIsolation", enableIsolation);
        csPaint.SetInt("_ActiveIslandId", activeId);
    }

    private void SetupCommonBrushParams(int kernel, RectInt rect, Vector2Int center, int radius, bool isBrushMode)
    {
        csPaint.SetInts("_RectXYWH", new int[] { rect.x, rect.y, rect.width, rect.height });
        csPaint.SetInts("_TexSize", new int[] { paintRT.width, paintRT.height });
        csPaint.SetInts("_Center", new int[] { center.x, center.y });
        csPaint.SetInt("_Radius", radius);
        csPaint.SetFloat("_Hardness", Mathf.Clamp01(brushHardness));
        csPaint.SetFloats("_Color", new float[] { brushColor.r, brushColor.g, brushColor.b, 1f });
        csPaint.SetFloat("_Opacity", Mathf.Clamp01(brushOpacity));
        csPaint.SetInt("_Mode", isBrushMode ? 0 : 1);
    }

    private void SetupCommonFillParams(int kernel, int islandId, bool isFillMode)
    {
        csPaint.SetInts("_TexSize", new int[] { paintRT.width, paintRT.height });
        csPaint.SetInt("_FillIslandId", islandId);
        csPaint.SetInt("_FillMode", isFillMode ? 0 : 1);
        csPaint.SetFloats("_Color", new float[] { brushColor.r, brushColor.g, brushColor.b, 1f });
        csPaint.SetFloat("_Opacity", Mathf.Clamp01(brushOpacity));
    }
}
#endif
// ------------------------------
// 底部版本行（copilote + LGC + v2.4.1：对称编辑禁画 + 锚点放大自适应 + 光标反馈 + Foldout本地化）
// ------------------------------
