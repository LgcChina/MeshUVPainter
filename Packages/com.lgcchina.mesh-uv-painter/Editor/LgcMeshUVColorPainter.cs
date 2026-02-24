#if UNITY_EDITOR
// LGC网格UV绘画工具 - Unity编辑器Mesh UV颜色绘制工具
// LgcMeshUVColorPainter.cs (v0.6.0) - 多语言 + 定位按钮修复 + 文本简化 + 底部可点击作者信息
// Unity 2022+

using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

public class LgcMeshUVColorPainter : EditorWindow
{
    internal const string VERSION = "v1.0.0"; // ★ 每次修改请更新版本号
    public static string VersionString => VERSION; // [LGC 修改] 供作者窗口读取

    // [LGC 修改] 日志前缀（需求：统一前缀）
    private const string LOG_PREFIX = "[LGC网格UV绘画工具] ";

    // ====== 工具区宽度控制 ======
    private const float LEFT_PANEL_MIN_WIDTH = 360f;
    private const float LEFT_PANEL_MAX_WIDTH = 520f;
    private float _leftPanelWidth;

    // ====== 选择对象/网格/渲染器/材质 ======
    [SerializeField] private GameObject targetObject;
    private Mesh targetMesh;
    private Renderer targetRenderer;

    // 多材质槽
    private int selectedMatIndex = 0;
    private Material[] sharedMats;
    private string[] matSlotNames;

    // 当前槽材质、主纹理属性
    private Material sourceMaterial;              // 当前实际使用的材质（可能为实例）
    private Material originalMaterialAsset;       // 原始材质资产（用于恢复）
    private string mainTexPropName = null;

    // 原始主纹理（恢复用）
    private Texture2D originalTexture;
    private string originalAssetPath;

    // 输出 PNG
    private string outputAssetPath;               // Assets/LGC/Tools/UV绘画/输出图片/<name>.png
    private bool outputCreatedInSession = false;  // 当前会话是否创建了该文件
    private bool appliedToOutput = false;         // 是否已应用修改（若 true，关闭时不删除输出文件）

    // 绘制副本
    private Texture2D paintTexture;
    private Texture2D restoreTexture;             // 橡皮擦还原参考

    // 背景棋盘格
    private static Texture2D checkerTex;

    // UV 叠加
    private bool showUVOverlay = true;
    private Color uvColor = new Color(0.65f, 0.65f, 0.65f, 1f);
    private float uvAlpha = 0.85f;

    // 模式互斥
    private enum PaintMode { Brush, Erase, Fill }
    private PaintMode mode = PaintMode.Brush;

    // 画笔参数
    private Color brushColor = Color.yellow;
    private int brushSize = 32;
    private float brushOpacity = 1.0f;
    private float brushHardness = 1.0f;

    // 笔刷核缓存
    private int cachedBrushSize = -1;
    private float cachedBrushHardness = -1f;
    private float[] brushMask;

    // UV 覆盖掩码
    private byte[] uvMask;
    private int maskW, maskH;
    private bool uvMaskDirty = true;

    // 右侧视图与交互
    private Rect rightPanelRect;
    private Rect previewRect;
    private float zoom = 1f;
    private Vector2 viewCenter = new Vector2(0.5f, 0.5f);
    private Vector2 lastUV = new Vector2(-1, -1);
    private bool isPainting = false;

    // Apply 节流
    private double lastApplyTime = 0;
    private const double APPLY_INTERVAL = 1.0 / 45.0;

    // 状态提示（文本改由多语言管理）
    private string infoMessage;
    private MessageType infoType = MessageType.Info;

    // 会话状态
    private bool anyEdits = false;

    // 左侧滚动
    private Vector2 leftScroll;

    // 折叠：材质信息
    private bool foldMaterialInfo = false;

    // ====== 第四部分（二次确认清空） ======
    private bool isClearPending = false;
    private float clearConfirmTimer = 0f;

    // ====== 实时预览总开关 ======
    private bool enableRealTimePreview = false;

    // ====== 改造2：定位用统一目录（可创建） ======
    // [LGC 修改] 需求：定位按钮总是定位到该目录，若不存在则创建；默认：Assets/LGC/Tools/UV绘画/输出图片
    private string _targetResourceDir = "Assets/LGC/Tools/UV绘画/输出图片";

    // ====== 菜单 ======
    [MenuItem("LGC/LGC网格UV绘画工具")]
    public static void ShowWindow()
    {
        var window = GetWindow<LgcMeshUVColorPainter>("LGC网格UV绘画工具");
        window.minSize = new Vector2(LEFT_PANEL_MIN_WIDTH + 600f, 600f);
        window.Show();
    }

    private void OnEnable()
    {
        EnsureCheckerTexture();
        mode = PaintMode.Brush;

        var L = EditorLanguageManager.Instance;
        L.InitLanguageData();
        infoMessage = L.GetText("InfoWelcome");
    }

    private void OnDestroy()
    {
        // 恢复原始材质资产
        RestoreOriginalMaterial();
        // 清理未应用的输出文件
        CleanupUnappliedOutputIfAny();
    }

