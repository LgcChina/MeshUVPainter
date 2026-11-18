#if UNITY_EDITOR
// LgcMeshUVColorPainter_Selection.cs (v2.13.1)
// Whole UV-face selection and independent paint selections with transactional affine previews.
using System;
using UnityEditor;
using UnityEngine;

public partial class LgcMeshUVColorPainter
{
    private bool[] selectedUvTriangles;
    private int[] paintSelectionMask;
    private ComputeBuffer paintSelectionBuffer;
    private RenderTexture paintTransformLayerRT, paintTransformDisplayRT, paintSelectionOverlayRT;
    private int kPaintTransform, kPaintSelectionOverlay, paintSelectionCount;
    private bool paintSelectionDirty = true, paintTransformDirty = true, paintTransformPending;
    private int paintSelectionControlId, paintSelectionDrag;
    private Vector2 paintBoxStart, paintBoxEnd, transformPointerStart, transformOffsetStart;
    private Vector2 paintTransformOffset, paintTransformScale = Vector2.one, transformScaleStart;
    private float paintTransformAngle, transformAngleStart;
    private bool paintBoxAdd, paintBoxSubtract;
    private Rect paintSelectionBounds;

    private static float Cross(Vector2 a, Vector2 b) => a.x*b.y-a.y*b.x;
    private static bool TriangleContains(Vector2 a,Vector2 b,Vector2 c,Vector2 p)
    {
        float area=Cross(b-a,c-a); if(Mathf.Abs(area)<1e-12f)return false;
        float u=Cross(b-a,p-a)/area,v=Cross(p-a,c-a)/area;
        return u>=-1e-6f&&v>=-1e-6f&&u+v<=1.000001f;
    }
    private static bool TriangleIntersectsRectangle(Vector2 a,Vector2 b,Vector2 c,Rect r)
    {
        if(Mathf.Abs(Cross(b-a,c-a))<1e-12f)return false;
        // Separating axes cover containment, edge crossings and a box around a shared UV vertex.
        Vector2[] tri={a,b,c};
        Vector2[] axes={Vector2.right,Vector2.up,new Vector2(a.y-b.y,b.x-a.x),new Vector2(b.y-c.y,c.x-b.x),new Vector2(c.y-a.y,a.x-c.x)};
        foreach(Vector2 axis in axes)
        {
            float lo=Vector2.Dot(tri[0],axis),hi=lo;
            for(int i=1;i<3;i++){float d=Vector2.Dot(tri[i],axis);lo=Mathf.Min(lo,d);hi=Mathf.Max(hi,d);}
            float center=Vector2.Dot(r.center,axis),radius=Mathf.Abs(axis.x)*r.width*.5f+Mathf.Abs(axis.y)*r.height*.5f;
            if(hi<center-radius-1e-7f||lo>center+radius+1e-7f)return false;
        }
        return true;
    }
    private void SelectUvTriangles(bool add,bool subtract,Func<Vector2,Vector2,Vector2,bool> match)
    {
        if(targetMesh==null||activeMaterialTriangles==null||regionState==null)return;
        Vector2[] uv=targetMesh.uv; int count=activeMaterialTriangles.Length/3;
        if(selectedUvTriangles==null||selectedUvTriangles.Length!=count)selectedUvTriangles=new bool[count];
        if(!add&&!subtract)Array.Clear(selectedUvTriangles,0,count);
        for(int i=0;i<count;i++)
        {
            int a=activeMaterialTriangles[i*3],b=activeMaterialTriangles[i*3+1],c=activeMaterialTriangles[i*3+2];
            if(a<uv.Length&&b<uv.Length&&c<uv.Length&&match(uv[a],uv[b],uv[c]))selectedUvTriangles[i]=!subtract;
        }
        for(int i=0;i<regionState.Length;i++)regionState[i]&=~1;
        int w=paintLayerRT.width,h=paintLayerRT.height;
        for(int i=0;i<count;i++)if(selectedUvTriangles[i])
        {
            Vector2 a=uv[activeMaterialTriangles[i*3]],b=uv[activeMaterialTriangles[i*3+1]],c=uv[activeMaterialTriangles[i*3+2]];
            int x0=Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.x,Mathf.Min(b.x,c.x))*w),0,w-1),x1=Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.x,Mathf.Max(b.x,c.x))*w),0,w-1);
            int y0=Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.y,Mathf.Min(b.y,c.y))*h),0,h-1),y1=Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.y,Mathf.Max(b.y,c.y))*h),0,h-1);
            for(int y=y0;y<=y1;y++)for(int x=x0;x<=x1;x++)if(TriangleContains(a,b,c,new Vector2((x+.5f)/w,(y+.5f)/h)))regionState[y*w+x]|=1;
        }
        RegionChanged();
    }

    private void EnsurePaintSelectionResources()
    {
        int count=paintLayerRT.width*paintLayerRT.height;
        if(paintSelectionMask==null||paintSelectionMask.Length!=count){paintSelectionMask=new int[count];paintSelectionDirty=true;}
        if(paintSelectionBuffer==null||paintSelectionBuffer.count!=count)
        { if(paintSelectionBuffer!=null)paintSelectionBuffer.Release();paintSelectionBuffer=new ComputeBuffer(count,sizeof(int));paintSelectionDirty=true; }
        if(paintSelectionDirty)
        {
            paintSelectionBuffer.SetData(paintSelectionMask);paintSelectionCount=0;
            int w=paintLayerRT.width,h=paintLayerRT.height,x0=w,y0=h,x1=0,y1=0;
            for(int i=0;i<count;i++)if(paintSelectionMask[i]!=0){paintSelectionCount++;x0=Mathf.Min(x0,i%w);x1=Mathf.Max(x1,i%w+1);y0=Mathf.Min(y0,i/w);y1=Mathf.Max(y1,i/w+1);}
            paintSelectionBounds=paintSelectionCount>0?Rect.MinMaxRect((float)x0/w,(float)y0/h,(float)x1/w,(float)y1/h):new Rect();paintSelectionDirty=false;
        }
        foreach(int kernel in new[]{kBrush,kFillIsland,kStamp,kMirrorLayer,kGradient,kPaintTransform,kPaintSelectionOverlay,kFillRegion})csPaint.SetBuffer(kernel,"_PaintSelection",paintSelectionBuffer);
        csPaint.SetInt("_UsePaintSelection",paintSelectionCount>0?1:0);
    }
    private void SelectPaintRectangle(Rect r,bool add,bool subtract)
    {
        if(paintLayerRT==null)return;CancelPaintTransform();EnsureRegionResources();
        if(!add&&!subtract)Array.Clear(paintSelectionMask,0,paintSelectionMask.Length);
        int w=paintLayerRT.width,h=paintLayerRT.height;
        int x0=Mathf.Clamp(Mathf.FloorToInt(r.xMin*w),0,w),x1=Mathf.Clamp(Mathf.CeilToInt(r.xMax*w),0,w);
        int y0=Mathf.Clamp(Mathf.FloorToInt(r.yMin*h),0,h),y1=Mathf.Clamp(Mathf.CeilToInt(r.yMax*h),0,h);
        for(int y=y0;y<y1;y++)for(int x=x0;x<x1;x++)paintSelectionMask[y*w+x]=subtract?0:1;
        PaintSelectionChanged();
    }
    private void PaintSelectionChanged(){paintSelectionDirty=true;EnsureRegionResources();MarkPreviewDirty();Repaint();}
    private void ClearPaintSelection()
    {CancelPaintTransform();if(paintSelectionMask!=null)Array.Clear(paintSelectionMask,0,paintSelectionMask.Length);PaintSelectionChanged();}
    private void ResamplePaintSelection(int[] old,int oldW,int oldH,int w,int h)
    {
        if(paintSelectionBuffer!=null)paintSelectionBuffer.Release();paintSelectionBuffer=null;
        paintSelectionMask=new int[w*h];
        if(old!=null&&old.Length==oldW*oldH)for(int y=0;y<h;y++)for(int x=0;x<w;x++)paintSelectionMask[y*w+x]=old[Mathf.Min(oldH-1,y*oldH/h)*oldW+Mathf.Min(oldW-1,x*oldW/w)];
        paintSelectionDirty=true;
    }
    private void DrawPaintSelectionSettings()
    {
        GUILayout.Label(T("PaintSelectionTitle"),EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(T("PaintSelectionHint"),MessageType.None);
        using(new EditorGUI.DisabledScope(paintSelectionCount==0))
        {
            using(new EditorGUI.DisabledScope(!paintTransformPending))
                if(GUILayout.Button(T("SelectionApply"),GUILayout.Height(26)))ApplyPaintTransform();
            using(new EditorGUILayout.HorizontalScope())
            {
                if(GUILayout.Button(T("SelectionCancel")))CancelPaintTransform();
                if(GUILayout.Button(T("RegionClearSelection")))ClearPaintSelection();
            }
        }
    }
    private Vector2 PaintSelectionCenterPixels=>new Vector2(paintSelectionBounds.center.x*paintLayerRT.width,paintSelectionBounds.center.y*paintLayerRT.height);
    private static Vector2 RotatePoint(Vector2 p,float degrees)
    {float a=degrees*Mathf.Deg2Rad,c=Mathf.Cos(a),s=Mathf.Sin(a);return new Vector2(c*p.x-s*p.y,s*p.x+c*p.y);}
    private Vector2 InversePaintPoint(Vector2 uv)
    {
        Vector2 p=new Vector2(uv.x*paintLayerRT.width,uv.y*paintLayerRT.height),center=PaintSelectionCenterPixels;
        p=RotatePoint(p-center-paintTransformOffset,-paintTransformAngle);
        return center+new Vector2(p.x/paintTransformScale.x,p.y/paintTransformScale.y);
    }
    private bool PaintSelectionAt(Vector2 uv)
    {
        Vector2 p=InversePaintPoint(uv);int x=Mathf.FloorToInt(p.x),y=Mathf.FloorToInt(p.y);
        return x>=0&&y>=0&&x<paintLayerRT.width&&y<paintLayerRT.height&&paintSelectionMask[y*paintLayerRT.width+x]!=0;
    }
    private void BindPaintTransform(int kernel)
    {
        EnsureRegionResources();int w=paintLayerRT.width,h=paintLayerRT.height;Vector2 c=PaintSelectionCenterPixels;
        csPaint.SetInts("_TexSize",w,h);csPaint.SetVector("_SelectionCenterOffset",new Vector4(c.x,c.y,paintTransformOffset.x,paintTransformOffset.y));
        csPaint.SetVector("_SelectionScaleAngle",new Vector4(paintTransformScale.x,paintTransformScale.y,paintTransformAngle*Mathf.Deg2Rad,0));
        csPaint.SetInt("_EnableBoundary",uvBoundaryLimitEnabled?1:0);csPaint.SetBuffer(kernel,"_UvMask",uvMaskBuffer);
    }
    private RenderTexture GetPaintTransformDisplay(bool refresh)
    {
        if(refresh)EnsurePreview();if(!paintTransformPending||paintSelectionCount==0)return previewRT;
        if(!refresh)return paintTransformDisplayRT!=null?paintTransformDisplayRT:previewRT;
        if(paintTransformLayerRT==null){paintTransformLayerRT=NewRT(paintLayerRT.width,paintLayerRT.height);paintTransformDisplayRT=NewRT(paintLayerRT.width,paintLayerRT.height);paintTransformDirty=true;}
        if(paintTransformDirty)
        {
            BindPaintTransform(kPaintTransform);csPaint.SetTexture(kPaintTransform,"_SelectionSource",paintLayerRT);csPaint.SetTexture(kPaintTransform,"_ResultRT",paintTransformLayerRT);
            csPaint.Dispatch(kPaintTransform,(paintLayerRT.width+15)/16,(paintLayerRT.height+15)/16,1);
            CompositeLayer(paintTransformLayerRT,paintTransformDisplayRT);paintTransformDirty=false;
        }
        return paintTransformDisplayRT;
    }
    private void ApplyPaintTransform()
    {
        if(!paintTransformPending||paintSelectionCount==0||paintLayerRT==null)return;
        GetPaintTransformDisplay(true);BeginStrokeSnapshot();BlitPreservingTarget(paintTransformLayerRT,paintLayerRT);
        // Keep the transformed mask independent of pixel color, including selected transparent areas.
        int w=paintLayerRT.width,h=paintLayerRT.height;int[] next=new int[w*h];
        Vector2 center=PaintSelectionCenterPixels, offset=paintTransformOffset, scale=paintTransformScale;
        float radians=-paintTransformAngle*Mathf.Deg2Rad, cos=Mathf.Cos(radians), sin=Mathf.Sin(radians);
        for(int y=0;y<h;y++)for(int x=0;x<w;x++)
        {
            float dx=x+.5f-center.x-offset.x,dy=y+.5f-center.y-offset.y;
            int sx=Mathf.FloorToInt(center.x+(cos*dx-sin*dy)/scale.x),sy=Mathf.FloorToInt(center.y+(sin*dx+cos*dy)/scale.y);
            if(sx>=0&&sy>=0&&sx<w&&sy<h&&paintSelectionMask[sy*w+sx]!=0)next[y*w+x]=1;
        }
        paintSelectionMask=next;CancelPaintTransform();PaintSelectionChanged();EndStrokeApply();
    }
    private void CancelPaintTransform()
    {
        paintTransformPending=false;paintTransformOffset=Vector2.zero;paintTransformScale=Vector2.one;paintTransformAngle=0;paintTransformDirty=true;
        CancelPaintSelectionDrag();Repaint();
    }
    private void CancelPaintSelectionDrag()
    {if(paintSelectionControlId!=0&&GUIUtility.hotControl==paintSelectionControlId)GUIUtility.hotControl=0;paintSelectionDrag=0;}
    private Vector2[] PaintSelectionCorners(Rect view)
    {
        Vector2 c=PaintSelectionCenterPixels;Rect r=paintSelectionBounds;
        Vector2[] corners={new Vector2(r.xMin,r.yMax),new Vector2(r.xMax,r.yMax),new Vector2(r.xMax,r.yMin),new Vector2(r.xMin,r.yMin)};
        for(int i=0;i<4;i++)
        {
            Vector2 p=Vector2.Scale(corners[i],new Vector2(paintLayerRT.width,paintLayerRT.height));
            p=c+paintTransformOffset+RotatePoint(Vector2.Scale(p-c,paintTransformScale),paintTransformAngle);
            corners[i]=PointGUI(new Vector2(p.x/paintLayerRT.width,p.y/paintLayerRT.height),view);
        }
        return corners;
    }
    private Rect PaintSelectionHandleRect(int corner,Rect view)
    {
        Vector2 p=PaintSelectionCorners(view)[corner];
        // Keep tiny selections usable and every button inside the canvas.
        return new Rect(Mathf.Clamp(p.x-10,previewRect.xMin,previewRect.xMax-20),Mathf.Clamp(p.y-10,previewRect.yMin,previewRect.yMax-20),20,20);
    }
    private int PaintSelectionHandleAt(Vector2 mouse,Rect view)
    {
        if(paintSelectionCount==0)return 0;
        if(PaintSelectionHandleRect(1,view).Contains(mouse))return 5;
        if(PaintSelectionHandleRect(0,view).Contains(mouse))return 3;
        if(PaintSelectionHandleRect(2,view).Contains(mouse))return 4;
        if(PaintSelectionHandleRect(3,view).Contains(mouse))return 2;
        return 0;
    }
    private void HandlePaintSelectionInput()
    {
        Event e=Event.current;if(e==null||paintLayerRT==null)return;
        if(e.type==EventType.KeyDown&&e.keyCode==KeyCode.Escape&&activeToolTab==3&&mode==PaintMode.Selection)
        {CancelPaintTransform();e.Use();return;}
        if(paintSelectionDrag!=0&&GUIUtility.hotControl!=paintSelectionControlId){CancelPaintSelectionDrag();return;}
        if(activeToolTab!=3||mode!=PaintMode.Selection)return;
        bool inside=previewRect.Contains(e.mousePosition);
        if(e.type==EventType.MouseDown&&e.button==0&&!e.alt&&inside&&GUIUtility.hotControl==0)
        {
            Vector2 uv=PointUV(e.mousePosition);
            int handle=PaintSelectionHandleAt(e.mousePosition,viewController.GetViewRect());
            if(!e.shift&&!e.control&&!e.command&&handle!=0)
            {
                if(handle==5){ClearPaintSelection();e.Use();Repaint();return;}
                paintSelectionDrag=handle;transformPointerStart=new Vector2(uv.x*paintLayerRT.width,uv.y*paintLayerRT.height);
                transformOffsetStart=paintTransformOffset;transformScaleStart=paintTransformScale;transformAngleStart=paintTransformAngle;
            }
            else
            {
                CancelPaintTransform();paintBoxStart=paintBoxEnd=uv;paintBoxAdd=e.shift;paintBoxSubtract=e.control||e.command;paintSelectionDrag=1;
            }
            GUIUtility.hotControl=paintSelectionControlId;e.Use();Repaint();return;
        }
        if(paintSelectionDrag!=0&&e.type!=EventType.Used&&(e.type==EventType.MouseDrag||e.rawType==EventType.MouseUp))
        {
            Vector2 uv=PointUV(e.mousePosition),p=new Vector2(uv.x*paintLayerRT.width,uv.y*paintLayerRT.height);
            if(paintSelectionDrag==1)paintBoxEnd=uv;
            else
            {
                Vector2 c=PaintSelectionCenterPixels+transformOffsetStart,a=transformPointerStart-c,b=p-c;
                if(paintSelectionDrag==2)paintTransformOffset=transformOffsetStart+p-transformPointerStart;
                else if(paintSelectionDrag==3)paintTransformAngle=transformAngleStart+Vector2.SignedAngle(a,b);
                else paintTransformScale=transformScaleStart*Mathf.Clamp(b.magnitude/Mathf.Max(1,a.magnitude),.01f,100);
                paintTransformPending=true;paintTransformDirty=true;
            }
            if(e.rawType==EventType.MouseUp)
            {
                if(paintSelectionDrag!=1 && (p-transformPointerStart).sqrMagnitude<.0001f)paintTransformPending=paintTransformOffset!=Vector2.zero||paintTransformScale!=Vector2.one||Mathf.Abs(paintTransformAngle)>.0001f;
                if(paintSelectionDrag==1)SelectPaintRectangle(Rect.MinMaxRect(Mathf.Min(paintBoxStart.x,paintBoxEnd.x),Mathf.Min(paintBoxStart.y,paintBoxEnd.y),Mathf.Max(paintBoxStart.x,paintBoxEnd.x),Mathf.Max(paintBoxStart.y,paintBoxEnd.y)),paintBoxAdd,paintBoxSubtract);
                CancelPaintSelectionDrag();
            }
            e.Use();Repaint();
        }
    }
    private void DrawPaintSelectionOverlay(Rect view)
    {
        if(Event.current.type!=EventType.Repaint)return;
        bool editing=activeToolTab==3&&mode==PaintMode.Selection;
        // The outline remains visible while other tools use the active selection as a mask.
        if(paintSelectionCount>0&&editing)
        {
            if(paintSelectionOverlayRT==null)paintSelectionOverlayRT=NewRT(paintLayerRT.width,paintLayerRT.height);
            BindPaintTransform(kPaintSelectionOverlay);csPaint.SetTexture(kPaintSelectionOverlay,"_ResultRT",paintSelectionOverlayRT);
            csPaint.Dispatch(kPaintSelectionOverlay,(paintLayerRT.width+15)/16,(paintLayerRT.height+15)/16,1);GUI.DrawTextureWithTexCoords(previewRect,paintSelectionOverlayRT,view,true);
        }
        if(paintSelectionCount>0)
        {
            GUI.BeginClip(previewRect);
            try
            {
                Vector2[] corners=PaintSelectionCorners(view);
                using(new Handles.DrawingScope(Color.cyan))for(int i=0;i<4;i++)Handles.DrawLine(corners[i]-previewRect.position,corners[(i+1)%4]-previewRect.position);
                if(editing)
                {
                    string[] icons={"↻","✕","↔","✥"};string[] tips={T("SelectionRotate"),T("SelectionRemoveHint"),T("SelectionScale"),T("SelectionMove")};
                    for(int i=0;i<4;i++){Rect r=PaintSelectionHandleRect(i,view);r.position-=previewRect.position;GUI.Box(r,new GUIContent(icons[i],tips[i]),EditorStyles.miniButton);}
                }
            }finally{GUI.EndClip();}
        }
        if(paintSelectionDrag==1)
        {
            GUI.BeginClip(previewRect);
            try{Vector2 a=PointGUI(paintBoxStart,view)-previewRect.position,b=PointGUI(paintBoxEnd,view)-previewRect.position;
                Handles.DrawSolidRectangleWithOutline(Rect.MinMaxRect(Mathf.Min(a.x,b.x),Mathf.Min(a.y,b.y),Mathf.Max(a.x,b.x),Mathf.Max(a.y,b.y)),new Color(.1f,.7f,1,.08f),Color.cyan);
            }finally{GUI.EndClip();}
        }
    }
    private void ReleasePaintSelectionResources()
    {
        CancelPaintTransform();SafeReleaseRT(paintTransformLayerRT);SafeReleaseRT(paintTransformDisplayRT);SafeReleaseRT(paintSelectionOverlayRT);
        paintTransformLayerRT=null;paintTransformDisplayRT=null;paintSelectionOverlayRT=null;
        if(paintSelectionBuffer!=null)paintSelectionBuffer.Release();paintSelectionBuffer=null;paintSelectionMask=null;paintSelectionCount=0;paintSelectionDirty=true;
    }
}
#endif
