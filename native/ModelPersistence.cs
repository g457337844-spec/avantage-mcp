using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace AvantageMcp {
public sealed partial class Engine {
    static string SpectrumFingerprint(SpectrumSession s) {
        using(var hash=SHA256.Create()) {
            var metadata=Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(J.Obj("axes",s.Axes,"raw_energy_scale",s.EnergyScale,"source_energy_ev",s.SourceEnergy)));
            hash.TransformBlock(metadata,0,metadata.Length,metadata,0);
            foreach(var values in new[]{s.X,s.Y,s.BindingEnergy??new double[0]}) {
                var count=BitConverter.GetBytes(values.Length);hash.TransformBlock(count,0,count.Length,count,0);
                var bytes=new byte[values.Length*sizeof(double)];Buffer.BlockCopy(values,0,bytes,0,bytes.Length);
                hash.TransformBlock(bytes,0,bytes.Length,bytes,0);
            }
            hash.TransformFinalBlock(new byte[0],0,0);
            return BitConverter.ToString(hash.Hash).Replace("-","");
        }
    }
    object ExportFitModel(SpectrumSession s,string path,bool overwrite) {
        var m=Model(s);path=Target(s,path,".json",overwrite);
        var record=J.Obj("format","avantage-mcp-fit-model","schema_version",1,
            "created_utc",DateTime.UtcNow.ToString("O"),"spectrum_fingerprint",SpectrumFingerprint(s),
            "spectrum",s.Info(),"background",s.BackgroundState==null?null:s.BackgroundState.Info(),
            "model",m.Info(),"note","A model recipe containing native peak parameters. This file is imported by Avantage MCP; it is not an Avantage .VGT file.");
        var json=new JavaScriptSerializer{MaxJsonLength=16*1024*1024,RecursionLimit=64};
        string temporary=Path.Combine(Path.GetDirectoryName(path),"."+Guid.NewGuid().ToString("N")+".json");
        try {File.WriteAllText(temporary,json.Serialize(record),new UTF8Encoding(false));Publish(temporary,path,overwrite);}
        finally {if(File.Exists(temporary))File.Delete(temporary);}
        return J.Obj("session_id",s.Id,"path",path,"format","avantage-mcp-fit-model","peak_count",m.Peaks.Count);
    }
    object ImportFitModel(SpectrumSession s,string path,bool allowDifferentSpectrum,bool preserveIds=false) {
        path=Path.GetFullPath(path);
        if(!File.Exists(path))throw new Fault("FILE_NOT_FOUND",path);
        if(new FileInfo(path).Length>16*1024*1024)throw new Fault("FILE_TOO_LARGE","Model recipes are limited to 16 MiB.");
        var json=new JavaScriptSerializer{MaxJsonLength=16*1024*1024,RecursionLimit=64};
        var record=json.Deserialize<Dictionary<string,object>>(File.ReadAllText(path,Encoding.UTF8));
        if(record==null||J.Str(record,"format","")!="avantage-mcp-fit-model"||J.Int(record,"schema_version",0)!=1)
            throw new Fault("UNSUPPORTED_FORMAT","Expected an Avantage MCP fit-model recipe with schema version 1.");
        bool same=String.Equals(J.Str(record,"spectrum_fingerprint",""),SpectrumFingerprint(s),StringComparison.OrdinalIgnoreCase);
        if(!same&&!allowDifferentSpectrum)throw new Fault("SPECTRUM_MISMATCH","Model spectrum differs from this session. To intentionally reuse parameters, set allow_different_spectrum=true.");
        var savedModel=ArgumentObject(record,"model");object entries;
        if(!savedModel.TryGetValue("peaks",out entries)||!(entries is IList))throw new Fault("INVALID_MODEL","Recipe peaks must be an array.");
        var peaks=(IList)entries;
        if(peaks.Count>100)throw new Fault("INVALID_MODEL","A model may contain at most 100 peaks.");
        NativeBackground background=null;var replacement=new NativePeakModel(s.Data);
        try {
            object bg;
            if(record.TryGetValue("background",out bg)&&bg!=null)background=NativeBackground.Create(s,ArgumentObject(record,"background"));
            var ids=new Dictionary<string,string>();
            foreach(object raw in peaks) {
                var old=raw as Dictionary<string,object>;if(old==null)throw new Fault("INVALID_MODEL","Every peak must be an object.");
                string oldId=J.Str(old,"peak_id");if(ids.ContainsKey(oldId))throw new Fault("INVALID_MODEL","Peak IDs must be unique.");
                var spec=new Dictionary<string,object>();
                foreach(string key in new[]{"label","height","fwhm_ev","gaussian_lorentz","area","area_ratio","height_ratio","tail_mix","tail_height","tail_exponential","gl_combine_method"})
                    if(old.ContainsKey(key))spec[key]=old[key];
                spec["centre_ev"]=J.Num(old,"centre_binding_ev");spec["energy_scale"]="binding";
                spec["label"]=J.Str(old,"label");spec["height"]=J.Num(old,"height");spec["fwhm_ev"]=J.Num(old,"fwhm_ev");
                if(!spec.ContainsKey("gl_combine_method")&&old.ContainsKey("native_gl_combine_method")) {
                    int method=J.Int(old,"native_gl_combine_method",0);
                    if(method<0||method>2)throw new Fault("INVALID_MODEL","Unsupported native G/L combination method.");
                    spec["gl_combine_method"]=new[]{"product","sum","convolution"}[method];
                }
                var created=(Dictionary<string,object>)replacement.Add(spec);string newId=J.Str(created,"peak_id");
                if(preserveIds){replacement.Find(newId).Id=oldId;newId=oldId;}
                ids.Add(oldId,newId);
            }
            if(background!=null)replacement.AttachBackground(background.Background,typeof(IAvantageBackground));
            foreach(object raw in peaks) {
                var old=(Dictionary<string,object>)raw;object constraints;
                if(!old.TryGetValue("constraints",out constraints)||constraints==null)continue;
                var list=constraints as Dictionary<string,object>;if(list==null)throw new Fault("INVALID_MODEL","Peak constraints must be an object.");
                foreach(var item in list) {
                    var source=item.Value as Dictionary<string,object>;if(source==null)throw new Fault("INVALID_MODEL","Constraint specification must be an object.");
                    var spec=new Dictionary<string,object>(source);spec.Remove("native");
                    if(spec.ContainsKey("reference_peak_id")) {
                        string oldReference=J.Str(spec,"reference_peak_id"),newReference;
                        if(!ids.TryGetValue(oldReference,out newReference))throw new Fault("INVALID_MODEL","A linked constraint refers to a missing peak.");
                        spec["reference_peak_id"]=newReference;
                    }
                    replacement.Constraint(ids[J.Str(old,"peak_id")],item.Key,spec);
                }
            }
            replacement.Info();replacement.Evaluate(s.X,false);
            if(s.FitState is IDisposable)((IDisposable)s.FitState).Dispose();if(s.BackgroundState!=null)s.BackgroundState.Dispose();
            s.FitState=replacement;s.BackgroundState=background;replacement=null;background=null;
            return J.Obj("session_id",s.Id,"spectrum_matches",same,"model",Model(s).Info(),"background",s.BackgroundState==null?null:s.BackgroundState.Info());
        } finally {if(replacement!=null)replacement.Dispose();if(background!=null)background.Dispose();}
    }
}
}
