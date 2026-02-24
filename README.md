# LGC网格UV绘画工具
LGC网格UV绘画工具 - VRChat基于Unity编辑器，基于网格Mesh UV进行颜色绘制的工具
Unity编辑器专用工具（适配VRChat生态），专注于Mesh UV贴图颜色快速绘制，支持多语言切换、简化UV画图上色流程。

## 📌 核心特性
- 🎨 便捷的UV颜色绘制：支持画笔/橡皮擦/UV孤岛填充模式，笔刷大小/硬度可调节；
- 🌐 多语言切换：内置中/日/英三种语言，面板文本全适配；
- 📂 资源目录定位：一键跳转Project面板输出目录，文件夹缺失自动创建；
- ℹ️ 作者信息面板：点击底部版本/作者信息，弹出独立面板查看详细信息；
- 🎯 轻量化：纯编辑器工具，无运行时代码，不增加项目体积。

## 🛠️ 安装方法（推荐VCC安装）
### 方式1：VRChat Creator Companion（VCC）/ALCOM
https://lgcchina.github.io/LGC-VRC-Packages/


### 方式2：手动安装/不建议
1. 下载仓库Release包（Releases页面获取`com.lgcchina.mesh-uv-painter-1.0.0.zip`）；
2. 解压后将`com.lgcchina.mesh-uv-painter`文件夹放入Unity项目的`Packages/`目录下。

## 📖 使用说明
1. 打开Unity（2022.3版本），在顶部菜单栏找到「LGC → 网格UV绘画工具」，打开工具面板；
2. 绑定对象：选择带MeshRenderer/SkinnedMeshRenderer的GameObject，拖入后加载材质和纹理；
3. 绘制操作：选择画笔/橡皮擦模式，调整笔刷参数，在右侧UV视图中绘制颜色；
4. 辅助功能：
   - 点击「定位到Project面板资源」，快速跳转到输出目录；
   - 顶部下拉框切换语言（中/日/英）；
   - 点击底部版本信息，查看作者详细信息。

## ⚠️ 注意事项
1. Unity版本要求：默认支持2022.3.x（适配VRChat主流Unity版本）；
2. 依赖说明：无需额外安装其他插件；
3. 数据安全：绘制前建议备份原始纹理，避免误操作导致数据丢失；
4. 兼容性：仅支持编辑器环境使用，不会打入VRChat构建包。

## 📜 版本日志
### v1.0.0（初始版本）
- 核心UV颜色绘制功能；
- 多语言（中/日/英）切换；
- 资源目录定位（自动创建缺失文件夹）；
- 作者信息面板。

## ✨ 作者信息
- 作者：LgcChina
- GitHub：https://github.com/LgcChina
- 功能建议/BUG反馈：提交Issue至本仓库即可，虽然我不一定会看，修也是丢给ai修。
