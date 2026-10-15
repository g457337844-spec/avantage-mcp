using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using VGDATASTORELib;

namespace AvantageMcp
{
    public static partial class LivePeakTable
    {
        // A fitted component has its own primary chemical/quantification entry in
        // Avantage. Keep that relationship, including all surviving RSF metadata.
        public static Dictionary<string, object> Save(string path, SpectrumSession session,
            IList<IntPtr> templates)
        {
            if (session == null) throw new ArgumentNullException("session");
            var model = session.FitState as NativePeakModel;
            if (model == null || model.Peaks.Count < 1 || model.Peaks.Count > 100)
                throw new Fault("MODEL_SIZE", "Define 1–100 fitted components before applying a model.");
            if (templates == null || templates.Count != model.Peaks.Count)
                throw new ArgumentException("Supply one primary template per fitted component.");
            if (session.BackgroundState == null)
                throw new Fault("BACKGROUND_REQUIRED", "An explicit native background is required.");
            path = Path.GetFullPath(path);
            if (File.Exists(path)) throw new Fault("FILE_EXISTS", "Choose a new native table path.");
            IntPtr verified = NativeGridTables.VerifiedModule;
            var original = model.Peaks.Select(p => p.Native).ToArray();
            var expected = NativePeakTablePersistence.ReadPeaks(original);
            if (expected[0]["background"] == null || expected.Any(peak =>
                !NativePeakTablePersistence.Equivalent(expected[0]["background"], peak["background"])))
                throw new Fault("MIXED_BACKGROUNDS", "Each component must use the session's common native background.");
            var primaries = new List<IntPtr>();
            var primaryObjects = new List<object>();
            var primarySnapshots = new List<Dictionary<string, object>>();
            var templateSnapshots = new List<Dictionary<string, object>>();
            var clones = new List<IAvFittedPeak>();
            var pointers = new List<IntPtr>();
            IAvantageBackground background = null;
            IntPtr bg = IntPtr.Zero, data = IntPtr.Zero;
            try
            {
                session.BackgroundState.Background.CloneBackground(out background);
                bg = Marshal.GetComInterfaceForObject(background, typeof(IAvantageBackground));
                data = Marshal.GetComInterfaceForObject(session.Data, typeof(IDataSpace));
                double start, end;
                background.GetStart(out start); background.GetEnd(out end);
                for (int i = 0; i < expected.Count; i++)
                {
                    object obj = Activator.CreateInstance(Type.GetTypeFromCLSID(
                        new Guid("010DE95A-68E5-11D2-851A-006097C81436")));
                    primaryObjects.Add(obj);
                    IntPtr primary = EntryPointer(obj); primaries.Add(primary);
                    var template = templates[i];
                    templateSnapshots.Add(template == IntPtr.Zero ? null : PrimarySnapshot(template));
                    if (template != IntPtr.Zero) SetPointer(primary, 67, template);
                    else
                    {
                        Marshal.ThrowExceptionForHR(Slot<ByteSetter>(primary, 35)(primary, 0));
                        Marshal.ThrowExceptionForHR(Slot<DoubleSetter>(primary, 51)(primary, 0));
                    }
                    // CloneFrom copies the template's table-owner interface as
                    // well as its persisted values. The export graph must not
                    // retain a table from another document or application.
                    SetPointer(primary, 57, IntPtr.Zero);
                    var saved = expected[i];
                    SetPointer(primary, 11, data); SetPointer(primary, 9, bg);
                    SetInteger(primary, 33, 1);
                    Marshal.ThrowExceptionForHR(Slot<StringSetter>(primary, 13)(primary, J.Str(saved, "label")));
                    Marshal.ThrowExceptionForHR(Slot<AttributesSetter>(primary, 5)(primary,
                        start, J.Num(saved, "centre_kinetic_ev"), end, J.Num(saved, "fwhm_ev")));
                    // Transmission/RSF settings are inherited, while the component's
                    // geometric attributes follow its current fitted parameters.
                    double height, area, tx, normalised;
                    Marshal.ThrowExceptionForHR(Slot<SubParametersGetter>(primary, 8)(primary,
                        out height, out area, out tx, out normalised));
                    Marshal.ThrowExceptionForHR(Slot<SubParametersSetter>(primary, 7)(primary,
                        J.Num(saved, "height"), J.Num(saved, "area"), tx, normalised));
                    primarySnapshots.Add(PrimarySnapshot(primary));
                    var clone = (IAvFittedPeak)Activator.CreateInstance(Type.GetTypeFromCLSID(
                        new Guid("B90B1A14-A54F-11D2-852A-00104B053B4C")));
                    clones.Add(clone);
                    clone.SetDataSpace(data); clone.SetBackground(bg); clone.SetPeakEntry(primary);
                    clone.SetAsymParams(J.Num(saved, "area"), J.Num(saved, "area_ratio"),
                        J.Num(saved, "centre_kinetic_ev"), J.Num(saved, "height"),
                        J.Num(saved, "height_ratio"), J.Num(saved, "fwhm_ev"),
                        J.Num(saved, "gaussian_lorentz"), J.Num(saved, "tail_mix"),
                        J.Num(saved, "tail_height"), J.Num(saved, "tail_exponential"));
                    clone.SetName(J.Str(saved, "label"));
                    clone.SetGLCombineMethod(J.Int(saved, "native_gl_combine_method", 0));
                    pointers.Add(Marshal.GetComInterfaceForObject(clone, typeof(IAvFittedPeak)));
                }
                for (int i = 0; i < expected.Count; i++)
                    foreach (var saved in (IEnumerable<Dictionary<string, object>>)expected[i]["constraints"])
                    {
                        var constraint = (IAvPeakConstraint)Activator.CreateInstance(Type.GetTypeFromCLSID(
                            new Guid("68F7DAF5-B53B-11D2-988E-00C04F8EBE0F")));
                        IntPtr raw = IntPtr.Zero;
                        try
                        {
                            IntPtr reference = IntPtr.Zero;
                            if (saved["reference_peak_index"] != null)
                            {
                                int target = Convert.ToInt32(saved["reference_peak_index"]);
                                if (target < 0 || target >= clones.Count)
                                    throw new Fault("EXTERNAL_CONSTRAINT", "A component references a peak outside this model.");
                                reference = pointers[target];
                            }
                            constraint.SetParameters(J.Int(saved, "type", 0), J.Num(saved, "value1"),
                                J.Num(saved, "value2"), J.Num(saved, "value3"), reference, J.Int(saved, "operator", 0));
                            raw = Marshal.GetComInterfaceForObject(constraint, typeof(IAvPeakConstraint));
                            clones[i].CreateInitialPeakFitConstraint(J.Int(saved, "attribute", 0), raw);
                        }
                        finally { if (raw != IntPtr.Zero) Marshal.Release(raw); J.Release(constraint); }
                    }
                // SetBackground resets native constraints. Check the reconstructed
                // graph before persistence so a future ABI or ordering change
                // cannot write an incomplete model even to a private staging file.
                if (!NativePeakTablePersistence.Equivalent(expected, NativePeakTablePersistence.ReadPeaks(clones)))
                    throw new Fault("CLONE_MISMATCH", "Native component cloning changed a peak, background or constraint.");
                using (var document = new NativePeakTablePersistence.Document(session.Data))
                {
                    foreach (var primary in primaries)
                        if (Bind<AddPrimary>("?AddPeak@CProcessGridDoc@@QEAAHPEAUIPeakTableEntry@@@Z")(document.Handle, primary) != 1)
                            throw new Fault("NATIVE_TABLE_FAILED", "Avantage refused a component's primary entry.");
                    foreach (var clone in clones) document.Add(clone);
                    if (document.Count != clones.Count ||
                        Bind<CountPrimary>("?GetNumPeaks@CProcessGridDoc@@QEAAHXZ")(document.Handle) != clones.Count)
                        throw new Fault("NATIVE_TABLE_FAILED", "Avantage did not retain every component and its primary entry.");
                    document.Save(path); SaveCompleteCollections(document.Handle, path);
                }
                var result = InspectComponents(path, session.Data);
                if (!NativePeakTablePersistence.PersistedEquivalent(expected, result["peaks"]) ||
                    !NativePeakTablePersistence.Equivalent(primarySnapshots, result["primaries"]))
                    throw new Fault("ROUNDTRIP_MISMATCH", "Native table persistence changed a component or its metadata.");
                if (!NativePeakTablePersistence.Equivalent(expected, NativePeakTablePersistence.ReadPeaks(original)))
                    throw new Fault("SOURCE_CHANGED", "Native export changed the editable model.");
                for (int i = 0; i < templates.Count; i++)
                    if (templates[i] != IntPtr.Zero && !NativePeakTablePersistence.Equivalent(
                        templateSnapshots[i], PrimarySnapshot(templates[i])))
                        throw new Fault("SOURCE_CHANGED", "Native export changed the original primary metadata.");
                result["roundtrip_verified"] = true;
                result["runtime_display_ratios_persisted"] = false;
                result["primary_metadata_preserved"] = true;
                return result;
            }
            finally
            {
                foreach (var pointer in pointers) Marshal.Release(pointer);
                foreach (var clone in clones) J.Release(clone);
                foreach (var pointer in primaries) Marshal.Release(pointer);
                foreach (var obj in primaryObjects) J.Release(obj);
                if (bg != IntPtr.Zero) Marshal.Release(bg);
                if (data != IntPtr.Zero) Marshal.Release(data);
                J.Release(background);
            }
        }

