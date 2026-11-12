# LGC网格UV绘画工具

当前版本：`v2.7.0`

LGC网格UV绘画工具是面向 Unity 2022.3 和 VRChat 项目维护流程的编辑器工具。它基于 Mesh UV 快速绘制颜色，支持材质槽隔离、UV 孤岛编辑、图案盖印、对称绘制和多语言界面。

详细说明：[包内 README](Packages/com.lgcchina.mesh-uv-painter/README.md)

完整修改日志：[CHANGELOG](Packages/com.lgcchina.mesh-uv-painter/CHANGELOG.md)

## 核心特性
- UV 颜色绘制：支持画笔、橡皮擦、UV 孤岛填充和孤岛橡皮擦；
- 材质 UV 隔离：按 Renderer 材质槽和 Mesh 子网格确定当前 UV 范围；
- 3 RenderTexture 架构：原图、绘画层和预览层分离；
- 图案盖印：支持盖印、盖印对称和镜像不透明度调整；
- 多语言切换：内置中、日、英三种语言；
- 资源目录定位：一键跳转 Project 面板输出目录；
- 轻量化：纯编辑器工具，无运行时代码。

## 安装方法 VCC/ALCOM
### 方式1：VRChat Creator Companion（VCC）/ALCOM
https://lgcchina.github.io/LGC-VRC-Packages/


### 方式2：手动安装/不建议
1. 从 [Releases](https://github.com/LgcChina/MeshUVPainter/releases) 下载最新 ZIP 或 unitypackage。
2. 使用 ZIP 时，将 `com.lgcchina.mesh-uv-painter` 文件夹放入 Unity 项目的 `Packages/` 目录。
3. 使用 unitypackage 时，直接拖入 Unity 项目并按提示导入。
4. 保留 `.meta` 文件，不要同时保留两份相同脚本。

## 使用说明
1. 打开Unity（2022.3版本），在顶部菜单栏找到「LGC → 网格UV绘画工具」，打开工具面板；
2. 绑定对象：选择带MeshRenderer/SkinnedMeshRenderer的GameObject，拖入后会加载材质和纹理；
3. 在“材质槽”选择身体、眼睛等需要编辑的材质；
4. 在“UV叠加设置 > UV显示范围”中保持 **当前材质组**，确认只显示所选材质的 UV；
5. 绘制操作：选择画笔/橡皮擦模式，调整笔刷参数，在右侧UV视图中绘制颜色；
6. 辅助功能：
   - 点击「定位到Project面板资源」，快速跳转到输出目录；
   - 顶部下拉框切换语言（中/日/英）；
   - 点击底部版本信息，查看作者详细信息。

## 注意事项
1. Unity版本要求：默认支持2022.3.x（适配VRChat主流Unity版本）；
2. 依赖说明：无需额外安装其他插件；
3. 数据安全：绘制前建议备份原始纹理，避免误操作导致数据丢失；
4. 兼容性：仅支持编辑器环境使用，不会打入VRChat构建包；
5. 多个材质共用同一主贴图时，共用贴图警告不能替代按材质拆分贴图；
6. 切换材质槽或刷新材质后，应重新确认“UV显示范围”是否符合预期。

## 版本日志

### v2.7.0
- 新增按材质组隔离 UV，默认只显示当前材质槽对应的 Mesh 子网格 UV；
- 同一个 Material 映射多个子网格时，自动合并为当前活动材质组；
- UV 遮蔽、孤岛识别、孤岛填充和孤岛橡皮擦统一使用当前材质组；
- 新增“全部子网格（仅查看）”和共用主贴图风险提示；
- 完整内容见 [v2.7.0 修改日志](Packages/com.lgcchina.mesh-uv-painter/CHANGELOG.md)。

### v2.6.0
- 新增图案盖印和盖印对称；
- 支持镜像不透明度调整；
- 盖印需要在顶部模式手动切换开启。

### v2.5.1
- 升级为 baseRT、paintLayerRT 和 previewRT 三 RenderTexture 架构；
- 优化透明层和白底导出；
- 优化边界扩展、对称线裁剪和预览脏标记；
- 统一 GPU 资源释放。

### v2.4.3
- 支持 UV 画笔、橡皮擦、孤岛填充和孤岛橡皮擦；
- 支持 UV 边界限制、孤岛隔离、撤销与重做；
- 引入 GPU 绘制和实时预览；
- 修复切换材质槽时旧材质恢复和新材质覆盖异常。

### v1.0.0（初始版本）
- 核心UV颜色绘制功能；
- 多语言（中/日/英）切换；
- 资源目录定位（自动创建缺失文件夹）；
- 作者信息面板。

## 作者信息
- 作者：LgcChina
- GitHub：https://github.com/LgcChina
- 功能建议/BUG反馈：提交Issue至本仓库即可，虽然我不一定会看，修也是丢给ai修。
