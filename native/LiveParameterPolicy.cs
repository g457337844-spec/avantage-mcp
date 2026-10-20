namespace AvantageMcp {
/// <summary>
/// Peak-topology contract for same-window live apply.
///
/// This file is deliberately free of COM and of any Avantage assembly reference,
/// so it can be compiled and exercised on its own without an installation. The
/// public live path only updates parameters of peaks the application already
/// owns; creating or removing peaks is a structural change and stays with the
/// private project flow until native ownership of inserted objects is verified.
/// </summary>
public static class LiveParameterPolicy {
    public const string EmptyModelCode="LIVE_EMPTY_MODEL";
    public const string StructureChangeCode="LIVE_STRUCTURE_CHANGE_UNSUPPORTED";

    /// <summary>
    /// Reject any change to the number of application-owned peaks.
    /// Error codes and messages match the pre-extraction behaviour.
    /// </summary>
    public static void ValidatePeakTopology(int existingCount,int candidateCount) {
        if(existingCount==0&&candidateCount>0)
            throw new Fault(EmptyModelCode,"The original spectrum has no peaks yet, so a new peak set cannot be applied in place. Add peaks in Avantage first, then reattach and apply the fitted parameters. No live change was sent.");
        if(existingCount!=candidateCount||candidateCount==0)
            throw new Fault(StructureChangeCode,"Same-window apply currently requires the same existing peak count; use private project processing for topology changes. No live change was sent.");
    }
}
}
