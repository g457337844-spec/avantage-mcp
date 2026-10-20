using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace AvantageMcp {
// Held handles protect private folders across workers, including entire VGP
// graphs without an attached spectrum session. Old unmarked folders are never
// automatically pruned, because their ownership cannot be established.
public sealed class WorkspaceLeases : IDisposable {
    public const string Marker = ".avantage-mcp-lease";
    readonly string root;
    readonly Dictionary<string,FileStream> held = new Dictionary<string,FileStream>(StringComparer.OrdinalIgnoreCase);
    public WorkspaceLeases(string rootPath){root=Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar);}
    public void Sync(IEnumerable<string> directories){
        var active=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(string directory in directories){
            if(String.IsNullOrWhiteSpace(directory))continue;
            string full=Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
            if(!String.Equals(Path.GetDirectoryName(full),root,StringComparison.OrdinalIgnoreCase))continue;
            active.Add(full);
            if(!held.ContainsKey(full))held.Add(full,new FileStream(Path.Combine(full,Marker),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None));
        }
        foreach(string closed in held.Keys.Where(p=>!active.Contains(p)).ToArray()){
            held[closed].Dispose();held.Remove(closed);
        }
    }
    public void Dispose(){foreach(var stream in held.Values)stream.Dispose();held.Clear();}
}
}
