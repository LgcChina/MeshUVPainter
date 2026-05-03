#if UNITY_EDITOR
using UnityEngine;

/// <summary>
/// LGC - GPU 画笔执行器
/// 仅负责把“画笔结果”画到 RenderTexture 上
/// 不处理鼠标、不处理 UI、不决定绘制时机
/// </summary>
internal class LgcBrushExecutor
{
    private readonly ComputeShader shader;
    private readonly int kBrush;
    private readonly int kFillIsland;

    private readonly RenderTexture paintRT;
    private readonly RenderTexture baseRT;

    private readonly ComputeBuffer uvMaskBuffer;
    private readonly ComputeBuffer islandIdBuffer;

    public LgcBrushExecutor(
        ComputeShader shader,
        int brushKernel,
        int fillKernel,
        RenderTexture paintRT,
        RenderTexture baseRT,
        ComputeBuffer uvMaskBuffer,
        ComputeBuffer islandIdBuffer)
    {
        this.shader = shader;
        this.kBrush = brushKernel;
        this.kFillIsland = fillKernel;
        this.paintRT = paintRT;
        this.baseRT = baseRT;
        this.uvMaskBuffer = uvMaskBuffer;
        this.islandIdBuffer = islandIdBuffer;
    }

    #region Public API

    public void PaintAtUV(
        Vector2 uv,
        bool isBrushMode,
        int activeIslandId,
        bool enableBoundary,
        bool enableIsolation,
        LgcBrushSettings brush)
    {
        if (!IsValid(uv)) return;
        brush.Clamp();

        int cx = Mathf.RoundToInt(uv.x * (paintRT.width - 1));
        int cy = Mathf.RoundToInt(uv.y * (paintRT.height - 1));
        int r = brush.Size;

        int x0 = Mathf.Clamp(cx - r, 0, paintRT.width - 1);
        int y0 = Mathf.Clamp(cy - r, 0, paintRT.height - 1);
        int x1 = Mathf.Clamp(cx + r, 0, paintRT.width - 1);
        int y1 = Mathf.Clamp(cy + r, 0, paintRT.height - 1);

        int rw = x1 - x0 + 1;
        int rh = y1 - y0 + 1;

        shader.SetTexture(kBrush, "_PaintRT", paintRT);
        shader.SetTexture(kBrush, "_BaseRT", baseRT);

        SetupCommonMasks(kBrush, activeIslandId, enableBoundary, enableIsolation);
        SetupBrushParams(
            kBrush,
            new RectInt(x0, y0, rw, rh),
            new Vector2Int(cx, cy),
            brush,
            isBrushMode
        );

        shader.Dispatch(
            kBrush,
            Mathf.Max(1, (rw + 7) / 8),
            Mathf.Max(1, (rh + 7) / 8),
            1);
    }

    public void DrawStroke(
        Vector2 uvA,
        Vector2 uvB,
        bool isBrushMode,
        int activeIslandId,
        bool enableBoundary,
        bool enableIsolation,
        LgcBrushSettings brush)
    {
        Vector2 pA = uvA * (paintRT.width - 1);
        Vector2 pB = uvB * (paintRT.width - 1);

        float dist = Vector2.Distance(pA, pB);
        float spacing = Mathf.Max(1f, brush.Size * (0.7f - 0.4f * brush.Hardness));
        int steps = Mathf.Max(1, Mathf.CeilToInt(dist / spacing));

        for (int i = 0; i <= steps; i++)
        {
            Vector2 uv = Vector2.Lerp(uvA, uvB, i / (float)steps);
            PaintAtUV(
                uv,
                isBrushMode,
                activeIslandId,
                enableBoundary,
                enableIsolation,
                brush);
        }
    }

    public void FillIsland(
        int islandId,
        bool isFill,
        LgcBrushSettings brush,
        bool enableBoundary)
    {
        brush.Clamp();

        shader.SetTexture(kFillIsland, "_PaintRT", paintRT);
        shader.SetTexture(kFillIsland, "_BaseRT", baseRT);

        SetupCommonMasks(
            kFillIsland,
            islandId,
            enableBoundary,
            false);

        shader.SetInts("_TexSize", paintRT.width, paintRT.height);
        shader.SetInt("_FillIslandId", islandId);
        shader.SetInt("_FillMode", isFill ? 0 : 1);
        shader.SetFloats("_Color",
            brush.Color.r,
            brush.Color.g,
            brush.Color.b,
            1f);
        shader.SetFloat("_Opacity", brush.Opacity);

        shader.Dispatch(
            kFillIsland,
            (paintRT.width + 15) / 16,
            (paintRT.height + 15) / 16,
            1);
    }

    #endregion

    #region Internals

    private void SetupBrushParams(
        int kernel,
        RectInt rect,
        Vector2Int center,
        LgcBrushSettings brush,
        bool isBrushMode)
    {
        shader.SetInts("_RectXYWH", rect.x, rect.y, rect.width, rect.height);
        shader.SetInts("_TexSize", paintRT.width, paintRT.height);
        shader.SetInts("_Center", center.x, center.y);
        shader.SetInt("_Radius", brush.Size);
        shader.SetFloat("_Hardness", brush.Hardness);
        shader.SetFloat("_Opacity", brush.Opacity);
        shader.SetFloats("_Color",
            brush.Color.r,
            brush.Color.g,
            brush.Color.b,
            1f);
        shader.SetInt("_Mode", isBrushMode ? 0 : 1);
    }

    private void SetupCommonMasks(
        int kernel,
        int activeIslandId,
        bool enableBoundary,
        bool enableIsolation)
    {
        if (uvMaskBuffer != null)
            shader.SetBuffer(kernel, "_UvMask", uvMaskBuffer);
        if (islandIdBuffer != null)
            shader.SetBuffer(kernel, "_IslandId", islandIdBuffer);

        shader.SetInt("_EnableBoundary", enableBoundary ? 1 : 0);
        shader.SetInt("_EnableIsolation", enableIsolation ? 1 : 0);
        shader.SetInt("_ActiveIslandId", enableIsolation ? activeIslandId : -1);
    }

    private bool IsValid(Vector2 uv)
    {
        return uv.x >= 0f && uv.x <= 1f && uv.y >= 0f && uv.y <= 1f;
    }

    #endregion
}
#endif
