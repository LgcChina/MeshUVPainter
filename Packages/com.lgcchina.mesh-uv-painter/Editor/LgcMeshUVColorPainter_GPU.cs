#if UNITY_EDITOR
// LGC网格UV绘画工具（GPU版） - Unity 2022+
// LgcMeshUVColorPainter_GPU.cs (v2.6.0)
// 【自定义图案盖印系统】
// - 新增 Stamp 模式，与画笔模式分离
// - 图章跟随UV缩放，可拖拽裁剪
// - 对称预览、光标反馈、撤销支持
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
    internal const string VERSION = "v2.6.0";
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
    private Material sourceMaterial;
    private Material originalMaterialAsset;
    private string mainTexPropName = null;

    private Texture originalTexture;
    private string originalAssetPath;

    private int previewMaterialSlotIndex = -1;

    // ====== GPU 三RT架构 ======
    private RenderTexture baseRT;
    private RenderTexture paintLayerRT;
    private RenderTexture previewRT;
    private const RenderTextureFormat RTFormat = RenderTextureFormat.ARGB32;
    private const GraphicsFormat GFormat = GraphicsFormat.R8G8B8A8_UNorm;

    private bool previewDirty = true;

    private ComputeShader csPaint;
    private int kBrush;
    private int kFillIsland;
    private int kComposite;
    private int kCompositeWhite;
    private int kStamp;
    private bool computeReady = false;

    private int maskW, maskH;
    private int[] uvMask;
    private int[] islandIdMask;
    private int islandsCount = 0;
    private int activeIslandId = -1;
    private bool uvMaskDirty = true;
    private bool islandsDirty = true;

    private ComputeBuffer uvMaskBuffer;
    private ComputeBuffer islandIdBuffer;

    private Rect rightPanelRect;
    private Rect previewRect;
    private Vector2 lastUV = new Vector2(-1, -1);
    private bool isPainting = false;
    private bool _strokeActive = false;

    private bool foldUVPanel = false;
    private bool showUVOverlay = true;
    private Color uvColor = new Color(0.65f, 0.65f, 0.65f, 1f);
    private float uvAlpha = 0.85f;

    // ★ 新增 Stamp 模式
    private enum PaintMode { Brush, Erase, Fill, IslandErase, Stamp }
    private PaintMode mode = PaintMode.Brush;

    private bool uvBoundaryLimitEnabled = true;
    private bool islandIsolationEnabled = true;
    private int uvBoundaryExpandPixels = 5;

    private bool enableRealTimePreview = false;

    // ====== 对称绘画 ======
    private bool symmetryEnabled = false;
    private bool symmetryLocked = false;
    private LgcSymmetryGizmoController symmetryController;

    // ====== 视图控制 ======
    private LgcUVViewController viewController;

    // ====== 笔刷参数 ======
    private LgcBrushSettings brush;

    // ====== GPU执行器 ======
    private LgcBrushExecutor brushExecutor;

    // ★ ====== 图章系统 ======
    private bool foldStampSection = false;
    private LgcStampSettings stampSettings;
    private LgcStampController stampController;
    private LgcStampExecutor stampExecutor;

    // ★ ====== 撤销/重做（改用队列管理，FIFO）=====
    // ★ 撤销/重做：使用 List 实现 LIFO（栈），超限时删除最旧记录
    private List<RenderTexture> undoList = new List<RenderTexture>();
    private Stack<RenderTexture> redoStack = new Stack<RenderTexture>();
    private const int UNDO_LIMIT = 50;

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
                    if (rt) SafeReleaseRT(rt);
                }
            }
            _dict.Clear();
        }
    }
    private RTPool rtPool;

    private static void SafeReleaseRT(RenderTexture rt)
    {
        if (rt == null) return;
        rt.Release();
        DestroyImmediate(rt);
    }

    private static Texture2D checkerTex;

    private string outputAssetPath;
    private bool appliedToOutput = false;
    private bool outputCreatedInSession = false;

    private bool exportPaintLayerOnWhite = false;

    private string infoMessage;
    private MessageType infoType = MessageType.Info;

    private Vector2 leftScroll;

    private string _targetResourceDir = "Assets/LGC/Tools/UV绘画/输出图片";

    // ====== 左侧折叠面板 ======
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

        symmetryController = new LgcSymmetryGizmoController();
        viewController = new LgcUVViewController();
        brush = new LgcBrushSettings();

        stampSettings = new LgcStampSettings();
        stampController = new LgcStampController(stampSettings);
        previewDirty = true;
    }

    private void OnDestroy()
    {
        RestoreOriginalMaterial();
        CleanupUnappliedOutputIfAny();
        ReleaseGPUResources();
        if (rtPool != null) rtPool.DisposeAll();
        LgcMeshUVOverlayDrawer.ClearCache();
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
        _ = infoType;
        var L = EditorLanguageManager.Instance;
        var wrapHelp = new GUIStyle(EditorStyles.helpBox) { wordWrap = true };
        EditorGUILayout.LabelField(infoMessage, wrapHelp);

        // 目标选择
        foldTargetSelection = EditorGUILayout.Foldout(foldTargetSelection, L.GetText("TargetSelectionFoldout"), true);
        if (foldTargetSelection)
        {
            EditorGUI.indentLevel++;
            EditorGUI.BeginChangeCheck();
            targetObject = (GameObject)EditorGUILayout.ObjectField(L.GetText("TargetObject"), targetObject, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck()) TryBindTarget(targetObject);

            using (new EditorGUI.DisabledScope(targetObject == null || targetMesh == null || targetRenderer == null))
            {
                if (sharedMats != null && sharedMats.Length > 0)
                {
                    int newIndex = EditorGUILayout.Popup(L.GetText("MatSlot"), selectedMatIndex, matSlotNames);
                    if (newIndex != selectedMatIndex)
                    {
                        ForceRestorePreviewMaterial();
                        selectedMatIndex = newIndex;
                        BindMaterialSlot();
                    }
                }
                else
                {
                    EditorGUILayout.LabelField(L.GetText("MatSlot"), L.GetText("MatSlotNone"));
                }
                if (GUILayout.Button(L.GetText("RefreshSlot"))) BindMaterialSlot();
            }
            EditorGUI.indentLevel--;
        }

        // 模式
        foldModeSection = EditorGUILayout.Foldout(foldModeSection, L.GetText("ModeSectionFoldout"), true);
        if (foldModeSection)
        {
            using (new EditorGUI.DisabledScope(paintLayerRT == null || targetRenderer == null))
            {
                bool newRealtime = EditorGUILayout.ToggleLeft(L.GetText("RealtimePreviewToggle"), enableRealTimePreview);
                if (newRealtime != enableRealTimePreview)
                {
                    enableRealTimePreview = newRealtime;
                    if (enableRealTimePreview && paintLayerRT != null)
                    {
                        MarkPreviewDirty();
                        EnsurePreview();
                        ApplyMaterialInstanceWithTexture(previewRT);
                        infoMessage = L.GetText("RealtimeOn"); infoType = MessageType.Info;
                    }
                    else
                    {
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

        // 笔刷
        foldBrushSection = EditorGUILayout.Foldout(foldBrushSection, L.GetText("BrushSectionFoldout"), true);
        if (foldBrushSection)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.Space(8);
            GUILayout.Label(L.GetText("BrushAndFill"), EditorStyles.boldLabel);
            brush.Color = EditorGUILayout.ColorField(L.GetText("BrushOrFillColor"), brush.Color);
            brush.Size = EditorGUILayout.IntSlider(L.GetText("BrushRadius"), brush.Size, 1, 256);
            brush.Opacity = EditorGUILayout.Slider(L.GetText("Opacity"), Mathf.Clamp01(brush.Opacity), 0f, 1f);
            brush.Hardness = EditorGUILayout.Slider(L.GetText("Hardness"), Mathf.Clamp01(brush.Hardness), 0f, 1f);
            EditorGUI.indentLevel--;
        }

        // 边界与隔离
        foldBoundarySection = EditorGUILayout.Foldout(foldBoundarySection, L.GetText("BoundarySectionFoldout"), true);
        if (foldBoundarySection)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.Space(8);
            GUILayout.Label(L.GetText("BoundaryAndIsolationTitle"), EditorStyles.boldLabel);

            uvBoundaryLimitEnabled = EditorGUILayout.ToggleLeft(L.GetText("ToggleUVBoundaryLimit"), uvBoundaryLimitEnabled);

            using (new EditorGUI.DisabledScope(!uvBoundaryLimitEnabled))
            {
                EditorGUI.BeginChangeCheck();
                int newExpand = EditorGUILayout.DelayedIntField(
                    L.GetText("BoundaryExpandPixels"),
                    uvBoundaryExpandPixels
                );
                if (EditorGUI.EndChangeCheck())
                {
                    uvBoundaryExpandPixels = Mathf.Clamp(newExpand, 0, 128);
                    uvMaskDirty = true;
                    islandsDirty = true;
                    ReleaseMaskBuffers();
                    EnsureMaskBuffersUpToDate();
                    if (computeReady && paintLayerRT != null &&
                        uvMaskBuffer != null && islandIdBuffer != null)
                    {
                        brushExecutor = new LgcBrushExecutor(csPaint, kBrush, kFillIsland, paintLayerRT, uvMaskBuffer, islandIdBuffer);
                        stampExecutor = new LgcStampExecutor(csPaint, kStamp, paintLayerRT, uvMaskBuffer, islandIdBuffer);
                    }
                    else
                    {
                        brushExecutor = null;
                        stampExecutor = null;
                    }
                    MarkPreviewDirty();
                }
            }

            islandIsolationEnabled = EditorGUILayout.ToggleLeft(L.GetText("ToggleIslandIsolation"), islandIsolationEnabled);
            EditorGUI.indentLevel--;
        }

        // 对称
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

        // ★ 图章区域（默认折叠）
        foldStampSection = EditorGUILayout.Foldout(foldStampSection, "盖印图案", true);
        if (foldStampSection)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.Space(4);

            stampSettings.StampTexture = (Texture2D)EditorGUILayout.ObjectField(
                "图案贴图",
                stampSettings.StampTexture,
                typeof(Texture2D),
                false);

            stampSettings.Opacity = EditorGUILayout.Slider(
                "不透明度",
                stampSettings.Opacity,
                0f, 1f);

            stampSettings.ShowPreview = EditorGUILayout.Toggle(
                "显示预览",
                stampSettings.ShowPreview);

            // ★ 修改：对称盖印 Toggle 自动启用主对称
            // ★ 对称盖印
            EditorGUI.BeginChangeCheck();
            bool newSym = EditorGUILayout.Toggle("对称盖印", stampSettings.EnableSymmetry);
            if (EditorGUI.EndChangeCheck())
            {
                stampSettings.EnableSymmetry = newSym;
                if (newSym && !symmetryEnabled)
                {
                    symmetryEnabled = true;
                    Repaint();
                }
            }

            // ★ 图案镜像（仅在对称盖印启用时显示）
            using (new EditorGUI.DisabledScope(!stampSettings.EnableSymmetry))
            {
                EditorGUI.indentLevel++;
                stampSettings.MirrorPattern = EditorGUILayout.Toggle("图案镜像", stampSettings.MirrorPattern);
                EditorGUI.indentLevel--;
            }

            using (new EditorGUI.DisabledScope(stampSettings.StampTexture == null))
            {
                if (GUILayout.Button("应用盖印"))
                {
                    ApplyStamp();
                }
            }

            EditorGUILayout.Space(4);
            EditorGUI.indentLevel--;
        }

        // UV叠加
        foldUVPanel = EditorGUILayout.Foldout(foldUVPanel, L.GetText("UVOverlayFoldout"), true);
        if (foldUVPanel)
        {
            EditorGUI.indentLevel++;
            showUVOverlay = EditorGUILayout.Toggle(L.GetText("ShowUVOverlay"), showUVOverlay);
            uvColor = EditorGUILayout.ColorField(L.GetText("UVLineColor"), uvColor);
            uvAlpha = EditorGUILayout.Slider(L.GetText("UVOverlayStrength"), uvAlpha, 0f, 1f);
            EditorGUI.indentLevel--;
        }

        // 保存导出
        foldSaveSection = EditorGUILayout.Foldout(foldSaveSection, L.GetText("SaveSectionFoldout"), true);
        if (foldSaveSection)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.Space(8);
            GUILayout.Label(L.GetText("GroupApplySaveLocate"), EditorStyles.boldLabel);
            if (GUILayout.Button(L.GetText("LocateProjectResBtn"))) LocateAndCreateResourceDirectory();

            using (new EditorGUI.DisabledScope(paintLayerRT == null || targetRenderer == null))
            {
                if (GUILayout.Button(L.GetText("SaveOutputFull"))) SaveOutputOnly();
                using (new GUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(L.GetText("ExportPaintLayer"))) ExportPaintLayerOnly();
                    exportPaintLayerOnWhite = GUILayout.Toggle(exportPaintLayerOnWhite, L.GetText("ExportWithWhiteBG"), GUILayout.Width(120));
                }
                EditorGUILayout.Space(4);
                if (GUILayout.Button(L.GetText("ClearCurrentEdits"))) ClearCurrentEdits();
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
        bool isStamp = mode == PaintMode.Stamp;

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

        // ★ 盖印模式按钮
        GUI.color = isStamp ? new Color(0.9f, 0.85f, 1f, 1f) : defaultColor;
        if (GUILayout.Button("盖印模式"))
        {
            if (mode != PaintMode.Stamp)
            {
                mode = PaintMode.Stamp;
                foldStampSection = true;
                Repaint();
            }
        }

        GUI.color = defaultColor;
    }

    private void DrawRightPanel(Rect containerRect)
    {
        var L = EditorLanguageManager.Instance;

        const float topBarH = 28f;
        Rect topBar = new Rect(containerRect.x, containerRect.y, containerRect.width, topBarH);
        Rect btnRect = new Rect(topBar.x + (topBar.width - 100f) * 0.5f, topBar.y + 4f, 100f, 20f);
        using (new EditorGUI.DisabledScope(paintLayerRT == null))
        {
            if (GUI.Button(btnRect, L.GetText("ResetViewBtn"))) { viewController.ResetView(); Repaint(); }
        }

        Rect areaRect = new Rect(containerRect.x, containerRect.y + topBarH + 2f,
                                 containerRect.width, containerRect.height - topBarH - 2f);

        if (paintLayerRT == null)
        {
            GUI.Label(new Rect(areaRect.x + 8, areaRect.y + 8, areaRect.width - 16, 20),
                L.GetText("NoPaintTargetHint"), EditorStyles.boldLabel);
            return;
        }

        if (previewRT == null || previewRT.width != paintLayerRT.width || previewRT.height != paintLayerRT.height)
            RecreatePreviewRT();

        EnsurePreview();

        float cw = Mathf.Max(50f, areaRect.width - 8f);
        float ch = Mathf.Max(50f, areaRect.height - 8f);
        float aspect = (float)previewRT.width / Mathf.Max(1, previewRT.height);
        float previewW = cw, previewH = previewW / aspect;
        if (previewH > ch) { previewH = ch; previewW = previewH * aspect; }
        previewRect = new Rect(areaRect.x + (areaRect.width - previewW) * 0.5f,
                               areaRect.y + (areaRect.height - previewH) * 0.5f,
                               previewW, previewH);

        var e = Event.current;
        if (e != null)
        {
            bool hover = previewRect.Contains(e.mousePosition);
            if (e.type == EventType.MouseMove)
            {
                if (hover && focusedWindow == this)
                    Repaint();
            }
            else if (e.type == EventType.MouseEnterWindow || e.type == EventType.MouseLeaveWindow)
            {
                Repaint();
            }
        }

        if (Event.current.type == EventType.Repaint && checkerTex != null)
            DrawTiledTexture(previewRect, checkerTex, 16);

        viewController.HandleNavigation(previewRect);
        Rect viewRect = viewController.GetViewRect();

        GUI.DrawTextureWithTexCoords(previewRect, previewRT,
            new Rect(viewRect.xMin, viewRect.yMin, viewRect.width, viewRect.height));

        if (showUVOverlay && targetMesh != null)
            LgcMeshUVOverlayDrawer.DrawUVOverlay(previewRect, viewRect, targetMesh, uvColor, uvAlpha);

        if (symmetryEnabled)
            symmetryController.DrawAndHandle(previewRect, viewRect, symmetryLocked, viewController.Zoom, true);

        // ★ 图章预览和交互仅在 Stamp 模式下显示
        if (mode == PaintMode.Stamp && stampController != null)
        {
            stampController.DrawPreview(previewRect, viewRect, viewController, symmetryController);
        }

        HandlePaintingEvents(viewRect);
        DrawBrushPreview(previewRect, viewRect);

        // ★ 图章交互仅在 Stamp 模式下处理
        if (mode == PaintMode.Stamp && stampController != null)
        {
            stampController.HandleInput(previewRect, viewRect, viewController, symmetryController);
        }
    }

    private void DrawBrushPreview(Rect rect, Rect viewRect)
    {
        if (paintLayerRT == null) return;
        Vector2 mouse = Event.current.mousePosition; if (!rect.Contains(mouse)) return;
        float scaleX = rect.width / Mathf.Max(1e-6f, viewRect.width * paintLayerRT.width);
        float scaleY = rect.height / Mathf.Max(1e-6f, viewRect.height * paintLayerRT.height);
        float guiRadius = brush.Size * (scaleX + scaleY) * 0.5f;
        Handles.BeginGUI();
        Handles.color = new Color(brush.Color.r, brush.Color.g, brush.Color.b, 0.9f);
        Handles.DrawWireDisc(mouse, Vector3.forward, guiRadius);
        Handles.DrawWireDisc(mouse, Vector3.forward, Mathf.Max(1f, guiRadius - 1.5f));
        Handles.EndGUI();
    }

    private void HandlePaintingEvents(Rect viewRect)
    {
        // ★ 如果处于 Stamp 模式，禁止画笔
        if (mode == PaintMode.Stamp) return;
        // ★ 如果图章正在编辑，禁止画笔（但图章编辑仅在 Stamp 模式下发生，此防御保留）
        if (stampController != null && stampController.IsEditing) return;

        if (!computeReady || paintLayerRT == null || brushExecutor == null) return;
        Event e = Event.current; if (e == null) return;

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
                MarkPreviewDirty();
                e.Use(); Repaint();
            }
            else if (e.type == EventType.MouseDrag && isPainting)
            {
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
                MarkPreviewDirty();
                e.Use(); Repaint();
            }
            else if (e.type == EventType.MouseUp && isPainting)
            {
                isPainting = false;
                EndStrokeApply(); _strokeActive = false;
                lastUV = new Vector2(-1, -1);
                activeIslandId = -1;
                MarkPreviewDirty();
                e.Use(); Repaint();
            }
        }
    }

    private void BeginStrokeSnapshotIfNeeded()
    {
        if (_strokeActive) return; BeginStrokeSnapshot(); _strokeActive = true;
    }

    private static bool IsUVInside01(Vector2 uv) => uv.x >= 0f && uv.x <= 1f && uv.y >= 0f && uv.y <= 1f;

    #region Preview Dirty Management

    private void MarkPreviewDirty() => previewDirty = true;

    private void EnsurePreview()
    {
        if (previewDirty)
        {
            CompositePreview();
            previewDirty = false;
        }
    }

    #endregion

    #region Mask Management

    private bool EnsureMaskBuffersUpToDate()
    {
        if (paintLayerRT == null) return false;
        if (targetMesh == null)
        {
            ReleaseMaskBuffers();
            return false;
        }
        RebuildUVCoverageMaskIfNeeded();

        if (uvMask == null || islandIdMask == null ||
            uvMask.Length != paintLayerRT.width * paintLayerRT.height ||
            islandIdMask.Length != paintLayerRT.width * paintLayerRT.height)
        {
            ReleaseMaskBuffers();
            return false;
        }

        if (uvMaskBuffer == null ||
            islandIdBuffer == null ||
            uvMaskBuffer.count != paintLayerRT.width * paintLayerRT.height ||
            islandIdBuffer.count != paintLayerRT.width * paintLayerRT.height)
        {
            UploadMaskBuffers();
        }
        return (uvMaskBuffer != null && islandIdBuffer != null);
    }

    private void RebuildUVCoverageMaskIfNeeded()
    {
        if (paintLayerRT == null || targetMesh == null)
        {
            uvMask = null; islandIdMask = null; islandsCount = 0; return;
        }
        if (!uvMaskDirty && uvMask != null && maskW == paintLayerRT.width && maskH == paintLayerRT.height) return;

        maskW = paintLayerRT.width; maskH = paintLayerRT.height;
        uvMask = new int[maskW * maskH];

        var uvs = targetMesh.uv; var tris = targetMesh.triangles;
        if (uvs == null || uvs.Length == 0 || tris == null || tris.Length == 0)
        {
            islandIdMask = null; islandsCount = 0; uvMaskDirty = false; islandsDirty = false; return;
        }

        for (int i = 0; i < tris.Length; i += 3)
        {
            int i0 = tris[i], i1 = tris[i + 1], i2 = tris[i + 2];
            if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= uvs.Length || i1 >= uvs.Length || i2 >= uvs.Length) continue;
            RasterizeTriangleToMask(uvs[i0], uvs[i1], uvs[i2]);
        }

        islandsDirty = true;
        RebuildIslandIdsIfNeeded();

        if (uvBoundaryExpandPixels > 0)
        {
            ExpandUvMaskAndIslands(uvBoundaryExpandPixels);
        }

        uvMaskDirty = false;
        islandsDirty = false;
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

    private void ExpandUvMaskAndIslands(int pixels)
    {
        if (uvMask == null || islandIdMask == null) return;
        if (pixels <= 0) return;

        int w = maskW, h = maskH;
        int total = w * h;

        int[] srcMask = uvMask;
        int[] srcIds = islandIdMask;

        int[] tmpMask = new int[total];
        int[] tmpIds = new int[total];

        for (int step = 0; step < pixels; step++)
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int idx = y * w + x;
                    if (srcMask[idx] == 1)
                    {
                        tmpMask[idx] = 1;
                        tmpIds[idx] = srcIds[idx];
                        continue;
                    }

                    bool expanded = false;
                    int neighborId = -1;

                    for (int oy = -1; oy <= 1; oy++)
                    {
                        for (int ox = -1; ox <= 1; ox++)
                        {
                            if (ox == 0 && oy == 0) continue;
                            int nx = x + ox, ny = y + oy;
                            if (nx >= 0 && nx < w && ny >= 0 && ny < h)
                            {
                                int nidx = ny * w + nx;
                                if (srcMask[nidx] == 1)
                                {
                                    expanded = true;
                                    if (neighborId == -1)
                                        neighborId = srcIds[nidx];
                                }
                            }
                        }
                    }

                    if (expanded)
                    {
                        tmpMask[idx] = 1;
                        tmpIds[idx] = neighborId >= 0 ? neighborId : -1;
                    }
                    else
                    {
                        tmpMask[idx] = 0;
                        tmpIds[idx] = -1;
                    }
                }
            }

            int[] swapMask = srcMask;
            srcMask = tmpMask;
            tmpMask = swapMask;

            int[] swapIds = srcIds;
            srcIds = tmpIds;
            tmpIds = swapIds;
        }

        uvMask = srcMask;
        islandIdMask = srcIds;
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
        if (paintLayerRT == null || islandIdMask == null) return -1;
        int w = paintLayerRT.width, h = paintLayerRT.height;
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
        if (paintLayerRT == null || uvMask == null || islandIdMask == null) return;
        int count = paintLayerRT.width * paintLayerRT.height;
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

    #endregion

    #region Composite Preview

    private void CompositePreview()
    {
        if (baseRT == null || paintLayerRT == null || previewRT == null) return;
        if (baseRT.width != previewRT.width || baseRT.height != previewRT.height) return;

        if (csPaint != null && kComposite >= 0)
        {
            csPaint.SetTexture(kComposite, "_BaseRT", baseRT);
            csPaint.SetTexture(kComposite, "_PaintLayerRT", paintLayerRT);
            csPaint.SetTexture(kComposite, "_PreviewRT", previewRT);
            csPaint.SetInts("_TexSize", previewRT.width, previewRT.height);
            csPaint.Dispatch(kComposite,
                Mathf.Max(1, (previewRT.width + 15) / 16),
                Mathf.Max(1, (previewRT.height + 15) / 16),
                1);
        }
        else
        {
            if (baseRT != null)
                Graphics.Blit(baseRT, previewRT);
            else
                ClearRT(previewRT, Color.gray);
        }
    }

    #endregion

    #region RT Management

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

    private void ClearRT(RenderTexture rt, Color c)
    {
        var prev = RenderTexture.active;
        RenderTexture.active = rt;
        GL.Clear(true, true, c);
        RenderTexture.active = prev;
    }

    private void RecreatePreviewRT()
    {
        if (previewRT != null)
        {
            SafeReleaseRT(previewRT);
        }
        previewRT = NewRT(paintLayerRT.width, paintLayerRT.height);
        MarkPreviewDirty();
    }

    #endregion

    #region Material / Texture Management

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

    private void ForceRestorePreviewMaterial()
    {
        if (targetRenderer == null) return;
        if (originalMaterialAsset == null) return;
        if (previewMaterialSlotIndex < 0) return;

        var mats = targetRenderer.sharedMaterials;
        if (mats == null) return;
        if (previewMaterialSlotIndex >= mats.Length) return;

        if (mats[previewMaterialSlotIndex] != originalMaterialAsset)
        {
            mats[previewMaterialSlotIndex] = originalMaterialAsset;
            targetRenderer.sharedMaterials = mats;
#if UNITY_2021_3_OR_NEWER
            PrefabUtility.RecordPrefabInstancePropertyModifications(targetRenderer);
#endif
        }

        previewMaterialSlotIndex = -1;
        sourceMaterial = null;
    }

    #endregion

    #region Save / Export

    private void SaveOutputOnly()
    {
        var L = EditorLanguageManager.Instance;
        if (paintLayerRT == null) { infoMessage = L.GetText("InfoNoMesh"); infoType = MessageType.Warning; return; }
        if (string.IsNullOrEmpty(outputAssetPath)) UpdateDefaultOutputPath();

        MarkPreviewDirty();
        EnsurePreview();
        WriteRTToPng_ColorCorrect(previewRT, outputAssetPath);
        AssetDatabase.ImportAsset(outputAssetPath, ImportAssetOptions.ForceUpdate);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();

        appliedToOutput = true;
        infoMessage = string.Format(L.GetText("SaveOk"), outputAssetPath);
        infoType = MessageType.Info;
    }

    private void ExportPaintLayerOnly()
    {
        var L = EditorLanguageManager.Instance;
        if (paintLayerRT == null) { infoMessage = L.GetText("InfoNoMesh"); infoType = MessageType.Warning; return; }

        string baseName = (originalTexture != null) ? originalTexture.name : "未命名";
        foreach (char ch in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(ch, '_');
        string folder = _targetResourceDir;
        if (!AssetDatabase.IsValidFolder(folder)) CreateFoldersRecursively(folder);

        string suffix = exportPaintLayerOnWhite ? "_绘制层_白底" : "_绘制层";
        string layerPath = $"{folder}/{baseName}{suffix}.png";

        if (exportPaintLayerOnWhite)
        {
            RenderTexture resultRT = null;
            try
            {
                resultRT = NewRT(paintLayerRT.width, paintLayerRT.height);
                if (csPaint != null && kCompositeWhite >= 0)
                {
                    csPaint.SetTexture(kCompositeWhite, "_PaintLayerRT", paintLayerRT);
                    csPaint.SetTexture(kCompositeWhite, "_ResultRT", resultRT);
                    csPaint.SetInts("_TexSize", resultRT.width, resultRT.height);
                    csPaint.Dispatch(kCompositeWhite,
                        Mathf.Max(1, (resultRT.width + 15) / 16),
                        Mathf.Max(1, (resultRT.height + 15) / 16),
                        1);
                    WriteRTToPng_ColorCorrect(resultRT, layerPath);
                }
                else
                {
                    WriteRTToPng_ColorCorrect(paintLayerRT, layerPath);
                }
            }
            finally
            {
                if (resultRT != null)
                {
                    SafeReleaseRT(resultRT);
                }
            }
        }
        else
        {
            WriteRTToPng_ColorCorrect(paintLayerRT, layerPath);
        }

        AssetDatabase.ImportAsset(layerPath, ImportAssetOptions.ForceUpdate);

        var importer = AssetImporter.GetAtPath(layerPath) as TextureImporter;
        if (importer != null)
        {
            importer.textureType = TextureImporterType.Default;
            importer.alphaSource = TextureImporterAlphaSource.FromInput;
            importer.alphaIsTransparency = true;
            importer.sRGBTexture = true;
            importer.mipmapEnabled = false;
            importer.SaveAndReimport();
        }

        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        var exportedAsset = AssetDatabase.LoadAssetAtPath<Object>(layerPath);
        if (exportedAsset != null)
        {
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = exportedAsset;
            EditorGUIUtility.PingObject(exportedAsset);
        }
        else
        {
            LocateAndCreateResourceDirectory();
        }

        infoMessage = string.Format(L.GetText("ExportLayerOk"), layerPath);
        infoType = MessageType.Info;
        Repaint();
    }

    #endregion

    #region Utils

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
        }
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

    private void ClearCurrentEdits()
    {
        var L = EditorLanguageManager.Instance;
        if (paintLayerRT == null || baseRT == null) { infoMessage = L.GetText("InfoNoMesh"); infoType = MessageType.Warning; return; }
        BeginStrokeSnapshot();
        ClearRT(paintLayerRT, new Color(0, 0, 0, 0));
        MarkPreviewDirty();
        EnsurePreview();
        EndStrokeApply(); _strokeActive = false;
        infoMessage = L.GetText("EditCleared");
        infoType = MessageType.Info;
        Repaint();
    }

    #endregion

    #region Undo/Redo（修正：使用队列管理，删除最旧记录）

    private RenderTexture GetSnapshotTarget()
    {
        return rtPool.Get(paintLayerRT.width, paintLayerRT.height);
    }

    private void BeginStrokeSnapshot()
    {
        if (paintLayerRT == null) return;
        RenderTexture snap = GetSnapshotTarget();
        Graphics.Blit(paintLayerRT, snap);

        undoList.Add(snap);
        while (undoList.Count > UNDO_LIMIT)
        {
            var old = undoList[0];
            undoList.RemoveAt(0);
            if (old != null) rtPool.Return(old);
        }

        while (redoStack.Count > 0)
        {
            var old = redoStack.Pop();
            if (old != null) rtPool.Return(old);
        }
    }

    private void EndStrokeApply()
    {
        MarkPreviewDirty();
        EnsurePreview();
        if (enableRealTimePreview && paintLayerRT != null)
        {
            ApplyMaterialInstanceWithTexture(previewRT);
        }
        SceneView.RepaintAll();
        Repaint();
    }

    private void DoUndo_Internal()
    {
        var L = EditorLanguageManager.Instance;
        if (undoList.Count == 0)
        {
            infoMessage = L.GetText("UndoEmpty");
            infoType = MessageType.Info;
            return;
        }
        // 取最后一项（最新）
        RenderTexture prev = undoList[undoList.Count - 1];
        undoList.RemoveAt(undoList.Count - 1);

        RenderTexture curr = GetSnapshotTarget();
        Graphics.Blit(paintLayerRT, curr);
        redoStack.Push(curr);
        Graphics.Blit(prev, paintLayerRT);
        rtPool.Return(prev);

        MarkPreviewDirty();
        EndStrokeApply();
        infoMessage = L.GetText("UndoOk");
        infoType = MessageType.Info;
    }

    private void DoRedo_Internal()
    {
        var L = EditorLanguageManager.Instance;
        if (redoStack.Count == 0)
        {
            infoMessage = L.GetText("RedoEmpty");
            infoType = MessageType.Info;
            return;
        }
        RenderTexture next = redoStack.Pop();
        RenderTexture curr = GetSnapshotTarget();
        Graphics.Blit(paintLayerRT, curr);
        undoList.Add(curr);
        Graphics.Blit(next, paintLayerRT);
        rtPool.Return(next);

        MarkPreviewDirty();
        EndStrokeApply();
        infoMessage = L.GetText("RedoOk");
        infoType = MessageType.Info;
    }

    #endregion

    #region Stamp System

    private void ApplyStamp()
    {
        if (stampSettings.StampTexture == null) return;
        if (stampExecutor == null) return;
        if (paintLayerRT == null) return;

        BeginStrokeSnapshot();

        // 主图案（不镜像）
        stampExecutor.ApplyStamp(
            stampSettings.StampTexture,
            stampSettings.UV,
            stampSettings.Scale,
            stampSettings.Rotation,
            stampSettings.Opacity,
            uvBoundaryLimitEnabled,
            islandIsolationEnabled,
            activeIslandId,
            false  // 主图案不翻转
        );

        // ★ 对称图案（根据设置决定是否翻转）
        if (stampSettings.EnableSymmetry && symmetryEnabled && symmetryController != null)
        {
            Vector2 mirroredUV = symmetryController.MirrorUV(stampSettings.UV);
            float mirroredRotation = -stampSettings.Rotation;
            stampExecutor.ApplyStamp(
                stampSettings.StampTexture,
                mirroredUV,
                stampSettings.Scale,
                mirroredRotation,
                stampSettings.Opacity,
                uvBoundaryLimitEnabled,
                islandIsolationEnabled,
                activeIslandId,
                stampSettings.MirrorPattern  // ★ 传入镜像标志
            );
        }

        EndStrokeApply();
        infoMessage = "盖印已应用 (可 Ctrl+Z 撤销)";
        infoType = MessageType.Info;
    }

    #endregion

    #region RT / GPU Setup

    private void SetupGPUTargets(Texture2D sourceTex, int w, int h)
    {
        ReleaseGPUResources();
        baseRT = NewRT(w, h);
        paintLayerRT = NewRT(w, h);
        previewRT = NewRT(w, h);

        if (sourceTex != null)
        {
            Graphics.Blit(sourceTex, baseRT);
        }
        else
        {
            ClearRT(baseRT, Color.white);
        }
        ClearRT(paintLayerRT, new Color(0, 0, 0, 0));

        MarkPreviewDirty();

        uvMaskDirty = true;
        islandsDirty = true;
        EnsureMaskBuffersUpToDate();

        if (computeReady)
        {
            // ★ 修正：创建时传入 Buffer
            stampExecutor = new LgcStampExecutor(csPaint, kStamp, paintLayerRT, uvMaskBuffer, islandIdBuffer);
        }
    }

    private void ReleaseGPUResources()
    {
        ReleaseMaskBuffers();
        if (paintLayerRT != null)
        {
            SafeReleaseRT(paintLayerRT);
            paintLayerRT = null;
        }
        if (baseRT != null)
        {
            SafeReleaseRT(baseRT);
            baseRT = null;
        }
        if (previewRT != null)
        {
            SafeReleaseRT(previewRT);
            previewRT = null;
        }
        ClearUndoStacks();
        previewDirty = true;
        stampExecutor = null;
    }

    private void ClearUndoStacks()
    {
        foreach (var rt in undoList)
        {
            if (rt != null) SafeReleaseRT(rt);
        }
        undoList.Clear();

        while (redoStack.Count > 0)
        {
            var rt = redoStack.Pop();
            if (rt != null) SafeReleaseRT(rt);
        }
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
            int kb = -1, kf = -1, kc = -1, kcw = -1, ks = -1;
            try
            {
                kb = c.FindKernel("KBrush");
                kf = c.FindKernel("KFillIsland");
                kc = c.FindKernel("KComposite");
                kcw = c.FindKernel("KCompositeWhite");
                ks = c.FindKernel("KStamp");
            }
            catch { }
            if (kb >= 0 && kf >= 0 && kc >= 0 && kcw >= 0 && ks >= 0)
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
            kComposite = csPaint.FindKernel("KComposite");
            kCompositeWhite = csPaint.FindKernel("KCompositeWhite");
            kStamp = csPaint.FindKernel("KStamp");
            computeReady = (kBrush >= 0 && kFillIsland >= 0 && kComposite >= 0 && kCompositeWhite >= 0 && kStamp >= 0);
        }
        else computeReady = false;
    }

    #endregion

    #region Bind Target

    private void TryBindTarget(GameObject obj)
    {
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

    private void BindMaterialSlot()
    {
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

        if (computeReady && paintLayerRT != null &&
            uvMaskBuffer != null && islandIdBuffer != null)
        {
            brushExecutor = new LgcBrushExecutor(csPaint, kBrush, kFillIsland, paintLayerRT, uvMaskBuffer, islandIdBuffer);
            // ★ 修正：传入 Buffer
            stampExecutor = new LgcStampExecutor(csPaint, kStamp, paintLayerRT, uvMaskBuffer, islandIdBuffer);
        }
        else
        {
            brushExecutor = null;
            stampExecutor = null;
            infoMessage = L.GetText("InfoComputeMissing");
            infoType = MessageType.Error;
        }

        MarkPreviewDirty();
        EnsurePreview();
        Repaint();
    }

    #endregion

    #region Texture Read / Write

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

    #endregion
}
#endif