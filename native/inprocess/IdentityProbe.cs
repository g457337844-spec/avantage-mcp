using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace AvantageMcp.InProcess
{
    // Separate class/assembly activation from the baseline probe already loaded
    // in the host. Only the fixed test document is accepted by the helper.
    [ComVisible(true)]
    [Guid("96D0A0F0-56FC-4305-92B0-0BA9CB776AB1")]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class IdentityProbe : ContextProbe
    {
        protected override Dictionary<string, object> CollectContext(
            IntPtr processing, bool gridAvailable, int inputCount,
            IntPtr inputs, IntPtr axes, int flags)
        {
            var result = base.CollectContext(processing, gridAvailable, inputCount, inputs, axes, flags);
            result["native_identity"] = NativeContextIdentity.Inspect(processing);
            return result;
        }
    }
}
