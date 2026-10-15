using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using VGDATASTORELib;

namespace AvantageMcp {
// VGP is a processing document with companion spectra, not a renamed VGT.
// Native collection loading reads the complete graph without initializing a UI view.
internal sealed class NativeProcessingProject : IDisposable {
    [DllImport("ole32.dll",CharSet=CharSet.Unicode)]static extern int StgOpenStorage(string path,IntPtr priority,uint mode,IntPtr exclude,uint reserved,out IntPtr storage);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]delegate int OpenStorage(IntPtr self,[MarshalAs(UnmanagedType.LPWStr)]string name,IntPtr priority,uint mode,IntPtr exclude,uint reserved,out IntPtr child);
    internal sealed class Spectrum : IDisposable {
        public int Index;public IDataSpace Data;public string SourceFile;public int PointCount,PeakCount;
        public double[] Intensity,KineticAxis;
        public Dictionary<string,object> Info(){return J.Obj("spectrum_index",Index,"name",Path.GetFileNameWithoutExtension(SourceFile),"source_path",SourceFile,"point_count",PointCount,"fitted_peak_count",PeakCount,"has_fitted_peaks",PeakCount>0,"intensity_min",Intensity.Min(),"intensity_max",Intensity.Max());}
        public void Dispose(){if(Data!=null){Marshal.ReleaseComObject(Data);Data=null;}}
    }
    public string Id,SourcePath,SourceHash,WorkingPath,CompanionDirectory;
    internal bool PreserveAllEntries;
    public readonly List<string> ProtectedFiles=new List<string>();
    public NativePeakTablePersistence.Document Document;
    public readonly List<Spectrum> Spectra=new List<Spectrum>();
    internal static void Validate(string path) {
        if(!File.Exists(path))throw new Fault("FILE_NOT_FOUND",path);
        if(!String.Equals(Path.GetExtension(path),".vgp",StringComparison.OrdinalIgnoreCase))throw new Fault("UNSUPPORTED_FORMAT","Processing project input must be an Avantage .VGP file.");
        if(new FileInfo(path).Length>64*1024*1024)throw new Fault("PROJECT_SIZE","VGP metadata is limited to 64 MiB.");
        byte[] signature=new byte[8];using(var file=File.OpenRead(path)){if(file.Read(signature,0,8)!=8||!signature.SequenceEqual(new byte[]{0xd0,0xcf,0x11,0xe0,0xa1,0xb1,0x1a,0xe1}))throw new Fault("INVALID_PROJECT","Expected an Avantage structured-storage processing project.");}
        IntPtr root=IntPtr.Zero;
        try {
            if(StgOpenStorage(path,IntPtr.Zero,0x20,IntPtr.Zero,0,out root)!=0)throw new Fault("INVALID_PROJECT","The VGP storage container cannot be opened.");
            var open=(OpenStorage)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(root),6*IntPtr.Size),typeof(OpenStorage));
            foreach(string name in new[]{"Grid Cells","Data Spaces","Peaks","Backgrounds","Fitted Peaks"}) {
                IntPtr section=IntPtr.Zero;try{if(open(root,name,IntPtr.Zero,0x10,IntPtr.Zero,0,out section)!=0)throw new Fault("INVALID_PROJECT","The project is missing native VGP storage: "+name);}finally{if(section!=IntPtr.Zero)Marshal.Release(section);}
            }
        }finally{if(root!=IntPtr.Zero)Marshal.Release(root);}
    }
    public void Load() {
        Validate(WorkingPath);Document=new NativePeakTablePersistence.Document(null);
        try {
            ProjectPersistence.LoadComplete(Document,WorkingPath);
            var values=Document.EnumerateDataSpaces();
            try {
                if(values.Count==0)throw new Fault("EMPTY_PROJECT","The VGP did not load any spectra. Keep its matching .DATA directory available.");
                for(int i=0;i<values.Count;i++) {
                    var spectrum=new Spectrum{Index=i,Data=(IDataSpace)Marshal.GetObjectForIUnknown(values[i])};
                    try {
                        spectrum.SourceFile=Convert.ToString(spectrum.Data.GetProperty(DSPropSet.STDPROPSET,DSPropID.DS_GEPROPID_SOURCE_FILE));
                        var axes=new DataAxesClass();var space=new SpaceAxesClass();
                        try {
                            spectrum.Data.GetDataAxes(axes);spectrum.Data.GetSpaceAxes(space);int count=axes.GetSize();
                            if(count<1||count>10000000)throw new Fault("DATA_SIZE","A project spectrum has an unsupported point count.");
                            spectrum.Intensity=new double[count];spectrum.Data.GetData(axes,out spectrum.Intensity[0],ref count);
                            if(count!=spectrum.Intensity.Length||spectrum.Intensity.Any(v=>Double.IsNaN(v)||Double.IsInfinity(v)))throw new Fault("INVALID_PROJECT_DATA","The VGP contains invalid spectrum intensities.");
                            spectrum.PointCount=count;int dimensions;space.GetNAxes(out dimensions);
                            if(dimensions==1){spectrum.KineticAxis=new double[count];for(int point=0;point<count;point++)space.GetAxisPoint(0,point,out spectrum.KineticAxis[point]);}
                        }finally{J.Release(axes);J.Release(space);}
                        var peaks=Document.PeaksFor(spectrum.Data);try{spectrum.PeakCount=peaks.Count;}finally{foreach(var peak in peaks)Marshal.ReleaseComObject(peak);}
                        Spectra.Add(spectrum);spectrum=null;
                    }finally{if(spectrum!=null)spectrum.Dispose();}
                }
            }finally{foreach(var value in values)Marshal.Release(value);}
        }catch{Dispose();throw;}
    }
    public Dictionary<string,object> Info(){return J.Obj("project_id",Id,"source_path",SourcePath,"source_sha256",SourceHash,"working_path",WorkingPath,"private_companion_directory",CompanionDirectory,"spectrum_count",Spectra.Count,"fitted_peak_count",Spectra.Sum(s=>s.PeakCount),"read_only_source",true,"backend","Avantage complete native VGP graph; isolated native data cache","save_project_supported",true);}
    public void Dispose(){foreach(var spectrum in Spectra)spectrum.Dispose();Spectra.Clear();if(Document!=null){Document.Dispose();Document=null;}}
}

