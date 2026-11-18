#if UNITY_EDITOR
// LgcAuthorInfoWindow.cs (v2.10.0)
// Scrollable release history with pinned, clickable GitHub links.
using UnityEditor;
using UnityEngine;

public class LgcAuthorInfoWindow : EditorWindow
{
    internal const string RepositoryUrl = "https://github.com/LgcChina/MeshUVPainter";
    internal const string CollectionUrl = "https://github.com/LgcChina/LGC-VRC-Packages";
    private Vector2 historyScroll;

    public static void OpenWindow()
    {
        var lang = EditorLanguageManager.Instance;
        lang.InitLanguageData();
        var win = GetWindow<LgcAuthorInfoWindow>(utility: true, lang.GetText("AuthorWindowTitle"));
        win.minSize = new Vector2(360f, 280f);
        win.maxSize = new Vector2(1000f, 1400f);
        var parent = EditorGUIUtility.GetMainWindowPosition();
        float height = Mathf.Clamp(parent.height * 0.75f, 360f, 680f);
        win.position = new Rect(parent.center.x - 230f, parent.center.y - height * 0.5f, 460f, height);
        win.ShowUtility();
        win.Focus();
    }

    private void OnGUI()
    {
        var L = EditorLanguageManager.Instance;
        L.InitLanguageData();
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.ExpandHeight(true)))
        {
            GUILayout.Label(L.GetText("AuthorName"), EditorStyles.boldLabel);
            GUILayout.Label(string.Format(L.GetText("BottomVersionInfo"), LgcMeshUVColorPainter.VersionString));
            GUILayout.Space(4);
            historyScroll = EditorGUILayout.BeginScrollView(historyScroll,
                GUILayout.MinHeight(60f), GUILayout.MaxHeight(float.MaxValue), GUILayout.ExpandHeight(true));
            GUILayout.Label(L.GetText("ToolDescDetailed"), EditorStyles.wordWrappedLabel, GUILayout.ExpandWidth(true));
            EditorGUILayout.EndScrollView();
            DrawLink(L.GetText("GitHubRepository"), RepositoryUrl);
            DrawLink(L.GetText("GitHubCollection"), CollectionUrl);
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("OK", GUILayout.Width(80), GUILayout.Height(24))) Close();
            }
        }
    }

    private static void DrawLink(string label, string url)
    {
        var content = new GUIContent(label, url);
        Rect rect = GUILayoutUtility.GetRect(content, EditorStyles.linkLabel, GUILayout.Height(22), GUILayout.ExpandWidth(true));
        EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
        if (GUI.Button(rect, content, EditorStyles.linkLabel)) Application.OpenURL(url);
    }
}
#endif
