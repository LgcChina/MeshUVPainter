#if UNITY_EDITOR
using System;
using UnityEngine;

/// <summary>
/// LGC - 画笔参数数据
/// 仅保存“画笔长什么样”的信息，不负责绘制、不负责事件、不负责 GPU
/// </summary>
[Serializable]
internal class LgcBrushSettings
{
    [Header("Brush")]
    public Color Color = Color.yellow;

    [Range(1, 256)]
    public int Size = 32;

    [Range(0f, 1f)]
    public float Opacity = 1.0f;

    [Range(0f, 1f)]
    public float Hardness = 1.0f;

    public void Clamp()
    {
        Size = Mathf.Clamp(Size, 1, 256);
        Opacity = Mathf.Clamp01(Opacity);
        Hardness = Mathf.Clamp01(Hardness);
    }

    public LgcBrushSettings Clone()
    {
        return new LgcBrushSettings
        {
            Color = this.Color,
            Size = this.Size,
            Opacity = this.Opacity,
            Hardness = this.Hardness
        };
    }
}
#endif