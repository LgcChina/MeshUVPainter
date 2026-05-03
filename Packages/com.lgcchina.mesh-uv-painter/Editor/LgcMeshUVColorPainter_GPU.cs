#if UNITY_EDITOR
// LGC网格UV绘画工具（GPU版） - Unity 2022+
// LgcMeshUVColorPainter_GPU.cs (v2.4.3)
//
// 本版（v2.4.3）更新：
// 1) 所有UI文本统一使用EditorLanguageManager.GetText()获取，移除硬编码文本
// 2) 左侧工具栏增加各功能分类的Foldout折叠支持
// 3) 【核心修复】解决实时预览材质在对象/材质切换时未正确还原的槽位错位问题
//
// 说明：已模块化拆分 UV绘制/对称Gizmo/视图控制/笔刷参数/GPU执行
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
    internal const string VERSION = "v2.4.3"; // ★ 每次修改请更新版本号
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

    // ★【新增字段】记录实时预览创建时所使用的材质槽位
    private int previewMaterialSlotIndex = -1;

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

    // 边界/隔离（默认开，但当无掩码可用时自动降级为关闭）
    private bool uvBoundaryLimitEnabled = true; // UV 边界限制（默认开）
    private bool islandIsolationEnabled = true; // 孤岛隔离（默认开）

    // 实时预览（移动到"边界与隔离"组）
    private bool enableRealTimePreview = false;

    // ====== 对称绘画（模块化） ======
    private bool symmetryEnabled = false;
    private bool symmetryLocked = false;
    private LgcSymmetryGizmoController symmetryController; // 新增：对称控制器

    // ====== 视图控制（模块化） ======
    private LgcUVViewController viewController; // 新增：视图控制器

    // ====== 笔刷参数（模块化） ======
    private LgcBrushSettings brush; // 新增：笔刷参数

    // ====== GPU执行器（模块化） ======
    private LgcBrushExecutor brushExecutor; // 新增：GPU执行器

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

    // ====== 左侧折叠面板控制 ======
    private bool foldTargetSelection = true;
    private bool foldModeSection = true;
    private bool foldBrushSection = true;
    private bool foldBoundarySection = true;
    private bool foldSymmetrySection = false;
    private bool foldSaveSection = true;

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

        // 初始化模块化控制器
        symmetryController = new LgcSymmetryGizmoController();
        viewController = new LgcUVViewController();
        brush = new LgcBrushSettings();
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
        _ = infoType; // 显式读一次，消除 IDE “未使用”警告
        var L = EditorLanguageManager.Instance;
        var wrapHelp = new GUIStyle(EditorStyles.helpBox) { wordWrap = true };
        EditorGUILayout.LabelField(infoMessage, wrapHelp);

        // 目标选择区域（折叠）
        foldTargetSelection = EditorGUILayout.Foldout(foldTargetSelection, L.GetText("TargetSelectionFoldout"), true);
        if (foldTargetSelection)
        {
            EditorGUI.indentLevel++;
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
                        // ★ 修复：在切换材质槽前先恢复之前的预览
                        ForceRestorePreviewMaterial();
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
            EditorGUI.indentLevel--;
        }

        // 模式选择区域（折叠）
        foldModeSection = EditorGUILayout.Foldout(foldModeSection, L.GetText("ModeSectionFoldout"), true);

        if (foldModeSection)
        {
            using (new EditorGUI.DisabledScope(paintRT == null || targetRenderer == null))
            {
                bool newRealtime = EditorGUILayout.ToggleLeft(L.GetText("RealtimePreviewToggle"), enableRealTimePreview);
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
                        // ★ 修复：当关闭实时预览时，强制恢复材质
                        ForceRestorePreviewMaterial();
                        infoMessage = L.GetText("RealtimeOff"); infoType = MessageType.Info;
                    }
                }
            }
            EditorGUI.indentLevel++;
            EditorGUILayout.Space(8);
            GUILayout.Label(L.GetText("ModeTitle"), EditorStyles.boldLabel);
            DrawModeButtons();

            EditorGUI.indentLevel--;
        }

        // 笔刷与填充区域（折叠）
        foldBrushSection = EditorGUILayout.Foldout(foldBrushSection, L.GetText("BrushSectionFoldout"), true);
        if (foldBrushSection)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.Space(8);
            GUILayout.Label(L.GetText("BrushAndFill"), EditorStyles.boldLabel);
            // 笔刷参数绑定到模块化类
            brush.Color = EditorGUILayout.ColorField(L.GetText("BrushOrFillColor"), brush.Color);
            brush.Size = EditorGUILayout.IntSlider(L.GetText("BrushRadius"), brush.Size, 1, 256);
            brush.Opacity = EditorGUILayout.Slider(L.GetText("Opacity"), Mathf.Clamp01(brush.Opacity), 0f, 1f);
            brush.Hardness = EditorGUILayout.Slider(L.GetText("Hardness"), Mathf.Clamp01(brush.Hardness), 0f, 1f);
            EditorGUI.indentLevel--;
        }

        // 边界与隔离区域（折叠）
        foldBoundarySection = EditorGUILayout.Foldout(foldBoundarySection, L.GetText("BoundarySectionFoldout"), true);
        if (foldBoundarySection)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.Space(8);
            GUILayout.Label(L.GetText("BoundaryAndIsolationTitle"), EditorStyles.boldLabel);
            uvBoundaryLimitEnabled = EditorGUILayout.ToggleLeft(L.GetText("ToggleUVBoundaryLimit"), uvBoundaryLimitEnabled);
            islandIsolationEnabled = EditorGUILayout.ToggleLeft(L.GetText("ToggleIslandIsolation"), islandIsolationEnabled);

            EditorGUI.indentLevel--;
        }

        // 对称绘画区域（折叠）
        foldSymmetrySection = EditorGUILayout.Foldout(foldSymmetrySection, L.GetText("SymmetrySectionFoldout"), true);
        if (foldSymmetrySection)
        {
            EditorGUI.indentLevel++;
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
                        symmetryController.Reset();
                        Repaint();
                    }
                }
                var help = new GUIStyle(EditorStyles.helpBox) { wordWrap = true };
                EditorGUILayout.LabelField(L.GetText("SymmetryHelp"), help);
            }
            EditorGUI.indentLevel--;
        }

        // UV 叠加显示区域（折叠）
        foldUVPanel = EditorGUILayout.Foldout(foldUVPanel, L.GetText("UVOverlayFoldout"), true);
        if (foldUVPanel)
        {
            EditorGUI.indentLevel++;
            showUVOverlay = EditorGUILayout.Toggle(L.GetText("ShowUVOverlay"), showUVOverlay);
            uvColor = EditorGUILayout.ColorField(L.GetText("UVLineColor"), uvColor);
            uvAlpha = EditorGUILayout.Slider(L.GetText("UVOverlayStrength"), uvAlpha, 0f, 1f);
            EditorGUI.indentLevel--;
        }

        // 保存/导出/定位区域（折叠）
        foldSaveSection = EditorGUILayout.Foldout(foldSaveSection, L.GetText("SaveSectionFoldout"), true);
        if (foldSaveSection)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.Space(8);
            GUILayout.Label(L.GetText("GroupApplySaveLocate"), EditorStyles.boldLabel);
            if (GUILayout.Button(L.GetText("LocateProjectResBtn")))
            {
                LocateAndCreateResourceDirectory();
            }
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
                if (GUILayout.Button(L.GetText("ClearCurrentEdits")))
                {
                    ClearCurrentEdits();
                }
            }

            EditorGUI.indentLevel--;
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
            if (GUI.Button(btnRect, L.GetText("ResetViewBtn"))) { viewController.ResetView(); Repaint(); }
        }

        Rect areaRect = new Rect(containerRect.x, containerRect.y + topBarH + 2f,
                                 containerRect.width, containerRect.height - topBarH - 2f);

        if (paintRT == null)
        {
            GUI.Label(new Rect(areaRect.x + 8, areaRect.y + 8, areaRect.width - 16, 20),
                L.GetText("NoPaintTargetHint"), EditorStyles.boldLabel);
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

        // ★ 修正：先处理导航事件，再获取视图矩形
        viewController.HandleNavigation(previewRect);
        Rect viewRect = viewController.GetViewRect();

        GUI.DrawTextureWithTexCoords(previewRect, paintRT,
            new Rect(viewRect.xMin, viewRect.yMin, viewRect.width, viewRect.height));

        // UV叠加绘制（模块化调用）
        if (showUVOverlay && targetMesh != null)
            LgcMeshUVOverlayDrawer.DrawUVOverlay(previewRect, viewRect, targetMesh, uvColor, uvAlpha);

        // 对称Gizmo绘制（模块化调用）
        if (symmetryEnabled)
            // ★ 修正：添加 repaintOnChange 参数
            symmetryController.DrawAndHandle(previewRect, viewRect, symmetryLocked, viewController.Zoom, true);

        // 视图导航（模块化）- 已移到前面
        HandlePaintingEvents(viewRect);
        DrawBrushPreview(previewRect, viewRect);
    }

    private void DrawBrushPreview(Rect rect, Rect viewRect)
    {
        if (paintRT == null) return;
        Vector2 mouse = Event.current.mousePosition; if (!rect.Contains(mouse)) return;
        float scaleX = rect.width / Mathf.Max(1e-6f, viewRect.width * paintRT.width);
        float scaleY = rect.height / Mathf.Max(1e-6f, viewRect.height * paintRT.height);
        float guiRadius = brush.Size * (scaleX + scaleY) * 0.5f;
        Handles.BeginGUI();
        Handles.color = new Color(brush.Color.r, brush.Color.g, brush.Color.b, 0.9f);
        Handles.DrawWireDisc(mouse, Vector3.forward, guiRadius);
        Handles.DrawWireDisc(mouse, Vector3.forward, Mathf.Max(1f, guiRadius - 1.5f));
        Handles.EndGUI();
    }

    private void HandlePaintingEvents(Rect viewRect)
    {
        if (!computeReady || paintRT == null || brushExecutor == null) return;
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
            if (e.keyCode == KeyCode.LeftBracket || e.character == '[') { brush.Size = Mathf.Max(1, brush.Size - 1); e.Use(); Repaint(); }
            else if (e.keyCode == KeyCode.RightBracket || e.character == ']') { brush.Size = Mathf.Min(256, brush.Size + 1); e.Use(); Repaint(); }
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
                    if (island >= 0 && islandIdBuffer != null)
                    {
                        // GPU孤岛填充（模块化）
                        brushExecutor.FillIsland(island, mode == PaintMode.Fill, brush, uvBoundaryLimitEnabled);
                    }
                    else { infoMessage = EditorLanguageManager.Instance.GetText("InfoNoIslandData"); infoType = MessageType.Info; }
                    if (symmetryEnabled)
                    {
                        Vector2 muv = symmetryController.MirrorUV(uv);
                        if (IsUVInside01(muv))
                        {
                            int islandM = GetIslandIdAtUV_CPU(muv);
                            if (islandM >= 0 && islandIdBuffer != null)
                                brushExecutor.FillIsland(islandM, mode == PaintMode.Fill, brush, uvBoundaryLimitEnabled);
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
                // GPU笔刷绘制（模块化）
                brushExecutor.PaintAtUV(uv, mode == PaintMode.Brush, activeIslandId, uvBoundaryLimitEnabled, islandIsolationEnabled, brush);
                if (symmetryEnabled)
                {
                    Vector2 muv = symmetryController.MirrorUV(uv);
                    if (IsUVInside01(muv))
                    {
                        int prev = activeIslandId;
                        int iso = islandIsolationEnabled ? GetIslandIdAtUV_CPU(muv) : -1;
                        activeIslandId = iso;
                        brushExecutor.PaintAtUV(muv, mode == PaintMode.Brush, activeIslandId, uvBoundaryLimitEnabled, islandIsolationEnabled, brush);
                        activeIslandId = prev;
                    }
                }
                e.Use(); Repaint();
            }
            else if (e.type == EventType.MouseDrag && isPainting)
            {
                // GPU笔划绘制（模块化）
                brushExecutor.DrawStroke(lastUV, uv, mode == PaintMode.Brush, activeIslandId, uvBoundaryLimitEnabled, islandIsolationEnabled, brush);
                if (symmetryEnabled)
                {
                    Vector2 uvA = symmetryController.MirrorUV(lastUV);
                    Vector2 uvB = symmetryController.MirrorUV(uv);
                    if (IsUVInside01(uvA) || IsUVInside01(uvB))
                    {
                        int prev = activeIslandId;
                        int iso = islandIsolationEnabled ? GetIslandIdAtUV_CPU(uvB) : -1;
                        activeIslandId = iso;
                        brushExecutor.DrawStroke(uvA, uvB, mode == PaintMode.Brush, activeIslandId, uvBoundaryLimitEnabled, islandIsolationEnabled, brush);
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

    private static bool IsUVInside01(Vector2 uv) => uv.x >= 0f && uv.x <= 1f && uv.y >= 0f && uv.y <= 1f;

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

    // ★ 修改 ApplyMaterialInstanceWithTexture 方法，在成功赋值后记录槽位
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

        // ★【关键修复】在成功应用材质实例后，记录当前槽位索引
        // 这是整个修复的核心：记录实时预览创建时使用的槽位
        previewMaterialSlotIndex = selectedMatIndex;
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

    // ★【修正版】强制恢复预览材质方法 - 使用记录的槽位索引
    private void ForceRestorePreviewMaterial()
    {
        // 没有有效上下文，直接返回
        if (targetRenderer == null) return;
        if (originalMaterialAsset == null) return;
        // ★ 修复：只有当存在预览槽位记录时才进行恢复
        if (previewMaterialSlotIndex < 0) return;

        var mats = targetRenderer.sharedMaterials;
        if (mats == null) return;
        // ★ 修复：检查记录的槽位索引是否有效
        if (previewMaterialSlotIndex >= mats.Length) return;

        // ★ 修复：只恢复记录的槽位，而不是当前选中的槽位
        if (mats[previewMaterialSlotIndex] != originalMaterialAsset)
        {
            mats[previewMaterialSlotIndex] = originalMaterialAsset;
            targetRenderer.sharedMaterials = mats;

#if UNITY_2021_3_OR_NEWER
            PrefabUtility.RecordPrefabInstancePropertyModifications(targetRenderer);
#endif
        }

        // 清理预览上下文
        previewMaterialSlotIndex = -1;
        sourceMaterial = null;
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
        infoMessage = L.GetText("EditCleared");
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

    // ★ 修改 TryBindTarget 方法 - 第一行必须调用 ForceRestorePreviewMaterial
    private void TryBindTarget(GameObject obj)
    {
        // ★ 第一行，必须在任何字段修改之前
        ForceRestorePreviewMaterial();

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
        viewController.ResetView();
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

    // ★ 修改 BindMaterialSlot 方法 - 第一行必须调用 ForceRestorePreviewMaterial
    private void BindMaterialSlot()
    {
        // ★ 第一行，必须先撤销旧槽位的实时预览
        ForceRestorePreviewMaterial();

        var L = EditorLanguageManager.Instance;
        viewController.ResetView();
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
        if (!string.IsNullOrEmpty(mainTexPropName))
            tex = sourceMaterial.GetTexture(mainTexPropName);
        if (tex == null)
            tex = sourceMaterial.mainTexture;

        if (tex is Texture2D t2d && t2d.width > 0 && t2d.height > 0)
        {
            originalTexture = t2d;
            originalAssetPath = AssetDatabase.GetAssetPath(t2d);
            SetupGPUTargets(t2d, t2d.width, t2d.height);
            infoMessage = string.Format(L.GetText("InfoBoundOk"), targetObject.name, selectedMatIndex, t2d.width, t2d.height);
            infoType = MessageType.Info;
        }
        else
        {
            originalTexture = null; originalAssetPath = null;
            SetupGPUTargets(null, 1024, 1024);
            infoMessage = string.Format(L.GetText("InfoNoTex"), selectedMatIndex);
            infoType = MessageType.Warning;
        }

        UpdateDefaultOutputPath();
        LoadComputeIfNeeded();
        EnsureMaskBuffersUpToDate();

        // ★ 强烈建议修改：确保GPU初始化具备原子性
        if (computeReady && paintRT != null && baseRT != null &&
            uvMaskBuffer != null && islandIdBuffer != null)
        {
            brushExecutor = new LgcBrushExecutor(csPaint, kBrush, kFillIsland, paintRT, baseRT, uvMaskBuffer, islandIdBuffer);
        }
        else
        {
            brushExecutor = null;
            infoMessage = L.GetText("InfoComputeMissing");
            infoType = MessageType.Error;
        }
        Repaint();
    }

    // ===================== 主脚本缺失方法补全 =====================
    private void SetupGPUTargets(Texture2D sourceTex, int w, int h)
    {
        ReleaseGPUResources();
        baseRT = NewRT(w, h);
        paintRT = NewRT(w, h);

        if (sourceTex != null)
        {
            Graphics.Blit(sourceTex, baseRT);
            Graphics.Blit(sourceTex, paintRT);
        }
        else
        {
            ClearRT(baseRT, Color.white);
            ClearRT(paintRT, Color.white);
        }

        uvMaskDirty = true;
        islandsDirty = true;
        EnsureMaskBuffersUpToDate();
    }

    private void BeginStrokeSnapshot()
    {
        if (paintRT == null) return;
        RenderTexture snap = GetSnapshotTarget();
        Graphics.Blit(paintRT, snap);

        undoStack.Push(snap);
        while (undoStack.Count > UNDO_LIMIT)
        {
            var old = undoStack.Pop();
            if (old) rtPool.Return(old);
        }

        while (redoStack.Count > 0)
        {
            var old = redoStack.Pop();
            if (old) rtPool.Return(old);
        }
    }

    private void EndStrokeApply()
    {
        if (enableRealTimePreview && paintRT != null)
        {
            ApplyMaterialInstanceWithTexture(paintRT);
        }
        SceneView.RepaintAll();
        Repaint();
    }

    private void DoUndo_Internal()
    {
        var L = EditorLanguageManager.Instance;
        if (undoStack.Count == 0)
        {
            infoMessage = L.GetText("UndoEmpty"); infoType = MessageType.Info;
            return;
        }
        RenderTexture prev = undoStack.Pop();
        RenderTexture curr = GetSnapshotTarget();
        Graphics.Blit(paintRT, curr);
        redoStack.Push(curr);
        Graphics.Blit(prev, paintRT);
        rtPool.Return(prev);

        EndStrokeApply();
        infoMessage = L.GetText("UndoOk"); infoType = MessageType.Info;
    }

    private void DoRedo_Internal()
    {
        var L = EditorLanguageManager.Instance;
        if (redoStack.Count == 0)
        {
            infoMessage = L.GetText("RedoEmpty"); infoType = MessageType.Info;
            return;
        }
        RenderTexture next = redoStack.Pop();
        RenderTexture curr = GetSnapshotTarget();
        Graphics.Blit(paintRT, curr);
        undoStack.Push(curr);
        Graphics.Blit(next, paintRT);
        rtPool.Return(next);

        EndStrokeApply();
        infoMessage = L.GetText("RedoOk"); infoType = MessageType.Info;
    }
}
#endif