#if UNITY_EDITOR
// LgcMeshUVColorPainter_Regions.cs (v3.0 beta)
// Native work resolution, immutable gradient previews and shared region permissions.
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public partial class LgcMeshUVColorPainter
{
    private int precisionLevel, requestedPrecision;
    private int kGradient, kRegionOverlay;
    private int[] regionState; // 1 selected, 2 hidden, 4 disabled
    private ComputeBuffer regionBuffer;
    private RenderTexture regionOverlayRT, gradientLayerRT, gradientDisplayRT;
    private bool regionDirty = true, regionOverlayDirty = true, onlyEditSelected;
    private bool lastRegionSelecting;
    private int regionRevision, hoveredIsland = -1, selectedPixelCount, selectedHiddenCount, selectedDisabledCount;
    private int selectionMode = 1; // 0 idle, 1 island, 2 rectangle
    private int regionControlId, regionDrag; // 1 rectangle, 2 gradient A, 3 gradient B
    private Vector2 rectangleStart, rectangleEnd;
    private bool rectangleAdd, rectangleSubtract;
    [SerializeField] private Gradient paintGradient;
    private int gradientSettingsHash;
    private bool gradientSettingsObserved;
    private double nextGradientEditorPoll;
    private bool onlyShowSelected;
    private readonly Vector4[] gradientColorKeys=new Vector4[8],gradientAlphaKeys=new Vector4[8];
    private Gradient PaintGradient
    {
        get
        {
            if(paintGradient==null)
            {
                paintGradient=new Gradient();
                paintGradient.SetKeys(new[]{new GradientColorKey(Color.magenta,0),new GradientColorKey(Color.black,1)},
                    new[]{new GradientAlphaKey(1,0),new GradientAlphaKey(0,1)});
            }
            return paintGradient;
        }
    }
    private Vector2 gradientStart = new Vector2(.5f,.2f), gradientEnd = new Vector2(.5f,.8f);
    private float gradientOpacity = 1f;
    private bool gradientPending = true, gradientDirty = true;
    private int gradientIsland = -1;
    private float gradientEraseStart=0,gradientEraseEnd=1;
    private bool IsRegionSelecting => activeToolTab == 2;
    private int HistoryLimit => paintLayerRT == null ? UNDO_LIMIT : Mathf.Clamp((int)(256L * 1024 * 1024 / Math.Max(1L,(long)paintLayerRT.width * paintLayerRT.height * 4)), 2, UNDO_LIMIT);
    private string T(string key) => EditorLanguageManager.Instance.GetText(key);

    private void DrawPrecisionSettings()
    {
        GUILayout.Space(4);
        GUILayout.Label(T("PrecisionTitle"), EditorStyles.boldLabel);
        requestedPrecision = EditorGUILayout.IntSlider(T("PrecisionLevel"), requestedPrecision, 0, 3);
        string[] levels = { T("ExportOriginal"), "1024", "2048", "4096" };
        EditorGUILayout.LabelField(levels[requestedPrecision], paintLayerRT != null ? $"{paintLayerRT.width}*{paintLayerRT.height}" : "—");
        using (new EditorGUI.DisabledScope(paintLayerRT == null || requestedPrecision == precisionLevel))
            if (GUILayout.Button(T("PrecisionApply"))) ChangePaintingPrecision(requestedPrecision);
        EditorGUILayout.HelpBox(T("PrecisionHint"), MessageType.None);
    }

    private Vector2Int PrecisionDimensions(int level)
    {
        int w = originalTexture != null ? originalTexture.width : 1024, h = originalTexture != null ? originalTexture.height : 1024;
        int edge = new[] { 0,1024,2048,4096 }[Mathf.Clamp(level,0,3)];
        if (edge == 0) return new Vector2Int(w,h);
        float scale = (float)edge / Mathf.Max(w,h);
        return new Vector2Int(Mathf.Max(1,Mathf.RoundToInt(w*scale)),Mathf.Max(1,Mathf.RoundToInt(h*scale)));
    }

    private void ChangePaintingPrecision(int level)
    {
        if (paintLayerRT == null || !computeReady) return;
        FinishActiveStroke(); CancelRegionInteraction(); CancelPaintTransform(); stampController?.CancelInteraction();
        Vector2Int size = PrecisionDimensions(level);
        if (size.x > SystemInfo.maxTextureSize || size.y > SystemInfo.maxTextureSize) return;
        int oldW = paintLayerRT.width, oldH = paintLayerRT.height;
        if (oldW == size.x && oldH == size.y) { precisionLevel = requestedPrecision = level; return; }
        RenderTexture newBase = null, newLayer = null, newPreview = null;
        try
        {
            newBase = NewRT(size.x,size.y); newLayer = NewRT(size.x,size.y); newPreview = NewRT(size.x,size.y);
            if (!newBase.IsCreated() || !newLayer.IsCreated() || !newPreview.IsCreated()) throw new InvalidOperationException("Unable to allocate painting textures.");
            BlitPreservingTarget(originalTexture != null ? originalTexture : baseRT,newBase);
            ResizePaintTexture(paintLayerRT,newLayer);
        }
        catch { SafeReleaseRT(newBase); SafeReleaseRT(newLayer); SafeReleaseRT(newPreview); throw; }
        int[] oldState = regionState;
        int[] oldPaintSelection = paintSelectionMask;
        SafeReleaseRT(baseRT); SafeReleaseRT(paintLayerRT); SafeReleaseRT(previewRT);
        baseRT = newBase; paintLayerRT = newLayer; previewRT = newPreview;
        ReleaseTransientPreviews(); ReleaseMaskBuffers();
        if (regionBuffer != null) { regionBuffer.Release(); regionBuffer = null; }
        regionState = new int[size.x*size.y];
        if (oldState != null && oldState.Length == oldW*oldH)
            for (int y=0;y<size.y;y++) for(int x=0;x<size.x;x++)
                regionState[y*size.x+x] = oldState[Mathf.Min(oldH-1,y*oldH/size.y)*oldW+Mathf.Min(oldW-1,x*oldW/size.x)];
        ResamplePaintSelection(oldPaintSelection, oldW, oldH, size.x, size.y);
        uvMaskDirty = true; islandsDirty = true; regionDirty = true;
        EnsureMaskBuffersUpToDate(); RecreateExecutors();
        precisionLevel = requestedPrecision = level;
        while (undoList.Count > HistoryLimit) { SafeReleaseRT(undoList[0]); undoList.RemoveAt(0); }
        while (redoStack.Count > HistoryLimit) SafeReleaseRT(redoStack.Pop());
        TrimHistoryBudget();
        rtPool?.DisposeAll();
        MarkPreviewDirty(); EnsurePreview();
        if (enableRealTimePreview) ApplyMaterialInstanceWithTexture(GetDisplayPreview());
        infoMessage = T("PrecisionChanged"); infoType = MessageType.Info; Repaint();
    }

    private void TrimHistoryBudget()
    {
        long bytes=0; foreach(var rt in undoList) if(rt!=null)bytes+=(long)rt.width*rt.height*4;
        foreach(var rt in redoStack) if(rt!=null)bytes+=(long)rt.width*rt.height*4;
        while(bytes>256L*1024*1024&&(undoList.Count>0||redoStack.Count>0))
        {
            RenderTexture oldest;
            if(undoList.Count>0){oldest=undoList[0];undoList.RemoveAt(0);}
            else
            {
                var entries=redoStack.ToArray(); oldest=entries[entries.Length-1];redoStack.Clear();
                for(int i=entries.Length-2;i>=0;i--)redoStack.Push(entries[i]);
            }
            if(oldest!=null){bytes-=(long)oldest.width*oldest.height*4;SafeReleaseRT(oldest);}
        }
    }

    private void ResizePaintTexture(RenderTexture source, RenderTexture destination)
    {
        csPaint.SetTexture(kResizeExport,"_ExportSource",source); csPaint.SetTexture(kResizeExport,"_ResultRT",destination);
        csPaint.SetInts("_TexSize",source.width,source.height); csPaint.SetInts("_ExportSize",destination.width,destination.height);
        csPaint.Dispatch(kResizeExport,(destination.width+15)/16,(destination.height+15)/16,1);
    }

    private void RecreateExecutors()
    {
        if (!computeReady || paintLayerRT == null) return;
        brushExecutor = new LgcBrushExecutor(csPaint,kBrush,kFillIsland,paintLayerRT,uvMaskBuffer,islandIdBuffer);
        stampExecutor = new LgcStampExecutor(csPaint,kStamp,paintLayerRT,uvMaskBuffer,islandIdBuffer);
    }

    private void EnsureRegionResources()
    {
        if (paintLayerRT == null || !computeReady) return;
        int count = paintLayerRT.width*paintLayerRT.height;
        if (regionState == null || regionState.Length != count) { regionState = new int[count]; regionDirty = true; }
        if (regionBuffer == null || regionBuffer.count != count)
        {
            if (regionBuffer != null) regionBuffer.Release();
            regionBuffer = new ComputeBuffer(count,sizeof(int)); regionDirty = true;
        }
        if (regionDirty) UploadRegionState();
        EnsurePaintSelectionResources();
        foreach (int kernel in new[] { kBrush,kFillIsland,kStamp,kMirrorLayer,kGradient,kRegionOverlay,kPaintTransform,kPaintSelectionOverlay,kFillRegion }) csPaint.SetBuffer(kernel,"_RegionMask",regionBuffer);
        csPaint.SetInt("_OnlyEditSelected",onlyEditSelected ? 1 : 0);
    }

    private void UploadRegionState()
    {
        if (regionBuffer == null || regionState == null || regionBuffer.count != regionState.Length) return;
        selectedPixelCount=selectedHiddenCount=selectedDisabledCount=0;
        foreach(int state in regionState)if((state&1)!=0){selectedPixelCount++;if((state&2)!=0)selectedHiddenCount++;if((state&4)!=0)selectedDisabledCount++;}
        regionBuffer.SetData(regionState); regionDirty = false; regionRevision++; regionOverlayDirty = true; gradientDirty = true; stampPreviewDirty = true;
    }

    private void RegionChanged()
    {
        regionDirty = true; EnsureRegionResources(); MarkPreviewDirty(); Repaint();
    }

    private int SelectedRegionPixels() => selectedPixelCount;

    private void DrawRegionSettings()
    {
        GUILayout.Label(T("RegionTitle"),EditorStyles.boldLabel);
        int next=1+GUILayout.Toolbar(selectionMode-1,new[]{T("RegionIsland"),T("RegionBox")},GUILayout.Height(24));
        if(next!=selectionMode){FinishActiveStroke();CancelRegionInteraction();selectionMode=next;hoveredIsland=-1;regionOverlayDirty=true;}
        EditorGUILayout.HelpBox(T("RegionHint"),MessageType.None);
        using(new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            GUILayout.Label(T("RegionSelectedActions"),EditorStyles.boldLabel);
            using(new EditorGUI.DisabledScope(SelectedRegionPixels()==0))
            using(new EditorGUILayout.HorizontalScope())
            {
                if(GUILayout.Button(T(AllSelectedRegionFlag(2)?"RegionShow":"RegionHide")))ToggleSelectedRegionFlag(2);
                if(GUILayout.Button(T(AllSelectedRegionFlag(4)?"RegionEnable":"RegionDisable")))ToggleSelectedRegionFlag(4);
            }
            GUILayout.Label(T("RegionFocusTitle"),EditorStyles.miniBoldLabel);
            using(new EditorGUILayout.HorizontalScope())
            {
                bool show=EditorGUILayout.ToggleLeft(T("RegionOnlyShow"),onlyShowSelected);
                bool edit=EditorGUILayout.ToggleLeft(T("RegionOnlyEdit"),onlyEditSelected);
                if(show!=onlyShowSelected){onlyShowSelected=show;RegionChanged();}
                if(edit!=onlyEditSelected){onlyEditSelected=edit;RegionChanged();}
            }
            using(new EditorGUILayout.HorizontalScope())
            {
                if(GUILayout.Button(T("RegionClearSelection")))ClearRegionSelection();
                if(GUILayout.Button(T("RegionResetAll")))ResetRegionVisibilityAndEditing();
            }
        }
    }
    private bool AllSelectedRegionFlag(int flag)
    {
        return selectedPixelCount>0&&(flag==2?selectedHiddenCount:flag==4?selectedDisabledCount:0)==selectedPixelCount;
    }
    private void ToggleSelectedRegionFlag(int flag)=>SetSelectedRegionState(flag,!AllSelectedRegionFlag(flag));
    private void ResetRegionVisibilityAndEditing()
    {
        onlyShowSelected=onlyEditSelected=false;
        if(regionState!=null)for(int i=0;i<regionState.Length;i++)regionState[i]&=~6;
        RegionChanged();
    }

    private void ClearRegionSelection()
    {
        onlyShowSelected=onlyEditSelected=false;
        if(regionState==null)return;
        selectedUvTriangles = null;
        for(int i=0;i<regionState.Length;i++)regionState[i]&=~1;
        RegionChanged();
    }
    private void SetSelectedRegionState(int flag,bool value)
    {
        if(regionState==null)return;
        for(int i=0;i<regionState.Length;i++)if((regionState[i]&1)!=0)regionState[i]=value?regionState[i]|flag:regionState[i]&~flag;
        RegionChanged();
    }
    private void ResetRegionFlag(int flag)
    {
        if(regionState==null)return;
        for(int i=0;i<regionState.Length;i++)regionState[i]&=~flag;
        RegionChanged();
    }
    private void SelectIsland(int island,bool add,bool subtract)
    {
        SelectUvTriangles(add, subtract, (a,b,c) => island >= 0 && GetIslandIdAtUV_CPU((a+b+c)/3f) == island);
    }
    private void SelectRectangle(Rect uvRect,bool add,bool subtract)
    {
        SelectUvTriangles(add, subtract, (a,b,c) => TriangleIntersectsRectangle(a,b,c,uvRect));
    }

    private void DrawGradientSettings()
    {
        float previousLabelWidth=EditorGUIUtility.labelWidth;
        EditorGUIUtility.labelWidth=Mathf.Min(150f,_leftPanelWidth*.35f);
        try
        {
        GUILayout.Label(T(IsGradientErasing?"EraseGradient":"GradientTitle"),EditorStyles.boldLabel);
        EditorGUI.BeginChangeCheck();
        if(IsGradientErasing)
        {
            gradientEraseStart=EditorGUILayout.Slider(T("EraseStart"),gradientEraseStart,0,1);
            gradientEraseEnd=EditorGUILayout.Slider(T("EraseEnd"),gradientEraseEnd,0,1);
        }
        else
        {
            paintGradient=EditorGUILayout.GradientField(T("GradientColors"),PaintGradient);
            EditorGUILayout.HelpBox(T("GradientKeysHint"),MessageType.None);
        }
        gradientOpacity=EditorGUILayout.Slider(T("Opacity"),gradientOpacity,0,1);

        if(EditorGUI.EndChangeCheck())GradientSettingsChanged();
        using(new EditorGUILayout.HorizontalScope())
        {
            if(GUILayout.Button(T(IsGradientErasing?"EraseSwap":"GradientReverse"))){if(IsGradientErasing){float strength=gradientEraseStart;gradientEraseStart=gradientEraseEnd;gradientEraseEnd=strength;}else ReversePaintGradient();GradientSettingsChanged();}
            if(!IsGradientErasing&&GUILayout.Button(T("GradientTransparent"))){SetGradientEndTransparent();GradientSettingsChanged();}
        }
        EditorGUILayout.HelpBox(T(IsGradientErasing?"GradientEraseHint":"GradientHint"),MessageType.None);
        using(new EditorGUI.DisabledScope(paintLayerRT==null||!gradientPending||(symmetryEnabled&&!symmetryLocked)||(islandIsolationEnabled&&gradientIsland<0&&SelectedRegionPixels()==0)))
            if(GUILayout.Button(T(IsGradientErasing?"EraseApply":"GradientApply"),GUILayout.Height(26))) ApplyGradient();
        if(GUILayout.Button(T("GradientCancel"))){gradientPending=false;gradientDirty=true;Repaint();}
        }finally{EditorGUIUtility.labelWidth=previousLabelWidth;}
    }

    private int GradientSettingsHash()
    {
        unchecked
        {
            int hash=17;
            void Add(int value){hash=hash*31+value;}
            Add(gradientOpacity.GetHashCode());Add(gradientEraseStart.GetHashCode());Add(gradientEraseEnd.GetHashCode());
            var gradient=PaintGradient;Add((int)gradient.mode);
            foreach(var key in gradient.colorKeys){Add(key.color.GetHashCode());Add(key.time.GetHashCode());}
            foreach(var key in gradient.alphaKeys){Add(key.alpha.GetHashCode());Add(key.time.GetHashCode());}
            return hash;
        }
    }
    private void GradientSettingsChanged()
    {
        gradientSettingsHash=GradientSettingsHash();gradientSettingsObserved=true;
        gradientPending=true;gradientDirty=true;Repaint();
    }
    private void UpdateGradientEditor()
    {
        if(this==null||!IsGradientEditing||EditorApplication.isPlayingOrWillChangePlaymode)return;
        // The native gradient popup mutates its Gradient while this window is unfocused.
        // Observe values instead of dispatching every frame or waiting for the popup to close.
        double now=EditorApplication.timeSinceStartup;
        if(now<nextGradientEditorPoll)return;
        nextGradientEditorPoll=now+1.0/30.0;
        int hash=GradientSettingsHash();
        if(!gradientSettingsObserved){gradientSettingsHash=hash;gradientSettingsObserved=true;return;}
        if(hash!=gradientSettingsHash)GradientSettingsChanged();
    }
    private void ReversePaintGradient()
    {
        var gradient=PaintGradient;var colors=gradient.colorKeys;var alpha=gradient.alphaKeys;
        for(int i=0;i<colors.Length;i++)colors[i].time=1-colors[i].time;
        for(int i=0;i<alpha.Length;i++)alpha[i].time=1-alpha[i].time;
        gradient.SetKeys(colors,alpha);
    }
    private void SetGradientEndTransparent()
    {
        var gradient=PaintGradient;var alpha=gradient.alphaKeys;
        if(alpha[alpha.Length-1].time>=1f)alpha[alpha.Length-1]=new GradientAlphaKey(0,1);
        else
        {
            // Native gradients allow up to eight alpha keys. Preserve the other keys.
            if(alpha.Length<8)Array.Resize(ref alpha,alpha.Length+1);
            alpha[alpha.Length-1]=new GradientAlphaKey(0,1);
        }
        gradient.alphaKeys=alpha;
    }
    private void BindGradientKeys()
    {
        var gradient=PaintGradient;var colors=gradient.colorKeys;var alpha=gradient.alphaKeys;
        for(int i=0;i<colors.Length;i++)gradientColorKeys[i]=new Vector4(colors[i].color.r,colors[i].color.g,colors[i].color.b,colors[i].time);
        for(int i=0;i<alpha.Length;i++)gradientAlphaKeys[i]=new Vector4(alpha[i].alpha,0,0,alpha[i].time);
        csPaint.SetVectorArray("_GradientColorKeys",gradientColorKeys);csPaint.SetVectorArray("_GradientAlphaKeys",gradientAlphaKeys);
        csPaint.SetInt("_GradientColorCount",colors.Length);csPaint.SetInt("_GradientAlphaCount",alpha.Length);
        csPaint.SetInt("_GradientFixed",gradient.mode==GradientMode.Fixed?1:0);
    }

    private void DispatchGradient(RenderTexture layer)
    {
        EnsureRegionResources();
        csPaint.SetTexture(kGradient,"_PaintLayerRT",layer); csPaint.SetBuffer(kGradient,"_UvMask",uvMaskBuffer); csPaint.SetBuffer(kGradient,"_IslandId",islandIdBuffer);
        csPaint.SetInts("_TexSize",layer.width,layer.height);
        csPaint.SetVector("_GradientColorsA",new Color(0,0,0,gradientEraseStart));csPaint.SetVector("_GradientColorsB",new Color(0,0,0,gradientEraseEnd));
        if(!IsGradientErasing)BindGradientKeys();
        csPaint.SetInt("_GradientErase",IsGradientErasing?1:0);
        csPaint.SetFloat("_GradientOpacity",gradientOpacity); csPaint.SetInt("_EnableBoundary",uvBoundaryLimitEnabled?1:0);
        int primaryIsland=islandIsolationEnabled&&SelectedRegionPixels()==0?GetIslandIdAtUV_CPU(gradientStart):-1;
        csPaint.SetInt("_GradientIsland",primaryIsland);
        csPaint.SetInt("_GradientUseSelection",SelectedRegionPixels()>0?1:0);
        csPaint.SetInt("_GradientHasSymmetry",symmetryEnabled&&symmetryLocked?1:0);
        csPaint.SetInt("_GradientMirrored",0);
        Vector2 mirroredStart=symmetryController.MirrorUV(gradientStart);
        csPaint.SetVector("_GradientOtherStart",new Vector4(mirroredStart.x,mirroredStart.y,0,0));
        csPaint.SetVector("_GradientAxis",new Vector4(gradientStart.x,gradientStart.y,gradientEnd.x,gradientEnd.y));
        csPaint.Dispatch(kGradient,(layer.width+15)/16,(layer.height+15)/16,1);
        if(symmetryEnabled&&symmetryLocked)
        {
            Vector2 a=symmetryController.MirrorUV(gradientStart),b=symmetryController.MirrorUV(gradientEnd);
            if(islandIsolationEnabled&&SelectedRegionPixels()==0&&GetIslandIdAtUV_CPU(a)<0)return;
            csPaint.SetVector("_GradientAxis",new Vector4(a.x,a.y,b.x,b.y));
            csPaint.SetInt("_GradientIsland",islandIsolationEnabled&&SelectedRegionPixels()==0?GetIslandIdAtUV_CPU(a):-1);
            csPaint.SetInt("_GradientMirrored",1);
            csPaint.SetVector("_GradientOtherStart",new Vector4(gradientStart.x,gradientStart.y,0,0));
            csPaint.Dispatch(kGradient,(layer.width+15)/16,(layer.height+15)/16,1);
            csPaint.SetInt("_GradientMirrored",0);
        }
    }
    private bool CanGradient => computeReady&&uvMaskBuffer!=null&&islandIdBuffer!=null&&gradientPending&&(!symmetryEnabled||symmetryLocked)&&(gradientEnd-gradientStart).sqrMagnitude>1e-8f&&(!islandIsolationEnabled||GetIslandIdAtUV_CPU(gradientStart)>=0||SelectedRegionPixels()>0);
    private RenderTexture GetGradientDisplay(bool refresh)
    {
        if(refresh)EnsurePreview();
        if(!CanGradient||!enableRealTimePreview||paintLayerRT==null||!computeReady)return previewRT;
        if(!refresh)return gradientDisplayRT!=null?gradientDisplayRT:previewRT;
        if(gradientLayerRT==null){gradientLayerRT=NewRT(paintLayerRT.width,paintLayerRT.height);gradientDisplayRT=NewRT(paintLayerRT.width,paintLayerRT.height);gradientDirty=true;}
        if(gradientDirty){BlitPreservingTarget(paintLayerRT,gradientLayerRT);DispatchGradient(gradientLayerRT);CompositeLayer(gradientLayerRT,gradientDisplayRT);gradientDirty=false;if(enableRealTimePreview)SceneView.RepaintAll();}
        return gradientDisplayRT;
    }
    private void ApplyGradient()
    {
        if(!CanGradient||paintLayerRT==null||IsRegionSelecting)return;
        FinishActiveStroke(); BeginStrokeSnapshot(); DispatchGradient(paintLayerRT); gradientPending=false; EndStrokeApply(); Repaint();
    }

    private void PrepareRegionInput()
    {
        regionControlId=GUIUtility.GetControlID("LgcUVRegionInput".GetHashCode(),FocusType.Passive);
        paintSelectionControlId=GUIUtility.GetControlID("LgcPaintSelectionInput".GetHashCode(),FocusType.Passive);
    }
    private void CancelRegionInteraction()
    {
        if(regionControlId!=0&&GUIUtility.hotControl==regionControlId)GUIUtility.hotControl=0;
        CancelPaintSelectionDrag();
        regionDrag=0; hoveredIsland=-1; regionOverlayDirty=true;
    }
    private Vector2 PointUV(Vector2 p)=>viewController.GUIToUV(p,previewRect);
    private Vector2 PointGUI(Vector2 uv,Rect view)=>new Vector2(previewRect.x+(uv.x-view.x)/view.width*previewRect.width,previewRect.y+(view.yMax-uv.y)/view.height*previewRect.height);
    private void HandleRegionAndGradientInput()
    {
        Event e=Event.current; if(e==null||paintLayerRT==null)return;
        bool inside=previewRect.Contains(e.mousePosition);
        if(regionDrag!=0&&(GUIUtility.hotControl!=regionControlId||e.type==EventType.KeyDown&&e.keyCode==KeyCode.Escape)){CancelRegionInteraction();if(e.type!=EventType.Used)e.Use();Repaint();return;}
        if(IsRegionSelecting)
        {
            int hover=inside&&selectionMode==1?GetIslandIdAtUV_CPU(PointUV(e.mousePosition)):-1;
            if(hover!=hoveredIsland){hoveredIsland=hover;regionOverlayDirty=true;Repaint();}
            if(e.type==EventType.MouseDown&&e.button==0&&!e.alt&&inside&&GUIUtility.hotControl==0)
            {
                if(selectionMode==1)SelectIsland(hoveredIsland,e.shift,e.control||e.command);
                else{rectangleStart=rectangleEnd=PointUV(e.mousePosition);rectangleAdd=e.shift;rectangleSubtract=e.control||e.command;regionDrag=1;GUIUtility.hotControl=regionControlId;}
                e.Use();Repaint();return;
            }
        }
        else if(IsGradientEditing&&(!symmetryEnabled||symmetryLocked)&&inside&&e.type==EventType.MouseDown&&e.button==0&&!e.alt&&GUIUtility.hotControl==0)
        {
            Rect view=viewController.GetViewRect();
            bool end=(PointGUI(gradientEnd,view)-e.mousePosition).sqrMagnitude<144;
            bool start=(PointGUI(gradientStart,view)-e.mousePosition).sqrMagnitude<144;
            if(!start&&!end){gradientStart=PointUV(e.mousePosition);gradientEnd=gradientStart;gradientIsland=GetIslandIdAtUV_CPU(gradientStart);regionDrag=3;}
            else{regionDrag=end?3:2; if(start)gradientIsland=GetIslandIdAtUV_CPU(gradientStart);}
            suppressedStampPreview=null;
            GUIUtility.hotControl=regionControlId;gradientPending=true;gradientDirty=true;e.Use();Repaint();return;
        }
        if(regionDrag!=0&&e.type!=EventType.Used&&(e.type==EventType.MouseDrag||e.rawType==EventType.MouseUp))
        {
            Vector2 uv=PointUV(e.mousePosition);
            if(regionDrag==1)rectangleEnd=uv;
            else if(regionDrag==2){gradientStart=uv;gradientIsland=GetIslandIdAtUV_CPU(uv);gradientPending=true;gradientDirty=true;}
            else{gradientEnd=uv;gradientPending=true;gradientDirty=true;}
            if(e.rawType==EventType.MouseUp)
            {
                if(regionDrag==1)SelectRectangle(Rect.MinMaxRect(Mathf.Min(rectangleStart.x,rectangleEnd.x),Mathf.Min(rectangleStart.y,rectangleEnd.y),Mathf.Max(rectangleStart.x,rectangleEnd.x),Mathf.Max(rectangleStart.y,rectangleEnd.y)),rectangleAdd,rectangleSubtract);
                CancelRegionInteraction();
            }
            if(e.type!=EventType.Used)e.Use();Repaint();
        }
    }
    private void DrawGradientHandles(Rect view)
    {
        if(Event.current.type!=EventType.Repaint)return;
        if(regionDrag==1)
        {
            Vector2 a=PointGUI(rectangleStart,view),b=PointGUI(rectangleEnd,view);
            var r=Rect.MinMaxRect(Mathf.Min(a.x,b.x),Mathf.Min(a.y,b.y),Mathf.Max(a.x,b.x),Mathf.Max(a.y,b.y));
            using(new Handles.DrawingScope(Color.green)) Handles.DrawSolidRectangleWithOutline(r,new Color(.2f,1,.3f,.1f),Color.green);
        }
        if(!IsGradientEditing)return;
        using(new Handles.DrawingScope(Color.white))
        {
            GUI.BeginClip(previewRect);
            try
            {
                Vector2 a=PointGUI(gradientStart,view)-previewRect.position,b=PointGUI(gradientEnd,view)-previewRect.position;
                Handles.DrawLine(a,b); Handles.color=IsGradientErasing?Color.Lerp(Color.white,Color.red,gradientEraseStart):PaintGradient.Evaluate(0);Handles.DrawSolidDisc(a,Vector3.forward,6);Handles.color=IsGradientErasing?Color.Lerp(Color.white,Color.red,gradientEraseEnd):new Color(PaintGradient.Evaluate(1).r,PaintGradient.Evaluate(1).g,PaintGradient.Evaluate(1).b,1);Handles.DrawWireDisc(b,Vector3.forward,8);
            }finally{GUI.EndClip();}
        }
    }
    private void DrawRegionOverlay(Rect view)
    {
        if(Event.current.type!=EventType.Repaint||regionBuffer==null||paintLayerRT==null)return;
        if(regionOverlayRT==null){regionOverlayRT=NewRT(paintLayerRT.width,paintLayerRT.height);regionOverlayDirty=true;}
        if(lastRegionSelecting!=IsRegionSelecting){lastRegionSelecting=IsRegionSelecting;regionOverlayDirty=true;}
        if(regionOverlayDirty)
        {
            csPaint.SetTexture(kRegionOverlay,"_ResultRT",regionOverlayRT);csPaint.SetBuffer(kRegionOverlay,"_IslandId",islandIdBuffer);
            csPaint.SetInts("_TexSize",paintLayerRT.width,paintLayerRT.height);csPaint.SetInt("_HoveredIsland",IsRegionSelecting?hoveredIsland:-1);csPaint.SetInt("_ShowRegionSelection",IsRegionSelecting?1:0);
            csPaint.SetInt("_OnlyShowSelected",onlyShowSelected&&SelectedRegionPixels()>0?1:0);
            csPaint.Dispatch(kRegionOverlay,(paintLayerRT.width+15)/16,(paintLayerRT.height+15)/16,1);regionOverlayDirty=false;
        }
        GUI.DrawTextureWithTexCoords(previewRect,regionOverlayRT,view,true);
    }
    private void ReleaseTransientPreviews()
    {
        SafeReleaseRT(paintTransformLayerRT); SafeReleaseRT(paintTransformDisplayRT); SafeReleaseRT(paintSelectionOverlayRT);
        paintTransformLayerRT=null; paintTransformDisplayRT=null; paintSelectionOverlayRT=null; paintTransformDirty=true;
        SafeReleaseRT(stampPreviewLayerRT);SafeReleaseRT(stampPreviewRT);stampPreviewLayerRT=null;stampPreviewRT=null;cachedStampPreview=null;suppressedStampPreview=null;
        SafeReleaseRT(gradientLayerRT);SafeReleaseRT(gradientDisplayRT);SafeReleaseRT(regionOverlayRT);gradientLayerRT=null;gradientDisplayRT=null;regionOverlayRT=null;gradientDirty=true;regionOverlayDirty=true;
    }
    private void ReleaseRegionResources()
    {
        ReleasePaintSelectionResources(); selectedUvTriangles = null;
        ReleaseTransientPreviews();if(regionBuffer!=null)regionBuffer.Release();regionBuffer=null;regionState=null;selectedPixelCount=selectedHiddenCount=selectedDisabledCount=0;onlyShowSelected=onlyEditSelected=false;regionDirty=true;gradientIsland=-1;gradientPending=true;precisionLevel=requestedPrecision=0;
    }
}
#endif
