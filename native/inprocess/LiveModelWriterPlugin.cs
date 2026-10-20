using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace AvantageMcp.InProcess
{
    [ComVisible(true)]
    [Guid("96D0A0F0-56FC-4305-92B0-0BA9CB776AB6")]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class LiveModelWriterPlugin : ContextProbe
    {
        string phase,token,verifiedHash;
        Dictionary<string,object> plan;
        Exception validationError;
        protected override bool ValidateRequest(IntPtr storage)
        {
            foreach(string value in new[]{"apply","commit","rollback","inspect","abandon"})
            {
                string marker=value=="inspect"?"inspect-live-editor-v1":value+"-region-model-v1";
                if(HasMarker(storage,"AvantageMcp.ProbeRequest",marker)){phase=value;break;}
            }
            if(phase==null)return false;
            try
            {
                if(phase=="apply")
                {
                    var json=new JavaScriptSerializer{MaxJsonLength=512*1024,RecursionLimit=64};
                    plan=json.Deserialize<Dictionary<string,object>>(Read(storage,"AvantageMcp.ModelPlan",512*1024));
                    LiveModelPlanPolicy.Validate(plan);
                }
                else if(phase!="inspect")
                {
                    token=Read(storage,"AvantageMcp.Transaction",64).Trim();
                    if(!Regex.IsMatch(token,"\\A[0-9a-fA-F]{32}\\z"))throw new InvalidOperationException("LIVE_TRANSACTION_TOKEN_INVALID");
                    if(phase=="commit"||phase=="rollback")
                    {
                        verifiedHash=Read(storage,"AvantageMcp.VerifiedModel",128).Trim();
                        if(!Regex.IsMatch(verifiedHash,"\\A[0-9a-fA-F]{64}\\z"))throw new InvalidOperationException("LIVE_TRANSACTION_HASH_INVALID");
                    }
                }
            }
            catch(Exception error){validationError=error;}
            // A recognized marker permits a bounded error response, but no
            // native operation occurs until all parsing and policy checks pass.
            return true;
        }
        protected override Dictionary<string,object> CollectContext(IntPtr processing,bool gridAvailable,
            int inputCount,IntPtr inputs,IntPtr axes,int flags)
        {
            if(validationError!=null)throw validationError;
            if(!gridAvailable||inputCount!=0||inputs!=IntPtr.Zero||axes!=IntPtr.Zero)throw new InvalidOperationException("LIVE_EDITOR_CONTEXT_INVALID");
            if(phase=="apply")return NativeContextIdentity.WithDocument(processing,(string)plan["document_path"],
                (nativeDoc,doc)=>NativeModelTransaction.Begin(processing,nativeDoc,doc,plan));
            NativeContextIdentity.InspectForPath(processing,null);
            var pending=NativeModelTransaction.Pending;
            if(phase=="inspect")return new Dictionary<string,object>{{"operation","inspect-live-editor-v1"},{"ready",true},{"pending",pending!=null},{"transaction_token",pending==null?null:pending.Token},{"request_id",pending==null?null:pending.RequestId},{"document_path",pending==null?null:pending.Path},{"applied",pending!=null&&pending.Applied}};
            if(pending==null||!string.Equals(token,pending.Token,StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("LIVE_TRANSACTION_NOT_FOUND");
            if(phase=="abandon")
            {
                if(!pending.Applied)throw new InvalidOperationException("LIVE_FAILED_TRANSACTION_REQUIRES_ROLLBACK");
                NativeModelTransaction.Pending=null;pending.Dispose();
                return new Dictionary<string,object>{{"operation","abandon-region-model-v1"},{"abandoned",true},{"model_changed",false}};
            }
            return NativeContextIdentity.WithDocument(processing,pending.Path,(nativeDoc,doc)=>
            {
                pending.RequireContext(processing,nativeDoc);pending.RequireCurrentHash(verifiedHash);
                if(phase=="commit"&&!pending.Applied)throw new InvalidOperationException("LIVE_TRANSACTION_NOT_APPLIED");
                if(phase=="rollback")pending.Rollback();
                NativeModelTransaction.Pending=null;pending.Dispose();
                return new Dictionary<string,object>{{"operation",phase+"-region-model-v1"},{"committed",phase=="commit"},{"rollback_verified",phase=="rollback"},{"transaction_token",token}};
            });
        }
        static string Read(IntPtr storage,string name,int limit)
        {
            IStream stream=null;object owned=Marshal.GetUniqueObjectForIUnknown(storage);
            try
            {
                ((IStorage)owned).OpenStream(name,IntPtr.Zero,0x10,0,out stream);
                System.Runtime.InteropServices.ComTypes.STATSTG stat;stream.Stat(out stat,1);
                if(stat.cbSize<=0||stat.cbSize>limit)throw new InvalidOperationException("LIVE_EDITOR_REQUEST_SIZE");
                var data=new byte[(int)stat.cbSize];int total=0;IntPtr count=Marshal.AllocCoTaskMem(4);
                try
                {
                    while(total<data.Length)
                    {
                        byte[] part=new byte[data.Length-total];Marshal.WriteInt32(count,0);stream.Read(part,part.Length,count);int read=Marshal.ReadInt32(count);
                        if(read<=0||read>part.Length)throw new InvalidOperationException("LIVE_EDITOR_REQUEST_TRUNCATED");
                        Buffer.BlockCopy(part,0,data,total,read);total+=read;
                    }
                }
                finally{Marshal.FreeCoTaskMem(count);}
                return new UTF8Encoding(false,true).GetString(data);
            }
            finally{if(stream!=null)Marshal.ReleaseComObject(stream);Marshal.ReleaseComObject(owned);}
        }
    }
}
