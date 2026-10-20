using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace AvantageMcp
{
    // Shared by the external bridge and the in-process writer. No COM or I/O.
    public static class LiveModelPlanPolicy
    {
        static readonly string[] PeakKeys={"index","label","area","area_ratio","centre_kinetic_ev","height","height_ratio","fwhm_ev","gaussian_lorentz","tail_mix","tail_height","tail_exponential","native_gl_combine_method","constraints","background"};
        static readonly string[] BackgroundKeys={"type","start_kinetic_ev","end_kinetic_ev","mode","points_to_average","step_ev","properties","extensions"};
        public static void Validate(Dictionary<string,object> plan)
        {
            try
            {
                Keys(plan,"plan","version","request_id","document_path","spectrum_index","expected_raw_sha256","expected_peaks","desired_peaks","source_peak_indices");
                Integer(plan["version"],1,1,"version");
                if(!Regex.IsMatch(Text(plan["request_id"],32,32,"request_id"),"\\A[0-9A-Fa-f]{32}\\z"))Invalid("request_id");
                string path=Text(plan["document_path"],1,32767,"document_path");
                if(!Regex.IsMatch(path,@"^(?:[A-Za-z]:[\\/]|\\\\[^\\/]+\\[^\\/]+\\)")||!string.Equals(Path.GetExtension(path),".vgp",StringComparison.OrdinalIgnoreCase))Invalid("document_path");
                Integer(plan["spectrum_index"],0,4095,"spectrum_index");
                if(!Regex.IsMatch(Text(plan["expected_raw_sha256"],64,64,"expected_raw_sha256"),"\\A[0-9A-Fa-f]{64}\\z"))Invalid("expected_raw_sha256");
                var expected=Array(plan["expected_peaks"],100,"expected_peaks");
                var desired=Array(plan["desired_peaks"],100,"desired_peaks");
                ValidatePeaks(expected,false);ValidatePeaks(desired,true);
                var sources=Array(plan["source_peak_indices"],100,"source_peak_indices");
                if(sources.Length!=desired.Length)Invalid("source_peak_indices.count");
                int previous=-1;bool newSuffix=false;
                foreach(var value in sources)
                {
                    if(value==null){newSuffix=true;continue;}
                    int index=Integer(value,0,expected.Length-1,"source_peak_indices.index");
                    if(newSuffix||index<=previous)Invalid("source_peak_indices.order");previous=index;
                }
            }
            catch(InvalidOperationException error)
            {if(error.Message.StartsWith("LIVE_PLAN_INVALID",StringComparison.Ordinal))throw;Invalid("shape");}
            catch(ArgumentException){Invalid("shape");}
            catch(OverflowException){Invalid("number");}
            catch(InvalidCastException){Invalid("shape");}
            catch(FormatException){Invalid("number");}
            catch(KeyNotFoundException){Invalid("missing-field");}
        }
        static void ValidatePeaks(object[] peaks,bool desired)
        {
            var graph=new List<int>[peaks.Length];object common=null;
            for(int index=0;index<peaks.Length;index++)
            {
                graph[index]=new List<int>();var peak=Record(peaks[index],"peak");Keys(peak,"peak",PeakKeys);
                Integer(peak["index"],index,index,"peak.index");Text(peak["label"],1,256,"peak.label");
                foreach(string key in new[]{"area","area_ratio","height","height_ratio","tail_height","tail_exponential"})Number(peak[key],0,1e15,false,key);
                Number(peak["centre_kinetic_ev"],-1e6,1e6,false,"centre_kinetic_ev");
                Number(peak["fwhm_ev"],0,1e5,true,"fwhm_ev");
                Number(peak["gaussian_lorentz"],0,1,false,"gaussian_lorentz");Number(peak["tail_mix"],0,1,false,"tail_mix");
                Integer(peak["native_gl_combine_method"],0,2,"native_gl_combine_method");
                var attributes=new HashSet<int>();
                foreach(object item in Array(peak["constraints"],8,"constraints"))
                {
                    var c=Record(item,"constraint");Keys(c,"constraint","attribute","type","value1","value2","value3","operator","reference_peak_index");
                    if(!attributes.Add(Integer(c["attribute"],1,8,"constraint.attribute")))Invalid("constraint.duplicate-attribute");
                    int type=Integer(c["type"],1,8,"constraint.type"),op=Integer(c["operator"],0,4,"constraint.operator");
                    foreach(string key in new[]{"value1","value2","value3"})Number(c[key],-1e15,1e15,false,"constraint."+key);
                    bool linked=type==5||type==7||type==8;
                    if(linked)
                    {
                        int target=Integer(c["reference_peak_index"],0,peaks.Length-1,"constraint.reference");
                        if(target==index||op==0)Invalid("constraint.link");
                        if(op==4&&Convert.ToDouble(c["value1"],CultureInfo.InvariantCulture)==0)Invalid("constraint.division-zero");
                        graph[index].Add(target);
                    }
                    else if(c["reference_peak_index"]!=null||op!=0)Invalid("constraint.unlinked");
                }
                object background=peak["background"];
                if(background==null){if(desired)Invalid("background.required");}
                else
                {
                    ValidateBackground(Record(background,"background"));
                    if(desired){if(common==null)common=background;else if(!Same(common,background,true))Invalid("background.mixed");}
                }
            }
            var state=new byte[peaks.Length];for(int i=0;i<peaks.Length;i++)Visit(i,graph,state);
        }
        static void Visit(int index,List<int>[] graph,byte[] state)
        {if(state[index]==1)Invalid("constraint.cycle");if(state[index]==2)return;state[index]=1;foreach(int target in graph[index])Visit(target,graph,state);state[index]=2;}
        static void ValidateBackground(IDictionary<string,object> bg)
        {
            Keys(bg,"background",BackgroundKeys);int type=Integer(bg["type"],0,4,"background.type");
            double start=Number(bg["start_kinetic_ev"],-1e6,1e6,false,"background.start"),end=Number(bg["end_kinetic_ev"],-1e6,1e6,false,"background.end");
            if(start>=end)Invalid("background.bounds");Integer(bg["mode"],0,1,"background.mode");Integer(bg["points_to_average"],0,100,"background.average");
            Number(bg["step_ev"],0,1e6,true,"background.step");Text(bg["properties"],0,8192,"background.properties");
            var ext=Record(bg["extensions"],"background.extensions");
            if(type==1)
            {
                Keys(ext,"shirley","shirley_iterations","shirley_convergence","shirley_smooth");
                Integer(ext["shirley_iterations"],1,10000,"shirley.iterations");Number(ext["shirley_convergence"],0,1e15,true,"shirley.convergence");Integer(ext["shirley_smooth"],0,1,"shirley.smooth");
            }
            else if(type==2)
            {
                Keys(ext,"tougaard","tougaard_y_offset","tougaard_type","tougaard_b","tougaard_c","tougaard_d","tougaard_energy_correction");
                Number(ext["tougaard_y_offset"],-1e15,1e15,false,"tougaard.offset");Integer(ext["tougaard_type"],0,1,"tougaard.type");
                Number(ext["tougaard_b"],0,1e15,false,"tougaard.b");Number(ext["tougaard_c"],0,1e15,true,"tougaard.c");Number(ext["tougaard_d"],0,1e15,false,"tougaard.d");Integer(ext["tougaard_energy_correction"],0,1,"tougaard.correction");
            }
            else Keys(ext,"background.extensions");
        }
        static IDictionary<string,object> Record(object value,string field)
        {var result=value as IDictionary<string,object>;if(result==null)Invalid(field);return result;}
        static void Keys(IDictionary<string,object> value,string field,params string[] keys)
        {if(value==null||value.Count!=keys.Length||keys.Any(key=>!value.ContainsKey(key)))Invalid(field+".fields");}
        static object[] Array(object value,int maximum,string field)
        {
            var sequence=value as IEnumerable;
            if(sequence==null||value is string||value is IDictionary)Invalid(field+".array");
            var result=sequence.Cast<object>().Take(maximum+1).ToArray();if(result.Length>maximum)Invalid(field+".count");return result;
        }
        static string Text(object value,int minimum,int maximum,string field)
        {var text=value as string;if(text==null||text.Length<minimum||text.Length>maximum||text.IndexOf('\0')>=0)Invalid(field);return text;}
        static bool Numeric(object value)
        {return value is byte||value is sbyte||value is short||value is ushort||value is int||value is uint||value is long||value is ulong||value is float||value is double||value is decimal;}
        static double Number(object value,double minimum,double maximum,bool positive,string field)
        {
            if(!Numeric(value))Invalid(field);double number=Convert.ToDouble(value,CultureInfo.InvariantCulture);
            if(double.IsNaN(number)||double.IsInfinity(number)||number<minimum||number>maximum||(positive&&number==minimum))Invalid(field);return number;
        }
        static int Integer(object value,int minimum,int maximum,string field)
        {
            double number=Number(value,minimum,maximum,false,field);
            if(Math.Truncate(number)!=number||(value is decimal&&decimal.Truncate((decimal)value)!=(decimal)value))Invalid(field);return (int)number;
        }
        static bool Same(object first,object second,bool ignoreProperties)
        {
            var a=first as IDictionary<string,object>;var b=second as IDictionary<string,object>;
            if(a!=null&&b!=null)return a.Count==b.Count&&a.All(p=>(ignoreProperties&&p.Key=="properties")||(b.ContainsKey(p.Key)&&Same(p.Value,b[p.Key],ignoreProperties)));
            if(Numeric(first)&&Numeric(second))return Convert.ToDouble(first,CultureInfo.InvariantCulture)==Convert.ToDouble(second,CultureInfo.InvariantCulture);
            return object.Equals(first,second);
        }
        static void Invalid(string field){throw new InvalidOperationException("LIVE_PLAN_INVALID: "+field);}
    }
}
