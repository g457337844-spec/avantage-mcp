using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace AvantageMcp {
public sealed partial class Engine {
    Dictionary<string,object> EvaluateModel(SpectrumSession s,bool asymmetric) {
        var model=Model(s);
        if(model.Peaks.Count==0)throw new Fault("MODEL_EMPTY","Add at least one peak before evaluating a model.");
        if((long)s.X.Length*model.Peaks.Count>2000000)throw new Fault("MODEL_SIZE","Curve evaluation is limited to two million component data points.");
        var result=(Dictionary<string,object>)model.Evaluate(s.X,asymmetric);
        var sum=(double[])result["peak_sum"];
        var total=new double[sum.Length];var residual=new double[sum.Length];
        int first=s.BackgroundState==null?0:s.BackgroundState.FirstIndex,last=s.BackgroundState==null?sum.Length-1:s.BackgroundState.LastIndex;
        double squares=0;
        for(int i=0;i<sum.Length;i++) {
            total[i]=sum[i]+(s.BackgroundState==null?0:s.BackgroundState.Curve[i]);
            residual[i]=s.Y[i]-total[i];
            if(i>=first&&i<=last)squares+=residual[i]*residual[i];
            if(Double.IsNaN(sum[i])||Double.IsInfinity(sum[i])||Double.IsNaN(total[i])||Double.IsInfinity(total[i])||Double.IsNaN(residual[i])||Double.IsInfinity(residual[i])||Double.IsNaN(squares)||Double.IsInfinity(squares))
                throw new Fault("NUMERICAL_OVERFLOW","Model amplitudes or residuals exceed the supported numerical range. Reduce peak amplitudes before evaluating or fitting.");
        }
        result["total_model"]=total;result["residual"]=residual;result["first_index"]=first;result["last_index"]=last;
        result["metrics"]=J.Obj("residual_sum_squares",squares,"rmse",Math.Sqrt(squares/(last-first+1)),"point_count",last-first+1);
        return result;
    }
    object GetModelCurve(SpectrumSession s,Dictionary<string,object>a) {
        int offset=J.Int(a,"offset",0),limit=J.Int(a,"limit",1000);
        if(offset<0||offset>s.X.Length||limit<1||limit>10000)throw new Fault("INVALID_ARGUMENT","offset must be in range and limit must be 1–10000.");
        var model=Model(s);
        if((long)limit*(model.Peaks.Count+6)>250000)throw new Fault("RESPONSE_SIZE","Choose a smaller page limit for a model with many peaks.");
        int count=Math.Min(limit,s.X.Length-offset);var full=EvaluateModel(s,J.Bool(a,"asymmetric",false));
        int first=(int)full["first_index"],last=(int)full["last_index"];
        var components=((List<object>)full["components"]).Cast<Dictionary<string,object>>().Select(c=>J.Obj("peak_id",c["peak_id"],"y",((double[])c["y"]).Skip(offset).Take(count).ToArray())).ToArray();
        Func<double[],object[]> region=values=>Enumerable.Range(offset,count).Select(i=>i<first||i>last?null:(object)values[i]).ToArray();
        return J.Obj("session_id",s.Id,"offset",offset,"count",count,"total",s.X.Length,
            "binding_energy",s.BindingEnergy.Skip(offset).Take(count).ToArray(),"intensity",s.Y.Skip(offset).Take(count).ToArray(),
            "components",components,"peak_sum",((double[])full["peak_sum"]).Skip(offset).Take(count).ToArray(),
            "background",s.BackgroundState==null?null:region(s.BackgroundState.Curve),"total_model",region((double[])full["total_model"]),
            "residual",region((double[])full["residual"]),"metrics",full["metrics"],"asymmetric",full["asymmetric"],
            "engine",full["engine"],"next_offset",offset+count<s.X.Length?(object)(offset+count):null);
    }
    object ExportModelCurves(SpectrumSession s,Dictionary<string,object>a) {
        string format=J.Str(a,"format","csv");if(format!="csv"&&format!="json")throw new Fault("INVALID_ARGUMENT","format must be csv or json.");
        bool overwrite=J.Bool(a,"overwrite");string path=Target(s,J.Str(a,"path"),"."+format,overwrite);
        var full=EvaluateModel(s,J.Bool(a,"asymmetric",false));
        var components=((List<object>)full["components"]).Cast<Dictionary<string,object>>().ToArray();
        int first=(int)full["first_index"],last=(int)full["last_index"];
        string temp=Path.Combine(Path.GetDirectoryName(path),"."+Guid.NewGuid().ToString("N")+".tmp");
        try {
            if(format=="json") {
                var json=new JavaScriptSerializer{MaxJsonLength=128*1024*1024};
                full["spectrum"]=s.Info();full["model"]=Model(s).Info();full["kinetic_energy"]=s.X;full["binding_energy"]=s.BindingEnergy;full["intensity"]=s.Y;
                Func<double[],object[]> masked=values=>Enumerable.Range(0,values.Length).Select(i=>i<first||i>last?null:(object)values[i]).ToArray();
                full["background"]=s.BackgroundState==null?null:masked(s.BackgroundState.Curve);
                full["total_model"]=masked((double[])full["total_model"]);full["residual"]=masked((double[])full["residual"]);
                full["valid_model_region"]=J.Obj("first_index",first,"last_index",last);
                File.WriteAllText(temp,json.Serialize(full),new UTF8Encoding(false));
            } else using(var writer=new StreamWriter(temp,false,new UTF8Encoding(true))) {
                writer.WriteLine("binding_energy_eV,kinetic_energy_eV,intensity,background,total_model,residual,"+String.Join(",",components.Select(c=>"peak_"+c["peak_id"])));
                for(int i=0;i<s.X.Length;i++) {
                    bool valid=i>=first&&i<=last;
                    var values=new List<string>{Number(s.BindingEnergy[i]),Number(s.X[i]),Number(s.Y[i]),
                        !valid||s.BackgroundState==null?"":Number(s.BackgroundState.Curve[i]),
                        valid?Number(((double[])full["total_model"])[i]):"",valid?Number(((double[])full["residual"])[i]):""};
                    values.AddRange(components.Select(c=>Number(((double[])c["y"])[i])));writer.WriteLine(String.Join(",",values));
                }
            }
            Publish(temp,path,overwrite);
        } finally {if(File.Exists(temp))File.Delete(temp);}
        return J.Obj("session_id",s.Id,"path",path,"format",format,"point_count",s.X.Length,"peak_count",components.Length,"metrics",full["metrics"]);
    }
    static string Number(double n){return n.ToString("R",CultureInfo.InvariantCulture);}
}
}