        internal static Dictionary<string, object> InspectComponents(string path, IDataSpace source)
        {
            NativePeakTablePersistence.Inspect(path, source);
            using (var document = new NativePeakTablePersistence.Document(source))
            {
                document.Load(path);
                int count = Bind<CountPrimary>("?GetNumPeaks@CProcessGridDoc@@QEAAHXZ")(document.Handle);
                var snapshots = new List<Dictionary<string, object>>();
                var identities = new List<IntPtr>();
                var linked = new List<int>();
                var peaks = document.Peaks();
                try
                {
                    IntPtr position = Bind<FirstPrimary>("?GetFirstPeak@CProcessGridDoc@@QEBAPEAU__POSITION@@XZ")(document.Handle);
                    while (position != IntPtr.Zero)
                    {
                        IntPtr item = Bind<NextPrimary>("?GetNextPeak@CProcessGridDoc@@QEAAAEAVCPeakItem@@AEAPEAU__POSITION@@@Z")(
                            document.Handle, ref position);
                        IntPtr primary = Marshal.ReadIntPtr(item, 0x60);
                        snapshots.Add(PrimarySnapshot(primary)); identities.Add(Identity(primary));
                    }
                    foreach (var peak in peaks)
                    {
                        IntPtr entry = IntPtr.Zero, identity = IntPtr.Zero;
                        try
                        {
                            peak.GetPeakEntry(out entry);
                            if (entry == IntPtr.Zero) throw new Fault("INVALID_LIVE_TABLE", "Missing primary component entry.");
                            identity = Identity(entry); linked.Add(identities.IndexOf(identity));
                        }
                        finally
                        {
                            if (identity != IntPtr.Zero) Marshal.Release(identity);
                            if (entry != IntPtr.Zero) Marshal.Release(entry);
                        }
                    }
                    if (count != snapshots.Count || count != peaks.Count || linked.Any(i => i < 0) ||
                        linked.Distinct().Count() != count)
                        throw new Fault("INVALID_LIVE_TABLE", "Every fitted component must have a distinct primary entry.");
                    return J.Obj("path", path, "primary_count", count, "primaries", linked.Select(i => snapshots[i]).ToArray(),
                        "peak_count", peaks.Count, "peaks", NativePeakTablePersistence.ReadPeaks(peaks),
                        "backend", "Avantage native per-component VGT");
                }
                finally
                {
                    foreach (var identity in identities) Marshal.Release(identity);
                    foreach (var peak in peaks) Marshal.ReleaseComObject(peak);
                }
            }
        }
    }
}
