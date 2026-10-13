using System;
using System.Collections.Generic;
using System.IO;

namespace AvantageMcp {
public sealed partial class Engine {
    object ExportPeakTable(SpectrumSession s,string path,bool overwrite) {
        var model=Model(s);
        if(s.BackgroundState==null)throw new Fault("BACKGROUND_NOT_DEFINED","Set an explicit background before exporting an Avantage peak table. Use simple for a zero baseline.");
        path=Target(s,path,".vgt",overwrite);
        string temp=Path.Combine(Path.GetDirectoryName(path),"."+Guid.NewGuid().ToString("N")+".vgt");
        try {
            var result=NativePeakTablePersistence.Export(temp,s.Data,model,s.BackgroundState);
            Publish(temp,path,overwrite);result["path"]=path;result["session_id"]=s.Id;return result;
        }finally{if(File.Exists(temp))File.Delete(temp);}
    }
    object InspectPeakTable(SpectrumSession s,string path) {
        path=PeakTableInput(path);
        var result=NativePeakTablePersistence.Inspect(path,s.Data);result["session_id"]=s.Id;return result;
    }
    object ImportPeakTable(SpectrumSession s,string path) {
        path=PeakTableInput(path);NativeBackground background;var replacement=NativePeakTablePersistence.LoadModel(path,s,out background);
        try {
            var info=replacement.Info();var bgInfo=background.Info();
            if(s.FitState is IDisposable)((IDisposable)s.FitState).Dispose();if(s.BackgroundState!=null)s.BackgroundState.Dispose();
            s.FitState=replacement;s.BackgroundState=background;replacement=null;background=null;
            return J.Obj("session_id",s.Id,"path",path,"model",info,"background",bgInfo,"optimization_executed",false,"note","Native peak parameters and constraints loaded. VGT contains no verifiable optimizer execution report.");
        }finally{if(replacement!=null)replacement.Dispose();if(background!=null)background.Dispose();}
    }
    static string PeakTableInput(string path) {
        path=Path.GetFullPath(path);
        if(!String.Equals(Path.GetExtension(path),".vgt",StringComparison.OrdinalIgnoreCase))throw new Fault("UNSUPPORTED_FORMAT","Expected an Avantage .VGT peak table.");
        if(!File.Exists(path))throw new Fault("FILE_NOT_FOUND",path);
        if(new FileInfo(path).Length>256*1024*1024)throw new Fault("FILE_TOO_LARGE","Peak tables are limited to 256 MiB.");
        return path;
    }
}
}
