using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using VGDATASTORELib;

namespace AvantageMcp {
public sealed partial class Engine {
    void InvalidateLiveLinks(string documentId) {
        foreach(var session in sessions.Values)if(session.LiveLink!=null&&session.LiveLink.DocumentId==documentId) {
            session.LiveLink.Dispose();session.LiveLink.Stale=true;
        }
    }
    static void VerifyGridVisibility(Dictionary<string,object> grid,IList<Dictionary<string,object>> document) {
        var pairs=(int[][])grid["grid_data_pairs"];
        if((int)grid["spectrum_count"]!=document.Count)throw new Fault("LIVE_GRID_MISMATCH","The saved grid has a different data list.");
        for(int index=0;index<document.Count;index++)if(pairs.Any(pair=>pair[1]==index)!=(bool)document[index]["visible_in_grid"])
            throw new Fault("LIVE_GRID_MISMATCH","A saved grid cell is not bound to its document spectrum. Reopen a healthy project before applying edits.");
    }
    static List<Dictionary<string,object>> CaptureLiveDocument(ProcessingDocument document,IList<IntPtr> data) {
        var result=new List<Dictionary<string,object>>();
        IntPtr grid=document.Grid();
        try {
        foreach(var pointer in data) {
            var source=(IDataSpace)Marshal.GetObjectForIUnknown(pointer);var peaks=ProcessingCom.Peaks(document.Pointer,pointer);
            try{IntPtr visual;ProcessingCom.At<ProcessingCom.FindVisual>(grid,38)(grid,pointer,out visual);bool visible=visual!=IntPtr.Zero;if(visual!=IntPtr.Zero)Marshal.Release(visual);result.Add(J.Obj("data",NativeSnapshot.Fingerprint(source),"model",NativePeakTablePersistence.ReadPeaks(peaks),"visible_in_grid",visible));}
            finally{ProcessingCom.ReleasePeaks(peaks);Marshal.ReleaseComObject(source);}
        }
        }finally{Marshal.Release(grid);}
        return result;
    }
}
}
