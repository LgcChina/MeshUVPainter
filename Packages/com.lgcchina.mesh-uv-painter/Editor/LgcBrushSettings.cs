#if UNITY_EDITOR
// LgcBrushSettings.cs (v2.8.0) - User radius and size mode; effective texture radius is computed by the window.
using System;
using UnityEngine;

internal enum LgcBrushSizeMode { Screen, Texture }

[Serializable]
internal class LgcBrushSettings
{
    public Color Color = Color.yellow;
    [Range(1, 256)] public int Size = 32;
    public LgcBrushSizeMode SizeMode = LgcBrushSizeMode.Screen;
    [Range(0f, 1f)] public float Opacity = 1f;
    [Range(0f, 1f)] public float Hardness = 1f;

    public void Clamp()
    {
        Size = Mathf.Clamp(Size, 1, 256);
        Opacity = Mathf.Clamp01(Opacity);
        Hardness = Mathf.Clamp01(Hardness);
    }

    public LgcBrushSettings Clone()
    {
        return new LgcBrushSettings { Color = Color, Size = Size, SizeMode = SizeMode, Opacity = Opacity, Hardness = Hardness };
    }
}
#endif
