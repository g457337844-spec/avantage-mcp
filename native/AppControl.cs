using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AvantageMcp
{
    /// <summary>Small, verified application surface. Data processing uses separate native COM objects.</summary>
    public static class AppControl
    {
        private static readonly Guid AppClass = new Guid("052CC3C7-B7E2-40C3-92F5-3FA00FA81C1D");
        private static readonly Guid DispatchInterface = new Guid("00020400-0000-0000-C000-000000000046");

        [DllImport("oleaut32.dll", ExactSpelling = true)]
        private static extern int GetActiveObject(ref Guid clsid, IntPtr reserved,
            [MarshalAs(UnmanagedType.IUnknown)] out object value);

        private static object Connect()
        {
            Guid clsid = AppClass;
            object value;
            // CoCreateInstance changes Avantage to Remote Controlled even when it
            // is already running, disabling File Open. Status must remain read-only.
            int hr = GetActiveObject(ref clsid, IntPtr.Zero, out value);
            if (hr == unchecked((int)0x800401e3))
                throw new Fault("INTERACTIVE_MODE_PROTECTED", "Avantage does not expose a running automation object. Status skipped automation activation to preserve interactive File Open.");
            Marshal.ThrowExceptionForHR(hr);
            return value;
        }

        private static void Release(object value)
        {
            if (value != null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value);
        }

        private static void Read(Dictionary<string, object> result, string name, Func<object> action)
        {
            try { result[name] = action(); }
            catch (Exception ex)
            {
                result[name] = null;
                result[name + "_error"] = ex.GetType().Name + ": " + ex.Message;
            }
        }

        private static string RegisteredExecutable()
        {
            using (RegistryKey key = Registry.ClassesRoot.OpenSubKey(
                @"CLSID\{052CC3C7-B7E2-40C3-92F5-3FA00FA81C1D}\LocalServer32"))
            {
                string command = key == null ? null : key.GetValue(null) as string;
                if (String.IsNullOrWhiteSpace(command)) return null;
                Match match = Regex.Match(command, "^\\s*\"([^\"]+)\"");
                string path = match.Success ? match.Groups[1].Value : command.Trim();
                return File.Exists(path) && String.Equals(Path.GetFileName(path), "Avantage.exe",
                    StringComparison.OrdinalIgnoreCase) ? Path.GetFullPath(path) : null;
            }
        }

        public static Dictionary<string, object> Status()
        {
            Dictionary<string, object> result = new Dictionary<string, object>();
            result["executable"] = RegisteredExecutable();
            Process[] running = Process.GetProcessesByName("Avantage");
            bool isRunning = running.Length > 0;
            bool anyResponding = false;
            foreach (Process process in running)
            {
                try { anyResponding |= process.Responding; }
                catch (InvalidOperationException) { }
                finally { process.Dispose(); }
            }
            result["running"] = isRunning;
            result["responding"] = isRunning ? (object)anyResponding : null;
            result["connected"] = false;
            result["visible_grid_control_verified"] = false;
            result["project_mutation_enabled"] = false;
            if (!isRunning) return result;
            if (!anyResponding)
            {
                result["connection_error_code"] = "APP_NOT_RESPONDING";
                result["connection_error"] = "Avantage is not responding; application COM calls were skipped.";
                return result;
            }

            object application = null, experiment = null;
            try
            {
                application = Connect();
                dynamic app = application;
                result["connected"] = true;
                Read(result, "application_state", () => (object)app.QueryStatus());
                Read(result, "experiment_run_state", () => (object)app.QueryExptRunState());
                Read(result, "experiment_path", () => (object)app.GetFullExptPath());
                Read(result, "progress_percent", () => (object)app.GetExptPercentageComplete());
                Read(result, "project_name", () => (object)app.ExptProjectName);
                Read(result, "project_root", () => (object)app.ExptProjectRoot);
                Read(result, "manual_mode", () => (object)app.ManualMode);
                Read(result, "remote_mode", () => (object)app.RemoteMode);
                experiment = app.GetExperimentIF();
                dynamic expt = experiment;
                Read(result, "experiment_modified", () => (object)expt.AnyModified());
                Read(result, "experiment_read_only", () => (object)expt.IsReadOnly());
                Read(result, "experiment_save_path", () => (object)expt.GetExperimentSavePath());
                Read(result, "experiment_run_mode", () => (object)expt.QueryExptRunMode());
            }
            catch (Exception ex)
            {
                Fault fault = ex as Fault;
                if (fault != null) result["connection_error_code"] = fault.Code;
                result["connection_error"] = ex.GetType().Name + ": " + ex.Message;
            }
            finally { Release(experiment); Release(application); }
            return result;
        }

        public static Dictionary<string, object> ShowDataFile(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("A .VGD file path is required.");
            path = Path.GetFullPath(path);
            if (!String.Equals(Path.GetExtension(path), ".vgd", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("This action only opens native .VGD data files.");
            if (!File.Exists(path)) throw new FileNotFoundException("Data file not found.", path);
            Process[] running = Process.GetProcessesByName("Avantage");
            bool anyResponding = false;
            foreach (Process process in running)
            {
                try { anyResponding |= process.Responding; }
                catch (InvalidOperationException) { }
                finally { process.Dispose(); }
            }
            if (running.Length > 0 && !anyResponding)
                throw new Fault("APP_NOT_RESPONDING", "Avantage is not responding; no open request was sent.");
            string executable = RegisteredExecutable();
            if (executable == null) throw new InvalidOperationException("Registered Avantage executable was not found.");
            ProcessStartInfo info = new ProcessStartInfo(executable, "\"" + path + "\"");
            info.UseShellExecute = false;
            info.WorkingDirectory = Path.GetDirectoryName(executable);
            using (Process request = Process.Start(info)) { }
            return new Dictionary<string, object> {
                { "path", path }, { "executable", executable },
                { "request_accepted", true }, { "display_verified", false },
                { "note", "Open request sent to Avantage. The application API does not expose processing-grid visibility verification." }
            };
        }
    }
}
