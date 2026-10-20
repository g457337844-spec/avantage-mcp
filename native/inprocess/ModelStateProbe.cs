using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using VGDATASTORELib;

namespace AvantageMcp.InProcess
{
    [ComVisible(true)]
    [Guid("96D0A0F0-56FC-4305-92B0-0BA9CB776AB4")]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class ModelStateProbe : ContextProbe
    {
        protected override Dictionary<string,object> CollectContext(IntPtr processing,bool gridAvailable,
            int inputCount,IntPtr inputs,IntPtr axes,int flags)
        {
            var report=base.CollectContext(processing,gridAvailable,inputCount,inputs,axes,flags);
            report["native_identity"]=NativeContextIdentity.Inspect(processing);
            report["model_state"]=NativeContextIdentity.WithDocument(processing,Read);
            return report;
        }
        static Dictionary<string,object> Read(IntPtr nativeDocument,IntPtr document)
        {
            var data=NativeDocumentAccess.DataSpaces(document);
            try
            {
                if(data.Count!=24)throw new InvalidOperationException("Model probe requires fixed test graph");
                var source=(IDataSpace)Marshal.GetUniqueObjectForIUnknown(data[23]);
                var peaks=NativeDocumentAccess.Peaks(document,data[23]);
                try
                {
                    string raw=NativeRawFingerprint.Fingerprint(source);
                    var snapshot=NativeModelSnapshotCore.ReadPeaks(peaks);
                    string json=new JavaScriptSerializer{MaxJsonLength=1024*1024}.Serialize(NativeModelSnapshotCore.PersistedModelParameters(snapshot));
                    string model;using(var sha=SHA256.Create())model=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(json))).Replace("-","");
                    return new Dictionary<string,object>{{"spectrum_index",23},{"peak_count",peaks.Count},{"raw_sha256",raw},{"model_sha256",model}};
                }
                finally{Marshal.ReleaseComObject(source);foreach(var p in peaks)Marshal.ReleaseComObject(p);}
            }
            finally{foreach(var p in data)Marshal.Release(p);}
        }
    }
}
