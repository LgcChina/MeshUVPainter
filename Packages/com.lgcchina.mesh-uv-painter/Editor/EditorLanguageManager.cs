#if UNITY_EDITOR
// EditorLanguageManager.cs (v3.0 beta)
// 单例 + 多语言文本管理（Chinese / Japanese / English）
// 用途：被编辑器工具与“关于作者”窗口统一调用
// 已补全：所有折叠标题/按钮/提示 强制必填Key

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public enum EditorLanguage
{
    Chinese = 0,
    Japanese = 1,
    English = 2
}

public sealed class EditorLanguageManager
{
    private static EditorLanguageManager _instance;
    public static EditorLanguageManager Instance => _instance ?? (_instance = new EditorLanguageManager());

    // 当前语言（默认中文）
    public EditorLanguage CurrentLang = EditorLanguage.Chinese;

    // 三语词库
    private readonly Dictionary<string, string> _zh = new Dictionary<string, string>();
    private readonly Dictionary<string, string> _ja = new Dictionary<string, string>();
    private readonly Dictionary<string, string> _en = new Dictionary<string, string>();

    private bool _inited = false;

    private EditorLanguageManager() { }

    // 初始化所有 UI / 文案键值
    public void InitLanguageData()
    {
        if (_inited) return;
        _inited = true;

        _zh["ModeSelection"] = "框选";
        _ja["ModeSelection"] = "範囲選択";
        _en["ModeSelection"] = "Select";
        _zh["PaintSelectionTitle"] = "绘制内容框选";
        _ja["PaintSelectionTitle"] = "描画範囲選択";
        _en["PaintSelectionTitle"] = "Paint Selection";
        _zh["SelectionMove"] = "移动";
        _ja["SelectionMove"] = "移動";
        _en["SelectionMove"] = "Move";
        _zh["SelectionRotate"] = "旋转";
        _ja["SelectionRotate"] = "回転";
        _en["SelectionRotate"] = "Rotate";
        _zh["SelectionScale"] = "缩放";
        _ja["SelectionScale"] = "拡縮";
        _en["SelectionScale"] = "Scale";
        _zh["SelectionFill"] = "孤岛填充";
        _ja["SelectionFill"] = "アイランド塗りつぶし";
        _en["SelectionFill"] = "Island Fill";
        _zh["SelectionOffset"] = "位移（像素）";
        _ja["SelectionOffset"] = "移動（ピクセル）";
        _en["SelectionOffset"] = "Offset (pixels)";
        _zh["SelectionAngle"] = "旋转角度";
        _ja["SelectionAngle"] = "回転角度";
        _en["SelectionAngle"] = "Rotation";
        _zh["SelectionApply"] = "应用变换";
        _ja["SelectionApply"] = "変形を適用";
        _en["SelectionApply"] = "Apply Transform";
        _zh["SelectionCancel"] = "取消变换";
        _ja["SelectionCancel"] = "変形をキャンセル";
        _en["SelectionCancel"] = "Cancel Transform";
        _zh["RegionHint"] = "悬停孤岛黄色高亮，点击绿色选中；框选选中与矩形相交的完整 UV 三角面。Shift 加选，Ctrl/Command 减选。隐藏只影响画布，隐藏和禁用区域不可编辑，不影响导出。";
        _ja["RegionHint"] = "ホバーは黄色、選択は緑。矩形と交差するUV三角面全体を選択。Shift追加、Ctrl解除。非表示・保護領域は編集不可、出力は維持。";
        _en["RegionHint"] = "Hover islands in yellow; selection is green. Rectangles select entire intersecting UV triangles. Shift adds, Ctrl/Command subtracts. Hidden/protected areas block edits; exports retain content.";
        _zh["CanvasUVSelectionHint"] = "左键选择 · Shift 加选 · Ctrl 减选 · 中键拖动画布";
        _ja["CanvasUVSelectionHint"] = "左：選択・Shift追加・Ctrl解除・中：パン";
        _en["CanvasUVSelectionHint"] = "Left select · Shift add · Ctrl subtract · Middle pan";
        _zh["SelectionGradientTab"] = "框选与渐变";
        _ja["SelectionGradientTab"] = "選択・グラデーション";
        _en["SelectionGradientTab"] = "Select / Gradient";
        _zh["SelectionRemoveHint"] = "移除选框并取消待应用变换，保留已绘制内容";
        _ja["SelectionRemoveHint"] = "選択と未適用の変形を解除（確定済みの描画を保持）";
        _en["SelectionRemoveHint"] = "Clear selection and cancel pending transform; keep committed paint";
        _zh["StampRemoveHint"] = "移除待应用盖印图案，保留已绘制内容";
        _ja["StampRemoveHint"] = "未適用スタンプを削除（確定済みの描画を保持）";
        _en["StampRemoveHint"] = "Remove pending stamp; keep committed paint";
        // v3.0 beta: Native gradient ramp and compact UV region controls.
        _zh["GradientColors"] = "渐变颜色";
        _ja["GradientColors"] = "グラデーション色";
        _en["GradientColors"] = "Gradient Colors";
        _zh["GradientKeysHint"] = "点击色带打开编辑器：下方添加/移动颜色标记，上方调整透明度；位置百分比控制各段占比。渐变效果统一由绘制目标区“实时预览效果”控制。";
        _ja["GradientKeysHint"] = "色帯をクリック：下のキーで色と位置、上のキーで透明度を編集。プレビューはターゲットのリアルタイム表示で切替。";
        _en["GradientKeysHint"] = "Click the ramp to edit color keys below and alpha keys above. Key positions control segment lengths. Use the target Live Preview switch.";
        _zh["GradientReverse"] = "反转渐变";
        _ja["GradientReverse"] = "グラデーション反転";
        _en["GradientReverse"] = "Reverse Gradient";
        _zh["RegionSelectedActions"] = "所选 UV 操作";
        _ja["RegionSelectedActions"] = "選択 UV の操作";
        _en["RegionSelectedActions"] = "Selected UV Actions";
        _zh["RegionFocusTitle"] = "聚焦所选（可同时启用）";
        _ja["RegionFocusTitle"] = "選択に集中（併用可）";
        _en["RegionFocusTitle"] = "Focus Selected (combine both)";
        _zh["RegionResetAll"] = "恢复全部显示与编辑";
        _ja["RegionResetAll"] = "表示と編集をすべて復元";
        _en["RegionResetAll"] = "Restore All Visibility / Editing";
        _zh["AdvancedTitle"] = "高级选项";
        _ja["AdvancedTitle"] = "詳細設定";
        _en["AdvancedTitle"] = "Advanced";
        _zh["OriginalSize"] = "原图尺寸：";
        _ja["OriginalSize"] = "元画像サイズ：";
        _en["OriginalSize"] = "Original: ";
        _zh["WorkingSize"] = "绘制清晰度：";
        _ja["WorkingSize"] = "描画解像度：";
        _en["WorkingSize"] = "Working: ";
        _zh["UnifiedFill"] = "填充";
        _ja["UnifiedFill"] = "塗りつぶし";
        _en["UnifiedFill"] = "Fill";
        _zh["FillScope"] = "填充范围";
        _ja["FillScope"] = "塗りつぶし範囲";
        _en["FillScope"] = "Fill Scope";
        _zh["FillByIsland"] = "按孤岛";
        _ja["FillByIsland"] = "アイランド";
        _en["FillByIsland"] = "UV Island";
        _zh["FillByContent"] = "按绘制内容";
        _ja["FillByContent"] = "描画内容";
        _en["FillByContent"] = "Painted Content";
        _zh["FillBySelection"] = "按框选范围";
        _ja["FillBySelection"] = "選択範囲";
        _en["FillBySelection"] = "Selection";
        _zh["UnifiedFillHint"] = "按孤岛：点击填充目标 UV 岛；按绘制内容：填充点击处相连的非透明笔画；按框选范围：填充全部框选区域。三种方式均遵守选区和 UV 保护。";
        _ja["UnifiedFillHint"] = "アイランド：クリックした島。描画内容：クリック位置につながる不透明部分。選択範囲：全選択領域。UV保護と選択制限を維持。";
        _en["UnifiedFillHint"] = "Island fills the clicked UV island. Painted Content fills connected nontransparent paint at the click. Selection fills the entire selected mask. All respect selection and UV protection.";
        _zh["FillNeedsSelection"] = "请先在“框选与渐变”中框选范围。";
        _ja["FillNeedsSelection"] = "先に「選択・グラデーション」で範囲を選択してください。";
        _en["FillNeedsSelection"] = "Create a selection in Select / Gradient first.";
        _zh["EraseType"] = "擦除方式";
        _ja["EraseType"] = "消去方法";
        _en["EraseType"] = "Erase Mode";
        _zh["EraseRegular"] = "常规擦除";
        _ja["EraseRegular"] = "ブラシ消去";
        _en["EraseRegular"] = "Brush Erase";
        _zh["EraseIsland"] = "孤岛擦除";
        _ja["EraseIsland"] = "アイランド消去";
        _en["EraseIsland"] = "Island Erase";
        _zh["EraseGradient"] = "渐变式擦除";
        _ja["EraseGradient"] = "グラデーション消去";
        _en["EraseGradient"] = "Gradient Erase";
        _zh["EraseStart"] = "起点擦除强度";
        _ja["EraseStart"] = "始点の消去量";
        _en["EraseStart"] = "Start Erase";
        _zh["EraseEnd"] = "终点擦除强度";
        _ja["EraseEnd"] = "終点の消去量";
        _en["EraseEnd"] = "End Erase";
        _zh["GradientEraseHint"] = "左键拖出渐变擦除方向，拖动两端圆点调整；设置起终点擦除强度和不透明度。预览后点击应用，支持一次撤回；原始贴图不会被擦除。";
        _ja["GradientEraseHint"] = "左ドラッグで消去方向を指定、端点で調整。始点・終点の消去量と不透明度を設定。プレビューはターゲットのリアルタイム表示で切替。適用で確定、元画像は保持。";
        _en["GradientEraseHint"] = "Drag an erase direction and adjust endpoints. Set start/end erase strength and opacity. Use target Live Preview, then Apply. One undo; the base texture is preserved.";
        _zh["PaintSelectionHint"] = "左键框选；Shift 加选，Ctrl/Command 减选。选框左上角旋转、左下角移动、右下角缩放，右上角移除选框（保留绘制内容），拖动小按钮即可预览。应用后可撤回；填充请回绘制 Tab 使用“填充”。选框在切换工具后保持显示并持续限制绘制，取消选择可恢复。";
        _ja["PaintSelectionHint"] = "左ドラッグで矩形、Shift追加、Ctrl解除。左上：回転、左下：移動、右下：拡縮、右上：選択解除（描画を保持）。選択枠はタブ切替後も表示。角ボタンをドラッグして適用。塗りつぶしは描画タブを使用。解除まで描画を制限。";
        _en["PaintSelectionHint"] = "Drag to select; Shift adds, Ctrl/Command subtracts. Drag top-left rotate, bottom-left move, bottom-right scale; top-right clears selection and keeps paint. The outline stays visible across tools. Apply to commit. Use Fill in Draw. Selection clips painting until cleared.";
        _zh["CanvasSelectionHint"] = "左键框选 · 角落按钮变换 · Shift 加选 · Ctrl 减选 · 中键拖动";
        _ja["CanvasSelectionHint"] = "左：選択・角：変形・Shift追加・Ctrl解除・中：パン";
        _en["CanvasSelectionHint"] = "Left select · Corner handles transform · Shift add · Ctrl subtract · Middle pan";
        _zh["CanvasSelectedPaintHint"] = "左键绘制（限选区）· 中键拖动画布 · 框选与渐变可取消选择";
        _ja["CanvasSelectedPaintHint"] = "左：選択内描画・中：パン・選択タブで解除";
        _en["CanvasSelectedPaintHint"] = "Left paint within selection · Middle pan · Clear in Select / Gradient";
        _zh["EraseSwap"] = "交换擦除强度";
        _ja["EraseSwap"] = "消去量を交換";
        _en["EraseSwap"] = "Swap Erase Strength";
        _zh["EraseApply"] = "应用渐变擦除";
        _ja["EraseApply"] = "グラデーション消去を適用";
        _en["EraseApply"] = "Apply Gradient Erase";
        _zh["CanvasGradientHint"] = "左键拖出方向 · 拖动端点调整 · 应用后写入 · 中键拖动画布";
        _ja["CanvasGradientHint"] = "左：方向線・端点で調整・適用で確定・中：パン";
        _en["CanvasGradientHint"] = "Left drag direction · Adjust endpoints · Apply to commit · Middle pan";
        // v2.9.0: Exact stamp preview and alpha-preserving color overlay.
        _zh["StampOverlayColor"] = "图案叠加颜色";
        _ja["StampOverlayColor"] = "スタンプ重ね色";
        _en["StampOverlayColor"] = "Overlay Color";
        _zh["StampColorStrength"] = "颜色叠加强度";
        _ja["StampColorStrength"] = "色の適用強度";
        _en["StampColorStrength"] = "Color Strength";
        _zh["StampPreviewHint"] = "移动、旋转、缩放和改色可预览结果；开启目标区实时预览可同步查看模型。点击应用盖印才写入，导出不包含待确认图案。";
        _ja["StampPreviewHint"] = "移動・回転・拡縮・色をプレビュー。ターゲットのリアルタイム表示でモデルにも反映。適用で確定し、未適用分は出力されません。";
        _en["StampPreviewHint"] = "Preview movement, rotation, scale and color. Enable target live preview to view on the model. Apply to commit; exports exclude pending stamps.";

        // ---- 通用 / 工具名 / 底部提示 ----
        _zh["ToolDisplayName"] = "LGC网格UV绘画工具";
        _ja["ToolDisplayName"] = "LGCメッシュUVペイントツール";
        _en["ToolDisplayName"] = "LGC Mesh UV Painter";

        _zh["ClickToSeeMore"] = "点击查看更多详细信息";
        _ja["ClickToSeeMore"] = "詳しい情報を見るにはクリック";
        _en["ClickToSeeMore"] = "Click to see more details";

        _zh["BottomAuthorInfo"] = "作者：Copilote + DeepSeek + LGC";
        _ja["BottomAuthorInfo"] = "作者：Copilote + DeepSeek + LGC";
        _en["BottomAuthorInfo"] = "Author: Copilote + DeepSeek + LGC";

        _zh["BottomVersionInfo"] = "版本: {0}";
        _ja["BottomVersionInfo"] = "バージョン: {0}";
        _en["BottomVersionInfo"] = "Version: {0}";

        // ---- 面板结构 ----
        _zh["LeftPanelTitle"] = "工具区";
        _ja["LeftPanelTitle"] = "ツールエリア";
        _en["LeftPanelTitle"] = "Tools";

        _zh["RightPanelTitle"] = "绘制区";
        _ja["RightPanelTitle"] = "ペイントエリア";
        _en["RightPanelTitle"] = "Canvas";

        // ---- 顶部语言切换 ----
        _zh["LangDropdownTooltip"] = "切换界面语言";
        _ja["LangDropdownTooltip"] = "UI言語を切り替え";
        _en["LangDropdownTooltip"] = "Switch UI language";

        // ==============================================
        // 一、【强制必填】折叠面板/分类标题 Key
        // ==============================================
        _zh["TargetSelectionFoldout"] = "目标选择";
        _ja["TargetSelectionFoldout"] = "ターゲット選択";
        _en["TargetSelectionFoldout"] = "Target Selection";

        _zh["ModeSectionFoldout"] = "绘制模式";
        _ja["ModeSectionFoldout"] = "描画モード";
        _en["ModeSectionFoldout"] = "Paint Mode";

        _zh["BrushSectionFoldout"] = "笔刷设置";
        _ja["BrushSectionFoldout"] = "ブラシ設定";
        _en["BrushSectionFoldout"] = "Brush Settings";

        _zh["BoundarySectionFoldout"] = "边界与隔离";
        _ja["BoundarySectionFoldout"] = "境界とアイソレーション";
        _en["BoundarySectionFoldout"] = "Boundary & Isolation";

        _zh["SymmetrySectionFoldout"] = "对称绘画";
        _ja["SymmetrySectionFoldout"] = "対称ペイント";
        _en["SymmetrySectionFoldout"] = "Symmetry Painting";

        _zh["UVOverlayFoldout"] = "UV叠加设置";
        _ja["UVOverlayFoldout"] = "UVオーバーレイ設定";
        _en["UVOverlayFoldout"] = "UV Overlay";

        _zh["SaveSectionFoldout"] = "保存与导出";
        _ja["SaveSectionFoldout"] = "保存と出力";
        _en["SaveSectionFoldout"] = "Save & Export";

        // ---- 目标对象 / 材质槽 ----
        _zh["TargetObject"] = "网格对象";
        _ja["TargetObject"] = "メッシュオブジェクト";
        _en["TargetObject"] = "Mesh Object";

        _zh["MatSlot"] = "材质槽";
        _ja["MatSlot"] = "マテリアルスロット";
        _en["MatSlot"] = "Material Slot";

        _zh["MatSlotNone"] = "（无）";
        _ja["MatSlotNone"] = "（なし）";
        _en["MatSlotNone"] = "(None)";

        _zh["RefreshSlot"] = "刷新/重新读取当前槽";
        _ja["RefreshSlot"] = "リフレッシュ/再読み込み";
        _en["RefreshSlot"] = "Refresh / Reload Slot";

        // 兼容保留键
        _zh["MaterialInfoFold"] = "材质信息";
        _ja["MaterialInfoFold"] = "マテリアル情報";
        _en["MaterialInfoFold"] = "Material Info";
        _zh["MatCurrent"] = "当前材质";
        _ja["MatCurrent"] = "現在のマテリアル";
        _en["MatCurrent"] = "Current Material";
        _zh["MatOriginalAsset"] = "原始材质资产";
        _ja["MatOriginalAsset"] = "元のマテリアルアセット";
        _en["MatOriginalAsset"] = "Original Material Asset";
        _zh["MainTexProp"] = "主纹理属性";
        _ja["MainTexProp"] = "メインテクスチャプロパティ";
        _en["MainTexProp"] = "Main Texture Property";
        _zh["MainTexOriginal"] = "原始主色图";
        _ja["MainTexOriginal"] = "元のメインテクスチャ";
        _en["MainTexOriginal"] = "Original Main Texture";
        _zh["OutputPng"] = "输出 PNG";
        _ja["OutputPng"] = "出力 PNG";
        _en["OutputPng"] = "Output PNG";
        _zh["PaintCopy"] = "绘制副本";
        _ja["PaintCopy"] = "ペイントコピー";
        _en["PaintCopy"] = "Paint Copy";

        // ==============================================
        // 二、【强制必填】功能按钮/控件 Key
        // ==============================================
        // 模式相关
        _zh["ModeTitle"] = "模式";
        _ja["ModeTitle"] = "モード";
        _en["ModeTitle"] = "Modes";
        _zh["ModeBrush"] = "画笔模式";
        _ja["ModeBrush"] = "ブラシモード";
        _en["ModeBrush"] = "Brush Mode";
        _zh["ModeErase"] = "橡皮擦模式";
        _ja["ModeErase"] = "消しゴムモード";
        _en["ModeErase"] = "Eraser Mode";
        _zh["ModeFill"] = "孤岛填充模式（UV）";
        _ja["ModeFill"] = "アイランド塗りつぶし（UV）";
        _en["ModeFill"] = "UV Island Fill";
        _zh["ModeIslandErase"] = "孤岛橡皮擦（UV）";
        _ja["ModeIslandErase"] = "アイランド消しゴム（UV）";
        _en["ModeIslandErase"] = "UV Island Eraser";

        // 笔刷/填充
        _zh["BrushAndFill"] = "画笔与填充";
        _ja["BrushAndFill"] = "ブラシと塗りつぶし";
        _en["BrushAndFill"] = "Brush & Fill";
        _zh["BrushOrFillColor"] = "画笔/填充颜色";
        _ja["BrushOrFillColor"] = "ブラシ/塗りつぶし色";
        _en["BrushOrFillColor"] = "Brush/Fill Color";
        _zh["BrushRadius"] = "画笔半径（像素）";
        _ja["BrushRadius"] = "ブラシ半径（px）";
        _en["BrushRadius"] = "Brush Radius (px)";
        _zh["Opacity"] = "不透明度";
        _ja["Opacity"] = "不透明度";
        _en["Opacity"] = "Opacity";
        _zh["Hardness"] = "硬度（边缘）";
        _ja["Hardness"] = "硬さ（エッジ）";
        _en["Hardness"] = "Hardness (Edge)";

        // 边界/隔离
        _zh["BoundaryAndIsolationTitle"] = "边界与隔离";
        _ja["BoundaryAndIsolationTitle"] = "境界とアイソレーション";
        _en["BoundaryAndIsolationTitle"] = "Boundary & Isolation";
        _zh["ToggleUVBoundaryLimit"] = "UV 边界限制（仅在 UV 岛内绘制）";
        _ja["ToggleUVBoundaryLimit"] = "UV境界制限（UVアイランド内のみ描画）";
        _en["ToggleUVBoundaryLimit"] = "UV Boundary Limit (restrict to island)";
        _zh["ToggleIslandIsolation"] = "孤岛隔离模式（仅作用当前岛的画笔/橡皮擦）";
        _ja["ToggleIslandIsolation"] = "アイソレーション（現在のアイランドのブラシ/消しのみ）";
        _en["ToggleIslandIsolation"] = "Island Isolation (affect current island only)";
        _zh["RealtimePreviewToggle"] = "实时预览效果";
        _ja["RealtimePreviewToggle"] = "リアルタイム表示";
        _en["RealtimePreviewToggle"] = "Realtime Preview";

        // ★ 新增：边界扩展（包含回车后应用提示）
        _zh["BoundaryExpandPixels"] = "边界扩展（回车后应用）";
        _ja["BoundaryExpandPixels"] = "境界拡張（Enterで適用）";
        _en["BoundaryExpandPixels"] = "Boundary Expand (Apply on Enter)";

        // 对称绘画
        _zh["SymmetryTitle"] = "对称绘画";
        _ja["SymmetryTitle"] = "対称ペイント";
        _en["SymmetryTitle"] = "Symmetry Painting";
        _zh["SymmetryEnable"] = "启用对称绘画";
        _ja["SymmetryEnable"] = "対称ペイントを有効化";
        _en["SymmetryEnable"] = "Enable symmetry painting";
        _zh["SymmetryLock"] = "锁定对称轴";
        _ja["SymmetryLock"] = "対称軸をロック";
        _en["SymmetryLock"] = "Lock symmetry axis";
        _zh["SymmetryReset"] = "重置对称轴（垂直 0.5）";
        _ja["SymmetryReset"] = "対称軸をリセット（縦0.5）";
        _en["SymmetryReset"] = "Reset axis (vertical 0.5)";
        _zh["SymmetryHelp"] = "在右侧画布拖动端点或线体可移动/旋转；锁定后禁止拖动。";
        _ja["SymmetryHelp"] = "右側キャンバスで端点/線をドラッグして移動/回転；ロック中はドラッグ不可。";
        _en["SymmetryHelp"] = "Drag endpoints/line on the canvas to move/rotate; locked = no drag.";

        // UV叠加
        _zh["ShowUVOverlay"] = "显示UV叠加";
        _ja["ShowUVOverlay"] = "UVオーバーレイを表示";
        _en["ShowUVOverlay"] = "Show UV Overlay";
        _zh["UVLineColor"] = "UV线颜色";
        _ja["UVLineColor"] = "UVライン色";
        _en["UVLineColor"] = "UV Line Color";
        _zh["UVOverlayStrength"] = "UV叠加强度";
        _ja["UVOverlayStrength"] = "UVオーバーレイ強度";
        _en["UVOverlayStrength"] = "UV Overlay Strength";
        _zh["UVDisplayScope"] = "UV显示范围";
        _ja["UVDisplayScope"] = "UV表示範囲";
        _en["UVDisplayScope"] = "UV Display Scope";
        _zh["UVScopeCurrentMaterial"] = "当前材质组";
        _ja["UVScopeCurrentMaterial"] = "現在のマテリアル";
        _en["UVScopeCurrentMaterial"] = "Current Material Group";
        _zh["UVScopeAllSubMeshes"] = "全部子网格（仅查看）";
        _ja["UVScopeAllSubMeshes"] = "全サブメッシュ（表示のみ）";
        _en["UVScopeAllSubMeshes"] = "All Sub Meshes (View Only)";
        _zh["UVScopeAllHint"] = "其他材质 UV 仅弱化显示，绘制仍限制在当前材质组。";
        _ja["UVScopeAllHint"] = "他のマテリアルのUVは薄く表示され、描画は現在のマテリアルに制限されます。";
        _en["UVScopeAllHint"] = "Other material UVs are dimmed for reference; painting remains limited to the current material group.";
        _zh["UVSharedTextureWarning"] = "多个材质共用同一主贴图；重叠 UV 区域可能互相影响，建议按材质拆分贴图。";
        _ja["UVSharedTextureWarning"] = "複数のマテリアルが同じメインテクスチャを共有しています。重なるUV領域が互いに影響する可能性があるため、マテリアル別のテクスチャ分割を推奨します。";
        _en["UVSharedTextureWarning"] = "Multiple materials share the same main texture. Overlapping UV areas may affect each other; splitting the texture by material is recommended.";

        // 保存/导出/定位
        _zh["GroupApplySaveLocate"] = "实时预览 / 保存 / 清空 / 定位";
        _ja["GroupApplySaveLocate"] = "リアルタイム表示 / 保存 / クリア / 移動";
        _en["GroupApplySaveLocate"] = "Realtime / Save / Clear / Locate";
        _zh["SaveOutputFull"] = "输出贴图保存（完整贴图导出）";
        _ja["SaveOutputFull"] = "テクスチャ出力（フル画像）";
        _en["SaveOutputFull"] = "Save Output (Full Texture)";
        _zh["ExportPaintLayer"] = "仅输出绘画层（透明底）";
        _ja["ExportPaintLayer"] = "描画レイヤーのみ出力（透明）";
        _en["ExportPaintLayer"] = "Export Paint Layer (Alpha)";
        // ★ 新增：白底导出选项
        _zh["ExportWithWhiteBG"] = "叠加白底";
        _ja["ExportWithWhiteBG"] = "白背景を合成";
        _en["ExportWithWhiteBG"] = "Overlay White BG";
        _zh["ClearCurrentEdits"] = "清空本次修改";
        _ja["ClearCurrentEdits"] = "今回の編集をクリア";
        _en["ClearCurrentEdits"] = "Clear Current Edits";
        _zh["LocateProjectResBtn"] = "定位到 Project 面板资源";
        _ja["LocateProjectResBtn"] = "Project パネルのリソースへ移動";
        _en["LocateProjectResBtn"] = "Locate Project Panel Resource";

        // ---- 右侧顶部按钮 ----
        _zh["ResetViewBtn"] = "重置视图";
        _ja["ResetViewBtn"] = "ビューをリセット";
        _en["ResetViewBtn"] = "Reset View";

        // ==============================================
        // 三、【强制必填】信息/提示类 Key
        // ==============================================
        // 基础信息
        _zh["InfoWelcome"] = "拖入模型后会将主色图复制到输出目录（PNG），在右侧进行绘制。保存按钮仅写文件，关闭窗口自动还原材质。";
        _ja["InfoWelcome"] = "モデルをドラッグするとメインテクスチャをPNGに複製し、右側で編集できます。保存はファイルのみ、ウィンドウを閉じるとマテリアルは復元されます。";
        _en["InfoWelcome"] = "Drag a model to copy its main texture to PNG, then paint on the right. Save only writes files; material is restored on close.";
        _zh["InfoNoRenderer"] = "未找到 Renderer（MeshRenderer/SkinnedMeshRenderer）。";
        _ja["InfoNoRenderer"] = "Renderer（MeshRenderer/SkinnedMeshRenderer）が見つかりません。";
        _en["InfoNoRenderer"] = "No Renderer (MeshRenderer/SkinnedMeshRenderer) found.";
        _zh["InfoNoMesh"] = "未找到有效 Mesh。";
        _ja["InfoNoMesh"] = "有効なメッシュが見つかりません。";
        _en["InfoNoMesh"] = "No valid Mesh found.";
        _zh["InfoNoMats"] = "未找到材质。已生成透明绘制副本。";
        _ja["InfoNoMats"] = "マテリアルがありません。透明のペイントコピーを生成しました。";
        _en["InfoNoMats"] = "No materials found. A transparent paint copy is created.";
        _zh["InfoSlotInvalid"] = "材质槽无效。";
        _ja["InfoSlotInvalid"] = "マテリアルスロットが無効です。";
        _en["InfoSlotInvalid"] = "Invalid material slot.";
        _zh["InfoSlotEmpty"] = "材质槽 {0} 为空。";
        _ja["InfoSlotEmpty"] = "マテリアルスロット {0} は空です。";
        _en["InfoSlotEmpty"] = "Material slot {0} is empty.";
        _zh["InfoBoundOk"] = "绑定成功：{0} 材质槽{1} 纹理{2}x{3}";
        _ja["InfoBoundOk"] = "バインド成功：{0} マテリアルスロット{1} テクスチャ{2}x{3}";
        _en["InfoBoundOk"] = "Bound successfully: {0} Slot{1} Texture{2}x{3}";
        _zh["InfoNoTex"] = "材质槽{0}无有效纹理";
        _ja["InfoNoTex"] = "マテリアルスロット{0}に有効なテクスチャがありません";
        _en["InfoNoTex"] = "No valid texture in slot{0}";
        _zh["InfoComputeMissing"] = "未找到计算着色器！无法绘画";
        _ja["InfoComputeMissing"] = "コンピュートシェーダーが見つかりません！描画できません";
        _en["InfoComputeMissing"] = "Compute Shader not found! Painting disabled";
        _zh["InfoNoIslandData"] = "未检测到UV孤岛数据";
        _ja["InfoNoIslandData"] = "UVアイランドデータが検出されません";
        _en["InfoNoIslandData"] = "No UV island data detected";

        // Undo/Redo
        _zh["UndoEmpty"] = "无撤销记录";
        _ja["UndoEmpty"] = "取り消し記録がありません";
        _en["UndoEmpty"] = "No undo history";
        _zh["UndoOk"] = "撤销成功";
        _ja["UndoOk"] = "取り消し成功";
        _en["UndoOk"] = "Undo successful";
        _zh["RedoEmpty"] = "无重做记录";
        _ja["RedoEmpty"] = "やり直し記録がありません";
        _en["RedoEmpty"] = "No redo history";
        _zh["RedoOk"] = "重做成功";
        _ja["RedoOk"] = "やり直し成功";
        _en["RedoOk"] = "Redo successful";

        // 状态提示
        _zh["RealtimeOn"] = "实时预览：已将绘制副本赋到材质（仅内存，不写文件）。";
        _ja["RealtimeOn"] = "リアルタイム表示：ペイントコピーをマテリアルへ（メモリのみ）。";
        _en["RealtimeOn"] = "Realtime preview: paint copy assigned to material (memory only).";
        _zh["RealtimeOff"] = "已关闭实时预览：材质还原为原始资产。";
        _ja["RealtimeOff"] = "リアルタイム表示OFF：マテリアルを元に戻しました。";
        _en["RealtimeOff"] = "Realtime preview turned off: material restored.";
        _zh["ClearEditsSuccess"] = "已清空改动";
        _ja["ClearEditsSuccess"] = "編集をクリアしました";
        _en["ClearEditsSuccess"] = "Cleared all edits";
        _zh["SymmetryEditingBlockPaint"] = "正在调整对称轴，已暂时禁用绘画。请先锁定或关闭对称模式。";
        _ja["SymmetryEditingBlockPaint"] = "対称軸を編集中です。ペイントは一時的に無効です。先に軸をロックするか対称モードを無効にしてください。";
        _en["SymmetryEditingBlockPaint"] = "Symmetry axis is being edited. Painting is temporarily disabled. Please lock the axis or disable symmetry first.";
        _zh["NoPaintTargetHint"] = "暂无绘制目标。请在左侧绑定对象并选择材质槽。";
        _ja["NoPaintTargetHint"] = "描画対象がありません。左側でオブジェクトをバインドし、マテリアルスロットを選択してください。";
        _en["NoPaintTargetHint"] = "No paint target. Please bind object on left and select material slot.";

        // ---- 兼容保留提示 ----
        _zh["InfoReadMainTexOk"] = "已读取主纹理：{0}。已在输出目录生成：{1}。可绘制并【输出贴图保存】。";
        _ja["InfoReadMainTexOk"] = "メインテクスチャ {0} を読み込みました。出力先：{1}。編集後【保存】できます。";
        _en["InfoReadMainTexOk"] = "Main texture {0} loaded. Output created at: {1}. You can paint and then [Save].";
        _zh["InfoNoMainTex"] = "不包含主纹理。已在输出目录生成空白 PNG：{0}。";
        _ja["InfoNoMainTex"] = "メインテクスチャがありません。空PNGを作成しました：{0}。";
        _en["InfoNoMainTex"] = "No main texture. Created a blank PNG at: {0}.";
        _zh["WarnFillOutsideUV"] = "点击位置不在网格 UV 覆盖区域内，无法填充。";
        _ja["WarnFillOutsideUV"] = "クリック位置はUVカバー領域外のため塗りつぶせません。";
        _en["WarnFillOutsideUV"] = "Click position is outside UV coverage; cannot fill.";
        _zh["SaveOk"] = "已保存当前绘制为完整贴图：{0}（未修改模型/材质，仅写文件）。";
        _ja["SaveOk"] = "現在の描画をフル画像として保存：{0}（モデル/マテリアル未変更）。";
        _en["SaveOk"] = "Saved full texture to: {0} (no model/material changes).";
        _zh["ExportLayerOk"] = "已导出【仅绘画层（透明底）】到：{0}（未覆盖原文件）。";
        _ja["ExportLayerOk"] = "【描画レイヤーのみ（透明）】を出力：{0}（既存ファイル未上書き）。";
        _en["ExportLayerOk"] = "Exported paint-only layer to: {0} (no overwrite).";
        _zh["ClearToSaved"] = "已清空当前未保存的绘制，回退到上一次保存的输出贴图。";
        _ja["ClearToSaved"] = "未保存の描画をクリアし、最後に保存した出力へ戻しました。";
        _en["ClearToSaved"] = "Cleared unsaved edits and reverted to last saved output.";
        _zh["ClearToOriginal"] = "未找到已保存输出，已回退到原始主纹理副本。";
        _ja["ClearToOriginal"] = "保存済み出力なしのため、元のメインテクスチャに戻しました。";
        _en["ClearToOriginal"] = "No saved output found; reverted to original texture.";
        _zh["ClearToBlank"] = "未找到可回退的贴图，已生成默认空白副本。";
        _ja["ClearToBlank"] = "戻すテクスチャがないため、空のコピーを作成しました。";
        _en["ClearToBlank"] = "No texture to revert; created a blank copy.";
        _zh["LocateCreateOk"] = "已创建目录：";
        _ja["LocateCreateOk"] = "ディレクトリを作成：";
        _en["LocateCreateOk"] = "Created directory: ";
        _zh["LocateExists"] = "目录已存在：";
        _ja["LocateExists"] = "ディレクトリは既に存在：";
        _en["LocateExists"] = "Directory exists: ";
        _zh["LocateOpenOk"] = "已打开目录：";
        _ja["LocateOpenOk"] = "ディレクトリを開きました：";
        _en["LocateOpenOk"] = "Opened directory: ";
        _zh["ConfirmClearEdits"] = "再次点击，确认清空";
        _ja["ConfirmClearEdits"] = "もう一度クリックで確定";
        _en["ConfirmClearEdits"] = "Click again to confirm";

        // ---- 作者窗口 ----
        _zh["AuthorWindowTitle"] = "LGC网格UV绘画工具 - 关于作者";
        _ja["AuthorWindowTitle"] = "LGCメッシュUVペイントツール - 作者について";
        _en["AuthorWindowTitle"] = "LGC Mesh UV Painter - About";
        _zh["AuthorName"] = "作者：Copilote + DeepSeek + LGC";
        _ja["AuthorName"] = "作者：Copilote + DeepSeek + LGC";
        _en["AuthorName"] = "Author: Copilote + DeepSeek + LGC";
        _zh["VersionInfo"] = "版本：{0}";
        _ja["VersionInfo"] = "バージョン：{0}";
        _en["VersionInfo"] = "Version: {0}";
        _zh["ContactInfo"] = "联系：GitHub LgcChina";
        _ja["ContactInfo"] = "連絡先：GitHub LgcChina";
        _en["ContactInfo"] = "Contact: GitHub LgcChina";

        // 工具详细说明（中/日/英多语言配置）
        _zh["ToolDescDetailed"] =
    "当前版本特色\n" +
    "- 支持基于 UV 的绘画、橡皮擦、UV 孤岛填充与孤岛橡皮擦。\n" +
    "- 提供 UV 边界限制与孤岛隔离模式，使绘制更精准安全。\n" +
    "- 所有操作支持 Undo/Redo，实时预览不修改原材质。\n" +
    "- 输出目录统一管理，可快速定位导出贴图。\n\n" +
    "v2.7.0 新增及改进\n" +
    "- 【材质 UV 范围】默认只显示并绘制当前材质组；同一材质映射多个子网格时自动合并，不再混入身体、眼睛等其他材质 UV。\n" +
    "- 【查看模式】新增“全部子网格（仅查看）”，其他材质 UV 弱化显示，绘制、边界遮罩和孤岛识别仍限定当前材质组。\n" +
    "- 【风险提示】多个材质共用同一主贴图时提示重叠 UV 风险，建议按材质拆分贴图。\n\n" +
    "v2.5.1 历史改进\n" +
    "- 【架构升级】三RT架构（baseRT + paintLayerRT + previewRT），绘制层独立存储，彻底解决颜色泄漏与透明导出偏色问题。\n" +
    "- 【导出增强】透明层直接导出绘制层，白底导出通过 GPU 合成，颜色纯净，支持叠加白底选项。\n" +
    "- 【交互优化】边界扩展使用延迟输入（回车后应用），避免数值调整时频繁重建；对称线增加裁剪，防止绘制到工具栏区域。\n" +
    "- 【性能优化】增加预览脏标记，仅在绘制/撤销/视图变化时合成，减少无效 GPU 调用；内存释放统一管理，长时间使用更稳定。\n" +
    "- 【接口清理】LgcBrushExecutor 移除 baseRT 依赖，职责更清晰。\n\n" +
    "当前版本基于 GPU（RenderTexture + Compute Shader）绘制与实时预览，显著提高绘画流畅度。";
        _ja["ToolDescDetailed"] =
    "現在のバージョンの機能\n" +
    "- UVベースのペイント、消しゴム、UVアイランドの塗りつぶし、アイランド消しゴムをサポートします。\n" +
    "- UV境界拘束とアイランド分離モードにより、より正確で安全なペイントが可能になります。\n" +
    "- すべての操作で元に戻す/やり直しが可能になり、元のマテリアルを変更することなくリアルタイムプレビューが可能です。\n" +
    "- 出力ディレクトリ管理を統合し、エクスポートしたテクスチャを素早く見つけられるようになりました。\n\n" +
    "v2.7.0 新機能と改善\n" +
    "- 【マテリアルUV範囲】既定では現在のマテリアルグループのみを表示・描画します。同じマテリアルが複数のサブメッシュにある場合は自動統合され、ボディや目のUVは混在しません。\n" +
    "- 【表示モード】「全サブメッシュ（表示のみ）」を追加しました。他のマテリアルUVは薄く表示され、描画・境界マスク・アイランド認識は現在のマテリアルに限定されます。\n" +
    "- 【リスク表示】複数マテリアルが同じメインテクスチャを共有する場合にUV重複リスクを表示し、マテリアル別のテクスチャ分割を推奨します。\n\n" +
    "v2.5.1 過去の改善\n" +
    "- 【アーキテクチャアップグレード】三RTアーキテクチャ（baseRT + paintLayerRT + previewRT）により、描画レイヤーを独立して保存し、色漏れや透明出力の色ずれを完全に解消しました。\n" +
    "- 【出力強化】透明レイヤーは描画レイヤーを直接出力、白背景出力はGPU合成により色が純粋になり、白背景オプションをサポートします。\n" +
    "- 【インタラクション改善】境界拡張は遅延入力（Enterで適用）を使用し、数値調整時の頻繁な再構築を回避；対称線にクリッピングを追加し、ツールバー領域への描画を防止します。\n" +
    "- 【パフォーマンス最適化】プレビューダーティフラグを追加し、描画/アンドゥ/ビュー変更時のみ合成することで、無効なGPUコールを削減；メモリ解放を一元管理し、長時間の使用でも安定性が向上します。\n" +
    "- 【インターフェース整理】LgcBrushExecutor から baseRT 依存を削除し、責務を明確化しました。\n\n" +
    "現在のバージョンは GPU（RenderTexture + Compute Shader）ペイントとリアルタイムプレビューに対応し、描画の滑らかさが大幅に向上しました。";
        _en["ToolDescDetailed"] =
    "Current Version Features\n" +
    "- Supports UV-based painting, eraser, UV island filling, and island eraser.\n" +
    "- Provides UV boundary constraints and island isolation modes for more precise and safer painting.\n" +
    "- All operations support Undo/Redo, with real-time preview without modifying original materials.\n" +
    "- Unified output directory management for quick location of exported textures.\n\n" +
    "v2.7.0 New & Improved\n" +
    "- 【Material UV Scope】Only the current material group is shown and painted by default. Sub-meshes using the same material are merged automatically, preventing body and eye UVs from mixing.\n" +
    "- 【View Mode】Added All Sub Meshes (View Only). Other material UVs are dimmed for reference; painting, boundary masks, and island detection stay limited to the current material group.\n" +
    "- 【Risk Warning】Warns when multiple materials share the same main texture and recommends splitting textures by material.\n\n" +
    "v2.5.1 Historical Improvements\n" +
    "- 【Architecture Upgrade】Three-RT architecture (baseRT + paintLayerRT + previewRT): paint layer stored independently,彻底 eliminating color leakage and transparent export color shifts.\n" +
    "- 【Export Enhancement】Transparent layer exports directly from paint layer; white-background export via GPU composition, color-pure, with optional white BG overlay.\n" +
    "- 【Interaction Optimization】Boundary expand uses delayed input (apply on Enter) to avoid frequent rebuilds; symmetry line clipping prevents drawing outside preview area.\n" +
    "- 【Performance Optimization】Preview dirty flag composites only on paint/undo/view changes, reducing unnecessary GPU calls; unified memory release for long-term stability.\n" +
    "- 【Interface Cleanup】LgcBrushExecutor removes baseRT dependency for clearer separation of concerns.\n\n" +
    "Current version runs on GPU (RenderTexture + Compute Shader) painting and real-time preview, significantly improving smoothness.";
        _zh["EditCleared"] = "当前绘画层已清空，可使用撤回恢复。";
        _ja["EditCleared"] = "描画レイヤーを消去しました。元に戻すことができます。";
        _en["EditCleared"] = "Paint layer cleared. Use Undo to restore it.";

        _zh["ToolDescDetailed"] = "v2.8.0 更新\n预览材质由工具管理，支持撤回/重做后恢复；新增画布历史按钮、锁轴引导、屏幕固定笔刷及分组布局。\n\n" + _zh["ToolDescDetailed"];
        _ja["ToolDescDetailed"] = "v2.8.0 更新\nプレビューマテリアルをツールで管理します。元に戻す・やり直し、対称軸ガイド、画面固定ブラシ、分類したUIを追加しました。\n\n" + _ja["ToolDescDetailed"];
        _en["ToolDescDetailed"] = "v2.8.0 Updates\nManaged preview materials, canvas Undo/Redo, symmetry axis guidance, screen-fixed brushes and grouped controls.\n\n" + _en["ToolDescDetailed"];
        _zh["SymmetryReset"] = "重置轴";
        _ja["SymmetryReset"] = "軸をリセット";
        _en["SymmetryReset"] = "Reset Axis";

        // v2.8.1 - Tabbed tools and fixed output, all guidance stays in the tools panel.
        _zh["DrawTab"] = "绘制";
        _ja["DrawTab"] = "描画";
        _en["DrawTab"] = "Draw";
        _zh["StampTab"] = "盖印";
        _ja["StampTab"] = "スタンプ";
        _en["StampTab"] = "Stamp";
        _zh["UVBoundaryTab"] = "UV边界";
        _ja["UVBoundaryTab"] = "UV境界";
        _en["UVBoundaryTab"] = "UV Boundary";
        _zh["PaintTabTitle"] = "绘画工具";
        _ja["PaintTabTitle"] = "描画ツール";
        _en["PaintTabTitle"] = "Paint Tools";
        _zh["OutputTitle"] = "输出";
        _ja["OutputTitle"] = "出力";
        _en["OutputTitle"] = "Output";
        _zh["ToolDescDetailed"] = "v2.8.1：绘制、盖印、UV边界三 Tab；输出固定底部；实时预览移到绘制目标；提示不占画布。\n\n" + _zh["ToolDescDetailed"];
        _ja["ToolDescDetailed"] = "v2.8.1：描画・スタンプ・UV境界のタブと固定出力。リアルタイム表示を対象設定に移動し、案内をキャンバス外に配置。\n\n" + _ja["ToolDescDetailed"];
        _en["ToolDescDetailed"] = "v2.8.1: Draw, Stamp and UV Boundary tabs, pinned output, preview in target settings, guidance outside the canvas.\n\n" + _en["ToolDescDetailed"];

        // v2.8.2 - Fix tab scroll area height constraints.
        _zh["ToolDescDetailed"] = "v2.8.2：修复 Tab 内容异常裁剪，滚动区域填满至固定输出区上方。\n\n" + _zh["ToolDescDetailed"];
        _ja["ToolDescDetailed"] = "v2.8.2：タブ領域の高さ制限を修正し、固定出力欄まで表示領域を拡張。\n\n" + _ja["ToolDescDetailed"];
        _en["ToolDescDetailed"] = "v2.8.2: Fix clipped tabs; the scrolling area fills the space above pinned output.\n\n" + _en["ToolDescDetailed"];

        _zh["ToolDescDetailed"] = "v2.9.0：标题分区始终展开；盖印支持实时预览与颜色叠加；历史操作固定同排，输出完整贴图位于底部，画布底部居中重置、右下角显示倍率。\n\n" + _zh["ToolDescDetailed"];
        _ja["ToolDescDetailed"] = "v2.9.0：設定を常時展開。スタンプのライブプレビューと重ね色、固定した履歴操作、下部の全体出力、キャンバス下部のリセットと右下の倍率。\n\n" + _ja["ToolDescDetailed"];
        _en["ToolDescDetailed"] = "v2.9.0: Expanded sections, live stamp preview and overlay color, pinned history controls, full texture output last, bottom-center reset and bottom-right zoom.\n\n" + _en["ToolDescDetailed"];

        // v2.9.1: Restore rendering state and release stamp pointer capture safely.
        _zh["ToolDescDetailed"] = "v2.9.1：修复盖印拖动时渲染目标泄漏引发的界面压缩，增加鼠标捕获及失焦、Escape、移出画布松开处理。\n\n" + _zh["ToolDescDetailed"];
        _ja["ToolDescDetailed"] = "v2.9.1：スタンプ移動時の描画先漏れによるUI圧縮を修正。マウス捕捉、フォーカス喪失、Escape、領域外での解放に対応。\n\n" + _ja["ToolDescDetailed"];
        _en["ToolDescDetailed"] = "v2.9.1: Fix compressed UI caused by leaked stamp render targets; capture drags and release on focus loss, Escape or mouse-up outside the canvas.\n\n" + _en["ToolDescDetailed"];

        // v2.8.0 - Grouped UI, guided symmetry and screen-space brush sizes.
        _zh["RefreshShort"] = "刷新";
        _ja["RefreshShort"] = "再読込";
        _en["RefreshShort"] = "Refresh";

        _zh["AdvancedSettings"] = "高级设置";
        _ja["AdvancedSettings"] = "詳細設定";
        _en["AdvancedSettings"] = "Advanced Settings";

        _zh["BrushSizeMode"] = "尺寸模式";
        _ja["BrushSizeMode"] = "サイズ基準";
        _en["BrushSizeMode"] = "Size Mode";

        _zh["BrushScreenFixed"] = "屏幕固定（推荐）";
        _ja["BrushScreenFixed"] = "画面固定（推奨）";
        _en["BrushScreenFixed"] = "Screen Fixed";

        _zh["BrushTextureFixed"] = "贴图固定";
        _ja["BrushTextureFixed"] = "テクスチャ固定";
        _en["BrushTextureFixed"] = "Texture Fixed";

        _zh["BrushScreenRadius"] = "屏幕半径（界面像素）";
        _ja["BrushScreenRadius"] = "画面半径（UI px）";
        _en["BrushScreenRadius"] = "Screen Radius (UI px)";

        _zh["BrushScreenHint"] = "缩放画布时光标大小保持不变，放大后可直接绘制细节。";
        _ja["BrushScreenHint"] = "ズームしてもカーソルの大きさは一定です。拡大すると細部を描けます。";
        _en["BrushScreenHint"] = "The cursor stays the same size while zooming. Zoom in to paint finer details.";

        _zh["BrushTextureHint"] = "按贴图像素设置半径，光标随画布缩放。";
        _ja["BrushTextureHint"] = "テクスチャのピクセル単位で半径を指定します。カーソルはズームに追従します。";
        _en["BrushTextureHint"] = "Radius is measured in texture pixels. The cursor scales with the canvas.";

        _zh["ModeBrushShort"] = "画笔";
        _ja["ModeBrushShort"] = "ブラシ";
        _en["ModeBrushShort"] = "Brush";

        _zh["ModeEraseShort"] = "橡皮擦";
        _ja["ModeEraseShort"] = "消しゴム";
        _en["ModeEraseShort"] = "Eraser";

        _zh["ModeFillShort"] = "孤岛填充";
        _ja["ModeFillShort"] = "島の塗りつぶし";
        _en["ModeFillShort"] = "Fill Island";

        _zh["ModeIslandEraseShort"] = "孤岛擦除";
        _ja["ModeIslandEraseShort"] = "島を消去";
        _en["ModeIslandEraseShort"] = "Erase Island";

        _zh["ModeStampShort"] = "盖印";
        _ja["ModeStampShort"] = "スタンプ";
        _en["ModeStampShort"] = "Stamp";

        _zh["UndoButton"] = "撤回";
        _ja["UndoButton"] = "元に戻す";
        _en["UndoButton"] = "Undo";

        _zh["RedoButton"] = "重做";
        _ja["RedoButton"] = "やり直し";
        _en["RedoButton"] = "Redo";

        _zh["UndoTooltip"] = "撤回上一笔绘画（Ctrl/Cmd+Z）";
        _ja["UndoTooltip"] = "直前の描画を戻す（Ctrl/Cmd+Z）";
        _en["UndoTooltip"] = "Undo the last paint action (Ctrl/Cmd+Z)";

        _zh["RedoTooltip"] = "重做绘画（Ctrl/Cmd+Y 或 Ctrl/Cmd+Shift+Z）";
        _ja["RedoTooltip"] = "描画をやり直す（Ctrl/Cmd+Y / Ctrl/Cmd+Shift+Z）";
        _en["RedoTooltip"] = "Redo painting (Ctrl/Cmd+Y or Ctrl/Cmd+Shift+Z)";

        _zh["SymmetrySetupHint"] = "正在调整对称轴：拖动端点或轴线，完成后点击「锁定并开始绘画」。";
        _ja["SymmetrySetupHint"] = "対称軸を調整中：端点か軸をドラッグし、完了後に「軸を固定して描画」を押してください。";
        _en["SymmetrySetupHint"] = "Adjust the symmetry axis by dragging its endpoints or line, then click Lock and Paint.";

        _zh["SymmetryLockedHint"] = "对称轴已锁定，可以开始绘画。需要修改位置时点击「调整轴」。";
        _ja["SymmetryLockedHint"] = "対称軸は固定済みです。描画できます。位置を変えるには「軸を調整」を押してください。";
        _en["SymmetryLockedHint"] = "Symmetry axis locked. Ready to paint. Click Adjust Axis to reposition it.";

        _zh["SymmetryLockAndPaint"] = "锁定并开始绘画";
        _ja["SymmetryLockAndPaint"] = "軸を固定して描画";
        _en["SymmetryLockAndPaint"] = "Lock and Paint";

        _zh["SymmetryAdjust"] = "调整轴";
        _ja["SymmetryAdjust"] = "軸を調整";
        _en["SymmetryAdjust"] = "Adjust Axis";

        _zh["SymmetryDisabled"] = "对称绘画已关闭。";
        _ja["SymmetryDisabled"] = "対称描画を無効にしました。";
        _en["SymmetryDisabled"] = "Symmetry painting disabled.";

        _zh["StampSettings"] = "盖印参数";
        _ja["StampSettings"] = "スタンプ設定";
        _en["StampSettings"] = "Stamp Settings";

        _zh["StampTexture"] = "图案贴图";
        _ja["StampTexture"] = "スタンプ画像";
        _en["StampTexture"] = "Stamp Texture";

        _zh["StampShowPreview"] = "显示预览";
        _ja["StampShowPreview"] = "プレビュー表示";
        _en["StampShowPreview"] = "Show Preview";

        _zh["StampSymmetry"] = "对称盖印";
        _ja["StampSymmetry"] = "対称スタンプ";
        _en["StampSymmetry"] = "Symmetric Stamp";

        _zh["StampMirror"] = "图案镜像";
        _ja["StampMirror"] = "画像を反転";
        _en["StampMirror"] = "Mirror Pattern";

        _zh["StampApply"] = "应用盖印";
        _ja["StampApply"] = "スタンプを適用";
        _en["StampApply"] = "Apply Stamp";

        _zh["StampApplied"] = "盖印已应用，可使用撤回按钮或 Ctrl+Z 撤回。";
        _ja["StampApplied"] = "スタンプを適用しました。「元に戻す」または Ctrl+Z で戻せます。";
        _en["StampApplied"] = "Stamp applied. Use Undo or Ctrl+Z to revert.";

        _zh["StampInteractionHint"] = "四角按钮：左上旋转、左下移动、右下缩放、右上移除待应用图案；也可拖动图案中心移动。完成后点击「应用盖印」。对称轴调整期间暂停图案操作。";
        _ja["StampInteractionHint"] = "角ボタン：左上は回転、左下は移動、右下は拡縮、右上は未適用スタンプ削除。中央ドラッグでも移動。完了後に適用。対称軸調整中は操作停止。";
        _en["StampInteractionHint"] = "Corner buttons: top-left rotate, bottom-left move, bottom-right scale, top-right remove pending stamp. Drag the center to move, then Apply. Editing pauses while adjusting the symmetry axis.";

        _zh["TargetSelectionFoldout"] = "绘制目标";
        _ja["TargetSelectionFoldout"] = "描画対象";
        _en["TargetSelectionFoldout"] = "Paint Target";

        _zh["ModeSectionFoldout"] = "绘画工具";
        _ja["ModeSectionFoldout"] = "描画ツール";
        _en["ModeSectionFoldout"] = "Paint Tools";

        _zh["BrushSectionFoldout"] = "当前工具参数";
        _ja["BrushSectionFoldout"] = "ツール設定";
        _en["BrushSectionFoldout"] = "Tool Settings";

        _zh["BrushRadius"] = "贴图半径（像素）";
        _ja["BrushRadius"] = "テクスチャ半径（px）";
        _en["BrushRadius"] = "Texture Radius (px)";

        // v2.10.0: Unified symmetry, input choices, export resolution and scrollable history.
        _zh["UVSharedTextureWarning"] = "疑似多个材质共用此贴图，注意检查实际效果";
        _ja["UVSharedTextureWarning"] = "複数のマテリアルがこのテクスチャを共有している可能性があります。実際の表示を確認してください。";
        _en["UVSharedTextureWarning"] = "This texture may be shared by multiple materials. Check the actual result.";
        _zh["BrushSizeMode"] = "画笔尺寸模式";
        _ja["BrushSizeMode"] = "ブラシサイズモード";
        _en["BrushSizeMode"] = "Brush Size Mode";
        _zh["BrushScreenFixed"] = "固定大小";
        _ja["BrushScreenFixed"] = "固定サイズ";
        _en["BrushScreenFixed"] = "Fixed Size";
        _zh["BrushTextureFixed"] = "同步缩放";
        _ja["BrushTextureFixed"] = "ズームに追従";
        _en["BrushTextureFixed"] = "Scale with Zoom";
        _zh["StampMirror"] = "对称图案镜像";
        _ja["StampMirror"] = "対称画像を反転";
        _en["StampMirror"] = "Mirror Symmetric Pattern";
        _zh["RightMouseFunction"] = "右键功能";
        _ja["RightMouseFunction"] = "右ボタン";
        _en["RightMouseFunction"] = "Right Button";
        _zh["RightMouseErase"] = "橡皮擦";
        _ja["RightMouseErase"] = "消しゴム";
        _en["RightMouseErase"] = "Eraser";
        _zh["RightMousePan"] = "拖动画布";
        _ja["RightMousePan"] = "キャンバス移動";
        _en["RightMousePan"] = "Pan Canvas";
        _zh["RightMouseNone"] = "无操作";
        _ja["RightMouseNone"] = "操作なし";
        _en["RightMouseNone"] = "No Action";
        _zh["CanvasPaintHint"] = "左键绘制 / 中键拖动画布 / 滚轮缩放";
        _ja["CanvasPaintHint"] = "左：描画 / 中：移動 / ホイール：ズーム";
        _en["CanvasPaintHint"] = "Left: paint / Middle: pan / Wheel: zoom";
        _zh["CanvasStampHint"] = "左键调整盖印 / 中键拖动画布 / 滚轮缩放";
        _ja["CanvasStampHint"] = "左：スタンプ調整 / 中：移動 / ホイール：ズーム";
        _en["CanvasStampHint"] = "Left: adjust stamp / Middle: pan / Wheel: zoom";
        _zh["MirrorSource"] = "镜像来源侧";
        _ja["MirrorSource"] = "コピー元";
        _en["MirrorSource"] = "Mirror From";
        _zh["MirrorSideA"] = "轴左侧 → 右侧";
        _ja["MirrorSideA"] = "軸の左 → 右";
        _en["MirrorSideA"] = "Axis Left → Right";
        _zh["MirrorSideB"] = "轴右侧 → 左侧";
        _ja["MirrorSideB"] = "軸の右 → 左";
        _en["MirrorSideB"] = "Axis Right → Left";
        _zh["MirrorContent"] = "镜像当前内容";
        _ja["MirrorContent"] = "現在の描画を反転コピー";
        _en["MirrorContent"] = "Mirror Current Content";
        _zh["MirrorContentHint"] = "先锁定对称轴；按轴起点→终点区分左右（默认下→上）。将来源侧绘制叠加到另一侧，可撤回；不复制原贴图。";
        _ja["MirrorContentHint"] = "軸を固定して実行。左右は軸の始点→終点基準（既定は下→上）。描画レイヤーのみ反対側へ重ね、元に戻せます。";
        _en["MirrorContentHint"] = "Lock the axis first. Left/right follows its start → end direction (bottom → top by default). Overlay paint onto the other side; undo is supported.";
        _zh["MirrorContentDone"] = "当前绘制内容已镜像，可撤回。";
        _ja["MirrorContentDone"] = "描画を反転コピーしました。元に戻せます。";
        _en["MirrorContentDone"] = "Current paint mirrored. Undo is available.";
        _zh["ExportOriginal"] = "原图分辨率";
        _ja["ExportOriginal"] = "元の解像度";
        _en["ExportOriginal"] = "Original";
        _zh["ExportResolutionHint"] = "完整贴图与绘制层共用；数值为长边像素，保持宽高比。高于原图时放大已有像素，不生成新细节。";
        _ja["ExportResolutionHint"] = "全体画像と描画レイヤーに適用。長辺のpx数、比率を維持。元の解像度を超える場合は既存ピクセルを拡大します。";
        _en["ExportResolutionHint"] = "Applies to full texture and paint layer. Long-edge pixels; aspect ratio preserved. Larger exports resample existing pixels without generating detail.";
        _zh["GitHubRepository"] = "GitHub · MeshUVPainter 工具仓库";
        _ja["GitHubRepository"] = "GitHub · MeshUVPainter";
        _en["GitHubRepository"] = "GitHub · MeshUVPainter Repository";
        _zh["GitHubCollection"] = "GitHub · LGC-VRC-Packages 合集";
        _ja["GitHubCollection"] = "GitHub · LGC-VRC-Packages";
        _en["GitHubCollection"] = "GitHub · LGC-VRC-Packages Collection";
        _zh["ToolDescDetailed"] = "v2.10.0：修复日志滚动与 GitHub 链接；统一盖印对称并默认镜像；新增已有内容镜像、导出分辨率与右键操作；重置按钮移出画布，右下显示原贴图尺寸和倍率。\n\n" + _zh["ToolDescDetailed"];
        _ja["ToolDescDetailed"] = "v2.10.0：履歴スクロールとGitHubリンクを修正。対称スタンプを統合、描画の反転コピー・出力解像度・右ボタン設定を追加。リセットをキャンバス外へ移動。\n\n" + _ja["ToolDescDetailed"];
        _en["ToolDescDetailed"] = "v2.10.0: Fix history scrolling and GitHub links. Unified stamp symmetry, layer mirroring, export resolution, right-button actions, separate reset controls and texture dimensions.\n\n" + _en["ToolDescDetailed"];

        // v2.11.0: Native work resolution, gradients and UV region management.
        _zh["ModeGradient"] = "渐变";
        _ja["ModeGradient"] = "グラデーション";
        _en["ModeGradient"] = "Gradient";
        _zh["PrecisionTitle"] = "绘制清晰度";
        _ja["PrecisionTitle"] = "描画精度";
        _en["PrecisionTitle"] = "Painting Precision";
        _zh["PrecisionLevel"] = "工作分辨率";
        _ja["PrecisionLevel"] = "作業解像度";
        _en["PrecisionLevel"] = "Work Resolution";
        _zh["PrecisionApply"] = "应用绘制精度";
        _ja["PrecisionApply"] = "解像度を適用";
        _en["PrecisionApply"] = "Apply Work Resolution";
        _zh["PrecisionHint"] = "提高实际绘制层分辨率，使之后的新笔触更清晰。滑条分档：原图 / 1024 / 2048 / 4096（长边）。已有像素保留并缩放，历史数量按显存预算调整。";
        _ja["PrecisionHint"] = "以降の描画を高解像度で作成。元サイズ / 1024 / 2048 / 4096（長辺）。既存描画は拡大縮小、履歴数はメモリ制限に合わせます。";
        _en["PrecisionHint"] = "New strokes use the actual work resolution: Original / 1024 / 2048 / 4096 (long edge). Existing paint is resampled; history is limited by memory.";
        _zh["PrecisionChanged"] = "绘制精度已更新；之后的新笔触使用新分辨率，导出时请选择对应尺寸。";
        _ja["PrecisionChanged"] = "描画解像度を更新しました。出力解像度も選択してください。";
        _en["PrecisionChanged"] = "Work resolution updated. Select the matching export resolution.";
        _zh["RegionTitle"] = "UV 选择与保护";
        _ja["RegionTitle"] = "UV 選択と保護";
        _en["RegionTitle"] = "UV Selection & Protection";
        _zh["RegionIdle"] = "绘制";
        _ja["RegionIdle"] = "描画";
        _en["RegionIdle"] = "Paint";
        _zh["RegionIsland"] = "孤岛选择";
        _ja["RegionIsland"] = "アイランド";
        _en["RegionIsland"] = "Islands";
        _zh["RegionBox"] = "框选区域";
        _ja["RegionBox"] = "矩形選択";
        _en["RegionBox"] = "Rectangle";
        _zh["RegionOnlyEdit"] = "只编辑所选区域";
        _ja["RegionOnlyEdit"] = "選択範囲のみ編集";
        _en["RegionOnlyEdit"] = "Edit Selected Only";
        _zh["RegionHide"] = "隐藏所选";
        _ja["RegionHide"] = "選択を非表示";
        _en["RegionHide"] = "Hide Selected";
        _zh["RegionShow"] = "显示所选";
        _ja["RegionShow"] = "選択を表示";
        _en["RegionShow"] = "Show Selected";
        _zh["RegionOnlyShow"] = "只显示所选";
        _ja["RegionOnlyShow"] = "選択のみ表示";
        _en["RegionOnlyShow"] = "Show Selected Only";
        _zh["RegionDisable"] = "禁用所选";
        _ja["RegionDisable"] = "選択を保護";
        _en["RegionDisable"] = "Disable Selected";
        _zh["RegionEnable"] = "恢复所选编辑";
        _ja["RegionEnable"] = "保護を解除";
        _en["RegionEnable"] = "Enable Selected";
        _zh["RegionClearSelection"] = "取消选择";
        _ja["RegionClearSelection"] = "選択解除";
        _en["RegionClearSelection"] = "Deselect";
        _zh["RegionShowAll"] = "显示全部";
        _ja["RegionShowAll"] = "すべて表示";
        _en["RegionShowAll"] = "Show All";
        _zh["RegionEnableAll"] = "恢复全部编辑";
        _ja["RegionEnableAll"] = "すべて保護解除";
        _en["RegionEnableAll"] = "Enable All";
        _zh["GradientTitle"] = "线性渐变挑染";
        _ja["GradientTitle"] = "線形グラデーション";
        _en["GradientTitle"] = "Linear Gradient";
        _zh["GradientStart"] = "起点颜色";
        _ja["GradientStart"] = "開始色";
        _en["GradientStart"] = "Start Color";
        _zh["GradientEnd"] = "终点颜色";
        _ja["GradientEnd"] = "終了色";
        _en["GradientEnd"] = "End Color";
        _zh["GradientSwap"] = "交换颜色";
        _ja["GradientSwap"] = "色を交換";
        _en["GradientSwap"] = "Swap Colors";
        _zh["GradientTransparent"] = "终点设为透明";
        _ja["GradientTransparent"] = "終点を透明に";
        _en["GradientTransparent"] = "Transparent End";
        _zh["GradientHint"] = "在画布左键拖出渐变方向，拖动端点调整；颜色支持透明度。选区存在时仅作用于选区，否则使用起点所在 UV 孤岛（启用孤岛隔离时）。点击应用前不写入绘制层。";
        _ja["GradientHint"] = "左ドラッグで方向を設定、端点をドラッグして調整。選択範囲優先、選択なしでは開始点のアイランドに制限（分離有効時）。適用前はプレビューのみ。";
        _en["GradientHint"] = "Drag on the canvas to define direction; drag endpoints to adjust. Uses the current selection, or the start-point UV island when isolation is enabled. Preview remains uncommitted until Apply.";
        _zh["GradientApply"] = "应用渐变";
        _ja["GradientApply"] = "グラデーションを適用";
        _en["GradientApply"] = "Apply Gradient";
        _zh["GradientCancel"] = "取消本次渐变预览";
        _ja["GradientCancel"] = "プレビューを取消";
        _en["GradientCancel"] = "Cancel Gradient Preview";
        _zh["ExportResolutionHint"] = "完整贴图与绘制层共用；数值为长边像素，保持比例。先提高绘制清晰度再画细节；高于工作分辨率的导出仅放大像素。";
        _ja["ExportResolutionHint"] = "全体と描画レイヤー共通の長辺px数。先に作業解像度を上げて描画してください。それを超える出力は拡大のみです。";
        _en["ExportResolutionHint"] = "Shared by full texture and paint layer; long-edge pixels with aspect preserved. Raise work resolution before painting detail. Exports beyond it only resample existing pixels.";
        _zh["ToolDescDetailed"] = "v2.11.0：新增实际绘制清晰度、线性渐变挑染、UV 孤岛悬停选择和框选，隐藏/禁用区域统一限制后续绘制。\n\n" + _zh["ToolDescDetailed"];
        _ja["ToolDescDetailed"] = "v2.11.0：描画解像度、線形グラデーション、UV 選択・矩形選択・非表示・保護を追加。\n\n" + _ja["ToolDescDetailed"];
        _en["ToolDescDetailed"] = "v2.11.0: Native painting resolution, linear gradients, island hover and rectangle selection, visibility and shared edit protection.\n\n" + _en["ToolDescDetailed"];

    }

    // 获取文本；未命中返回 key 本身作为兜底
    public string GetText(string key)
    {
        InitLanguageData();
        Dictionary<string, string> dict =
            CurrentLang == EditorLanguage.Japanese ? _ja :
            CurrentLang == EditorLanguage.English ? _en : _zh;
        return dict.TryGetValue(key, out var val) ? val : key;
    }
}
#endif
