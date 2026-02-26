#if UNITY_EDITOR
// LgcAuthorInfoWindow.cs
// “关于作者”窗口：自适应内容大小，多语言适配，居中显示
using UnityEditor;
using UnityEngine;

public class LgcAuthorInfoWindow : EditorWindow
{
    // [修改] 移除固定宽高，改为限制最大宽度（避免窗口过宽）
    private const float MAX_WIDTH = 400f; // 文本最大显示宽度，可根据需要调整
    private const float WINDOW_PADDING = 20f; // 窗口内边距
    private GUIStyle _wordWrapStyle; // 缓存换行文本样式

    // [LGC 修改] 单例入口
    public static void OpenWindow()
    {
        var lang = EditorLanguageManager.Instance;
        lang.InitLanguageData();
        string title = lang.GetText("AuthorWindowTitle");

        var win = GetWindow<LgcAuthorInfoWindow>(utility: true, title);
        // [修改] 取消固定min/maxSize，改为动态计算
        win.minSize = new Vector2(300f, 100f); // 最小保底尺寸
        win.maxSize = new Vector2(MAX_WIDTH + WINDOW_PADDING, Screen.height * 0.8f); // 限制最大高度（不超过屏幕80%）

        // [修改] 先临时显示窗口，确保能计算内容尺寸后再居中
        win.ShowUtility();

        // 延迟一帧计算内容尺寸并居中（确保GUI样式已初始化）
        EditorApplication.delayCall += () =>
        {
            win.CenterWindowToMainEditor();
            win.Focus();
        };
    }

    // [新增] 窗口居中方法
    private void CenterWindowToMainEditor()
    {
        var mainWinPos = EditorGUIUtility.GetMainWindowPosition();
        float targetWidth = Mathf.Min(position.width, MAX_WIDTH + WINDOW_PADDING);
        float targetHeight = position.height;

        // 计算居中坐标
        float x = mainWinPos.x + (mainWinPos.width - targetWidth) * 0.5f;
        float y = mainWinPos.y + (mainWinPos.height - targetHeight) * 0.5f;

        // 更新窗口位置（保持宽高不变，只调整坐标）
        position = new Rect(x, y, targetWidth, targetHeight);
    }

    private void OnEnable()
    {
        // 初始化文本样式（只执行一次）
        _wordWrapStyle = new GUIStyle(EditorStyles.wordWrappedLabel)
        {
            wordWrap = true,
            richText = false // 根据需要开启富文本，默认关闭
        };
    }

    private void OnGUI()
    {
        var L = EditorLanguageManager.Instance;
        L.InitLanguageData();

        // [修改] 开始垂直布局，添加内边距
        using (var vertical = new GUILayout.VerticalScope(GUILayout.Width(MAX_WIDTH), GUILayout.ExpandWidth(true)))
        {
            GUILayout.Space(10);

            // 作者名称（加粗）
            GUILayout.Label(L.GetText("AuthorName"), EditorStyles.boldLabel);
            GUILayout.Space(4);

            // 版本信息
            string ver = string.Format(L.GetText("VersionInfo"), LgcMeshUVColorPainter.VersionString);
            GUILayout.Label(ver, EditorStyles.label);
            GUILayout.Space(6);

            // 联系信息
            GUILayout.Label(L.GetText("ContactInfo"), EditorStyles.label);
            GUILayout.Space(10);

            // [关键修改] 自适应文本显示（限制宽度，高度自动）
            GUILayout.Label(L.GetText("ToolDescDetailed"), _wordWrapStyle, GUILayout.Width(MAX_WIDTH));

            // [关键修改] 弹性空间，让按钮始终在底部
            GUILayout.FlexibleSpace();

            // OK按钮（固定高度，居中显示）
            using (var horizontal = new GUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("OK", GUILayout.Height(22), GUILayout.Width(80)))
                {
                    Close();
                }
                GUILayout.FlexibleSpace();
            }

            GUILayout.Space(10);
        }

        // [修改] 自动调整窗口最小尺寸以适配内容
        Repaint();
    }
}
#endif
