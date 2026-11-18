#if UNITY_EDITOR
// LgcMeshUVColorPainter_Fill.cs (v2.13.0)
// One fill action for UV islands, connected painted pixels and the independent selection mask.
using UnityEngine;

public partial class LgcMeshUVColorPainter
{
    private int kFillRegion;
    private void ApplyUnifiedFill(Vector2 uv,bool erase)
    {
        if(paintLayerRT==null||!computeReady||!IsUVInside01(uv))return;
        FinishActiveStroke();EnsureRegionResources();
        int scope=erase?0:fillScope,w=paintLayerRT.width,h=paintLayerRT.height,count=w*h;
        int[] mask=new int[count];
        bool Allowed(int i)=> (regionState[i]&6)==0&&(!onlyEditSelected||(regionState[i]&1)!=0)&&(!uvBoundaryLimitEnabled||uvMask[i]!=0)&&(paintSelectionCount==0||paintSelectionMask[i]!=0);
        Vector2[] seeds=symmetryEnabled&&symmetryLocked?new[]{uv,symmetryController.MirrorUV(uv)}:new[]{uv};
        if(scope==2)
        {
            if(paintSelectionCount==0){infoMessage=T("FillNeedsSelection");infoType=UnityEditor.MessageType.Info;return;}
            for(int i=0;i<count;i++)if(paintSelectionMask[i]!=0&&Allowed(i))mask[i]=1;
        }
        else if(scope==0)
        {
            foreach(var seed in seeds)
            {
                int island=GetIslandIdAtUV_CPU(seed);if(island<0)continue;
                for(int i=0;i<count;i++)if(islandIdMask[i]==island&&Allowed(i))mask[i]=1;
            }
        }
        else
        {
            Texture2D pixels=null;RenderTexture previous=RenderTexture.active;
            try
            {
                RenderTexture.active=paintLayerRT;pixels=new Texture2D(w,h,TextureFormat.RGBA32,false,true);
                pixels.ReadPixels(new Rect(0,0,w,h),0,0);pixels.Apply();Color32[] colors=pixels.GetPixels32();
                int[] queue=new int[count];
                foreach(var seed in seeds)
                {
                    if(!IsUVInside01(seed))continue;
                    int sx=Mathf.Clamp((int)(seed.x*w),0,w-1),sy=Mathf.Clamp((int)(seed.y*h),0,h-1),start=sy*w+sx;
                    if(mask[start]!=0||colors[start].a==0||!Allowed(start))continue;
                    int head=0,tail=0;queue[tail++]=start;mask[start]=1;
                    void Visit(int i){if(mask[i]==0&&colors[i].a!=0&&Allowed(i)){mask[i]=1;queue[tail++]=i;}}
                    while(head<tail)
                    {
                        int i=queue[head++],x=i%w,y=i/w;
                        if(x>0)Visit(i-1);if(x+1<w)Visit(i+1);if(y>0)Visit(i-w);if(y+1<h)Visit(i+w);
                    }
                }
            }
            finally{RenderTexture.active=previous;if(pixels!=null)DestroyImmediate(pixels);}
        }
        bool any=false;foreach(int value in mask)if(value!=0){any=true;break;}
        if(!any)return;
        using(var buffer=new ComputeBuffer(count,sizeof(int)))
        {
            buffer.SetData(mask);csPaint.SetBuffer(kFillRegion,"_FillRegion",buffer);
            csPaint.SetTexture(kFillRegion,"_PaintLayerRT",paintLayerRT);csPaint.SetInts("_TexSize",w,h);
            csPaint.SetVector("_Color",brush.Color);csPaint.SetFloat("_Opacity",brush.Opacity);csPaint.SetInt("_FillMode",erase?1:0);
            BeginStrokeSnapshot();csPaint.Dispatch(kFillRegion,(w+15)/16,(h+15)/16,1);EndStrokeApply();
        }
    }
}
#endif
