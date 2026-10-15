using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace AvantageMcp {
public sealed partial class Engine {
    object SaveLiveDocument(string id,string path) {
        path=Path.GetFullPath(path);
        if(!String.Equals(Path.GetExtension(path),".vgp",StringComparison.OrdinalIgnoreCase))throw new Fault("INVALID_PATH","Choose a new .VGP file for the complete processing document. Use save_spectrum for a standalone VGD.");
        string companion=Path.ChangeExtension(path,".DATA");
        if(File.Exists(path)||Directory.Exists(path)||File.Exists(companion)||Directory.Exists(companion))throw new Fault("FILE_EXISTS","The new VGP and its companion DATA path must not exist.");
        if(!Directory.Exists(Path.GetDirectoryName(path)))throw new Fault("DIRECTORY_NOT_FOUND","Create the output directory first.");
        var document=Documents.Find(id);
        var pointers=ProcessingCom.EnumerateData(document.Pointer);
        List<Dictionary<string,object>> before;
        try{before=CaptureLiveDocument(document,pointers);}
        finally{foreach(var p in pointers)Marshal.Release(p);}
        InvalidateLiveLinks(id);
        string folder=Path.Combine(workRoot,"document-save-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        string checkpoint=Path.Combine(folder,"checkpoint.vgp");
        // Capture a private checkpoint, rebase its complete owned graph and open
        // the verified final copy in a fresh document/view.
        Documents.SaveCopy(id,checkpoint);Trace("live.save.checkpoint.done");
        NativeProcessingProject source=null,verification=null;
        string staging=Path.Combine(Path.GetDirectoryName(path),".avantage-document-"+Guid.NewGuid().ToString("N"));
        bool companionPublished=false,metadataPublished=false;
        try {
            var opened=(Dictionary<string,object>)OpenProcessingProject(checkpoint);
            source=ProcessingProject((string)opened["project_id"]);VerifyCompleteSnapshot(source,before);
            var originalGrid=ProjectGridValidation.Capture(source.WorkingPath);VerifyGridVisibility(originalGrid,before);
            var originalPrimaries=ProjectPersistence.PrimarySnapshots(source);
            Directory.CreateDirectory(staging);
            string staged=Path.Combine(staging,Path.GetFileName(path)),stagedCompanion=Path.Combine(staging,Path.GetFileName(companion));
            ProjectPersistence.CopyShared(source.WorkingPath,staged);Directory.CreateDirectory(stagedCompanion);
            var hashes=Directory.EnumerateFiles(source.CompanionDirectory,"*",SearchOption.AllDirectories).ToDictionary(file=>file,ProjectPersistence.HashShared);
            foreach(var item in hashes) {
                string target=Path.Combine(stagedCompanion,item.Key.Substring(source.CompanionDirectory.Length+1));
                Directory.CreateDirectory(Path.GetDirectoryName(target));ProjectPersistence.CopyShared(item.Key,target);
                if(ProjectPersistence.HashShared(target)!=item.Value)throw new Fault("SAVE_NOT_VERIFIED","A companion spectrum changed during save.");
            }
            ProjectPersistence.RebaseCompanion(source.Document,source.CompanionDirectory,Path.GetFileName(companion));
            ProjectPersistence.SaveComplete(source.Document,staged);
            if(!NativePeakTablePersistence.Equivalent(originalGrid,ProjectGridValidation.Capture(staged)))throw new Fault("SAVE_NOT_VERIFIED","The saved grid/data mapping changed.");
            foreach(string stream in new[]{"Contents","Grid Cells/Contents","Grid Cells/XRefs"})
                if(!ProjectPersistence.ReadStream(source.WorkingPath,stream.Split('/')).SequenceEqual(ProjectPersistence.ReadStream(staged,stream.Split('/'))))
                    throw new Fault("SAVE_NOT_VERIFIED","The saved project changed its grid layout.");
            opened=(Dictionary<string,object>)OpenProcessingProject(staged);
            verification=ProcessingProject((string)opened["project_id"]);VerifyCompleteSnapshot(verification,before);
            var verifiedPrimaries=ProjectPersistence.PrimarySnapshots(verification);
            if(originalPrimaries.Count!=verifiedPrimaries.Count||originalPrimaries.Any(pair=>!verifiedPrimaries.ContainsKey(pair.Key)||!NativePeakTablePersistence.Equivalent(pair.Value,verifiedPrimaries[pair.Key])))throw new Fault("SAVE_NOT_VERIFIED","The saved project changed primary peak metadata.");
            Directory.CreateDirectory(companion);companionPublished=true;
            foreach(var item in hashes) {
                string relative=item.Key.Substring(source.CompanionDirectory.Length+1),target=Path.Combine(companion,relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target));ProjectPersistence.CopyShared(Path.Combine(stagedCompanion,relative),target);
                if(ProjectPersistence.HashShared(target)!=item.Value)throw new Fault("SAVE_NOT_VERIFIED","A published companion spectrum changed.");
            }
            ProjectPersistence.CopyShared(staged,path);metadataPublished=true;Trace("live.save.published");
            Documents.Open(path);
            document=Documents.FindPath(path);pointers=ProcessingCom.EnumerateData(document.Pointer);
            try{if(!NativePeakTablePersistence.PersistedEquivalent(before,CaptureLiveDocument(document,pointers)))throw new Fault("LIVE_DISPLAY_NOT_VERIFIED","The saved project exists but its application readback differs: "+path);}
            finally{foreach(var p in pointers)Marshal.Release(p);}
            return J.Obj("document",document.Info(),"path",path,"bytes",new FileInfo(path).Length,"companion_data_directory",companion,
                "checkpoint_path",checkpoint,"roundtrip_verified",true,"live_readback_verified",true,"raw_data_verified",true,
                "all_spectrum_models_verified",true,"primary_metadata_preserved",true,"grid_layout_preserved",true,"grid_bindings_verified",true,
                "note","Avantage displays the complete verified saved copy. Reattach a spectrum before further live edits.");
        }catch {
            // A published VGP is retained even if application display fails.
            // Partial unpublished companions are kept for diagnosis; no user
            // files or directories are recursively removed on a failure path.
            if(companionPublished&&!metadataPublished)Console.Error.WriteLine("Incomplete new companion directory retained: "+companion);
            throw;
        }finally {
            if(verification!=null){verification.Dispose();projects.Remove(verification.Id);}
            if(source!=null){source.Dispose();projects.Remove(source.Id);}
            string stagingParent=Path.GetDirectoryName(Path.GetFullPath(staging));
            if(String.Equals(stagingParent,Path.GetDirectoryName(path),StringComparison.OrdinalIgnoreCase))
                try{if(Directory.Exists(staging))Directory.Delete(staging,true);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }
    static void VerifyCompleteSnapshot(NativeProcessingProject project,IList<Dictionary<string,object>> expected) {
        if(project.Spectra.Count!=expected.Count)throw new Fault("SAVE_NOT_VERIFIED","The saved document has a different number of spectra.");
        for(int i=0;i<project.Spectra.Count;i++) {
            var peaks=project.Document.PeaksFor(project.Spectra[i].Data);
            try {
                if(!NativePeakTablePersistence.Equivalent(expected[i]["data"],NativeSnapshot.Fingerprint(project.Spectra[i].Data))||
                   !NativePeakTablePersistence.PersistedEquivalent(expected[i]["model"],NativePeakTablePersistence.ReadPeaks(peaks)))
                    throw new Fault("SAVE_NOT_VERIFIED","The saved document differs from spectrum "+i+".");
            }finally{ProcessingCom.ReleasePeaks(peaks);}
        }
    }
}
}
