using System;
using System.Runtime.InteropServices;

namespace AvantageMcp.InProcess
{
    // Only the supervisor's fixed-copy persistence experiment may arm this.
    // The plugin itself never saves a file. Driver must save/re-read, remove
    // the uniquely labelled candidate, then save/re-read the restored model.
    [ComVisible(true)]
    [Guid("96D0A0F0-56FC-4305-92B0-0BA9CB776AB3")]
    [ClassInterface(ClassInterfaceType.None)]
    public sealed class PeakPersistenceProbe : PeakRoundtripProbe
    {
        bool cleanup;
        protected override bool RetainCandidate { get { return !cleanup; } }
        protected override bool CleanupOnly { get { return cleanup; } }
        protected override bool ValidateRequest(IntPtr storage)
        {
            if(!base.ValidateRequest(storage))return false;
            cleanup=HasMarker(storage,"AvantageMcp.PersistenceRequest","remove-test-candidate-v1");
            return cleanup||HasMarker(storage,"AvantageMcp.PersistenceRequest","retain-test-candidate-v1");
        }
    }
}
