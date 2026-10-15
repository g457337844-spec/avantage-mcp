using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace AvantageMcp {
// IFittedPeak.SetBackground discards its native fit constraints. Keep the actual
// peak objects (and their IDs / chemical metadata associations) and restore the
// captured native values and links after every background assignment.
internal static class NativeModelBackgroundChange {
    static readonly Guid Unknown = new Guid("00000000-0000-0000-C000-000000000046");

    static IntPtr Identity(IntPtr value) {
        if (value == IntPtr.Zero) return IntPtr.Zero;
        IntPtr identity; Guid iid = Unknown;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(value, ref iid, out identity));
        return identity;
    }

    sealed class SavedConstraint : IDisposable {
        public int Attribute, Type, Operator, XRefIndex, Active;
        public double Value1, Value2, Value3;
        public IntPtr Reference, ReferenceIdentity;
        public IAvPeakConstraint Copy;

        public SavedConstraint(int attribute, IntPtr raw) {
            Attribute = attribute;
            IAvPeakConstraint source = null;
            try {
                source = (IAvPeakConstraint)Marshal.GetObjectForIUnknown(raw);
                source.GetParameters(out Type, out Value1, out Value2, out Value3,
                    out Reference, out Operator);
                ReferenceIdentity = Identity(Reference);
                source.GetXRefIndex(out XRefIndex); source.IsConstrained(out Active);
                Copy = (IAvPeakConstraint)Activator.CreateInstance(TypeFromConstraint());
                Copy.SetParameters(Type, Value1, Value2, Value3, Reference, Operator);
                Copy.SetXRefIndex(XRefIndex);
            } catch { Dispose(); throw; }
            finally { if (source != null) Marshal.ReleaseComObject(source); }
        }

        static Type TypeFromConstraint() {
            return System.Type.GetTypeFromCLSID(new Guid("68F7DAF5-B53B-11D2-988E-00C04F8EBE0F"));
        }

        public void Restore(IAvFittedPeak peak) {
            IntPtr pointer = Marshal.GetComInterfaceForObject(Copy, typeof(IAvPeakConstraint));
            try {
                // SetBackground clears existing constraints. Explicitly remove any
                // remaining native entry so this also works during rollback.
                IntPtr present; peak.GetPeakFitConstraint(Attribute, out present);
                try { if (present != IntPtr.Zero) peak.RemovePeakFitConstraint(present, Attribute); }
                finally { if (present != IntPtr.Zero) Marshal.Release(present); }
                peak.CreateInitialPeakFitConstraint(Attribute, pointer);
            } finally { Marshal.Release(pointer); }
        }

        public bool Matches(IntPtr raw) {
            if (raw == IntPtr.Zero) return false;
            IAvPeakConstraint actual = null; IntPtr reference = IntPtr.Zero, identity = IntPtr.Zero;
            try {
                actual = (IAvPeakConstraint)Marshal.GetObjectForIUnknown(raw);
                int type, op, xref, active; double v1, v2, v3;
                actual.GetParameters(out type, out v1, out v2, out v3, out reference, out op);
                identity = Identity(reference);
                actual.GetXRefIndex(out xref); actual.IsConstrained(out active);
                return type == Type && op == Operator && v1 == Value1 && v2 == Value2
                    && v3 == Value3 && xref == XRefIndex && active == Active
                    && identity == ReferenceIdentity;
            } finally {
                if (identity != IntPtr.Zero) Marshal.Release(identity);
                if (reference != IntPtr.Zero) Marshal.Release(reference);
                if (actual != null) Marshal.ReleaseComObject(actual);
            }
        }

        public void Dispose() {
            J.Release(Copy); Copy = null;
            if (ReferenceIdentity != IntPtr.Zero) { Marshal.Release(ReferenceIdentity); ReferenceIdentity = IntPtr.Zero; }
            if (Reference != IntPtr.Zero) { Marshal.Release(Reference); Reference = IntPtr.Zero; }
        }
    }

    sealed class SavedPeak : IDisposable {
        public readonly IAvFittedPeak Peak;
        public IntPtr Background, EntryIdentity, DataIdentity;
        public string Name;
        public int CombineMethod;
        public double Area, AreaRatio, Centre, Height, HeightRatio, Width, GL, TailMix, TailHeight, TailExp;
        public readonly List<SavedConstraint> Constraints = new List<SavedConstraint>();
        public bool Touched;

        public SavedPeak(IAvFittedPeak peak) {
            Peak = peak;
            try {
                peak.GetName(out Name); peak.GetGLCombineMethod(out CombineMethod);
                peak.GetAsymParams(out Area, out AreaRatio, out Centre, out Height, out HeightRatio,
                    out Width, out GL, out TailMix, out TailHeight, out TailExp);
                try { peak.GetBackground(out Background); }
                catch (Exception e) { if (e.HResult != unchecked((int)0x80004003)) throw; }
                IntPtr entry = IntPtr.Zero, data = IntPtr.Zero;
                try {
                    peak.GetPeakEntry(out entry); peak.GetDataSpace(out data);
                    EntryIdentity = Identity(entry); DataIdentity = Identity(data);
                } finally {
                    if (entry != IntPtr.Zero) Marshal.Release(entry);
                    if (data != IntPtr.Zero) Marshal.Release(data);
                }
                for (int attribute = 1; attribute <= 8; attribute++) {
                    IntPtr raw; peak.GetPeakFitConstraint(attribute, out raw);
                    try { if (raw != IntPtr.Zero) Constraints.Add(new SavedConstraint(attribute, raw)); }
                    finally { if (raw != IntPtr.Zero) Marshal.Release(raw); }
                }
            } catch { Dispose(); throw; }
        }

        public void Assign(IntPtr background) {
            Touched = true;
            Peak.SetBackground(background);
            Peak.SetAsymParams(Area, AreaRatio, Centre, Height, HeightRatio, Width, GL, TailMix, TailHeight, TailExp);
            Peak.SetName(Name); Peak.SetGLCombineMethod(CombineMethod);
            foreach (var constraint in Constraints) constraint.Restore(Peak);
        }

        public void Verify(IntPtr background) {
            string name; int method; double a, ar, c, h, hr, w, g, tm, th, te;
            Peak.GetName(out name); Peak.GetGLCombineMethod(out method);
            Peak.GetAsymParams(out a, out ar, out c, out h, out hr, out w, out g, out tm, out th, out te);
            bool equal = name == Name && method == CombineMethod
                && new[] {a, ar, c, h, hr, w, g, tm, th, te}.SequenceEqual(
                    new[] {Area, AreaRatio, Centre, Height, HeightRatio, Width, GL, TailMix, TailHeight, TailExp});
            IntPtr actualBackground = IntPtr.Zero, entry = IntPtr.Zero, data = IntPtr.Zero;
            IntPtr entryIdentity = IntPtr.Zero, dataIdentity = IntPtr.Zero;
            try {
                try { Peak.GetBackground(out actualBackground); }
                catch (Exception e) { if (e.HResult != unchecked((int)0x80004003)) throw; }
                Peak.GetPeakEntry(out entry); Peak.GetDataSpace(out data);
                entryIdentity = Identity(entry); dataIdentity = Identity(data);
                equal = equal && NativePeakModel.EquivalentBackground(actualBackground, background)
                    && entryIdentity == EntryIdentity && dataIdentity == DataIdentity;
            } finally {
                foreach (IntPtr pointer in new[] {actualBackground, entry, data, entryIdentity, dataIdentity})
                    if (pointer != IntPtr.Zero) Marshal.Release(pointer);
            }
            for (int attribute = 1; attribute <= 8; attribute++) {
                var expected = Constraints.FirstOrDefault(item => item.Attribute == attribute);
                IntPtr raw; Peak.GetPeakFitConstraint(attribute, out raw);
                try { equal = equal && (expected == null ? raw == IntPtr.Zero : expected.Matches(raw)); }
                finally { if (raw != IntPtr.Zero) Marshal.Release(raw); }
            }
            if (!equal) throw new Fault("BACKGROUND_MODEL_MISMATCH",
                "Avantage did not preserve the peak parameters, metadata association or native constraints during background replacement.");
        }

        public void Dispose() {
            foreach (var constraint in Constraints) constraint.Dispose(); Constraints.Clear();
            foreach (IntPtr pointer in new[] {Background, EntryIdentity, DataIdentity})
                if (pointer != IntPtr.Zero) Marshal.Release(pointer);
            Background = EntryIdentity = DataIdentity = IntPtr.Zero;
        }
    }

    internal static void Replace(NativePeakModel model, NativeBackground replacement) {
        var saved = new List<SavedPeak>(); IntPtr background = IntPtr.Zero;
        try {
            foreach (var peak in model.Peaks) saved.Add(new SavedPeak(peak.Native));
            background = Marshal.GetComInterfaceForObject(replacement.Background, typeof(IAvantageBackground));
            try {
                foreach (var peak in saved) peak.Assign(background);
                foreach (var peak in saved) peak.Verify(background);
            } catch (Exception failure) {
                try {
                    foreach (var peak in saved.Where(item => item.Touched)) peak.Assign(peak.Background);
                    foreach (var peak in saved.Where(item => item.Touched)) peak.Verify(peak.Background);
                } catch (Exception rollback) {
                    throw new Fault("BACKGROUND_ROLLBACK_FAILED",
                        "Background replacement failed and the original model could not be fully restored. Reopen this private session. "
                        + failure.Message + " Rollback: " + rollback.Message);
                }
                throw;
            }
            model.InvalidateFit();
        } finally {
            if (background != IntPtr.Zero) Marshal.Release(background);
            foreach (var peak in saved) peak.Dispose();
        }
    }
}
}
