#if UNITY_EDITOR
// LgcAuthorInfoWindow.cs
// “关于作者”窗口：固定尺寸，简洁信息展示，多语言适配

using UnityEditor;
using UnityEngine;

public class LgcAuthorInfoWindow : EditorWindow
{
    private const int WIDTH = 300;
    private const int HEIGHT = 200;

    // [LGC 修改] 单例入口
    public static void OpenWindow()
    {
        var lang = EditorLanguageManager.Instance; lang.InitLanguageData();
        string title = lang.GetText("AuthorWindowTitle");

        var win = GetWindow<LgcAuthorInfoWindow>(utility: true, title);
        win.minSize = new Vector2(WIDTH, HEIGHT);
        win.maxSize = new Vector2(WIDTH, HEIGHT);

        // 尝试居中（近似）
        var pos = win.position;
        var main = EditorGUIUtility.GetMainWindowPosition(); // 2021+ 可用
        float x = main.x + (main.width - WIDTH) * 0.5f;
        float y = main.y + (main.height - HEIGHT) * 0.5f;
        win.position = new Rect(x, y, WIDTH, HEIGHT);

        win.ShowUtility();
        win.Focus();
    }

    private void OnGUI()
    {
        var L = EditorLanguageManager.Instance; L.InitLanguageData();

        GUILayout.Space(10);
        GUILayout.Label(L.GetText("AuthorName"), EditorStyles.boldLabel);

        // 从主工具类读取版本号（只读静态访问）
        string ver = string.Format(L.GetText("VersionInfo"), LgcMeshUVColorPainter.VersionString);

        GUILayout.Label(ver, EditorStyles.label);
        GUILayout.Space(6);
        GUILayout.Label(L.GetText("ContactInfo"), EditorStyles.label);
        GUILayout.Space(10);

        var wrap = new GUIStyle(EditorStyles.wordWrappedLabel) { wordWrap = true };
        GUILayout.Label(L.GetText("ToolDesc"), wrap);

        GUILayout.FlexibleSpace();
        if (GUILayout.Button("OK", GUILayout.Height(22))) // 简洁关闭
        {
            Close();
        }
    }
}
#endif