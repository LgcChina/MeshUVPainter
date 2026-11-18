#if UNITY_EDITOR
// LgcBrushExecutor.cs (v2.11.0) - Shared fractional radius for GPU coverage and stroke spacing.
using UnityEngine;

/// <summary>
/// LGC - GPU画笔执行器
/// 仅负责修改 PaintLayerRT（绘制层）
/// 不处理输入、不处理预览合成、不处理导出
/// </summary>
internal class LgcBrushExecutor
{
    private readonly ComputeShader shader;
    private readonly int kBrush;
    private readonly int kFillIsland;

    private readonly RenderTexture paintLayerRT;

    private readonly ComputeBuffer uvMaskBuffer;
    private readonly ComputeBuffer islandIdBuffer;

    public LgcBrushExecutor(
        ComputeShader shader,
        int brushKernel,
        int fillKernel,
        RenderTexture paintLayerRT,
        ComputeBuffer uvMaskBuffer,
        ComputeBuffer islandIdBuffer)
    {
        this.shader = shader;
        this.kBrush = brushKernel;
        this.kFillIsland = fillKernel;
        this.paintLayerRT = paintLayerRT;
        this.uvMaskBuffer = uvMaskBuffer;
        this.islandIdBuffer = islandIdBuffer;
    }

    public void PaintAtUV(
        Vector2 uv,
        bool isBrushMode,
        int activeIslandId,
        bool enableBoundary,
        bool enableIsolation,
        LgcBrushSettings brush,
        float effectiveRadius)
    {
        if (!IsValid(uv)) return;
        brush.Clamp();

        float cx = uv.x * paintLayerRT.width;
        float cy = uv.y * paintLayerRT.height;
        float radius = Mathf.Max(0.5f, effectiveRadius);
        int r = Mathf.CeilToInt(radius + 1f);

        int x0 = Mathf.Clamp(Mathf.FloorToInt(cx) - r, 0, paintLayerRT.width - 1);
        int y0 = Mathf.Clamp(Mathf.FloorToInt(cy) - r, 0, paintLayerRT.height - 1);
        int x1 = Mathf.Clamp(Mathf.CeilToInt(cx) + r, 0, paintLayerRT.width - 1);
        int y1 = Mathf.Clamp(Mathf.CeilToInt(cy) + r, 0, paintLayerRT.height - 1);

        int rw = x1 - x0 + 1;
        int rh = y1 - y0 + 1;

        shader.SetTexture(kBrush, "_PaintLayerRT", paintLayerRT);

        SetupCommonMasks(kBrush, activeIslandId, enableBoundary, enableIsolation);
        SetupBrushParams(
            kBrush,
            new RectInt(x0, y0, rw, rh),
            new Vector2(cx, cy),
            brush,
            radius,
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
        LgcBrushSettings brush,
        float effectiveRadius)
    {
        Vector2 textureSize = new Vector2(paintLayerRT.width - 1, paintLayerRT.height - 1);
        Vector2 pA = Vector2.Scale(uvA, textureSize);
        Vector2 pB = Vector2.Scale(uvB, textureSize);

        float dist = Vector2.Distance(pA, pB);
        float spacing = Mathf.Max(1f, Mathf.Max(0.5f, effectiveRadius) * (0.7f - 0.4f * brush.Hardness));
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
                brush,
                effectiveRadius);
        }
    }

    public void FillIsland(
        int islandId,
        bool isFill,
        LgcBrushSettings brush,
        bool enableBoundary)
    {
        brush.Clamp();

        shader.SetTexture(kFillIsland, "_PaintLayerRT", paintLayerRT);

        SetupCommonMasks(
            kFillIsland,
            islandId,
            enableBoundary,
            false);

        shader.SetInts("_TexSize", paintLayerRT.width, paintLayerRT.height);
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
            (paintLayerRT.width + 15) / 16,
            (paintLayerRT.height + 15) / 16,
            1);
    }

    #region Internals

    private void SetupBrushParams(
        int kernel,
        RectInt rect,
        Vector2 center,
        LgcBrushSettings brush,
        float effectiveRadius,
        bool isBrushMode)
    {
        shader.SetInts("_RectXYWH", rect.x, rect.y, rect.width, rect.height);
        shader.SetInts("_TexSize", paintLayerRT.width, paintLayerRT.height);
        shader.SetFloats("_Center", center.x, center.y);
        shader.SetFloat("_Radius", effectiveRadius);
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