    // 恢复材质为原始资产
    private void RestoreOriginalMaterial()
    {
        if (targetRenderer == null
            || originalMaterialAsset == null) return;

        var mats = targetRenderer.sharedMaterials;
        if (mats == null
            || mats.Length == 0
            || selectedMatIndex < 0
            || selectedMatIndex >= mats.Length)
            return;

        // 如果当前材质已经是原始资产，则无需操作
        if (mats[selectedMatIndex] == originalMaterialAsset)
            return;

        // 恢复为原始资产
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

    // ========== GUI ==========
    private void OnGUI()
    {
        var L = EditorLanguageManager.Instance; L.InitLanguageData();

        // [LGC 修改] 清空确认倒计时到期自动取消（全局检查）
        if (isClearPending && (float)EditorApplication.timeSinceStartup >= clearConfirmTimer)
        {
            isClearPending = false;
            Repaint();
        }

        // [LGC 修改] 顶部工具条：右对齐语言下拉（宽120）
        using (new GUILayout.HorizontalScope(EditorStyles.toolbar))
        {
            GUILayout.FlexibleSpace();
            var cur = L.CurrentLang;
            var next = (EditorLanguage)EditorGUILayout.EnumPopup(new GUIContent("", L.GetText("LangDropdownTooltip")), cur, GUILayout.Width(120));
            if (next != cur)
            {
                L.CurrentLang = next;
                Repaint();
            }
        }

        float targetLeft = position.width * 0.45f;
        _leftPanelWidth = Mathf.Clamp(targetLeft, LEFT_PANEL_MIN_WIDTH, LEFT_PANEL_MAX_WIDTH);
        minSize = new Vector2(LEFT_PANEL_MIN_WIDTH + 600f, 560f);
        EditorGUIUtility.labelWidth = Mathf.Max(160f, _leftPanelWidth - 160f);

        EditorGUILayout.BeginHorizontal();

        // 左侧：工具区
        EditorGUILayout.BeginVertical(GUILayout.MinWidth(LEFT_PANEL_MIN_WIDTH), GUILayout.Width(_leftPanelWidth));
        GUILayout.Label(L.GetText("LeftPanelTitle"), EditorStyles.boldLabel);
        leftScroll = EditorGUILayout.BeginScrollView(leftScroll);
        DrawLeftPanel();
        EditorGUILayout.EndScrollView();
        EditorGUILayout.EndVertical();

        // 右侧：绘制区
        EditorGUILayout.BeginVertical(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        GUILayout.Label(L.GetText("RightPanelTitle"), EditorStyles.boldLabel);
        rightPanelRect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        DrawRightPanel(rightPanelRect);
        EditorGUILayout.EndVertical();

        EditorGUILayout.EndHorizontal();

        // [LGC 修改] 底部“作者/版本”为可点击文本（按钮样式=标签）
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
            var content = new GUIContent($"{authorTxt}    {verTxt}", L.GetText("ClickToSeeMore"));

            // 模拟文本按钮：无背景/边框
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

    // ====== 左侧面板 ======
    private void DrawLeftPanel()
    {
        var L = EditorLanguageManager.Instance;

        var wrapHelp = new GUIStyle(EditorStyles.helpBox) { wordWrap = true };
        EditorGUILayout.LabelField(infoMessage, wrapHelp);

        // 对象
        EditorGUI.BeginChangeCheck();
        targetObject = (GameObject)EditorGUILayout.ObjectField(L.GetText("TargetObject"), targetObject, typeof(GameObject), true);
        if (EditorGUI.EndChangeCheck())
        {
            isClearPending = false;
            CleanupUnappliedOutputIfAny();
            TryBindTarget(targetObject);
        }

        using (new EditorGUI.DisabledScope(targetObject == null || targetMesh == null || targetRenderer == null))
        {
            // 材质槽
            if (sharedMats != null && sharedMats.Length > 0)
            {
                int newIndex = EditorGUILayout.Popup(L.GetText("MatSlot"), selectedMatIndex, matSlotNames);
                if (newIndex != selectedMatIndex)
                {
                    isClearPending = false;
                    CleanupUnappliedOutputIfAny();
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
                isClearPending = false;
                CleanupUnappliedOutputIfAny();
                BindMaterialSlot();
            }
        }

        // 材质信息（默认折叠）
        EditorGUILayout.Space(6);
        foldMaterialInfo = EditorGUILayout.Foldout(foldMaterialInfo, L.GetText("MaterialInfoFold"), true);
        if (foldMaterialInfo)
        {
            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField(L.GetText("MatCurrent"), sourceMaterial ? sourceMaterial.name : L.GetText("MatSlotNone"));
            EditorGUILayout.LabelField(L.GetText("MatOriginalAsset"), originalMaterialAsset ? originalMaterialAsset.name : L.GetText("MatSlotNone"));
            EditorGUILayout.LabelField(L.GetText("MainTexProp"), string.IsNullOrEmpty(mainTexPropName) ? L.GetText("MatSlotNone") : mainTexPropName);
            EditorGUILayout.LabelField(L.GetText("MainTexOriginal"), originalTexture ? originalTexture.name : L.GetText("MatSlotNone"));
            EditorGUILayout.LabelField(L.GetText("OutputPng"), string.IsNullOrEmpty(outputAssetPath) ? L.GetText("MatSlotNone") : outputAssetPath);
            EditorGUILayout.LabelField(L.GetText("PaintCopy"), paintTexture ? $"{paintTexture.width}x{paintTexture.height}" : L.GetText("MatSlotNone"));
            EditorGUI.indentLevel--;
        }

        // 模式（互斥）
        EditorGUILayout.Space(8);
        GUILayout.Label(L.GetText("ModeTitle"), EditorStyles.boldLabel);
        DrawModeButtons(); // 已在 v0.5.0 改为 Button 互斥（保留）

        // 预览与叠加
        EditorGUILayout.Space(8);
        GUILayout.Label(L.GetText("PreviewOverlayTitle"), EditorStyles.boldLabel);
        showUVOverlay = EditorGUILayout.Toggle(L.GetText("ShowUVOverlay"), showUVOverlay);
        uvColor = EditorGUILayout.ColorField(L.GetText("UVLineColor"), uvColor);
        uvAlpha = EditorGUILayout.Slider(L.GetText("UVOverlayStrength"), uvAlpha, 0f, 1f);

        // 画笔与填充
        EditorGUILayout.Space(8);
        GUILayout.Label(L.GetText("BrushAndFill"), EditorStyles.boldLabel);
        var newBrushColor = EditorGUILayout.ColorField(L.GetText("BrushOrFillColor"), brushColor);
        var newBrushSize = EditorGUILayout.IntSlider(L.GetText("BrushRadius"), brushSize, 1, 256);
        var newBrushOpacity = EditorGUILayout.Slider(L.GetText("Opacity"), brushOpacity, 0f, 1f);
        var newBrushHardness = EditorGUILayout.Slider(L.GetText("Hardness"), brushHardness, 0f, 1f);
        if (newBrushSize != brushSize || !Mathf.Approximately(newBrushHardness, brushHardness))
        {
            brushSize = newBrushSize; brushHardness = newBrushHardness; RebuildBrushMask();
        }
        brushColor = newBrushColor; brushOpacity = newBrushOpacity;

        // 实时预览 / 保存 / 清空 / 定位
        EditorGUILayout.Space(8);
        GUILayout.Label(L.GetText("GroupApplySaveLocate"), EditorStyles.boldLabel);

        using (new EditorGUI.DisabledScope(paintTexture == null || targetRenderer == null))
        {
            // 1) 实时预览（仅内存，不写盘）
            bool newRealtime = EditorGUILayout.Toggle(L.GetText("RealtimePreviewToggle"), enableRealTimePreview);
            if (newRealtime != enableRealTimePreview)
            {
                isClearPending = false;
                enableRealTimePreview = newRealtime;
                if (enableRealTimePreview && paintTexture != null)
                {
                    ApplyMaterialInstanceWithTexture(paintTexture);
                    infoMessage = L.GetText("RealtimeOn");
                    infoType = MessageType.Info;
                }
                else
                {
                    RestoreOriginalMaterial();
                    infoMessage = L.GetText("RealtimeOff");
                    infoType = MessageType.Info;
                }
            }

            // 2) 输出贴图保存（完整）
            if (GUILayout.Button(L.GetText("SaveOutputFull")))
            {
                isClearPending = false;
                SaveOutputOnly();
            }

            // 3) 仅输出绘画层（透明底）
            if (GUILayout.Button(L.GetText("ExportPaintLayer")))
            {
                isClearPending = false;
                ExportPaintLayerOnly();
            }
        }

        // 4) 清空（带二次确认）
        using (new EditorGUI.DisabledScope(originalTexture == null || targetRenderer == null || paintTexture == null))
        {
            var prevColor = GUI.color;
            if (isClearPending) GUI.color = Color.red;

            string clearBtnText = isClearPending ? L.GetText("ConfirmClearEdits") : L.GetText("ClearEdits");
            if (GUILayout.Button(clearBtnText))
            {
                if (!isClearPending)
                {
                    isClearPending = true;
                    clearConfirmTimer = (float)EditorApplication.timeSinceStartup + 3f;
                }
                else
                {
                    ClearThisSessionEdits();
                    isClearPending = false;
                }
            }
            GUI.color = prevColor;
        }

        // 5) 定位到 Project 面板资源（文案简化 + 逻辑修复）
        if (GUILayout.Button(L.GetText("LocateProjectResBtn")))
        {
            isClearPending = false;
            LocateAndCreateResourceDirectory(); // [LGC 修改] 新逻辑：始终定位统一目录，可递归创建
        }
    }

    private void DrawModeButtons()
    {
        var L = EditorLanguageManager.Instance;
        bool isBrush = mode == PaintMode.Brush;
        bool isErase = mode == PaintMode.Erase;
        bool isFill = mode == PaintMode.Fill;

        var defaultColor = GUI.color;

        GUI.color = isBrush ? new Color(0.85f, 1f, 0.85f, 1f) : defaultColor;
        if (GUILayout.Button(L.GetText("ModeBrush")))
        {
            if (mode != PaintMode.Brush) { mode = PaintMode.Brush; isClearPending = false; Repaint(); }
        }

        GUI.color = isErase ? new Color(1f, 0.9f, 0.8f, 1f) : defaultColor;
        if (GUILayout.Button(L.GetText("ModeErase")))
        {
            if (mode != PaintMode.Erase) { mode = PaintMode.Erase; isClearPending = false; Repaint(); }
        }

        GUI.color = isFill ? new Color(0.85f, 0.9f, 1f, 1f) : defaultColor;
        if (GUILayout.Button(L.GetText("ModeFill")))
        {
            if (mode != PaintMode.Fill) { mode = PaintMode.Fill; isClearPending = false; Repaint(); }
        }

        GUI.color = defaultColor;
    }

    // ====== 右侧绘制区 ======
    private void DrawRightPanel(Rect containerRect)
    {
        var L = EditorLanguageManager.Instance;

        // 顶部重置视图按钮（水平居中）
        const float topBarH = 28f;
        Rect topBar = new Rect(containerRect.x, containerRect.y, containerRect.width, topBarH);
        Rect btnRect = new Rect(topBar.x + (topBar.width - 100f) * 0.5f, topBar.y + 4f, 100f, 20f);
        using (new EditorGUI.DisabledScope(paintTexture == null))
        {
            if (GUI.Button(btnRect, L.GetText("ResetViewBtn")))
            {
                ResetView();
                Repaint();
            }
        }

        Rect areaRect = new Rect(containerRect.x, containerRect.y + topBarH + 2f, containerRect.width, containerRect.height - topBarH - 2f);

        if (paintTexture == null)
        {
            GUI.Label(new Rect(areaRect.x + 8, areaRect.y + 8, areaRect.width - 16, 20),
                      "暂无绘制副本。请在左侧绑定对象并选择材质槽。", EditorStyles.boldLabel); // 可选：也可多语言化此句
            return;
        }

        // 计算 previewRect（在 areaRect 内自适应）
        float cw = Mathf.Max(50f, areaRect.width - 8f);
        float ch = Mathf.Max(50f, areaRect.height - 8f);
        float aspect = (float)paintTexture.width / Mathf.Max(1, paintTexture.height);
        float previewW = cw, previewH = previewW / aspect;
        if (previewH > ch) { previewH = ch; previewW = previewH * aspect; }
        previewRect = new Rect(
            areaRect.x + (areaRect.width - previewW) * 0.5f,
            areaRect.y + (areaRect.height - previewH) * 0.5f,
            previewW, previewH
        );

        // 仅在 previewRect 内绘制棋盘格
        if (Event.current.type == EventType.Repaint && checkerTex != null)
            DrawTiledTexture(previewRect, checkerTex, 16);

        // 绘制贴图（考虑视图窗口）
        Rect viewRect = GetViewRect();
        GUI.DrawTextureWithTexCoords(previewRect, paintTexture,
            new Rect(viewRect.xMin, viewRect.yMin, viewRect.width, viewRect.height));

        // UV 叠加
        if (showUVOverlay && targetMesh != null && targetMesh.uv != null && targetMesh.uv.Length > 0)
            DrawUVImmediate(previewRect, viewRect, targetMesh, uvColor, uvAlpha);

        // 交互（缩放、平移）
        HandleViewNavigation(previewRect);

        // 绘制逻辑（鼠标 + 快捷键）
        HandlePaintingEvents(paintTexture, viewRect);

        // 笔刷预览
        DrawBrushPreview(previewRect, viewRect);
    }

    // ====== 视图控制与笔刷预览（与 v0.5.0 相同，略） ======
    private Rect GetViewRect() { float sz = 1f / Mathf.Max(1f, zoom); float half = sz * 0.5f; float cx = Mathf.Clamp(viewCenter.x, half, 1f - half); float cy = Mathf.Clamp(viewCenter.y, half, 1f - half); return new Rect(cx - half, cy - half, sz, sz); }
    private void SetViewFromRect(Rect vr) { float sz = Mathf.Clamp(vr.width, 1e-6f, 1f); zoom = 1f / sz; viewCenter = new Vector2(vr.center.x, vr.center.y); }
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
        if (paintTexture == null) return;
        Vector2 mouse = Event.current.mousePosition; if (!rect.Contains(mouse)) return;
        float scaleX = rect.width / Mathf.Max(1e-6f, viewRect.width * paintTexture.width);
        float scaleY = rect.height / Mathf.Max(1e-6f, viewRect.height * paintTexture.height);
        float guiRadius = brushSize * (scaleX + scaleY) * 0.5f;

        Handles.BeginGUI();
        Handles.color = new Color(brushColor.r, brushColor.g, brushColor.b, 0.9f);
        Handles.DrawWireDisc(mouse, Vector3.forward, guiRadius);
        Handles.DrawWireDisc(mouse, Vector3.forward, Mathf.Max(1f, guiRadius - 1.5f));
        Handles.EndGUI();
    }

    // ====== UV 即时绘制（与 v0.5.0 相同，略） ======
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

    // ====== 笔刷核重建（与 v0.5.0 相同，略） ======
    private void RebuildBrushMask()
    {
        cachedBrushSize = Mathf.Max(1, brushSize);
        cachedBrushHardness = Mathf.Clamp01(brushHardness);
        int r = cachedBrushSize;
        int d = r * 2 + 1;
        brushMask = new float[d * d];

        float hard = cachedBrushHardness;
        float inner = r * hard;

        for (int y = -r; y <= r; y++)
            for (int x = -r; x <= r; x++)
            {
                float dist = Mathf.Sqrt(x * x + y * y);
                float w;
                if (hard >= 0.999f) w = (dist <= r + 0.0001f) ? 1f : 0f;
                else
                {
                    float t = Mathf.InverseLerp(inner, r, dist);
                    w = 1f - Mathf.Clamp01(t);
                    if (dist > r) w = 0f;
                }
                brushMask[(y + r) * d + (x + r)] = w;
            }
    }
    private void EnsureBrushMask()
    {
        if (brushMask == null || cachedBrushSize != brushSize || !Mathf.Approximately(cachedBrushHardness, brushHardness))
            RebuildBrushMask();
    }

    // ====== 绘制事件处理（含 [ / ] 快捷键 与 实时预览钩子） ======
    private void HandlePaintingEvents(Texture2D displayTex, Rect viewRect)
    {
        var L = EditorLanguageManager.Instance;
        Event e = Event.current;
        if (e == null) return;

        bool inside = previewRect.Contains(e.mousePosition);

        // [ / ] 快捷键
        if (inside && e.type == EventType.KeyDown)
        {
            if (e.keyCode == KeyCode.LeftBracket || e.character == '[')
            {
                brushSize = Mathf.Max(1, brushSize - 1);
                RebuildBrushMask();
                isClearPending = false;
                e.Use(); Repaint();
            }
            else if (e.keyCode == KeyCode.RightBracket || e.character == ']')
            {
                brushSize = Mathf.Min(256, brushSize + 1);
                RebuildBrushMask();
                isClearPending = false;
                e.Use(); Repaint();
            }
        }

        if (!inside)
        {
            if (e.type == EventType.MouseUp) { EndStrokeApply(); isPainting = false; lastUV = new Vector2(-1, -1); }
            return;
        }

        Vector2 local = e.mousePosition - previewRect.position;
        Vector2 local01 = new Vector2(Mathf.Clamp01(local.x / Mathf.Max(1, previewRect.width)), Mathf.Clamp01(local.y / Mathf.Max(1, previewRect.height)));
        Vector2 uv = new Vector2(viewRect.xMin + local01.x * viewRect.width, viewRect.yMax - local01.y * viewRect.height);

        if (e.button == 0)
        {
            if (mode == PaintMode.Fill)
            {
                if (e.type == EventType.MouseDown)
                {
                    BeginStrokeUndo();
                    isClearPending = false;

                    FillIslandAtUV(uv);
                    anyEdits = true;
                    EndStrokeApply();

                    e.Use(); Repaint();
                }
                return;
            }

            if (e.type == EventType.MouseDown)
            {
                BeginStrokeUndo();
                isPainting = true; lastUV = uv;
                isClearPending = false;

                PaintAtUV_Optimized(uv);
                anyEdits = true;
                ThrottledApply();

                e.Use(); Repaint();
            }
            else if (e.type == EventType.MouseDrag && isPainting)
            {
                DrawStrokeBetween_Optimized(lastUV, uv);
                lastUV = uv; anyEdits = true;
                ThrottledApply();

                e.Use(); Repaint();
            }
            else if (e.type == EventType.MouseUp && isPainting)
            {
                isPainting = false;
                EndStrokeApply();
                lastUV = new Vector2(-1, -1);

                e.Use(); Repaint();
            }
        }
    }

    private void BeginStrokeUndo()
    {
        if (paintTexture != null)
            Undo.RegisterCompleteObjectUndo(paintTexture, "LgcMeshUVColorPainter Stroke/Fill");
    }
    private void EndStrokeApply()
    {
        if (paintTexture != null)
        {
            paintTexture.Apply(false, false);
            EditorUtility.SetDirty(paintTexture);

            if (enableRealTimePreview)
                ApplyMaterialInstanceWithTexture(paintTexture);
        }
    }
    private void ThrottledApply()
    {
        double now = EditorApplication.timeSinceStartup;
        if (now - lastApplyTime >= APPLY_INTERVAL)
        {
            if (paintTexture != null)
            {
                paintTexture.Apply(false, false);
                EditorUtility.SetDirty(paintTexture);

                if (enableRealTimePreview)
                    ApplyMaterialInstanceWithTexture(paintTexture);
            }
            lastApplyTime = now;
        }
    }

    private void DrawStrokeBetween_Optimized(Vector2 uvA, Vector2 uvB)
    {
        if (paintTexture == null) return;
        Vector2 pA = new Vector2(uvA.x * (paintTexture.width - 1), uvA.y * (paintTexture.height - 1));
        Vector2 pB = new Vector2(uvB.x * (paintTexture.width - 1), uvB.y * (paintTexture.height - 1));
        float distPixels = Vector2.Distance(pA, pB);
        float spacing = Mathf.Max(1f, brushSize * 0.5f);
        int steps = Mathf.Max(1, Mathf.CeilToInt(distPixels / spacing));
        for (int i = 0; i <= steps; i++)
        {
            float t = steps == 0 ? 1f : i / (float)steps;
            Vector2 uv = Vector2.Lerp(uvA, uvB, t);
            PaintAtUV_Optimized(uv);
        }
    }

    private void PaintAtUV_Optimized(Vector2 uv)
    {
        if (paintTexture == null) return;
        EnsureBrushMask();

        int r = Mathf.Max(1, brushSize);
        int cx = Mathf.RoundToInt(uv.x * (paintTexture.width - 1));
        int cy = Mathf.RoundToInt(uv.y * (paintTexture.height - 1));

        int x0 = Mathf.Clamp(cx - r, 0, paintTexture.width - 1);
        int x1 = Mathf.Clamp(cx + r, 0, paintTexture.width - 1);
        int y0 = Mathf.Clamp(cy - r, 0, paintTexture.height - 1);
        int y1 = Mathf.Clamp(cy + r, 0, paintTexture.height - 1);

        int w = x1 - x0 + 1, h = y1 - y0 + 1;

        Color[] dstPixels = paintTexture.GetPixels(x0, y0, w, h);
        int d = r * 2 + 1;

        float op = Mathf.Clamp01(brushOpacity);

        Color[] srcPixels = null;
        bool canRestore = (mode == PaintMode.Erase && restoreTexture != null &&
                           restoreTexture.width == paintTexture.width && restoreTexture.height == paintTexture.height);
        if (canRestore)
            srcPixels = restoreTexture.GetPixels(x0, y0, w, h);

        for (int yy = 0; yy < h; yy++)
        {
            for (int xx = 0; xx < w; xx++)
            {
                int px = x0 + xx, py = y0 + yy;
                int mx = px - (cx - r), my = py - (cy - r);
                if (mx < 0 || my < 0 || mx >= d || my >= d) continue;

                float weight = brushMask[my * d + mx];
                if (weight <= 0f) continue;

                int idx = yy * w + xx;
                Color dst = dstPixels[idx];

                if (mode == PaintMode.Brush)
                {
                    float a = op * weight;
                    Color src = new Color(brushColor.r, brushColor.g, brushColor.b, 1f);
                    dstPixels[idx] = Color.Lerp(dst, src, a);
                }
                else if (mode == PaintMode.Erase)
                {
                    float a = op * weight;
                    if (canRestore)
                    {
                        Color baseC = srcPixels[idx];
                        dstPixels[idx] = Color.Lerp(dst, baseC, a);
                    }
                    else
                    {
                        dst.a = Mathf.Max(0f, dst.a - a);
                        dstPixels[idx] = dst;
                    }
                }
            }
        }

        paintTexture.SetPixels(x0, y0, w, h, dstPixels);
        EditorUtility.SetDirty(paintTexture);
    }

    // ====== 绑定与初始化（信息文本多语言化） ======
    private void TryBindTarget(GameObject obj)
    {
        var L = EditorLanguageManager.Instance;

        // 切换目标前，若曾开启实时预览，先恢复材质
        RestoreOriginalMaterial();
        isClearPending = false;

        targetRenderer = null; targetMesh = null;
        sharedMats = null; matSlotNames = null;
        sourceMaterial = null; mainTexPropName = null;
        originalMaterialAsset = null;
        originalTexture = null; originalAssetPath = null;
        outputAssetPath = null; outputCreatedInSession = false;
        paintTexture = null; restoreTexture = null;
        anyEdits = false; appliedToOutput = false;

        ResetView();
        uvMaskDirty = true;

        if (obj == null)
        {
            infoMessage = L.GetText("InfoWelcome");
            infoType = MessageType.Info;
            return;
        }

        targetRenderer = obj.GetComponent<SkinnedMeshRenderer>();
        if (!targetRenderer) targetRenderer = obj.GetComponent<MeshRenderer>();
        if (!targetRenderer)
        {
            infoMessage = L.GetText("InfoNoRenderer");
            infoType = MessageType.Warning;
            return;
        }

        MeshFilter mf = obj.GetComponent<MeshFilter>();
        if (mf && mf.sharedMesh) targetMesh = mf.sharedMesh;
        else if (targetRenderer is SkinnedMeshRenderer sk && sk.sharedMesh) targetMesh = sk.sharedMesh;

        if (!targetMesh || targetMesh.vertexCount == 0)
        {
            infoMessage = L.GetText("InfoNoMesh");
            infoType = MessageType.Warning;
            return;
        }

        sharedMats = targetRenderer.sharedMaterials;
        if (sharedMats == null || sharedMats.Length == 0)
        {
            infoMessage = L.GetText("InfoNoMats");
            infoType = MessageType.Info;

            GenerateDefaultPaintTexture(1024, 1024, new Color(0, 0, 0, 0));
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
        isClearPending = false;
        ResetView();

        anyEdits = false; appliedToOutput = false;
        sourceMaterial = null; mainTexPropName = null;
        originalMaterialAsset = null;
        originalTexture = null; originalAssetPath = null;
        outputAssetPath = null; outputCreatedInSession = false;
        paintTexture = null; restoreTexture = null;

        if (sharedMats == null || sharedMats.Length == 0 || selectedMatIndex < 0 || selectedMatIndex >= sharedMats.Length)
        {
            infoMessage = L.GetText("InfoSlotInvalid");
            infoType = MessageType.Warning;
            UpdateDefaultOutputPath();
            return;
        }

        originalMaterialAsset = sharedMats[selectedMatIndex];
        if (!originalMaterialAsset)
        {
            infoMessage = string.Format(L.GetText("InfoSlotEmpty"), selectedMatIndex);
            infoType = MessageType.Warning;
            UpdateDefaultOutputPath();
            return;
        }

        sourceMaterial = originalMaterialAsset;
        mainTexPropName = FindMainTexturePropertyName(sourceMaterial);
        Texture tex = null;
        if (!string.IsNullOrEmpty(mainTexPropName))
            tex = sourceMaterial.GetTexture(mainTexPropName);

        if (tex is Texture2D tex2D)
        {
            originalTexture = tex2D;
            originalAssetPath = AssetDatabase.GetAssetPath(originalTexture);

            CreatePaintCopyFromSource(originalTexture);
            restoreTexture = DuplicateReadable(originalTexture);

            UpdateDefaultOutputPath();
            bool existedBefore = AssetDatabase.LoadAssetAtPath<Object>(outputAssetPath) != null;
            WriteTextureToPng(paintTexture, outputAssetPath);
            outputCreatedInSession = !existedBefore;

            infoMessage = string.Format(L.GetText("InfoReadMainTexOk"), originalTexture.name, outputAssetPath);
            infoType = MessageType.Info;
        }
        else
        {
            GenerateDefaultPaintTexture(1024, 1024, new Color(0, 0, 0, 0));
            restoreTexture = null;

            UpdateDefaultOutputPath();
            bool existedBefore = AssetDatabase.LoadAssetAtPath<Object>(outputAssetPath) != null;
            WriteTextureToPng(paintTexture, outputAssetPath);
            outputCreatedInSession = !existedBefore;

            infoMessage = string.Format(L.GetText("InfoNoMainTex"), outputAssetPath);
            infoType = MessageType.Info;
        }

        uvMaskDirty = true;
    }

    // ====== 保存/导出/清空（与 v0.5.0 语义一致，信息文本多语言化） ======
    private void SaveOutputOnly()
    {
        var L = EditorLanguageManager.Instance;

        if (paintTexture == null)
        {
            infoMessage = L.GetText("InfoNoMesh"); // 简化处理：复用提示
            infoType = MessageType.Warning;
            return;
        }
        if (string.IsNullOrEmpty(outputAssetPath))
            UpdateDefaultOutputPath();

        WriteTextureToPng(paintTexture, outputAssetPath);
        AssetDatabase.ImportAsset(outputAssetPath, ImportAssetOptions.ForceUpdate);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();

        appliedToOutput = true;
        anyEdits = false;

        infoMessage = string.Format(L.GetText("SaveOk"), outputAssetPath);
        infoType = MessageType.Info;
    }

    private void ExportPaintLayerOnly()
    {
        var L = EditorLanguageManager.Instance;

        if (paintTexture == null)
        {
            infoMessage = L.GetText("InfoNoMesh");
            infoType = MessageType.Warning;
            return;
        }

        int w = paintTexture.width, h = paintTexture.height;
        Texture2D outTex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
        outTex.wrapMode = TextureWrapMode.Clamp;
        outTex.filterMode = FilterMode.Bilinear;

        Color[] painted = paintTexture.GetPixels();
        Color[] basePx = (restoreTexture != null && restoreTexture.width == w && restoreTexture.height == h)
                        ? restoreTexture.GetPixels()
                        : null;

        const float eps = 1f / 255f;
        for (int i = 0; i < painted.Length; i++)
        {
            Color p = painted[i];
            if (basePx == null)
            {
                if (p.a <= eps) outTex.SetPixel(i % w, i / w, new Color(0, 0, 0, 0));
                else outTex.SetPixel(i % w, i / w, new Color(p.r, p.g, p.b, p.a));
            }
            else
            {
                Color b = basePx[i];
                float dr = Mathf.Abs(p.r - b.r);
                float dg = Mathf.Abs(p.g - b.g);
                float db = Mathf.Abs(p.b - b.b);
                float da = Mathf.Abs(p.a - b.a);
                float diff = Mathf.Max(Mathf.Max(dr, dg), Mathf.Max(db, da));

                if (diff <= eps) outTex.SetPixel(i % w, i / w, new Color(0, 0, 0, 0));
                else outTex.SetPixel(i % w, i / w, new Color(p.r, p.g, p.b, Mathf.Clamp01(diff)));
            }
        }
        outTex.Apply(false, false);

        string baseName = originalTexture ? originalTexture.name : "未命名";
        foreach (char ch in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(ch, '_');
        string folder = "Assets/LGC/Tools/UV绘画/输出图片";
        if (!AssetDatabase.IsValidFolder(folder)) CreateFoldersRecursively(folder);
        string layerPath = $"{folder}/{baseName}_绘画层.png";

        WriteTextureToPng(outTex, layerPath);
        AssetDatabase.ImportAsset(layerPath, ImportAssetOptions.ForceUpdate);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
        DestroyImmediate(outTex);

        infoMessage = string.Format(L.GetText("ExportLayerOk"), layerPath);
        infoType = MessageType.Info;
        Repaint();
    }

    private void ClearThisSessionEdits()
    {
        var L = EditorLanguageManager.Instance;

        Texture2D saved = !string.IsNullOrEmpty(outputAssetPath) ? AssetDatabase.LoadAssetAtPath<Texture2D>(outputAssetPath) : null;

        if (saved != null)
        {
            CreatePaintCopyFromSource(saved);
            infoMessage = L.GetText("ClearToSaved");
            infoType = MessageType.Info;
        }
        else if (originalTexture != null)
        {
            CreatePaintCopyFromSource(originalTexture);
            infoMessage = L.GetText("ClearToOriginal");
            infoType = MessageType.Info;
        }
        else
        {
            GenerateDefaultPaintTexture(1024, 1024, new Color(0, 0, 0, 0));
            infoMessage = L.GetText("ClearToBlank");
            infoType = MessageType.Info;
        }

        if (originalTexture != null)
            restoreTexture = DuplicateReadable(originalTexture);
        else
            restoreTexture = null;

        anyEdits = false;

        if (enableRealTimePreview && paintTexture != null)
            ApplyMaterialInstanceWithTexture(paintTexture);

        Repaint();
    }

    // ====== 改造2：定位按钮逻辑修复（递归创建并打开目录） ======
    private void LocateAndCreateResourceDirectory()
    {
        var L = EditorLanguageManager.Instance;

        // 递归创建
        if (!AssetDatabase.IsValidFolder(_targetResourceDir))
        {
            CreateFoldersRecursively(_targetResourceDir);
            AssetDatabase.Refresh();

            Debug.Log(LOG_PREFIX + L.GetText("LocateCreateOk") + _targetResourceDir);
        }
        else
        {
            Debug.Log(LOG_PREFIX + L.GetText("LocateExists") + _targetResourceDir);
        }

        // 高亮并打开
        var obj = AssetDatabase.LoadAssetAtPath<Object>(_targetResourceDir);
        if (obj != null)
        {
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = obj;
            EditorGUIUtility.PingObject(obj);

            // 尝试打开（对文件夹未必有可见动作，但调用无害）
            AssetDatabase.OpenAsset(obj);
            Debug.Log(LOG_PREFIX + L.GetText("LocateOpenOk") + _targetResourceDir);
        }
    }

    // ====== 材质 & 资产（与 v0.5.0 相同） ======
    private void ApplyMaterialInstanceWithTexture(Texture2D tex)
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
            foreach (var fb in fallbacks)
            {
                if (mat.HasProperty(fb) && mat.GetTexture(fb) != null)
                    return fb;
            }
        }
        return best;
    }

    private void AssignTextureToAllLikelyMainProps(Material mat, Texture2D tex)
    {
        if (mat == null || tex == null) return;

        if (string.IsNullOrEmpty(mainTexPropName))
            mainTexPropName = FindMainTexturePropertyName(mat);
        if (!string.IsNullOrEmpty(mainTexPropName) && mat.HasProperty(mainTexPropName))
        {
            Undo.RecordObject(mat, "Assign MainTex (Primary)");
            mat.SetTexture(mainTexPropName, tex);
        }

        string[] candidates = { "_BaseMap", "_MainTex", "_BaseColorMap", "_BaseColorTexture", "_Albedo", "_AlbedoMap", "MainTex" };
        foreach (var n in candidates)
        {
            if (mat.HasProperty(n))
            {
                Undo.RecordObject(mat, $"Assign {n}");
                mat.SetTexture(n, tex);
            }
        }

        Undo.RecordObject(mat, "Assign mainTexture");
        mat.mainTexture = tex;
        EditorUtility.SetDirty(mat);
    }

    // ====== UV 孤岛填充与掩码（与 v0.5.0 一致，提示语多语言化） ======
    private void FillIslandAtUV(Vector2 uv)
    {
        var L = EditorLanguageManager.Instance;

        if (paintTexture == null) return;
        RebuildUVCoverageMaskIfNeeded();

        int w = paintTexture.width, h = paintTexture.height;
        int sx = Mathf.Clamp(Mathf.RoundToInt(uv.x * (w - 1)), 0, w - 1);
        int sy = Mathf.Clamp(Mathf.RoundToInt(uv.y * (h - 1)), 0, h - 1);
        int startIdx = sy * w + sx;

        if (uvMask == null || uvMask.Length != w * h || uvMask[startIdx] == 0)
        {
            infoMessage = L.GetText("WarnFillOutsideUV");
            infoType = MessageType.Warning;
            return;
        }

        Color[] pixels = paintTexture.GetPixels();
        float a = Mathf.Clamp01(brushOpacity);
        Color fillC = new Color(brushColor.r, brushColor.g, brushColor.b, 1f);

        bool[] visited = new bool[w * h];
        Stack<int> stack = new Stack<int>(2048);

        stack.Push(startIdx); visited[startIdx] = true;
        int filled = 0;

        while (stack.Count > 0)
        {
            int idx = stack.Pop();
            Color dst = pixels[idx];
            pixels[idx] = Color.Lerp(dst, fillC, a);
            filled++;

            int x = idx % w, y = idx / w;
            TryPush(x - 1, y); TryPush(x + 1, y); TryPush(x, y - 1); TryPush(x, y + 1);

            void TryPush(int nx, int ny)
            {
                if (nx < 0 || ny < 0 || nx >= w || ny >= h) return;

                int nidx = ny * w + nx;
                if (!visited[nidx] && uvMask[nidx] == 1)
                {
                    visited[nidx] = true; stack.Push(nidx);
                }
            }
        }

        paintTexture.SetPixels(pixels);
        EditorUtility.SetDirty(paintTexture);
        infoMessage = $"UV岛填充像素数：{filled}";
        infoType = MessageType.Info;
    }

    private void RebuildUVCoverageMaskIfNeeded()
    {
        if (paintTexture == null || targetMesh == null) { uvMask = null; return; }
        if (!uvMaskDirty && uvMask != null && maskW == paintTexture.width && maskH == paintTexture.height) return;

        maskW = paintTexture.width; maskH = paintTexture.height;
        uvMask = new byte[maskW * maskH];

        var uvs = targetMesh.uv; var tris = targetMesh.triangles;
        if (uvs == null || uvs.Length == 0 || tris == null || tris.Length == 0) return;

        for (int i = 0; i < tris.Length; i += 3)
        {
            int i0 = tris[i], i1 = tris[i + 1], i2 = tris[i + 2];
            if (i0 < 0 || i1 < 0 || i2 < 0 || i0 >= uvs.Length || i1 >= uvs.Length || i2 >= uvs.Length) continue;
            RasterizeTriangleToMask(uvs[i0], uvs[i1], uvs[i2]);
        }

        uvMaskDirty = false;
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

    // ====== 纹理/文件 工具 ======
    private void GenerateDefaultPaintTexture(int w, int h, Color c)
    {
        CleanupWorkingPaint();
        paintTexture = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
        paintTexture.wrapMode = TextureWrapMode.Clamp;
        paintTexture.filterMode = FilterMode.Bilinear;
        var px = Enumerable.Repeat(c, w * h).ToArray();
        paintTexture.SetPixels(px); paintTexture.Apply(false, false);
        EditorUtility.SetDirty(paintTexture);
    }

    private void CreatePaintCopyFromSource(Texture2D src)
    {
        CleanupWorkingPaint();
        paintTexture = DuplicateReadable(src);
        paintTexture.name = src.name + "_PaintCopy";
        paintTexture.wrapMode = TextureWrapMode.Clamp;
        paintTexture.filterMode = FilterMode.Bilinear;
        EditorUtility.SetDirty(paintTexture);
    }

    private void CleanupWorkingPaint()
    {
        if (paintTexture != null) DestroyImmediate(paintTexture);
        paintTexture = null;
    }

    private Texture2D DuplicateReadable(Texture2D src)
    {
        RenderTexture rt = RenderTexture.GetTemporary(src.width, src.height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default);
        Graphics.Blit(src, rt);
        var prev = RenderTexture.active; RenderTexture.active = rt;
        Texture2D dst = new Texture2D(src.width, src.height, TextureFormat.RGBA32, false, false);
        dst.ReadPixels(new Rect(0, 0, src.width, src.height), 0, 0);
        dst.Apply(false, false);
        RenderTexture.active = prev; RenderTexture.ReleaseTemporary(rt);
        return dst;
    }

    private void WriteTextureToPng(Texture2D tex, string assetPath)
    {
        if (tex == null || string.IsNullOrEmpty(assetPath)) return;

        string dir = Path.GetDirectoryName(assetPath).Replace('\\', '/');
        if (!AssetDatabase.IsValidFolder(dir)) CreateFoldersRecursively(dir);

        string assetsFull = Application.dataPath;
        string sub = assetPath.Substring("Assets".Length).TrimStart('/', '\\');
        string fullPath = Path.Combine(assetsFull, sub);

        byte[] bytes = tex.EncodeToPNG();
        File.WriteAllBytes(fullPath, bytes);
        AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);
        AssetDatabase.SaveAssets(); AssetDatabase.Refresh();
    }

    private void CreateFoldersRecursively(string path)
    {
        string[] parts = path.Split('/');
        string current = parts[0];
        for (int i = 1; i < parts.Length; i++)
        {
            string next = current + "/" + parts[i];
            if (!AssetDatabase.IsValidFolder(next))
                AssetDatabase.CreateFolder(current, parts[i]);
            current = next;
        }
    }

    private void UpdateDefaultOutputPath()
    {
        string baseName = originalTexture ? originalTexture.name : "未命名";
        foreach (char ch in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(ch, '_');
        string folder = "Assets/LGC/Tools/UV绘画/输出图片";
        if (!AssetDatabase.IsValidFolder(folder)) CreateFoldersRecursively(folder);
        outputAssetPath = $"{folder}/{baseName}.png";
    }

    // 清理未应用的输出文件（关闭窗口或切换时调用）
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

    // ====== 背景棋盘格 ======
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
        {
            for (int x = 0; x < s; x++)
            {
                bool useC0 = ((x < s / 2) ^ (y < s / 2));
                checkerTex.SetPixel(x, y, useC0 ? c0 : c1);
            }
        }
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

    private void ResetView()
    {
        zoom = 1f;
        viewCenter = new Vector2(0.5f, 0.5f);
    }
}
#endif