#if UNITY_EDITOR
// EditorLanguageManager.cs
// 单例 + 多语言文本管理（Chinese / Japanese / English）
// 用途：被编辑器工具与“关于作者”窗口统一调用
// 本版新增：ModeIslandErase / ToggleUVBoundaryLimit / ToggleIslandIsolation / 对称绘画相关键

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

        // ---- 通用 / 工具名 / 底部提示 ----
        _zh["ToolDisplayName"] = "LGC网格UV绘画工具";
        _ja["ToolDisplayName"] = "LGCメッシュUVペイントツール";
        _en["ToolDisplayName"] = "LGC Mesh UV Painter";

        _zh["ClickToSeeMore"] = "点击查看更多详细信息";
        _ja["ClickToSeeMore"] = "詳しい情報を見るにはクリック";
        _en["ClickToSeeMore"] = "Click to see more details";

        _zh["BottomAuthorInfo"] = "作者: copilote + LGC";
        _ja["BottomAuthorInfo"] = "作者: copilote + LGC";
        _en["BottomAuthorInfo"] = "Author: copilote + LGC";

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

        // ---- 目标对象 / 材质槽（为向后兼容保留以下键，即使你已移除材质信息区域）----
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

        // （以下“材质信息”相关键为兼容保留，UI 可不使用）
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

        // ---- 模式 ----
        _zh["ModeTitle"] = "模式";
        _ja["ModeTitle"] = "モード";
        _en["ModeTitle"] = "Modes";

        _zh["ModeBrush"] = " 画笔模式";
        _ja["ModeBrush"] = " ブラシモード";
        _en["ModeBrush"] = " Brush Mode";

        _zh["ModeErase"] = " 橡皮擦模式";
        _ja["ModeErase"] = " 消しゴムモード";
        _en["ModeErase"] = " Eraser Mode";

        _zh["ModeFill"] = " 孤岛填充模式（UV）";
        _ja["ModeFill"] = " アイランド塗りつぶし（UV）";
        _en["ModeFill"] = " UV Island Fill";

        // ★ 新增：孤岛橡皮擦模式
        _zh["ModeIslandErase"] = " 孤岛橡皮擦（UV）";
        _ja["ModeIslandErase"] = " アイランド消しゴム（UV）";
        _en["ModeIslandErase"] = " UV Island Eraser";

        // ---- 预览叠加 ----
        _zh["PreviewOverlayTitle"] = "预览与叠加";
        _ja["PreviewOverlayTitle"] = "プレビューとオーバーレイ";
        _en["PreviewOverlayTitle"] = "Preview & Overlay";

        _zh["ShowUVOverlay"] = "显示UV叠加";
        _ja["ShowUVOverlay"] = "UVオーバーレイを表示";
        _en["ShowUVOverlay"] = "Show UV Overlay";

        _zh["UVLineColor"] = "UV线颜色";
        _ja["UVLineColor"] = "UVライン色";
        _en["UVLineColor"] = "UV Line Color";

        _zh["UVOverlayStrength"] = "UV叠加强度";
        _ja["UVOverlayStrength"] = "UVオーバーレイ強度";
        _en["UVOverlayStrength"] = "UV Overlay Strength";

        // ---- 画笔与填充 ----
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

        // ---- 新增：边界与隔离开关 ----
        _zh["BoundaryAndIsolationTitle"] = "边界与隔离";
        _ja["BoundaryAndIsolationTitle"] = "境界とアイソレーション";
        _en["BoundaryAndIsolationTitle"] = "Boundary & Isolation";

        _zh["ToggleUVBoundaryLimit"] = "UV 边界限制（仅在 UV 岛内绘制）";
        _ja["ToggleUVBoundaryLimit"] = "UV境界制限（UVアイランド内のみ描画）";
        _en["ToggleUVBoundaryLimit"] = "UV Boundary Limit (restrict to island)";

        _zh["ToggleIslandIsolation"] = "孤岛隔离模式（仅作用当前岛的画笔/橡皮擦）";
        _ja["ToggleIslandIsolation"] = "アイソレーション（現在のアイランドのブラシ/消しのみ）";
        _en["ToggleIslandIsolation"] = "Island Isolation (affect current island only)";

        // ---- 新增：对称绘画 ----
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

        // ---- 右侧顶部按钮 ----
        _zh["ResetViewBtn"] = "重置视图";
        _ja["ResetViewBtn"] = "ビューをリセット";
        _en["ResetViewBtn"] = "Reset View";

        // ---- 实时预览 / 保存 / 导出 / 清空 / 定位 ----
        _zh["GroupApplySaveLocate"] = "实时预览 / 保存 / 清空 / 定位";
        _ja["GroupApplySaveLocate"] = "リアルタイム表示 / 保存 / クリア / 移動";
        _en["GroupApplySaveLocate"] = "Realtime / Save / Clear / Locate";

        _zh["RealtimePreviewToggle"] = "实时预览效果";
        _ja["RealtimePreviewToggle"] = "リアルタイム表示";
        _en["RealtimePreviewToggle"] = "Realtime Preview";

        _zh["SaveOutputFull"] = "输出贴图保存（完整贴图导出）";
        _ja["SaveOutputFull"] = "テクスチャ出力（フル画像）";
        _en["SaveOutputFull"] = "Save Output (Full Texture)";

        _zh["ExportPaintLayer"] = "仅输出绘画层（透明底）";
        _ja["ExportPaintLayer"] = "描画レイヤーのみ出力（透明）";
        _en["ExportPaintLayer"] = "Export Paint Layer (Alpha)";

        _zh["ClearEdits"] = "清空本次修改";
        _ja["ClearEdits"] = "今回の編集をクリア";
        _en["ClearEdits"] = "Clear Edits";

        _zh["ConfirmClearEdits"] = "再次点击，确认清空";
        _ja["ConfirmClearEdits"] = "もう一度クリックで確定";
        _en["ConfirmClearEdits"] = "Click again to confirm";

        _zh["LocateProjectResBtn"] = "定位到 Project 面板资源";
        _ja["LocateProjectResBtn"] = "Project パネルのリソースへ移動";
        _en["LocateProjectResBtn"] = "Locate Project Panel Resource";

        // ---- 信息/提示 ----
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

        _zh["InfoReadMainTexOk"] = "已读取主纹理：{0}。已在输出目录生成：{1}。可绘制并【输出贴图保存】。";
        _ja["InfoReadMainTexOk"] = "メインテクスチャ {0} を読み込みました。出力先：{1}。編集後【保存】できます。";
        _en["InfoReadMainTexOk"] = "Main texture {0} loaded. Output created at: {1}. You can paint and then [Save].";

        _zh["InfoNoMainTex"] = "不包含主纹理。已在输出目录生成空白 PNG：{0}。";
        _ja["InfoNoMainTex"] = "メインテクスチャがありません。空PNGを作成しました：{0}。";
        _en["InfoNoMainTex"] = "No main texture. Created a blank PNG at: {0}.";

        _zh["WarnFillOutsideUV"] = "点击位置不在网格 UV 覆盖区域内，无法填充。";
        _ja["WarnFillOutsideUV"] = "クリック位置はUVカバー領域外のため塗りつぶせません。";
        _en["WarnFillOutsideUV"] = "Click position is outside UV coverage; cannot fill.";

        _zh["RealtimeOn"] = "实时预览：已将绘制副本赋到材质（仅内存，不写文件）。";
        _ja["RealtimeOn"] = "リアルタイム表示：ペイントコピーをマテリアルへ（メモリのみ）。";
        _en["RealtimeOn"] = "Realtime preview: paint copy assigned to material (memory only).";

        _zh["RealtimeOff"] = "已关闭实时预览：材质还原为原始资产。";
        _ja["RealtimeOff"] = "リアルタイム表示OFF：マテリアルを元に戻しました。";
        _en["RealtimeOff"] = "Realtime preview turned off: material restored.";

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

        // ---- 作者窗口 ----
        _zh["AuthorWindowTitle"] = "LGC网格UV绘画工具 - 关于作者";
        _ja["AuthorWindowTitle"] = "LGCメッシュUVペイントツール - 作者について";
        _en["AuthorWindowTitle"] = "LGC Mesh UV Painter - About";

        _zh["AuthorName"] = "作者：copilote + LGC";
        _ja["AuthorName"] = "作者：copilote + LGC";
        _en["AuthorName"] = "Author: copilote + LGC";

        _zh["VersionInfo"] = "版本：{0}";
        _ja["VersionInfo"] = "バージョン：{0}";
        _en["VersionInfo"] = "Version: {0}";

        _zh["ContactInfo"] = "联系：GitHub LgcChina";
        _ja["ContactInfo"] = "連絡先：GitHub LgcChina";
        _en["ContactInfo"] = "Contact: GitHub LgcChina";

        // ---- 对称绘画提示 & UV叠加折叠面板 ----
        _zh["SymmetryEditingBlockPaint"] = "正在调整对称轴，已暂时禁用绘画。请先锁定或关闭对称模式。";
        _ja["SymmetryEditingBlockPaint"] = "対称軸を編集中です。ペイントは一時的に無効です。先に軸をロックするか対称モードを無効にしてください。";
        _en["SymmetryEditingBlockPaint"] = "Symmetry axis is being edited. Painting is temporarily disabled. Please lock the axis or disable symmetry first.";

        _zh["UVOverlayFoldout"] = "UV 线条叠加";
        _ja["UVOverlayFoldout"] = "UV オーバーレイ";
        _en["UVOverlayFoldout"] = "UV Overlay";

        // 工具详细说明（中/日/英多语言配置）
        // 中文版本说明
        _zh["ToolDescDetailed"] =
            "当前版本特色\n" +
            "- 支持基于 UV 的绘画、橡皮擦、UV 孤岛填充与孤岛橡皮擦。\n" +
            "- 提供 UV 边界限制与孤岛隔离模式，使绘制更精准安全。\n" +
            "- 所有操作支持 Undo/Redo，实时预览不修改原材质。\n" +
            "- 输出目录统一管理，可快速定位导出贴图。\n\n" +
            "实验性。\n\n" +
            "当前版本迁移至 GPU（RenderTexture + Shader/Compute Shader）绘制与实时预览。\n" +
            "- GPU 版本将显著提高绘画流畅度，减少卡顿并提升超大贴图的编辑效率。";

        // 日文版本说明
        _ja["ToolDescDetailed"] =
            "現在のバージョンの機能\n" +
            "- UVベースのペイント、消しゴム、UVアイランドの塗りつぶし、アイランド消しゴムをサポートします。\n" +
            "- UV境界拘束とアイランド分離モードにより、より正確で安全なペイントが可能になります。\n" +
            "- すべての操作で元に戻す/やり直しが可能になり、元のマテリアルを変更することなくリアルタイムプレビューが可能です。\n" +
            "- 出力ディレクトリ管理を統合し、エクスポートしたテクスチャを素早く見つけられるようになりました。\n\n" +
            "試験的機能です。\n\n" +
            "現在のバージョンでは、GPU（レンダーテクスチャ + シェーダー/コンピュートシェーダー）ペイントとリアルタイムプレビューに移行しました。\n" +
            "- GPUバージョンでは、ペイントの滑らかさが大幅に向上し、スタッターが軽減され、大きなテクスチャの編集効率が向上します。";

        // 英文版本说明
        _en["ToolDescDetailed"] =
            "Current Version Features\n" +
            "- Supports UV-based painting, eraser, UV island filling, and island eraser.\n" +
            "- Provides UV boundary constraints and island isolation modes for more precise and safer painting.\n" +
            "- All operations support Undo/Redo, with real-time preview without modifying original materials.\n" +
            "- Unified output directory management for quick location of exported textures.\n\n" +
            "Experimental.\n\n" +
            "The current version has migrated to GPU (RenderTexture + Shader/Compute Shader) painting and real-time preview.\n" +
            "- The GPU version will significantly improve painting smoothness, reduce stuttering, and enhance the editing efficiency of large textures.";
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
