using System;
using System.IO;
using System.Security.Cryptography;

namespace AvantageMcp {
internal static class VerifiedBuild {
    internal static readonly string[][] ProcessingFiles = {
        new[]{"ProcessGrid.dll",NativeGridTables.SupportedSha256},
        new[]{"DataCache.ocx","812C32A4947B2A9DFFC91CCAAAFF61C80FD7379D79706EA3B95D0520B788A00D"},
        new[]{"Peaks.dll","D5FD7ACFF7C49FBCDCE24DBD34EAF5A7294C434E5B6F5AA1AB031983E1F583B9"}
    };
    internal static readonly string[][] InteractiveFiles = {
        new[]{"Avantage.exe","6985232B5D77D3493F272F9A482FB0159890F0F13D55DF29D43FEF319FF1C4AE"},
        new[]{"ProcessingManager.dll","7D9AA9623E818BCE0794030CD108CD20371CCB2D8AB686DFDADC54085B0E61A9"},
        new[]{"ProcessingInterfacesps.dll","35CB5A0E55CCE5584ADA0BD745CD3108087C1B4F7A5FFF3D7729F801DFF83D17"},
        new[]{"UserInterfaceIFps.dll","91CF9CB21F209F8BAFA3FD08557384CC68F01AEF5A3AEB1F59E4E3DE5A823D4C"}
    };
    internal const string MfcSha256="FC9B1F94752D7080436824B480E83BA1EC930E3F7BAA881F4E842B882E715CFC";
    internal static bool Matches(string path,string expected) {
        if(!File.Exists(path))return false;
        using(var file=File.OpenRead(path))using(var sha=SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","")==expected;
    }
    internal static bool ProcessingSupported() {
        if(IntPtr.Size!=8)return false;
        foreach(var pair in ProcessingFiles)if(!Matches(Path.Combine(Program.InstallRoot,"bin",pair[0]),pair[1]))return false;
        return true;
    }
    internal static bool InteractiveSupported() {
        if(!ProcessingSupported())return false;
        foreach(var pair in InteractiveFiles)if(!Matches(Path.Combine(Program.InstallRoot,"bin",pair[0]),pair[1]))return false;
        return Matches(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"mfc140u.dll"),MfcSha256);
    }
    internal static void RequireProcessing() {
        if(!ProcessingSupported())throw new Fault("UNSUPPORTED_BUILD","Native project processing requires the verified Avantage 6.9.0.59 libraries.");
    }
}
}
