using System;
using System.Collections.Generic;
using AvantageMcp;

class LiveModelPlanPolicyTests
{
    static Dictionary<string,object> Background(){return new Dictionary<string,object>{{"type",1},{"start_kinetic_ev",500.0},{"end_kinetic_ev",550.0},{"mode",0},{"points_to_average",2},{"step_ev",0.1},{"properties","Shirley"},{"extensions",new Dictionary<string,object>{{"shirley_iterations",100},{"shirley_convergence",1.0},{"shirley_smooth",0}}}};}
    static Dictionary<string,object> Peak(int index){return new Dictionary<string,object>{{"index",index},{"label","Cu component"},{"area",100.0},{"area_ratio",1.0},{"centre_kinetic_ev",525.0},{"height",10.0},{"height_ratio",1.0},{"fwhm_ev",1.5},{"gaussian_lorentz",0.3},{"tail_mix",0.0},{"tail_height",0.0},{"tail_exponential",0.0},{"native_gl_combine_method",0},{"constraints",new object[0]},{"background",Background()}};}
    static Dictionary<string,object> Link(int reference){return new Dictionary<string,object>{{"attribute",2},{"type",5},{"value1",1.0},{"value2",0.0},{"value3",0.0},{"operator",1},{"reference_peak_index",reference}};}
    static Dictionary<string,object> Plan(){return new Dictionary<string,object>{{"version",1},{"request_id",new string('A',32)},{"document_path",@"E:\test\2.vgp"},{"spectrum_index",23},{"expected_raw_sha256",new string('A',64)},{"expected_peaks",new object[]{Peak(0),Peak(1)}},{"desired_peaks",new object[]{Peak(0),Peak(1),Peak(2)}},{"source_peak_indices",new object[]{0,1,null}}};}
    static int Main(string[] args){
        var plan=Plan();var peaks=(object[])plan["desired_peaks"];var peak=(Dictionary<string,object>)peaks[0];var bg=(Dictionary<string,object>)peak["background"];var ext=(Dictionary<string,object>)bg["extensions"];
        var link=Link(1);
        switch(args[0]){
            case "valid":break;
            case "valid-empty-source":plan["expected_peaks"]=new object[0];plan["source_peak_indices"]=new object[]{null,null,null};break;
            case "valid-clear":plan["desired_peaks"]=new object[0];plan["source_peak_indices"]=new object[0];break;
            case "source-wrong-count":plan["source_peak_indices"]=new object[]{0,1};break;
            case "source-duplicate":plan["source_peak_indices"]=new object[]{0,0,null};break;
            case "source-reversed":plan["source_peak_indices"]=new object[]{1,0,null};break;
            case "source-new-in-middle":plan["source_peak_indices"]=new object[]{0,null,1};break;
            case "source-outside":plan["source_peak_indices"]=new object[]{0,1,2};break;
            case "request-id-malformed":plan["request_id"]=new string('z',32);break;
            case "valid-unc":plan["document_path"]=@"\\server\share\2.vgp";break;
            case "valid-linked":peak["constraints"]=new object[]{link};break;
            case "valid-description-difference":bg["properties"]="Different description only";break;
            case "valid-old-missing-background":((Dictionary<string,object>)((object[])plan["expected_peaks"])[0])["background"]=null;break;
            case "null-plan":plan=null;break;
            case "missing-top":plan.Remove("expected_peaks");break;
            case "extra-top":plan["command"]="anything";break;
            case "version-fraction":plan["version"]=1.5;break;
            case "integer-decimal-fraction":plan["spectrum_index"]=1.0000000000000000000000000001m;break;
            case "integer-string":plan["version"]="1";break;
            case "integer-bool":plan["version"]=true;break;
            case "path-relative":plan["document_path"]=@"test\2.vgp";break;
            case "path-drive-relative":plan["document_path"]="C:2.vgp";break;
            case "path-url":plan["document_path"]="https://server/2.vgp";break;
            case "path-wrong-ext":plan["document_path"]=@"E:\test\2.exe";break;
            case "path-null":plan["document_path"]="E:\\test\0\\2.vgp";break;
            case "bad-hash":plan["expected_raw_sha256"]=new string('z',64);break;
            case "negative-index":plan["spectrum_index"]=-1;break;
            case "large-index":plan["spectrum_index"]=4096;break;
            case "dictionary-array":plan["desired_peaks"]=new Dictionary<string,object>();break;
            case "string-array":plan["desired_peaks"]="peaks";break;
            case "null-array":plan["desired_peaks"]=null;break;
            case "too-many-peaks":var many=new object[101];for(int i=0;i<many.Length;i++)many[i]=Peak(i);plan["desired_peaks"]=many;break;
            case "missing-peak-field":peak.Remove("area");break;
            case "extra-peak-field":peak["dll"]="anything";break;
            case "bad-peak-index":peak["index"]=1;break;
            case "empty-label":peak["label"]="";break;
            case "long-label":peak["label"]=new string('x',257);break;
            case "label-null":peak["label"]="Cu\0component";break;
            case "number-string":peak["height"]="5";break;
            case "number-bool":peak["height"]=true;break;
            case "number-nan":peak["height"]=double.NaN;break;
            case "number-infinity":peak["height"]=double.PositiveInfinity;break;
            case "negative-height":peak["height"]=-1.0;break;
            case "zero-width":peak["fwhm_ev"]=0.0;break;
            case "bad-gl":peak["gaussian_lorentz"]=1.1;break;
            case "no-background":peak["background"]=null;break;
            case "mixed-background":bg["end_kinetic_ev"]=560.0;break;
            case "bg-bounds":bg["start_kinetic_ev"]=560.0;break;
            case "bg-zero-step":bg["step_ev"]=0;break;
            case "extra-bg-field":bg["path"]="anything";break;
            case "missing-extension":ext.Remove("shirley_smooth");break;
            case "extra-extension":ext["other"]=1;break;
            case "bad-extension-number":ext["shirley_iterations"]="100";break;
            case "constraint-self":link["reference_peak_index"]=0;peak["constraints"]=new object[]{link};break;
            case "constraint-outside":link["reference_peak_index"]=4;peak["constraints"]=new object[]{link};break;
            case "constraint-null":link["reference_peak_index"]=null;peak["constraints"]=new object[]{link};break;
            case "constraint-cycle":peak["constraints"]=new object[]{link};((Dictionary<string,object>)peaks[1])["constraints"]=new object[]{Link(0)};break;
            case "constraint-duplicate":peak["constraints"]=new object[]{link,Link(2)};break;
            case "constraint-zero-operator":link["operator"]=0;peak["constraints"]=new object[]{link};break;
            case "constraint-division-zero":link["operator"]=4;link["value1"]=0.0;peak["constraints"]=new object[]{link};break;
            case "constraint-unlinked-reference":link["type"]=1;peak["constraints"]=new object[]{link};break;
            case "constraint-bool":link["attribute"]=true;peak["constraints"]=new object[]{link};break;
            case "bad-old-model":((Dictionary<string,object>)((object[])plan["expected_peaks"])[0])["fwhm_ev"]=0;break;
            default:Console.WriteLine("UNKNOWN_CASE");return 2;
        }
        try{LiveModelPlanPolicy.Validate(plan);Console.WriteLine("OK");return 0;}
        catch(InvalidOperationException e){Console.WriteLine(e.Message);return 0;}
        catch(Exception e){Console.WriteLine("UNEXPECTED:"+e.GetType().Name);return 1;}
    }
}