public sealed partial class Engine {
    readonly Dictionary<string,NativeProcessingProject> projects=new Dictionary<string,NativeProcessingProject>();
    NativeProcessingProject ProcessingProject(string id){NativeProcessingProject project;if(!projects.TryGetValue(id,out project))throw new Fault("PROJECT_NOT_FOUND","The project is not open in this worker. Reopen it after a worker restart.");return project;}
    object OpenProcessingProject(string path) {
        path=Path.GetFullPath(path);if(!File.Exists(path))throw new Fault("FILE_NOT_FOUND",path);
        if(!String.Equals(Path.GetExtension(path),".vgp",StringComparison.OrdinalIgnoreCase))throw new Fault("UNSUPPORTED_FORMAT","Processing project input must be an Avantage .VGP file.");
        if(projects.Count>=8)throw new Fault("PROJECT_LIMIT","Close an existing project before opening more than eight projects.");
        string sourceCompanion=Path.Combine(Path.GetDirectoryName(path),Path.GetFileNameWithoutExtension(path)+".DATA");
        if(!Directory.Exists(sourceCompanion))throw new Fault("COMPANION_DATA_MISSING","Keep the matching "+Path.GetFileName(sourceCompanion)+" directory beside the .VGP file.");
        string id=Guid.NewGuid().ToString("N"),directory=Path.Combine(workRoot,"project-"+id);Directory.CreateDirectory(directory);
        string copy=Path.Combine(directory,Path.GetFileName(path)),sourceHash=Hash(path);File.Copy(path,copy,false);File.SetAttributes(copy,FileAttributes.Normal);
        string companion=Path.Combine(directory,Path.GetFileName(sourceCompanion));Directory.CreateDirectory(companion);
        var dataFiles=Directory.EnumerateFiles(sourceCompanion,"*",SearchOption.AllDirectories).ToArray();
        if(dataFiles.Length>32768||dataFiles.Sum(file=>new FileInfo(file).Length)>2L*1024*1024*1024)throw new Fault("PROJECT_SIZE","Companion data exceeds 32768 files or 2 GiB.");
        foreach(string dataFile in dataFiles) {
            string relative=Path.GetFullPath(dataFile).Substring(Path.GetFullPath(sourceCompanion).Length+1),target=Path.Combine(companion,relative);Directory.CreateDirectory(Path.GetDirectoryName(target));
            string before=Hash(dataFile);File.Copy(dataFile,target,false);File.SetAttributes(target,FileAttributes.Normal);
            if(Hash(target)!=before||Hash(dataFile)!=before)throw new Fault("SOURCE_CHANGED","A companion data file changed during the project snapshot. Retry after saving the project.");
        }
        if(Hash(copy)!=sourceHash||Hash(path)!=sourceHash)throw new Fault("SOURCE_CHANGED","The VGP changed during the snapshot. Retry after saving it in Avantage.");
        var project=new NativeProcessingProject{Id=id,SourcePath=path,SourceHash=sourceHash,WorkingPath=copy,CompanionDirectory=companion};
        project.ProtectedFiles.AddRange(dataFiles);project.ProtectedFiles.AddRange(Directory.EnumerateFiles(companion,"*",SearchOption.AllDirectories));
        try{project.Load();projects.Add(id,project);return J.Obj("project",project.Info(),"project_id",id,"spectra",project.Spectra.Select(s=>s.Info()).ToArray());}
        catch{project.Dispose();throw;}
    }
    object ListProjectSpectra(string id){var project=ProcessingProject(id);return J.Obj("project",project.Info(),"project_id",id,"spectra",project.Spectra.Select(s=>s.Info()).ToArray());}
    object CloseProcessingProject(string id){var project=ProcessingProject(id);project.Dispose();projects.Remove(id);return J.Obj("project_id",id,"closed",true,"sessions_retained",true);}
    bool IsProtectedProjectPath(string path){return projects.Values.Any(p=>FileSafety.SameFile(path,p.SourcePath)||FileSafety.SameFile(path,p.WorkingPath)||p.ProtectedFiles.Any(file=>FileSafety.SameFile(path,file))||p.Spectra.Any(s=>!String.IsNullOrWhiteSpace(s.SourceFile)&&FileSafety.SameFile(path,s.SourceFile)));}
    void DisposeProjects(){foreach(var project in projects.Values)project.Dispose();projects.Clear();}
    object OpenProjectSpectrum(string projectId,int index) {
        var project=ProcessingProject(projectId);if(index<0||index>=project.Spectra.Count)throw new Fault("SPECTRUM_NOT_FOUND","Choose a spectrum index returned by list_project_spectra.");
        if(sessions.Count>=32)throw new Fault("SESSION_LIMIT","Close an existing session before opening another spectrum.");
        var original=project.Spectra[index];string id=Guid.NewGuid().ToString("N"),directory=Path.Combine(workRoot,id);Directory.CreateDirectory(directory);
        string path=Path.Combine(directory,"spectrum.vgd");var s=new SpectrumSession{Id=id,SourcePath=project.SourcePath,SourceHash=project.SourceHash,DirectoryPath=directory,WorkingPath=path};
        try {
            NativeSnapshot.CopyDataSpaceToFile(original.Data,path);
            s.Data=new DataSpaceClass();s.Data.LoadPersistFile(path,0,1,0);s.Refresh();
            if(!s.Y.SequenceEqual(original.Intensity)||(original.KineticAxis!=null&&!s.X.SequenceEqual(original.KineticAxis)))throw new Fault("PROJECT_SNAPSHOT_MISMATCH","The native VGD copy did not preserve every project spectrum value.");
            var peaks=project.Document.PeaksFor(original.Data);
            try {ImportNativeSnapshots(s,NativePeakTablePersistence.ReadPeaks(peaks));}
            finally {foreach(var peak in peaks)Marshal.ReleaseComObject(peak);}
            s.SourceProjectId=projectId;s.ProjectSpectrumIndex=index;
            if(s.FitState is NativePeakModel)s.ProjectOriginalPeakIds.AddRange(((NativePeakModel)s.FitState).Peaks.Select(peak=>peak.Id));
            sessions.Add(id,s);return J.Obj("session_id",id,"session",s.Info(),"project",project.Info(),"spectrum",original.Info(),"model",s.FitState==null?null:Model(s).Info(),"background",s.BackgroundState==null?null:s.BackgroundState.Info(),"private_snapshot_verified",true);
        }catch{s.Dispose();throw;}
    }
    object SaveProjectModel(SpectrumSession session,string path) {
        if(String.IsNullOrWhiteSpace(session.SourceProjectId))throw new Fault("PROJECT_SESSION_REQUIRED","Select a spectrum with open_project_spectrum before saving a complete VGP model.");
        var source=ProcessingProject(session.SourceProjectId);var model=Model(session,true);
        var originalGrid=ProjectGridValidation.Capture(source.WorkingPath);
        if(model.Peaks.Count>100)throw new Fault("MODEL_SIZE","A region may contain at most 100 fitted components.");
        path=Target(session,path,".vgp",false);
        string destinationCompanion=Path.Combine(Path.GetDirectoryName(path),Path.GetFileNameWithoutExtension(path)+".DATA");
        if(Directory.Exists(destinationCompanion)||File.Exists(destinationCompanion))throw new Fault("FILE_EXISTS","The destination .DATA companion path already exists. Choose a new project name.");
        string originalHash=ProjectPersistence.HashShared(source.SourcePath),workingHash=ProjectPersistence.HashShared(source.WorkingPath);
        var sourceDataHashes=Directory.EnumerateFiles(source.CompanionDirectory,"*",SearchOption.AllDirectories).ToDictionary(file=>file,ProjectPersistence.HashShared);
        string staging=Path.Combine(Path.GetDirectoryName(path),".avantage-project-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(staging);
        string stagedProject=Path.Combine(staging,Path.GetFileName(source.WorkingPath));
        string stagedCompanion=Path.Combine(staging,Path.GetFileName(source.CompanionDirectory));
        string publishMetadata=Path.Combine(Path.GetDirectoryName(path),".avantage-vgp-"+Guid.NewGuid().ToString("N")+".vgp");
        bool companionPublished=false,metadataPublished=false;
        try {
            ProjectPersistence.CopyShared(source.WorkingPath,stagedProject);File.SetAttributes(stagedProject,FileAttributes.Normal);Directory.CreateDirectory(stagedCompanion);
            foreach(var file in sourceDataHashes) {
                string relative=file.Key.Substring(source.CompanionDirectory.Length+1),copy=Path.Combine(stagedCompanion,relative);
                Directory.CreateDirectory(Path.GetDirectoryName(copy));ProjectPersistence.CopyShared(file.Key,copy);File.SetAttributes(copy,FileAttributes.Normal);
                if(ProjectPersistence.HashShared(copy)!=file.Value)throw new Fault("SOURCE_CHANGED","A private companion file changed during project save.");
            }
            var beforeData=new List<string>();var beforePeaks=new List<List<Dictionary<string,object>>>();
            Dictionary<int,List<Dictionary<string,object>>> beforePrimaries;
            var unchangedStreams=new Dictionary<string,byte[]>();
            foreach(string stream in new[]{"Contents","Grid Cells/Contents","Grid Cells/XRefs","Chem States/Contents","Procedural Peaks/Contents"})
                unchangedStreams.Add(stream,ProjectPersistence.ReadStream(stagedProject,stream.Split('/')));
            string table=Path.Combine(staging,"model.vgt");Dictionary<string,object> exported=null;
            using(var complete=new NativeProcessingProject{WorkingPath=stagedProject,PreserveAllEntries=true}) {
                Trace("project.stage.load.begin");
                complete.Load();int index=session.ProjectSpectrumIndex;
                Trace("project.stage.loaded");
                if(index<0||index>=complete.Spectra.Count)throw new Fault("SPECTRUM_NOT_FOUND","The selected project region is no longer available.");
                if(NativeSnapshot.Fingerprint(session.Data)!=NativeSnapshot.Fingerprint(complete.Spectra[index].Data))
                    throw new Fault("PROJECT_DATA_CHANGED","The session's raw data or energy calibration has changed. Complete-project model save currently requires the original spectrum data.");
                foreach(var spectrum in complete.Spectra) {
                    beforeData.Add(NativeSnapshot.Fingerprint(spectrum.Data));var peaks=complete.Document.PeaksFor(spectrum.Data);
                    try{beforePeaks.Add(NativePeakTablePersistence.ReadPeaks(peaks));}finally{foreach(var peak in peaks)Marshal.ReleaseComObject(peak);}
                }
                beforePrimaries=ProjectPersistence.PrimarySnapshots(complete);
                if(model.Peaks.Count>0) {
                    var templates=new List<IntPtr>();var originals=complete.Document.PeaksFor(complete.Spectra[index].Data);
                    try {
                        if(session.ProjectOriginalPeakIds.Count>0&&session.ProjectOriginalPeakIds.Count!=originals.Count)
                            throw new Fault("PROJECT_MODEL_CHANGED","The original model no longer matches the selected session. Reopen the project spectrum.");
                        foreach(var peak in model.Peaks) {
                            int originalIndex=session.ProjectOriginalPeakIds.IndexOf(peak.Id);IntPtr primary=IntPtr.Zero;
                            if(originalIndex>=0)originals[originalIndex].GetPeakEntry(out primary);
                            templates.Add(primary);
                        }
                        Trace("project.table.begin");exported=LivePeakTable.Save(table,session,templates);Trace("project.table.saved");
                    } finally{foreach(IntPtr primary in templates)if(primary!=IntPtr.Zero)Marshal.Release(primary);foreach(var peak in originals)Marshal.ReleaseComObject(peak);}
                }
                ProjectPersistence.ReplaceModel(complete,index,model.Peaks.Count==0?null:table);
                Trace("project.model.replaced");
                string newCompanionName=Path.GetFileName(destinationCompanion);
                ProjectPersistence.RebaseCompanion(complete.Document,stagedCompanion,newCompanionName);
                ProjectPersistence.SaveComplete(complete.Document,stagedProject);
                Trace("project.collections.saved");
            }
            string finalStagedCompanion=Path.Combine(staging,Path.GetFileName(destinationCompanion));
            if(!String.Equals(stagedCompanion,finalStagedCompanion,StringComparison.OrdinalIgnoreCase)) {
                // Native IDataSpace persistence may retain file handles after an
                // owned graph is disposed. Copy companion files; directory moves
                // would fail while those vendor handles are still alive.
                Directory.CreateDirectory(finalStagedCompanion);
                foreach(var file in sourceDataHashes) {
                    string relative=file.Key.Substring(source.CompanionDirectory.Length+1),copy=Path.Combine(finalStagedCompanion,relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(copy));ProjectPersistence.CopyShared(Path.Combine(stagedCompanion,relative),copy);
                }
            }
            foreach(var stream in unchangedStreams)
                if(!stream.Value.SequenceEqual(ProjectPersistence.ReadStream(stagedProject,stream.Key.Split('/'))))
                    throw new Fault("PROJECT_LAYOUT_CHANGED","Native model save changed unrelated document/layout storage: "+stream.Key);
            if(!NativePeakTablePersistence.Equivalent(originalGrid,ProjectGridValidation.Capture(stagedProject)))
                throw new Fault("PROJECT_LAYOUT_CHANGED","Native model save changed the grid/data reference mapping.");
            string verificationDirectory=Path.Combine(staging,"verification");Directory.CreateDirectory(verificationDirectory);
            string verificationPath=Path.Combine(verificationDirectory,Path.GetFileName(stagedProject));
            ProjectPersistence.CopyShared(stagedProject,verificationPath);
            string verificationCompanion=Path.Combine(verificationDirectory,Path.GetFileName(destinationCompanion));Directory.CreateDirectory(verificationCompanion);
            foreach(var file in sourceDataHashes) {
                string relative=file.Key.Substring(source.CompanionDirectory.Length+1),copy=Path.Combine(verificationCompanion,relative);
                Directory.CreateDirectory(Path.GetDirectoryName(copy));ProjectPersistence.CopyShared(Path.Combine(finalStagedCompanion,relative),copy);
            }
            using(var verification=new NativeProcessingProject{WorkingPath=verificationPath,PreserveAllEntries=true}) {
                Trace("project.verify.begin");verification.Load();Trace("project.verify.loaded");if(verification.Spectra.Count!=beforeData.Count)throw new Fault("PROJECT_ROUNDTRIP_MISMATCH","The saved project changed the number of spectra.");
                for(int i=0;i<verification.Spectra.Count;i++) {
                    if(NativeSnapshot.Fingerprint(verification.Spectra[i].Data)!=beforeData[i])throw new Fault("PROJECT_ROUNDTRIP_MISMATCH","The saved project changed spectrum data or energy calibration.");
                    var actual=verification.Document.PeaksFor(verification.Spectra[i].Data);
                    try {
                        object expected=i==session.ProjectSpectrumIndex?(object)NativePeakTablePersistence.ReadPeaks(model.Peaks.Select(peak=>peak.Native).ToArray()):beforePeaks[i];
                        if(!NativePeakTablePersistence.PersistedEquivalent(expected,NativePeakTablePersistence.ReadPeaks(actual)))
                            throw new Fault("PROJECT_ROUNDTRIP_MISMATCH","The saved project did not preserve the expected native model for spectrum "+i+".");
                    } finally{foreach(var peak in actual)Marshal.ReleaseComObject(peak);}
                }
                var afterPrimaries=ProjectPersistence.PrimarySnapshots(verification);
                foreach(var pair in beforePrimaries)if(pair.Key!=session.ProjectSpectrumIndex) {
                    List<Dictionary<string,object>> actual;
                    if(!afterPrimaries.TryGetValue(pair.Key,out actual)||!NativePeakTablePersistence.Equivalent(pair.Value,actual))
                        throw new Fault("PROJECT_ROUNDTRIP_MISMATCH","An unrelated primary peak's chemical or quantification metadata changed.");
                }
                if(exported!=null) {
                    List<Dictionary<string,object>> actual;
                    if(!afterPrimaries.TryGetValue(session.ProjectSpectrumIndex,out actual)||!NativePeakTablePersistence.Equivalent(exported["primaries"],actual))
                        throw new Fault("PROJECT_ROUNDTRIP_MISMATCH","The saved model did not preserve component chemical/quantification metadata.");
                }
            }
            if(ProjectPersistence.HashShared(source.SourcePath)!=originalHash||ProjectPersistence.HashShared(source.WorkingPath)!=workingHash||sourceDataHashes.Any(file=>ProjectPersistence.HashShared(file.Key)!=file.Value))
                throw new Fault("SOURCE_CHANGED","A source or private project snapshot changed during model save.");
            foreach(var file in sourceDataHashes) {
                string relative=file.Key.Substring(source.CompanionDirectory.Length+1);
                if(ProjectPersistence.HashShared(Path.Combine(finalStagedCompanion,relative))!=file.Value)throw new Fault("PROJECT_ROUNDTRIP_MISMATCH","A saved companion file changed.");
            }
            Directory.CreateDirectory(destinationCompanion);companionPublished=true;
            foreach(var file in sourceDataHashes) {
                string relative=file.Key.Substring(source.CompanionDirectory.Length+1),copy=Path.Combine(destinationCompanion,relative);
                Directory.CreateDirectory(Path.GetDirectoryName(copy));ProjectPersistence.CopyShared(Path.Combine(finalStagedCompanion,relative),copy);
                if(ProjectPersistence.HashShared(copy)!=file.Value)throw new Fault("PROJECT_ROUNDTRIP_MISMATCH","A published companion file changed.");
            }
            ProjectPersistence.CopyShared(stagedProject,publishMetadata);File.Move(publishMetadata,path);metadataPublished=true;
            return J.Obj("session_id",session.Id,"project_id",source.Id,"path",path,"companion_directory",destinationCompanion,"spectrum_index",session.ProjectSpectrumIndex,
                "peak_count",model.Peaks.Count,"roundtrip_verified",true,"source_unchanged",true,"raw_data_verified",true,"unrelated_spectra_verified",true,
                "grid_layout_preserved",true,"grid_references_verified",true,"primary_metadata_preserved",true,"retained_serialized_fitted_count",beforePeaks.Where((peaks,index)=>index!=session.ProjectSpectrumIndex).Sum(peaks=>peaks.Count)+model.Peaks.Count,
                "backend","Avantage complete native VGP collections; original OLE layout preserved");
        } catch {
            if(companionPublished&&!metadataPublished&&Directory.Exists(destinationCompanion))Directory.Delete(destinationCompanion,true);
            throw;
        } finally{
            if(File.Exists(publishMetadata))File.Delete(publishMetadata);
            // Avantage can keep private companion files open until the worker
            // exits. Cleanup must not turn a verified save into a failure.
            try{if(Directory.Exists(staging))Directory.Delete(staging,true);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }
    // Rebuild locally from native snapshots. Native constraints are attached only after the
    // common background, because SetBackground resets native constraints in Avantage.
    void ImportNativeSnapshots(SpectrumSession s,List<Dictionary<string,object>> saved) {
        if(saved.Count==0)return;if(saved.Count>100)throw new Fault("MODEL_SIZE","A spectrum may contain at most 100 fitted peaks.");
        var bgSaved=saved[0]["background"] as Dictionary<string,object>;
        if(bgSaved==null||saved.Any(p=>!NativePeakTablePersistence.Equivalent(p["background"],bgSaved)))throw new Fault("MIXED_BACKGROUNDS","This spectrum must have one common native background for conversational model edits.");
        int kind=J.Int(bgSaved,"type",-1);if(kind<0||kind>4)throw new Fault("UNSUPPORTED_BACKGROUND","Unsupported native background type.");
        var bgArgs=new Dictionary<string,object>((Dictionary<string,object>)bgSaved["extensions"]);
        bgArgs["kind"]=new[]{"linear","shirley","tougaard","smart","simple"}[kind];
        bgArgs["start_binding_ev"]=EnergyConversions.KineticToBinding(s.Data,J.Num(bgSaved,"end_kinetic_ev"));bgArgs["end_binding_ev"]=EnergyConversions.KineticToBinding(s.Data,J.Num(bgSaved,"start_kinetic_ev"));
        bgArgs["native_mode"]=bgSaved["mode"];bgArgs["points_to_average"]=bgSaved["points_to_average"];
        if(bgArgs.ContainsKey("shirley_smooth"))bgArgs["shirley_smooth"]=Convert.ToInt32(bgArgs["shirley_smooth"])!=0;
        if(kind==2){bgArgs["tougaard_type"]=J.Int(bgArgs,"tougaard_type",0)==0?"bc":"bcd";bgArgs["tougaard_energy_correction"]=J.Int(bgArgs,"tougaard_energy_correction",0)!=0;}
        NativeBackground background=null;NativePeakModel replacement=null;
        try {
            background=NativeBackground.Create(s,bgArgs);background.Background.SeteVgranularity(J.Num(bgSaved,"step_ev"));
            replacement=new NativePeakModel(s.Data);
            foreach(var peak in saved){int method=J.Int(peak,"native_gl_combine_method",-1);if(method<0||method>2)throw new Fault("UNSUPPORTED_MODEL","Unsupported native G/L combination method.");var spec=new Dictionary<string,object>(peak);spec["centre_ev"]=peak["centre_kinetic_ev"];spec["energy_scale"]="kinetic";spec["gl_combine_method"]=new[]{"product","sum","convolution"}[method];replacement.Add(spec);}
            replacement.AttachBackground(background.Background,typeof(IAvantageBackground));
            string[] attributes={null,"area","centre","height","fwhm","gl","tail_height","tail_mix","tail_exponential"},types={null,"fixed","plus_current","minus_current","plus_minus_current","linked","range","linked_range","linked_range_percent"},operators={"none","plus","minus","multiply","divide"};
            for(int i=0;i<saved.Count;i++)foreach(var constraint in (IEnumerable<Dictionary<string,object>>)saved[i]["constraints"]) {
                int attr=J.Int(constraint,"attribute",-1),type=J.Int(constraint,"type",-1),op=J.Int(constraint,"operator",-1);
                if(attr<1||attr>8||type<1||type>8||op<0||op>4)throw new Fault("UNSUPPORTED_CONSTRAINT","Unsupported native project peak constraint.");
                var spec=J.Obj("type",types[type],"value1",constraint["value1"],"value2",constraint["value2"],"value3",constraint["value3"],"operator",operators[op],"energy_scale","kinetic");
                if(constraint["reference_peak_index"]!=null){int target=Convert.ToInt32(constraint["reference_peak_index"]);if(target<0||target>=saved.Count)throw new Fault("EXTERNAL_CONSTRAINT","A fitted peak is linked to a peak in another spectrum; this model cannot be edited independently.");spec["reference_peak_id"]=replacement.Peaks[target].Id;}
                replacement.Constraint(replacement.Peaks[i].Id,attributes[attr],spec);
            }
            var actual=NativePeakTablePersistence.ReadPeaks(replacement.Peaks.Select(p=>p.Native).ToArray());
            if(!NativePeakTablePersistence.Equivalent(saved,actual))throw new Fault("MODEL_SNAPSHOT_MISMATCH","The private model did not preserve every native peak parameter, background and constraint.");
            replacement.Info();replacement.Evaluate(s.X,false);
            if(s.FitState is IDisposable)((IDisposable)s.FitState).Dispose();if(s.BackgroundState!=null)s.BackgroundState.Dispose();
            s.FitState=replacement;s.BackgroundState=background;replacement=null;background=null;
        }finally{if(replacement!=null)replacement.Dispose();if(background!=null)background.Dispose();}
    }
}
}
