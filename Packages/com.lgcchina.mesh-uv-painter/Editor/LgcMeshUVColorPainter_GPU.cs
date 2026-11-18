#if UNITY_EDITOR
// LGC网格UV绘画工具（GPU版） - Unity 2022+
// LgcMeshUVColorPainter_GPU.cs (v3.0 beta)
// v3.0 beta: Live multi-stop gradients, symmetry cards and reversible UV focus controls.
// v2.10.0: Unified symmetry, undoable layer mirror, configurable input and resolution export.
// v2.9.1: Restore render targets after GPU copies; capture stamp drags and isolate GUI state.
// 【材质组 UV 作用域】
// - UV 叠加、边界遮罩与孤岛按当前材质组生成
// - 支持全部子网格只读查看模式
// 【自定义图案盖印系统】
// - 新增 Stamp 模式，与画笔模式分离
// - 图章跟随UV缩放，可拖拽裁剪
// - 对称预览、光标反馈、撤销支持
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

public partial class LgcMeshUVColorPainter : EditorWindow
{
    internal const string VERSION = "v3.0 beta";
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
    private Material previewMaterial;
    private Renderer previewRenderer;
    private Material previewOriginalMaterial;
    private bool previewRepairPending;

    // ====== GPU 三RT架构 ======
    private RenderTexture baseRT;
    private RenderTexture paintLayerRT;
    private RenderTexture previewRT;
    private RenderTexture stampPreviewLayerRT;
    private RenderTexture stampPreviewRT;
    private LgcStampSettings cachedStampPreview;
    private LgcStampSettings suppressedStampPreview;
    private Vector2 cachedStampSymA, cachedStampSymB;
    private Vector2 suppressedStampSymA, suppressedStampSymB;
    private bool stampPreviewDirty = true;
    private const RenderTextureFormat RTFormat = RenderTextureFormat.ARGB32;
    private const GraphicsFormat GFormat = GraphicsFormat.R8G8B8A8_UNorm;

    private bool previewDirty = true;

    private ComputeShader csPaint;
    private int kBrush;
    private int kFillIsland;
    private int kComposite;
    private int kCompositeWhite;
    private int kStamp;
    private int kMirrorLayer;
    private int kResizeExport;
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
    private enum RightMouseAction { Erase, Pan, None }
    [SerializeField] private RightMouseAction rightMouseAction = RightMouseAction.Erase;
    [SerializeField] private int exportResolutionIndex;
    private int mirrorSourceSide;
    private int paintControlId;
    private int strokeButton;
    private bool strokeErase;
    private bool _strokeActive = false;

    private bool showUVOverlay = true;
    private Color uvColor = new Color(0.65f, 0.65f, 0.65f, 1f);
    private float uvAlpha = 0.85f;
    [SerializeField] private UVOverlayScope uvOverlayScope = UVOverlayScope.CurrentMaterialGroup;
    private int[] activeMaterialTriangles;
    private int[] inactiveUvTriangles;
    private bool materialUvScopeDirty = true;
    private bool sharedAtlasDetected = false;

    // ★ 新增 Stamp 模式
    private enum PaintMode { Brush, Erase, Fill, IslandErase, Stamp, Gradient, Selection }
    private enum UVOverlayScope { CurrentMaterialGroup, AllSubMeshes }
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
    private int activeToolTab = 0; // 0 Draw, 1 Stamp, 2 UV Boundary, 3 Selection/Gradient
    private readonly Vector2[] toolTabScroll = new Vector2[4];
    private PaintMode lastDrawMode = PaintMode.Brush;
    private PaintMode lastSelectionMode = PaintMode.Selection;
    private int eraseMode, fillScope;
    private bool drawAdvancedTab;
    private bool IsGradientEditing => activeToolTab == 3 && mode == PaintMode.Gradient || activeToolTab == 0 && mode == PaintMode.Erase && eraseMode == 2;
    private bool IsGradientErasing => activeToolTab == 0 && mode == PaintMode.Erase && eraseMode == 2;
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
            if ((long)rt.width * rt.height * 4 > 32L * 1024 * 1024) { SafeReleaseRT(rt); return; }
            var key = (rt.width, rt.height);
            if (!_dict.TryGetValue(key, out var s)) _dict[key] = s = new Stack<RenderTexture>();
            if (s.Count >= 1) { SafeReleaseRT(rt); return; }
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
        if (RenderTexture.active == rt) RenderTexture.active = null;
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


    private string _targetResourceDir = "Assets/LGC/Tools/UV绘画/输出图片";

    // ====== 左侧折叠面板 ======

    [MenuItem("LGC/LGC网格UV绘画工具（GPU）")]
    public static void ShowWindow()
    {
        var window = GetWindow<LgcMeshUVColorPainter>($"LGC网格UV绘画工具 {VERSION}");
        window.minSize = new Vector2(LEFT_PANEL_MIN_WIDTH + 600f, 560f);
        window.Show();
    }

    private void OnEnable()
    {
        titleContent = new GUIContent($"LGC网格UV绘画工具 {VERSION}");
        EditorApplication.update -= UpdateGradientEditor;
        EditorApplication.update += UpdateGradientEditor;
        Undo.undoRedoPerformed -= OnUnityUndoRedo;
        Undo.undoRedoPerformed += OnUnityUndoRedo;
        EditorSceneManager.sceneSaving -= OnSceneSaving;
        EditorSceneManager.sceneSaving += OnSceneSaving;
        AssemblyReloadEvents.beforeAssemblyReload -= RestoreOriginalMaterial;
        AssemblyReloadEvents.beforeAssemblyReload += RestoreOriginalMaterial;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
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
        if (targetObject != null) TryBindTarget(targetObject);
    }

    private void OnDisable()
    {
        EditorApplication.update -= UpdateGradientEditor;
        CleanupUnappliedOutputIfAny();
        Undo.undoRedoPerformed -= OnUnityUndoRedo;
        EditorSceneManager.sceneSaving -= OnSceneSaving;
        AssemblyReloadEvents.beforeAssemblyReload -= RestoreOriginalMaterial;
        EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
        EditorApplication.delayCall -= RepairPreviewAfterUndo;
        previewRepairPending = false;
        RestoreOriginalMaterial();
        ReleaseGPUResources();
        if (rtPool != null) rtPool.DisposeAll();
        LgcMeshUVOverlayDrawer.ClearCache();
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
        Matrix4x4 matrix = GUI.matrix;
        Color color = GUI.color, contentColor = GUI.contentColor, backgroundColor = GUI.backgroundColor;
        RenderTexture renderTarget = RenderTexture.active;
        float labelWidth = EditorGUIUtility.labelWidth;
        try { DrawWindowGUI(); }
        finally
        {
            GUI.matrix = matrix; GUI.color = color; GUI.contentColor = contentColor; GUI.backgroundColor = backgroundColor;
            EditorGUIUtility.labelWidth = labelWidth;
            RenderTexture.active = renderTarget;
        }
    }

    private void DrawWindowGUI()
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
        EditorGUIUtility.labelWidth = Mathf.Clamp(_leftPanelWidth * 0.44f, 135f, 190f);

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.BeginVertical(GUILayout.MinWidth(LEFT_PANEL_MIN_WIDTH), GUILayout.Width(_leftPanelWidth), GUILayout.ExpandHeight(true));
        GUILayout.Label(L.GetText("LeftPanelTitle"), EditorStyles.boldLabel);
        DrawLeftPanel();
        EditorGUILayout.EndVertical();

        EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        DrawCanvasHeader();
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
            string sizeInfo = GetTextureSizeInfo();
            GUILayout.Label(new GUIContent(sizeInfo,sizeInfo),EditorStyles.miniLabel,GUILayout.Width(_leftPanelWidth));
            GUILayout.FlexibleSpace();
            var authorTxt = L.GetText("BottomAuthorInfo");
            var verTxt = string.Format(L.GetText("BottomVersionInfo"), VERSION);
            var content = new GUIContent($"{authorTxt} {verTxt}", L.GetText("ClickToSeeMore"));
            Rect r = GUILayoutUtility.GetRect(content,labelStyle,GUILayout.Height(18));
            EditorGUIUtility.AddCursorRect(r,MouseCursor.Link);
            if(GUI.Button(r,content,labelStyle))LgcAuthorInfoWindow.OpenWindow();
            GUILayout.FlexibleSpace();
            GUILayout.Label(GetTextureViewInfo(),EditorStyles.miniLabel,GUILayout.Width(64));
        }
    }

    private void HandleGlobalShortcuts(Event e)
    {
        if (e == null) return;
        if (e.type != EventType.KeyDown) return;
        if (focusedWindow != this || EditorGUIUtility.editingTextField) return;
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

    private void DrawCanvasHeader()
    {
        var L = EditorLanguageManager.Instance;
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.Label(L.GetText("RightPanelTitle"), EditorStyles.boldLabel, GUILayout.Width(58));
            string hint = L.GetText(IsGradientEditing ? "CanvasGradientHint" : activeToolTab == 2 ? "CanvasUVSelectionHint" : activeToolTab == 1 ? "CanvasStampHint" : mode == PaintMode.Selection ? "CanvasSelectionHint" : paintSelectionCount > 0 ? "CanvasSelectedPaintHint" : "CanvasPaintHint");
            Rect hintRect = GUILayoutUtility.GetRect(20f, 18f, GUILayout.ExpandWidth(true));
            GUI.Label(hintRect, new GUIContent(hint, hint), EditorStyles.miniLabel);

        }
    }

    private string GetTextureViewInfo() => $"{viewController.Zoom:0.##}x";
    private string GetTextureSizeInfo()
    {
        string original=originalTexture!=null?$"{originalTexture.width} X {originalTexture.height}":"—";
        string work=paintLayerRT!=null?$"{paintLayerRT.width} X {paintLayerRT.height}":"—";
        return $"{T("OriginalSize")}{original} - {T("WorkingSize")}{work}";
    }

    private void DrawLeftPanel()
    {
        DrawTargetPanel();
        DrawToolTabs();
        GUILayout.Space(4);
        DrawOperationStatus();
        DrawOutputPanel();
    }

    private void DrawTargetPanel()
    {
        var L = EditorLanguageManager.Instance;
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            GUILayout.Label(L.GetText("TargetSelectionFoldout"), EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            targetObject = (GameObject)EditorGUILayout.ObjectField(L.GetText("TargetObject"), targetObject, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck()) TryBindTarget(targetObject);
            using (new EditorGUI.DisabledScope(targetRenderer == null || targetMesh == null))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (sharedMats != null && sharedMats.Length > 0)
                {
                    int next = EditorGUILayout.Popup(L.GetText("MatSlot"), selectedMatIndex, matSlotNames);
                    if (next != selectedMatIndex)
                    {
                        ForceRestorePreviewMaterial();
                        selectedMatIndex = next;
                        BindMaterialSlot();
                    }
                }
                else EditorGUILayout.LabelField(L.GetText("MatSlot"), L.GetText("MatSlotNone"));
                if (GUILayout.Button(new GUIContent(L.GetText("RefreshShort"), L.GetText("RefreshSlot")), GUILayout.Width(64))) BindMaterialSlot();
            }
            using (new EditorGUI.DisabledScope(paintLayerRT == null || targetRenderer == null || EditorApplication.isPlayingOrWillChangePlaymode))
            {
                bool nextRealtime = EditorGUILayout.ToggleLeft(L.GetText("RealtimePreviewToggle"), enableRealTimePreview);
                if (nextRealtime != enableRealTimePreview) SetRealtimePreview(nextRealtime);
            }
            if (sharedAtlasDetected) EditorGUILayout.HelpBox(L.GetText("UVSharedTextureWarning"), MessageType.Warning);
        }
    }

    private void DrawToolTabs()
    {
        var L = EditorLanguageManager.Instance;
        using (new EditorGUILayout.VerticalScope(GUILayout.ExpandHeight(true)))
        {
            string[] tabs = { L.GetText("DrawTab"), L.GetText("StampTab"), L.GetText("UVBoundaryTab"), T("SelectionGradientTab") };
            int next = GUILayout.Toolbar(activeToolTab, tabs, GUILayout.Width(_leftPanelWidth),GUILayout.Height(26));
            if (next != activeToolTab)
            {
                FinishActiveStroke();
                CancelRegionInteraction();
                regionOverlayDirty = true;
                stampController?.CancelInteraction();
                CancelPaintTransform();
                gradientPending=false;gradientDirty=true;
                if (activeToolTab == 0) lastDrawMode = mode;
                if (activeToolTab == 3) lastSelectionMode = mode;
                activeToolTab = next;
                if (activeToolTab == 1) mode = PaintMode.Stamp;
                else if (activeToolTab == 0) mode = lastDrawMode;
                else if (activeToolTab == 3) mode = lastSelectionMode;
                Repaint();
            }
            // MinHeight alone can also clamp maxHeight; keep the range open before enabling stretch.
            toolTabScroll[activeToolTab] = EditorGUILayout.BeginScrollView(toolTabScroll[activeToolTab],
                GUILayout.Width(_leftPanelWidth), GUILayout.MinHeight(60f), GUILayout.MaxHeight(float.MaxValue), GUILayout.ExpandHeight(true));
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox,GUILayout.Width(Mathf.Max(100f,_leftPanelWidth-36f))))
            {
                if (activeToolTab == 0) DrawPaintTab();
                else if (activeToolTab == 1) DrawStampTab();
                else if (activeToolTab == 2) DrawUVBoundaryTab();
                else DrawSelectionGradientTab();
            }
            EditorGUILayout.EndScrollView();
        }
    }

    private void DrawPaintTab()
    {
        var L=EditorLanguageManager.Instance;
        GUILayout.Label(L.GetText("PaintTabTitle"),EditorStyles.boldLabel);
        DrawModeButtons();
        if(drawAdvancedTab){DrawAdvancedSettings();return;}
        if(mode==PaintMode.Fill)
            fillScope=EditorGUILayout.Popup(T("FillScope"),fillScope,new[]{T("FillByIsland"),T("FillByContent"),T("FillBySelection")});
        if(mode==PaintMode.Erase)
        {
            int next=EditorGUILayout.Popup(T("EraseType"),eraseMode,new[]{T("EraseRegular"),T("EraseIsland"),T("EraseGradient")});
            if(next!=eraseMode){FinishActiveStroke();CancelRegionInteraction();gradientPending=false;eraseMode=next;gradientDirty=true;}
        }
        if(IsGradientErasing)DrawGradientSettings();
        else
        {
            GUILayout.Space(4);GUILayout.Label(L.GetText("BrushSectionFoldout"),EditorStyles.boldLabel);
            if(mode==PaintMode.Brush||mode==PaintMode.Erase&&eraseMode==0)
                brush.SizeMode=(LgcBrushSizeMode)EditorGUILayout.Popup(L.GetText("BrushSizeMode"),(int)brush.SizeMode,new[]{L.GetText("BrushScreenFixed"),L.GetText("BrushTextureFixed")});
            if(mode==PaintMode.Brush||mode==PaintMode.Fill)brush.Color=EditorGUILayout.ColorField(L.GetText("BrushOrFillColor"),brush.Color);
            if(mode==PaintMode.Brush||mode==PaintMode.Erase&&eraseMode==0)
            {
                string radiusLabel=brush.SizeMode==LgcBrushSizeMode.Screen?"BrushScreenRadius":"BrushRadius";
                brush.Size=EditorGUILayout.IntSlider(L.GetText(radiusLabel),brush.Size,1,256);
                brush.Hardness=EditorGUILayout.Slider(L.GetText("Hardness"),brush.Hardness,0,1);
                EditorGUILayout.HelpBox(L.GetText(brush.SizeMode==LgcBrushSizeMode.Screen?"BrushScreenHint":"BrushTextureHint"),MessageType.None);
            }
            brush.Opacity=EditorGUILayout.Slider(L.GetText("Opacity"),brush.Opacity,0,1);
            if(mode==PaintMode.Fill)
            {
                EditorGUILayout.HelpBox(T("UnifiedFillHint"),MessageType.None);
            }
        }
        DrawSymmetrySettings();
    }
    private void DrawAdvancedSettings()
    {
        GUILayout.Space(6);GUILayout.Label(T("AdvancedTitle"),EditorStyles.boldLabel);
        int action=EditorGUILayout.Popup(T("RightMouseFunction"),(int)rightMouseAction,new[]{T("RightMouseErase"),T("RightMousePan"),T("RightMouseNone")});
        if(action!=(int)rightMouseAction){FinishActiveStroke();rightMouseAction=(RightMouseAction)action;}
        DrawPrecisionSettings();
    }
    private void DrawSelectionGradientTab()
    {
        int selected=mode==PaintMode.Gradient?1:0;
        int next=GUILayout.Toolbar(selected,new[]{T("ModeSelection"),T("ModeGradient")},GUILayout.Height(26));
        if(next!=selected){FinishActiveStroke();CancelRegionInteraction();CancelPaintTransform();gradientPending=false;mode=next==0?PaintMode.Selection:PaintMode.Gradient;lastSelectionMode=mode;}
        if(mode==PaintMode.Gradient){DrawGradientSettings();DrawSymmetrySettings();}
        else DrawPaintSelectionSettings();
    }

    private void DrawStampTab()
    {
        DrawStampSettings();
        DrawSymmetrySettings();
    }

    private void DrawUVBoundaryTab()
    {
        EditorGUI.BeginChangeCheck();
        DrawRegionSettings();
        DrawBoundarySettings();
        DrawUVSettings();
        if (EditorGUI.EndChangeCheck()) MarkPreviewDirty();
    }

    private void DrawStampSettings()
    {
        var L = EditorLanguageManager.Instance;
        GUILayout.Label(L.GetText("StampSettings"), EditorStyles.boldLabel);
        stampSettings.StampTexture = (Texture2D)EditorGUILayout.ObjectField(L.GetText("StampTexture"), stampSettings.StampTexture, typeof(Texture2D), false);
        stampSettings.OverlayColor = EditorGUILayout.ColorField(new GUIContent(L.GetText("StampOverlayColor")), stampSettings.OverlayColor, true, false, false);
        stampSettings.ColorStrength = EditorGUILayout.Slider(L.GetText("StampColorStrength"), stampSettings.ColorStrength, 0f, 1f);
        stampSettings.Opacity = EditorGUILayout.Slider(L.GetText("Opacity"), stampSettings.Opacity, 0f, 1f);
        stampSettings.ShowPreview = EditorGUILayout.Toggle(L.GetText("StampShowPreview"), stampSettings.ShowPreview);
        EditorGUILayout.HelpBox(L.GetText("StampInteractionHint"), MessageType.None);
        EditorGUILayout.HelpBox(L.GetText("StampPreviewHint"), MessageType.None);
        using (new EditorGUI.DisabledScope(stampSettings.StampTexture == null || stampExecutor == null || (symmetryEnabled && !symmetryLocked)))
            if (GUILayout.Button(L.GetText("StampApply"), GUILayout.Height(26))) ApplyStamp();
    }

    private void SetSymmetryEnabled(bool enabled)
    {
        FinishActiveStroke();
        symmetryEnabled = enabled;
        gradientDirty = true;
        symmetryLocked = false;
        stampPreviewDirty = true;
        infoMessage = EditorLanguageManager.Instance.GetText(enabled ? "SymmetrySetupHint" : "SymmetryDisabled");
        infoType = MessageType.Info;
        Repaint();
    }

    private void DrawSymmetrySettings()
    {
        var L = EditorLanguageManager.Instance;
        EditorGUILayout.Space(4);
        using(new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
        GUILayout.Label(L.GetText("SymmetrySectionFoldout"), EditorStyles.boldLabel);
        bool next = EditorGUILayout.ToggleLeft(L.GetText("SymmetryEnable"), symmetryEnabled);
        if (next != symmetryEnabled) SetSymmetryEnabled(next);
        using (new EditorGUI.DisabledScope(!symmetryEnabled))
        {
            if (symmetryEnabled)
                EditorGUILayout.HelpBox(L.GetText(symmetryLocked ? "SymmetryLockedHint" : "SymmetrySetupHint"), symmetryLocked ? MessageType.None : MessageType.Info);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button(L.GetText(symmetryLocked ? "SymmetryAdjust" : "SymmetryLockAndPaint")))
                {
                    FinishActiveStroke();
                    symmetryLocked = !symmetryLocked;
                    gradientDirty = true;
                    Repaint();
                }
                if (GUILayout.Button(L.GetText("SymmetryReset")))
                {
                    FinishActiveStroke();
                    symmetryController.Reset();
                    gradientDirty = true;
                    symmetryLocked = false;
                    Repaint();
                }
            }
            if (activeToolTab == 1)
                stampSettings.MirrorPattern = EditorGUILayout.Toggle(L.GetText("StampMirror"), stampSettings.MirrorPattern);
            EditorGUILayout.Space(4);
            mirrorSourceSide = EditorGUILayout.Popup(L.GetText("MirrorSource"), mirrorSourceSide,
                new[] { L.GetText("MirrorSideA"), L.GetText("MirrorSideB") });
            EditorGUILayout.HelpBox(L.GetText("MirrorContentHint"), MessageType.None);
            using (new EditorGUI.DisabledScope(!symmetryLocked || paintLayerRT == null || (symmetryController.SymB - symmetryController.SymA).sqrMagnitude < 1e-8f))
                if (GUILayout.Button(L.GetText("MirrorContent"))) MirrorCurrentContent();
        }
        }
    }

    private void MirrorCurrentContent()
    {
        if (!computeReady || paintLayerRT == null || !symmetryEnabled || !symmetryLocked || uvMaskBuffer == null) return;
        EnsureRegionResources();
        Vector2 a = symmetryController.SymA, b = symmetryController.SymB;
        if ((b - a).sqrMagnitude < 1e-8f) return;
        FinishActiveStroke();
        RenderTexture source = null;
        try
        {
            source = NewRT(paintLayerRT.width, paintLayerRT.height);
            BlitPreservingTarget(paintLayerRT, source);
            BeginStrokeSnapshot();
            csPaint.SetTexture(kMirrorLayer, "_MirrorSource", source);
            csPaint.SetTexture(kMirrorLayer, "_PaintLayerRT", paintLayerRT);
            csPaint.SetBuffer(kMirrorLayer, "_UvMask", uvMaskBuffer);
            csPaint.SetInts("_TexSize", paintLayerRT.width, paintLayerRT.height);
            csPaint.SetVector("_MirrorAxis", new Vector4(a.x, a.y, b.x, b.y));
            csPaint.SetInt("_MirrorSourceSide", mirrorSourceSide);
            csPaint.SetInt("_EnableBoundary", uvBoundaryLimitEnabled ? 1 : 0);
            csPaint.Dispatch(kMirrorLayer, (paintLayerRT.width + 15) / 16, (paintLayerRT.height + 15) / 16, 1);
            SuppressStampPreview();
            EndStrokeApply();
            infoMessage = EditorLanguageManager.Instance.GetText("MirrorContentDone");
            infoType = MessageType.Info;
        }
        finally { SafeReleaseRT(source); }
        Repaint();
    }

    private void DrawBoundarySettings()
    {
        var L = EditorLanguageManager.Instance;
        EditorGUILayout.Space(4);
        GUILayout.Label(L.GetText("BoundarySectionFoldout"), EditorStyles.boldLabel);
        uvBoundaryLimitEnabled = EditorGUILayout.ToggleLeft(L.GetText("ToggleUVBoundaryLimit"), uvBoundaryLimitEnabled);
        using (new EditorGUI.DisabledScope(!uvBoundaryLimitEnabled))
        {
            EditorGUI.BeginChangeCheck();
            int expand = EditorGUILayout.DelayedIntField(L.GetText("BoundaryExpandPixels"), uvBoundaryExpandPixels);
            if (EditorGUI.EndChangeCheck())
            {
                uvBoundaryExpandPixels = Mathf.Clamp(expand, 0, 128);
                uvMaskDirty = true;
                islandsDirty = true;
                ReleaseMaskBuffers();
                EnsureMaskBuffersUpToDate();
                if (computeReady && paintLayerRT != null && uvMaskBuffer != null && islandIdBuffer != null)
                {
                    brushExecutor = new LgcBrushExecutor(csPaint, kBrush, kFillIsland, paintLayerRT, uvMaskBuffer, islandIdBuffer);
                    stampExecutor = new LgcStampExecutor(csPaint, kStamp, paintLayerRT, uvMaskBuffer, islandIdBuffer);
                }
                else { brushExecutor = null; stampExecutor = null; }
                MarkPreviewDirty();
            }
        }
        islandIsolationEnabled = EditorGUILayout.ToggleLeft(L.GetText("ToggleIslandIsolation"), islandIsolationEnabled);
    }

    private void DrawUVSettings()
    {
        var L = EditorLanguageManager.Instance;
        EditorGUILayout.Space(4);
        GUILayout.Label(L.GetText("UVOverlayFoldout"), EditorStyles.boldLabel);
        showUVOverlay = EditorGUILayout.Toggle(L.GetText("ShowUVOverlay"), showUVOverlay);
        int scope = EditorGUILayout.Popup(L.GetText("UVDisplayScope"), (int)uvOverlayScope,
            new[] { L.GetText("UVScopeCurrentMaterial"), L.GetText("UVScopeAllSubMeshes") });
        if (scope != (int)uvOverlayScope)
        {
            uvOverlayScope = (UVOverlayScope)scope;
            RefreshUVScopeDataIfNeeded(true);
            LgcMeshUVOverlayDrawer.ClearCache();
            Repaint();
        }
        uvColor = EditorGUILayout.ColorField(L.GetText("UVLineColor"), uvColor);
        uvAlpha = EditorGUILayout.Slider(L.GetText("UVOverlayStrength"), uvAlpha, 0f, 1f);
        if (uvOverlayScope == UVOverlayScope.AllSubMeshes)
            EditorGUILayout.HelpBox(L.GetText("UVScopeAllHint"), MessageType.Info);
    }

    private void DrawOperationStatus()
    {
        if (string.IsNullOrEmpty(infoMessage)) return;
        if (infoType == MessageType.Warning || infoType == MessageType.Error)
            EditorGUILayout.HelpBox(infoMessage, infoType);
        else
            EditorGUILayout.LabelField(new GUIContent(infoMessage, infoMessage), EditorStyles.miniLabel, GUILayout.Height(18));
    }

    private void DrawOutputPanel()
    {
        var L = EditorLanguageManager.Instance;
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            Rect history = GUILayoutUtility.GetRect(0f, 26f, GUILayout.ExpandWidth(true));
            const float gap = 4f;
            float usable = Mathf.Max(0f, history.width - gap * 2f);
            using (new EditorGUI.DisabledScope(paintLayerRT == null || undoList.Count == 0))
                if (GUI.Button(new Rect(history.x, history.y, usable * 0.3f, history.height), new GUIContent(L.GetText("UndoButton"), L.GetText("UndoTooltip")))) DoUndo_Internal();
            using (new EditorGUI.DisabledScope(paintLayerRT == null || redoStack.Count == 0))
                if (GUI.Button(new Rect(history.x + usable * 0.3f + gap, history.y, usable * 0.3f, history.height), new GUIContent(L.GetText("RedoButton"), L.GetText("RedoTooltip")))) DoRedo_Internal();
            using (new EditorGUI.DisabledScope(paintLayerRT == null))
                if (GUI.Button(new Rect(history.x + usable * 0.6f + gap * 2f, history.y, usable * 0.4f, history.height), L.GetText("ClearCurrentEdits"))) ClearCurrentEdits();
            GUILayout.Space(4);
            GUILayout.Label(L.GetText("OutputTitle"), EditorStyles.boldLabel);
            if (GUILayout.Button(L.GetText("LocateProjectResBtn"))) LocateAndCreateResourceDirectory();
            using (new EditorGUI.DisabledScope(paintLayerRT == null || targetRenderer == null))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(L.GetText("ExportPaintLayer"))) ExportPaintLayerOnly();
                    exportPaintLayerOnWhite = GUILayout.Toggle(exportPaintLayerOnWhite, L.GetText("ExportWithWhiteBG"), GUILayout.Width(110));
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(L.GetText("SaveOutputFull"), GUILayout.Height(26))) SaveOutputOnly();
                    exportResolutionIndex = EditorGUILayout.Popup(exportResolutionIndex,
                        new[] { new GUIContent(L.GetText("ExportOriginal"), L.GetText("ExportResolutionHint")),
                            new GUIContent("1024", L.GetText("ExportResolutionHint")), new GUIContent("2048", L.GetText("ExportResolutionHint")),
                            new GUIContent("4096", L.GetText("ExportResolutionHint")) }, GUILayout.Width(98), GUILayout.Height(26));
                }
            }
        }
    }

    private void DrawModeButtons()
    {
        int selected=drawAdvancedTab?3:mode==PaintMode.Erase||mode==PaintMode.IslandErase?1:mode==PaintMode.Fill?2:0;
        int next=GUILayout.Toolbar(selected,new[]{T("ModeBrushShort"),T("ModeEraseShort"),T("UnifiedFill"),T("AdvancedTitle")},GUILayout.Height(26));
        if(next!=selected)
        {
            FinishActiveStroke();CancelRegionInteraction();gradientPending=false;
            drawAdvancedTab=next==3;
            if(!drawAdvancedTab){mode=next==0?PaintMode.Brush:next==1?PaintMode.Erase:PaintMode.Fill;lastDrawMode=mode;}
            Repaint();
        }
    }

    private void FinishActiveStroke()
    {
        if (_strokeActive && paintLayerRT != null) EndStrokeApply();
        _strokeActive = false;
        isPainting = false;
        if (paintControlId != 0 && GUIUtility.hotControl == paintControlId) GUIUtility.hotControl = 0;
        lastUV = new Vector2(-1, -1);
        activeIslandId = -1;
    }

    private void DrawRightPanel(Rect containerRect)
    {
        var L = EditorLanguageManager.Instance;

        stampController?.PrepareInput();
        paintControlId = GUIUtility.GetControlID("LgcPaintStroke".GetHashCode(), FocusType.Passive);
        Rect areaRect = new Rect(containerRect.x, containerRect.y, containerRect.width, Mathf.Max(1f, containerRect.height - 30f));
        if (paintLayerRT == null)
        {
            GUI.Label(new Rect(areaRect.x + 8, areaRect.y + 8, areaRect.width - 16, 20),
                L.GetText("NoPaintTargetHint"), EditorStyles.boldLabel);
            return;
        }

        EnsureRegionResources();
        if (previewRT == null || previewRT.width != paintLayerRT.width || previewRT.height != paintLayerRT.height)
            RecreatePreviewRT();

        if (Event.current.type == EventType.Repaint) EnsurePreview();

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

        PrepareRegionInput();
        HandlePaintSelectionInput();
        HandleRegionAndGradientInput();

        // Captured stamp drags get first refusal, including MouseUp outside the canvas.
        bool stampCanEdit = activeToolTab == 1 && mode == PaintMode.Stamp && (!symmetryEnabled || symmetryLocked) && !IsRegionSelecting;
        if (stampController != null)
        {
            if (!stampCanEdit) stampController.CancelInteraction();
            else if (stampController.IsEditing)
                stampController.HandleInput(previewRect, viewController.GetViewRect(), viewController, symmetryController);
        }

        // Bottom controls consume pointer events before the canvas handles them.
        Rect resetRect = new Rect(containerRect.center.x - 54f, containerRect.yMax - 26f, 108f, 24f);
        if (GUI.Button(resetRect, L.GetText("ResetViewBtn"))) { viewController.ResetView(); Repaint(); }
        if (resetRect.Contains(Event.current.mousePosition) && (Event.current.type == EventType.MouseDown || Event.current.type == EventType.MouseDrag || Event.current.type == EventType.MouseUp || Event.current.type == EventType.ScrollWheel)) Event.current.Use();

        viewController.HandleNavigation(previewRect, rightMouseAction == RightMouseAction.Pan);
        Rect viewRect = viewController.GetViewRect();

        RenderTexture display = GetDisplayPreviewForGUI(Event.current.type == EventType.Repaint);
        if (Event.current.type == EventType.Repaint && enableRealTimePreview && !previewRepairPending && !EditorApplication.isPlayingOrWillChangePlaymode) ApplyMaterialInstanceWithTexture(display);
        GUI.DrawTextureWithTexCoords(previewRect, display,
            new Rect(viewRect.xMin, viewRect.yMin, viewRect.width, viewRect.height));

        if (showUVOverlay && targetMesh != null)
        {
            RefreshUVScopeDataIfNeeded();
            int[] inactiveTris = uvOverlayScope == UVOverlayScope.AllSubMeshes
                ? inactiveUvTriangles
                : null;
            LgcMeshUVOverlayDrawer.DrawUVOverlay(
                previewRect,
                viewRect,
                targetMesh,
                activeMaterialTriangles,
                inactiveTris,
                uvColor,
                uvAlpha);
        }

        DrawRegionOverlay(viewRect);
        DrawGradientHandles(viewRect);
        DrawPaintSelectionOverlay(viewRect);

        if (symmetryEnabled && !IsRegionSelecting && mode != PaintMode.Selection)
            symmetryController.DrawAndHandle(previewRect, viewRect, symmetryLocked, viewController.Zoom, true);

        // ★ 图章预览和交互仅在 Stamp 模式下显示
        if (activeToolTab == 1 && mode == PaintMode.Stamp && stampController != null)
        {
            stampController.DrawPreview(previewRect, viewRect, viewController, symmetryEnabled ? symmetryController : null);
        }

        HandlePaintingEvents(viewRect);
        DrawBrushPreview(previewRect, viewRect);

        // ★ 图章交互仅在 Stamp 模式下处理
        if (stampCanEdit && stampController != null && !stampController.IsEditing)
        {
            stampController.HandleInput(previewRect, viewRect, viewController, symmetryController);
        }

    }

    private float GetEffectiveBrushRadius(Rect viewRect)
    {
        if (brush.SizeMode == LgcBrushSizeMode.Texture || paintLayerRT == null) return brush.Size;
        float scaleX = previewRect.width / Mathf.Max(1e-6f, viewRect.width * paintLayerRT.width);
        float scaleY = previewRect.height / Mathf.Max(1e-6f, viewRect.height * paintLayerRT.height);
        return Mathf.Max(0.5f, brush.Size / Mathf.Max(1e-6f, (scaleX + scaleY) * 0.5f));
    }

    private void DrawBrushPreview(Rect rect, Rect viewRect)
    {
        if (IsRegionSelecting || mode == PaintMode.Selection || IsGradientEditing) return;
        if (Event.current.type != EventType.Repaint) return;
        if (paintLayerRT == null || (mode != PaintMode.Brush && mode != PaintMode.Erase && !(Event.current.button == 1 && rightMouseAction == RightMouseAction.Erase))) return;
        if (symmetryEnabled && !symmetryLocked) return;
        Vector2 mouse = Event.current.mousePosition;
        if (!rect.Contains(mouse)) return;
        float scaleX = rect.width / Mathf.Max(1e-6f, viewRect.width * paintLayerRT.width);
        float scaleY = rect.height / Mathf.Max(1e-6f, viewRect.height * paintLayerRT.height);
        float guiRadius = GetEffectiveBrushRadius(viewRect) * (scaleX + scaleY) * 0.5f;
        Matrix4x4 matrix = GUI.matrix;
        using (new Handles.DrawingScope((mode == PaintMode.Erase || (Event.current.button == 1 && rightMouseAction == RightMouseAction.Erase)) ? Color.white : new Color(brush.Color.r, brush.Color.g, brush.Color.b, 0.9f)))
        {
            GUI.BeginClip(rect);
            try
            {
                Vector2 localMouse = mouse - rect.position;
                Handles.DrawWireDisc(localMouse, Vector3.forward, guiRadius);
                Handles.DrawWireDisc(localMouse, Vector3.forward, Mathf.Max(1f, guiRadius - 1.5f));
            }
            finally { GUI.EndClip(); GUI.matrix = matrix; }
        }
    }

    private void HandlePaintingEvents(Rect viewRect)
    {
        // ★ 如果处于 Stamp 模式，禁止画笔
        if (mode == PaintMode.Selection || IsRegionSelecting || (IsGradientEditing && Event.current.button != 1 && !isPainting)) return;
        bool rightErase = rightMouseAction == RightMouseAction.Erase && Event.current != null && Event.current.button == 1;
        if ((mode == PaintMode.Stamp || IsGradientEditing) && !rightErase && !isPainting) return;
        // ★ 如果图章正在编辑，禁止画笔（但图章编辑仅在 Stamp 模式下发生，此防御保留）
        if (stampController != null && stampController.IsEditing) return;

        if (!computeReady || paintLayerRT == null || brushExecutor == null) return;
        EnsureRegionResources();
        Event e = Event.current; if (e == null) return;

        if (isPainting && (GUIUtility.hotControl != paintControlId || e.rawType == EventType.MouseUp || (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)))
        {
            FinishActiveStroke();
            if (e.type != EventType.Used) e.Use();
            Repaint();
            return;
        }

        if (symmetryEnabled && !symmetryLocked)
        {
            if (previewRect.Contains(e.mousePosition) && (e.button == 0 || rightErase) && (e.type == EventType.MouseDown || e.type == EventType.MouseDrag || e.type == EventType.MouseUp))
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
        if (e.alt || EditorGUIUtility.editingTextField) return;
        float effectiveRadius = GetEffectiveBrushRadius(viewRect);

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

        if ((e.button == 0 && mode != PaintMode.Stamp && !IsGradientEditing) || rightErase || (isPainting && e.button == strokeButton))
        {
            if (!rightErase && (mode == PaintMode.Fill || mode == PaintMode.IslandErase || mode == PaintMode.Erase && eraseMode==1))
            {
                if(e.type==EventType.MouseDown&&GUIUtility.hotControl==0){ApplyUnifiedFill(uv,mode!=PaintMode.Fill);e.Use();Repaint();}
                return;
            }

            if (e.type == EventType.MouseDown && GUIUtility.hotControl == 0)
            {
                strokeButton = e.button;
                strokeErase = rightErase || mode == PaintMode.Erase;
                GUIUtility.hotControl = paintControlId;
                BeginStrokeSnapshotIfNeeded();
                isPainting = true; lastUV = uv; _strokeActive = true;
                activeIslandId = islandIsolationEnabled ? GetIslandIdAtUV_CPU(uv) : -1;
                brushExecutor.PaintAtUV(uv, !strokeErase, activeIslandId, uvBoundaryLimitEnabled, islandIsolationEnabled, brush, effectiveRadius);
                if (symmetryEnabled)
                {
                    Vector2 muv = symmetryController.MirrorUV(uv);
                    if (IsUVInside01(muv))
                    {
                        int prev = activeIslandId;
                        int iso = islandIsolationEnabled ? GetIslandIdAtUV_CPU(muv) : -1;
                        activeIslandId = iso;
                        brushExecutor.PaintAtUV(muv, !strokeErase, activeIslandId, uvBoundaryLimitEnabled, islandIsolationEnabled, brush, effectiveRadius);
                        activeIslandId = prev;
                    }
                }
                MarkPreviewDirty();
                e.Use(); Repaint();
            }
            else if (e.type == EventType.MouseDrag && isPainting)
            {
                brushExecutor.DrawStroke(lastUV, uv, !strokeErase, activeIslandId, uvBoundaryLimitEnabled, islandIsolationEnabled, brush, effectiveRadius);
                if (symmetryEnabled)
                {
                    Vector2 uvA = symmetryController.MirrorUV(lastUV);
                    Vector2 uvB = symmetryController.MirrorUV(uv);
                    if (IsUVInside01(uvA) || IsUVInside01(uvB))
                    {
                        int prev = activeIslandId;
                        int iso = islandIsolationEnabled ? GetIslandIdAtUV_CPU(uvB) : -1;
                        activeIslandId = iso;
                        brushExecutor.DrawStroke(uvA, uvB, !strokeErase, activeIslandId, uvBoundaryLimitEnabled, islandIsolationEnabled, brush, effectiveRadius);
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

    private void MarkPreviewDirty() { paintTransformDirty = true; previewDirty = true; stampPreviewDirty = true; gradientDirty = true; }

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

        RefreshUVScopeDataIfNeeded();
        var uvs = targetMesh.uv;
        var tris = activeMaterialTriangles;
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
        EnsureRegionResources();
        UploadRegionState();
    }

    private void ReleaseMaskBuffers()
    {
        if (uvMaskBuffer != null) { uvMaskBuffer.Release(); uvMaskBuffer = null; }
        if (islandIdBuffer != null) { islandIdBuffer.Release(); islandIdBuffer = null; }
    }

    #endregion

    #region Composite Preview

    private void CompositePreview() => CompositeLayer(paintLayerRT, previewRT);

    private void CompositeLayer(RenderTexture layer, RenderTexture result)
    {
        if (baseRT == null || layer == null || result == null) return;
        if (baseRT.width != result.width || baseRT.height != result.height) return;

        if (csPaint != null && kComposite >= 0)
        {
            csPaint.SetTexture(kComposite, "_BaseRT", baseRT);
            csPaint.SetTexture(kComposite, "_PaintLayerRT", layer);
            csPaint.SetTexture(kComposite, "_PreviewRT", result);
            csPaint.SetInts("_TexSize", result.width, result.height);
            csPaint.Dispatch(kComposite,
                Mathf.Max(1, (result.width + 15) / 16),
                Mathf.Max(1, (result.height + 15) / 16),
                1);
        }
        else
        {
            if (baseRT != null)
                BlitPreservingTarget(baseRT, result);
            else
                ClearRT(result, Color.gray);
        }
    }

    #endregion

    #region RT Management

    // v2.9.1: Graphics.Blit leaves its destination active. Never leak it into EditorWindow GUI rendering.
    private static void BlitPreservingTarget(Texture source, RenderTexture destination)
    {
        RenderTexture previous = RenderTexture.active;
        try { Graphics.Blit(source, destination); }
        finally { RenderTexture.active = previous; }
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

    private void ClearRT(RenderTexture rt, Color c)
    {
        var prev = RenderTexture.active;
        try
        {
            RenderTexture.active = rt;
            GL.Clear(true, true, c);
        }
        finally { RenderTexture.active = prev; }
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

    // v2.9.1: preview objects are owned by this window, never by Unity's global Undo.
    private void SetRealtimePreview(bool enabled)
    {
        FinishActiveStroke();
        enableRealTimePreview = enabled;
        if (enabled && paintLayerRT != null)
        {
            EnsurePreview();
            ApplyMaterialInstanceWithTexture(GetDisplayPreview());
        }
        else ForceRestorePreviewMaterial();
        infoMessage = EditorLanguageManager.Instance.GetText(enabled ? "RealtimeOn" : "RealtimeOff");
        infoType = MessageType.Info;
        Repaint();
    }

    private void OnUnityUndoRedo()
    {
        if (!enableRealTimePreview || previewRepairPending) return;
        previewRepairPending = true;
        EditorApplication.delayCall += RepairPreviewAfterUndo;
    }

    private void RepairPreviewAfterUndo()
    {
        previewRepairPending = false;
        if (this == null || !enableRealTimePreview || EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (targetRenderer == null || originalMaterialAsset == null || paintLayerRT == null) return;
        EnsurePreview();
        ApplyMaterialInstanceWithTexture(GetDisplayPreview());
        SceneView.RepaintAll();
        Repaint();
    }

    private void OnSceneSaving(Scene scene, string path)
    {
        if (previewRenderer == null || previewRenderer.gameObject.scene != scene) return;
        ForceRestorePreviewMaterial();
        OnUnityUndoRedo();
    }

    private void OnPlayModeStateChanged(PlayModeStateChange state)
    {
        if (state == PlayModeStateChange.ExitingEditMode) RestoreOriginalMaterial();
        else if (state == PlayModeStateChange.EnteredEditMode && enableRealTimePreview) OnUnityUndoRedo();
    }

    private void ApplyMaterialInstanceWithTexture(Texture tex)
    {
        if (targetRenderer == null || originalMaterialAsset == null || tex == null || EditorApplication.isPlayingOrWillChangePlaymode) return;
        if (previewMaterialSlotIndex >= 0 && (previewRenderer != targetRenderer || previewMaterialSlotIndex != selectedMatIndex))
            ForceRestorePreviewMaterial();
        var mats = targetRenderer.sharedMaterials;
        if (selectedMatIndex < 0 || selectedMatIndex >= mats.Length) return;
        if (previewMaterial == null)
        {
            previewMaterial = new Material(originalMaterialAsset)
            {
                name = originalMaterialAsset.name + " (LGC Preview)",
                hideFlags = HideFlags.HideAndDontSave
            };
        }
        bool changed = mats[selectedMatIndex] != previewMaterial;
        if (changed)
        {
            mats[selectedMatIndex] = previewMaterial;
            targetRenderer.sharedMaterials = mats;
        }
        bool textureChanged = previewMaterial.mainTexture != tex;
        if (changed || textureChanged)
            AssignTextureToAllLikelyMainProps(previewMaterial, tex);
        previewRenderer = targetRenderer;
        previewOriginalMaterial = originalMaterialAsset;
        previewMaterialSlotIndex = selectedMatIndex;
        sourceMaterial = previewMaterial;
        if (changed || textureChanged) SceneView.RepaintAll();
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
            mat.SetTexture(mainTexPropName, tex);
        string[] candidates = { "_BaseMap", "_MainTex", "_BaseColorMap", "_BaseColorTexture", "_Albedo", "_AlbedoMap", "MainTex" };
        foreach (var name in candidates)
            if (mat.HasProperty(name)) mat.SetTexture(name, tex);
        mat.mainTexture = tex;
    }

    private void RestoreOriginalMaterial()
    {
        ForceRestorePreviewMaterial();
    }

    private void ForceRestorePreviewMaterial()
    {
        if (previewRenderer != null && previewMaterialSlotIndex >= 0)
        {
            var mats = previewRenderer.sharedMaterials;
            if (previewMaterialSlotIndex < mats.Length &&
                (mats[previewMaterialSlotIndex] == previewMaterial || mats[previewMaterialSlotIndex] == null))
            {
                mats[previewMaterialSlotIndex] = previewOriginalMaterial;
                previewRenderer.sharedMaterials = mats;
            }
        }
        if (previewMaterial != null) Object.DestroyImmediate(previewMaterial);
        previewMaterial = null;
        previewRenderer = null;
        previewOriginalMaterial = null;
        previewMaterialSlotIndex = -1;
        sourceMaterial = originalMaterialAsset;
        SceneView.RepaintAll();
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
        ConfigureExportImporter(outputAssetPath);
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
            importer.maxTextureSize = GetExportImportSize();
            importer.npotScale = TextureImporterNPOTScale.None;
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
        FinishActiveStroke();
        SuppressStampPreview();
        EnsureRegionResources();
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
        BlitPreservingTarget(paintLayerRT, snap);

        undoList.Add(snap);
        while (undoList.Count > HistoryLimit)
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
        TrimHistoryBudget();
    }

    private void EndStrokeApply()
    {
        MarkPreviewDirty();
        EnsurePreview();
        if (enableRealTimePreview && paintLayerRT != null)
        {
            ApplyMaterialInstanceWithTexture(GetDisplayPreview());
        }
        SceneView.RepaintAll();
        Repaint();
    }

    private void DoUndo_Internal()
    {
        FinishActiveStroke();
        SuppressStampPreview();
        if (paintLayerRT == null || rtPool == null) return;
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
        BlitPreservingTarget(paintLayerRT, curr);
        redoStack.Push(curr);
        if (prev.width == paintLayerRT.width && prev.height == paintLayerRT.height) BlitPreservingTarget(prev, paintLayerRT);
        else ResizePaintTexture(prev, paintLayerRT);
        rtPool.Return(prev);
        TrimHistoryBudget();

        MarkPreviewDirty();
        EndStrokeApply();
        infoMessage = L.GetText("UndoOk");
        infoType = MessageType.Info;
    }

    private void DoRedo_Internal()
    {
        FinishActiveStroke();
        SuppressStampPreview();
        if (paintLayerRT == null || rtPool == null) return;
        var L = EditorLanguageManager.Instance;
        if (redoStack.Count == 0)
        {
            infoMessage = L.GetText("RedoEmpty");
            infoType = MessageType.Info;
            return;
        }
        RenderTexture next = redoStack.Pop();
        RenderTexture curr = GetSnapshotTarget();
        BlitPreservingTarget(paintLayerRT, curr);
        undoList.Add(curr);
        while (undoList.Count > HistoryLimit)
        {
            rtPool.Return(undoList[0]);
            undoList.RemoveAt(0);
        }
        if (next.width == paintLayerRT.width && next.height == paintLayerRT.height) BlitPreservingTarget(next, paintLayerRT);
        else ResizePaintTexture(next, paintLayerRT);
        rtPool.Return(next);
        TrimHistoryBudget();

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

        if (symmetryEnabled && !symmetryLocked)
        {
            infoMessage = EditorLanguageManager.Instance.GetText("SymmetrySetupHint");
            infoType = MessageType.Info;
            Repaint();
            return;
        }
        FinishActiveStroke();
        BeginStrokeSnapshot();

        StampToLayer(stampExecutor);
        SuppressStampPreview();

        EndStrokeApply();
        infoMessage = EditorLanguageManager.Instance.GetText("StampApplied");
        infoType = MessageType.Info;
    }

    private void StampToLayer(LgcStampExecutor executor)
    {
        EnsureRegionResources();
        // 主图案（不镜像）
        executor.ApplyStamp(
            stampSettings.StampTexture,
            stampSettings.UV,
            stampSettings.Scale,
            stampSettings.Rotation,
            stampSettings.Opacity,
            uvBoundaryLimitEnabled,
            islandIsolationEnabled,
            activeIslandId,
            false, stampSettings.OverlayColor, stampSettings.ColorStrength  // 主图案不翻转
        );

        // ★ 对称图案（根据设置决定是否翻转）
        if (symmetryEnabled && symmetryController != null)
        {
            Vector2 mirroredUV = symmetryController.MirrorUV(stampSettings.UV);
            Vector2 axis = symmetryController.SymB - symmetryController.SymA;
            float mirroredRotation = 2f * Mathf.Atan2(axis.y, axis.x) * Mathf.Rad2Deg - stampSettings.Rotation - 180f;
            executor.ApplyStamp(
                stampSettings.StampTexture,
                mirroredUV,
                stampSettings.Scale,
                mirroredRotation,
                stampSettings.Opacity,
                uvBoundaryLimitEnabled,
                islandIsolationEnabled,
                activeIslandId,
                stampSettings.MirrorPattern, stampSettings.OverlayColor, stampSettings.ColorStrength
            );
        }

    }

    private void SuppressStampPreview()
    {
        CancelPaintTransform();
        gradientPending = false;
        suppressedStampPreview = stampSettings?.Clone();
        suppressedStampSymA = symmetryEnabled ? symmetryController.SymA : Vector2.zero;
        suppressedStampSymB = symmetryEnabled ? symmetryController.SymB : Vector2.zero;
        stampPreviewDirty = true;
    }

    private static bool SameStamp(LgcStampSettings a, LgcStampSettings b)
    {
        return a != null && b != null && a.StampTexture == b.StampTexture && a.UV == b.UV &&
            a.Rotation == b.Rotation && a.Scale == b.Scale && a.Opacity == b.Opacity &&
            a.OverlayColor == b.OverlayColor && a.ColorStrength == b.ColorStrength &&
            a.ShowPreview == b.ShowPreview && a.MirrorPattern == b.MirrorPattern;
    }

    // v2.9.1: Scratch preview never mutates paintLayerRT, export data, or Undo history.
    private RenderTexture GetDisplayPreview() => GetDisplayPreviewForGUI(true);

    private RenderTexture GetDisplayPreviewForGUI(bool refresh)
    {
        if (activeToolTab == 3 && mode == PaintMode.Selection && paintTransformPending) return GetPaintTransformDisplay(refresh);
        if (IsGradientEditing) return GetGradientDisplay(refresh);
        if (refresh) EnsurePreview();
        if (paintLayerRT == null || !computeReady || stampExecutor == null || activeToolTab != 1 || mode != PaintMode.Stamp ||
            stampSettings?.StampTexture == null || !stampSettings.ShowPreview || (symmetryEnabled && !symmetryLocked)) return previewRT;

        Vector2 symA = symmetryEnabled ? symmetryController.SymA : Vector2.zero;
        Vector2 symB = symmetryEnabled ? symmetryController.SymB : Vector2.zero;
        if (suppressedStampPreview != null)
        {
            if (SameStamp(stampSettings, suppressedStampPreview) && symA == suppressedStampSymA && symB == suppressedStampSymB) return previewRT;
            suppressedStampPreview = null;
        }
        if (!refresh) return stampPreviewRT != null && cachedStampPreview != null ? stampPreviewRT : previewRT;
        if (stampPreviewLayerRT == null || stampPreviewLayerRT.width != paintLayerRT.width || stampPreviewLayerRT.height != paintLayerRT.height)
        {
            if (stampPreviewLayerRT != null) SafeReleaseRT(stampPreviewLayerRT);
            if (stampPreviewRT != null) SafeReleaseRT(stampPreviewRT);
            stampPreviewLayerRT = NewRT(paintLayerRT.width, paintLayerRT.height);
            stampPreviewRT = NewRT(paintLayerRT.width, paintLayerRT.height);
            stampPreviewDirty = true;
        }
        if (stampPreviewDirty || !SameStamp(stampSettings, cachedStampPreview) || symA != cachedStampSymA || symB != cachedStampSymB)
        {
            BlitPreservingTarget(paintLayerRT, stampPreviewLayerRT);
            StampToLayer(new LgcStampExecutor(csPaint, kStamp, stampPreviewLayerRT, uvMaskBuffer, islandIdBuffer));
            CompositeLayer(stampPreviewLayerRT, stampPreviewRT);
            cachedStampPreview = stampSettings.Clone();
            cachedStampSymA = symA; cachedStampSymB = symB;
            stampPreviewDirty = false;
            if (enableRealTimePreview) SceneView.RepaintAll();
        }
        return stampPreviewRT;
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
            BlitPreservingTarget(sourceTex, baseRT);
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

    private void OnLostFocus()
    {
        FinishActiveStroke();
        CancelRegionInteraction();
        viewController?.CancelNavigation();
        stampController?.CancelInteraction();
        Repaint();
    }

    private void ReleaseGPUResources()
    {
        if (paintControlId != 0 && GUIUtility.hotControl == paintControlId) GUIUtility.hotControl = 0;
        CancelRegionInteraction();
        ReleaseRegionResources();
        viewController?.CancelNavigation();
        stampController?.CancelInteraction();
        ForceRestorePreviewMaterial();
        _strokeActive = false;
        isPainting = false;
        lastUV = new Vector2(-1, -1);
        activeIslandId = -1;
        brushExecutor = null;
        if (stampPreviewLayerRT != null) SafeReleaseRT(stampPreviewLayerRT);
        if (stampPreviewRT != null) SafeReleaseRT(stampPreviewRT);
        stampPreviewLayerRT = null; stampPreviewRT = null;
        cachedStampPreview = null; suppressedStampPreview = null; stampPreviewDirty = true;
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
                c.FindKernel("KGradient"); c.FindKernel("KRegionOverlay"); c.FindKernel("KPaintTransform"); c.FindKernel("KPaintSelectionOverlay"); c.FindKernel("KFillRegion");
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
            kMirrorLayer = csPaint.FindKernel("KMirrorLayer");
            kResizeExport = csPaint.FindKernel("KResizeExport");
            kGradient = csPaint.FindKernel("KGradient");
            kRegionOverlay = csPaint.FindKernel("KRegionOverlay");
            kPaintTransform = csPaint.FindKernel("KPaintTransform");
            kPaintSelectionOverlay = csPaint.FindKernel("KPaintSelectionOverlay");
            kFillRegion = csPaint.FindKernel("KFillRegion");
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
        activeMaterialTriangles = null;
        inactiveUvTriangles = null;
        materialUvScopeDirty = true;
        sharedAtlasDetected = false;
        LgcMeshUVOverlayDrawer.ClearCache();
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
        materialUvScopeDirty = true;
        sharedAtlasDetected = false;
        LgcMeshUVOverlayDrawer.ClearCache();
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
        RefreshUVScopeDataIfNeeded(true);
        RefreshSharedTextureConflict();

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
        if (enableRealTimePreview) ApplyMaterialInstanceWithTexture(GetDisplayPreview());
        Repaint();
    }

    private void RefreshUVScopeDataIfNeeded(bool force = false)
    {
        if (!force && !materialUvScopeDirty) return;

        var activeTriangles = new List<int>();
        var inactiveTriangles = new List<int>();

        if (targetMesh != null &&
            sharedMats != null &&
            sharedMats.Length > 0 &&
            selectedMatIndex >= 0 &&
            selectedMatIndex < sharedMats.Length)
        {
            Material activeMaterial = sharedMats[selectedMatIndex];
            int subMeshCount = Mathf.Min(targetMesh.subMeshCount, sharedMats.Length);

            if (activeMaterial != null)
            {
                for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
                {
                    int[] subMeshTriangles = targetMesh.GetTriangles(subMeshIndex);
                    if (sharedMats[subMeshIndex] == activeMaterial)
                        activeTriangles.AddRange(subMeshTriangles);
                    else
                        inactiveTriangles.AddRange(subMeshTriangles);
                }
            }
        }

        activeMaterialTriangles = activeTriangles.ToArray();
        inactiveUvTriangles = inactiveTriangles.ToArray();
        materialUvScopeDirty = false;
        uvMaskDirty = true;
        islandsDirty = true;
        LgcMeshUVOverlayDrawer.ClearCache();
    }

    private void RefreshSharedTextureConflict()
    {
        sharedAtlasDetected = false;

        if (targetMesh == null ||
            sharedMats == null ||
            sharedMats.Length == 0 ||
            selectedMatIndex < 0 ||
            selectedMatIndex >= sharedMats.Length)
            return;

        Material selectedMaterial = sharedMats[selectedMatIndex];
        if (selectedMaterial == null) return;

        Texture selectedTexture = GetMainTexture(selectedMaterial, true);
        if (selectedTexture == null) return;

        int subMeshCount = Mathf.Min(targetMesh.subMeshCount, sharedMats.Length);
        for (int subMeshIndex = 0; subMeshIndex < subMeshCount; subMeshIndex++)
        {
            if (subMeshIndex == selectedMatIndex) continue;

            Material otherMaterial = sharedMats[subMeshIndex];
            if (otherMaterial == null || otherMaterial == selectedMaterial) continue;

            Texture otherTexture = GetMainTexture(otherMaterial, false);
            if (SameTextureAsset(selectedTexture, otherTexture))
            {
                sharedAtlasDetected = true;
                return;
            }
        }

        Texture GetMainTexture(Material material, bool preferSelectedProperty)
        {
            string propertyName = preferSelectedProperty
                ? mainTexPropName
                : FindMainTexturePropertyName(material);

            Texture texture = null;
            if (!string.IsNullOrEmpty(propertyName) && material.HasProperty(propertyName))
                texture = material.GetTexture(propertyName);
            if (texture == null)
                texture = material.mainTexture;
            return texture;
        }

        static bool SameTextureAsset(Texture first, Texture second)
        {
            if (first == null || second == null) return false;
            if (first == second) return true;

            string firstPath = AssetDatabase.GetAssetPath(first);
            string secondPath = AssetDatabase.GetAssetPath(second);
            return !string.IsNullOrEmpty(firstPath) && firstPath == secondPath;
        }
    }

    #endregion

    #region Texture Read / Write

    private Texture2D ReadRT_Linear(RenderTexture rt)
    {
        var prev = RenderTexture.active;
        Texture2D texLinear = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, true);
        try
        {
            RenderTexture.active = rt;
            texLinear.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            texLinear.Apply(false, false);
        }
        finally { RenderTexture.active = prev; }
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

    private Vector2Int GetExportDimensions()
    {
        int w = paintLayerRT != null ? paintLayerRT.width : 1, h = paintLayerRT != null ? paintLayerRT.height : 1;
        int[] sizes = { 0, 1024, 2048, 4096 };
        int edge = sizes[Mathf.Clamp(exportResolutionIndex, 0, sizes.Length - 1)];
        if (edge == 0) return originalTexture != null ? new Vector2Int(originalTexture.width, originalTexture.height) : new Vector2Int(w, h);
        float scale = (float)edge / Mathf.Max(w, h);
        return new Vector2Int(Mathf.Max(1, Mathf.RoundToInt(w * scale)), Mathf.Max(1, Mathf.RoundToInt(h * scale)));
    }

    private int GetExportImportSize()
    {
        Vector2Int size = GetExportDimensions();
        return Mathf.Clamp(Mathf.NextPowerOfTwo(Mathf.Max(size.x, size.y)), 32, SystemInfo.maxTextureSize);
    }

    private void ConfigureExportImporter(string assetPath)
    {
        var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
        if (importer == null) return;
        importer.maxTextureSize = GetExportImportSize();
        importer.npotScale = TextureImporterNPOTScale.None;
        importer.SaveAndReimport();
    }

    private void WriteRTToPng_ColorCorrect(RenderTexture rt, string assetPath)
    {
        if (rt == null || string.IsNullOrEmpty(assetPath)) return;
        string dir = Path.GetDirectoryName(assetPath).Replace('\\', '/');
        if (!AssetDatabase.IsValidFolder(dir)) CreateFoldersRecursively(dir);
        RenderTexture resized = null;
        Texture2D pixels = null;
        try
        {
            Vector2Int size = GetExportDimensions();
            if (size.x != rt.width || size.y != rt.height)
            {
                resized = NewRT(size.x, size.y);
                csPaint.SetTexture(kResizeExport, "_ExportSource", rt);
                csPaint.SetTexture(kResizeExport, "_ResultRT", resized);
                csPaint.SetInts("_TexSize", rt.width, rt.height);
                csPaint.SetInts("_ExportSize", size.x, size.y);
                csPaint.Dispatch(kResizeExport, (size.x + 15) / 16, (size.y + 15) / 16, 1);
            }
            pixels = ToSRGBIfNeeded(ReadRT_Linear(resized != null ? resized : rt));
            string sub = assetPath.Substring("Assets".Length).TrimStart('/', '\\');
            File.WriteAllBytes(Path.Combine(Application.dataPath, sub), pixels.EncodeToPNG());
        }
        finally
        {
            if (pixels != null) Object.DestroyImmediate(pixels);
            SafeReleaseRT(resized);
        }
    }

    #endregion
}
#endif
