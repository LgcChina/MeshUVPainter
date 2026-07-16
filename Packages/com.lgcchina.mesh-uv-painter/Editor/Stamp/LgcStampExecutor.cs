#if UNITY_EDITOR
using UnityEngine;

/// <summary>
/// LGC - 盖印图案执行器 (v2.6.0)
/// 支持 UV 边界限制、孤岛隔离、图案镜像
/// </summary>
internal class LgcStampExecutor
{
    private readonly ComputeShader shader;
    private readonly int kStamp;
    private readonly RenderTexture paintLayerRT;

    private readonly ComputeBuffer uvMaskBuffer;
    private readonly ComputeBuffer islandIdBuffer;

    public LgcStampExecutor(
        ComputeShader shader,
        int stampKernel,
        RenderTexture paintLayerRT,
        ComputeBuffer uvMaskBuffer = null,
        ComputeBuffer islandIdBuffer = null)
    {
        this.shader = shader;
        this.kStamp = stampKernel;
        this.paintLayerRT = paintLayerRT;
        this.uvMaskBuffer = uvMaskBuffer;
        this.islandIdBuffer = islandIdBuffer;
    }

    public void ApplyStamp(
        Texture2D stampTex,
        Vector2 uvCenter,
        float scale,
        float rotation,
        float opacity,
        bool enableBoundary = false,
        bool enableIsolation = false,
        int activeIslandId = -1,
        bool mirrorPattern = false)  // ★ 新增参数
    {
        if (stampTex == null || paintLayerRT == null || shader == null || kStamp < 0) return;

        int w = paintLayerRT.width, h = paintLayerRT.height;
        float texAspect = (float)stampTex.width / stampTex.height;
        float halfSize = scale * 0.5f;

        Vector2[] corners = new Vector2[4];
        corners[0] = new Vector2(-halfSize * texAspect, -halfSize);
        corners[1] = new Vector2(halfSize * texAspect, -halfSize);
        corners[2] = new Vector2(halfSize * texAspect, halfSize);
        corners[3] = new Vector2(-halfSize * texAspect, halfSize);

        float rotRad = rotation * Mathf.Deg2Rad;
        float cos = Mathf.Cos(rotRad);
        float sin = Mathf.Sin(rotRad);
        for (int i = 0; i < 4; i++)
        {
            float x = corners[i].x, y = corners[i].y;
            corners[i].x = x * cos - y * sin;
            corners[i].y = x * sin + y * cos;
            corners[i] += uvCenter;
        }

        float uMin = Mathf.Min(corners[0].x, corners[1].x, corners[2].x, corners[3].x);
        float uMax = Mathf.Max(corners[0].x, corners[1].x, corners[2].x, corners[3].x);
        float vMin = Mathf.Min(corners[0].y, corners[1].y, corners[2].y, corners[3].y);
        float vMax = Mathf.Max(corners[0].y, corners[1].y, corners[2].y, corners[3].y);

        uMin = Mathf.Clamp01(uMin); uMax = Mathf.Clamp01(uMax);
        vMin = Mathf.Clamp01(vMin); vMax = Mathf.Clamp01(vMax);

        int xMin = Mathf.Clamp(Mathf.RoundToInt(uMin * w), 0, w - 1);
        int xMax = Mathf.Clamp(Mathf.RoundToInt(uMax * w), 0, w - 1);
        int yMin = Mathf.Clamp(Mathf.RoundToInt(vMin * h), 0, h - 1);
        int yMax = Mathf.Clamp(Mathf.RoundToInt(vMax * h), 0, h - 1);

        int rectW = xMax - xMin + 1, rectH = yMax - yMin + 1;
        if (rectW <= 0 || rectH <= 0) return;

        shader.SetTexture(kStamp, "_PaintLayerRT", paintLayerRT);
        shader.SetTexture(kStamp, "_StampTex", stampTex);

        if (uvMaskBuffer != null)
            shader.SetBuffer(kStamp, "_UvMask", uvMaskBuffer);
        if (islandIdBuffer != null)
            shader.SetBuffer(kStamp, "_IslandId", islandIdBuffer);

        shader.SetInts("_TexSize", w, h);
        shader.SetInts("_StampRect", xMin, yMin, rectW, rectH);
        shader.SetFloats("_StampCenter", uvCenter.x, uvCenter.y);
        shader.SetFloat("_StampScale", scale);
        shader.SetFloat("_StampRotation", rotation * Mathf.Deg2Rad);
        shader.SetFloat("_StampOpacity", Mathf.Clamp01(opacity));
        shader.SetFloats("_StampTexSize", stampTex.width, stampTex.height);
        shader.SetFloat("_StampMirror", mirrorPattern ? 1f : 0f);  // ★ 传递给 Compute

        shader.SetInt("_EnableBoundary", enableBoundary ? 1 : 0);
        shader.SetInt("_EnableIsolation", enableIsolation ? 1 : 0);
        shader.SetInt("_ActiveIslandId", enableIsolation ? activeIslandId : -1);

        int groupsX = Mathf.Max(1, (rectW + 15) / 16);
        int groupsY = Mathf.Max(1, (rectH + 15) / 16);
        shader.Dispatch(kStamp, groupsX, groupsY, 1);
    }
}
#endif