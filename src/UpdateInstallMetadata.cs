using System;
using System.IO;
using System.Collections.Generic;
using System.Web.Script.Serialization;
using Microsoft.Win32;

static class UpdateInstallMetadata {
    const string RegistryPath="Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\FC27PadLock";
    sealed class Snapshot {public string Root,Receipt;public bool Registered,HadVersion;public object DisplayVersion;public RegistryValueKind Kind;}
    public static object Capture(string root) {
        var snapshot=new Snapshot{Root=Path.GetFullPath(root)};string path=Path.Combine(root,"setup-installation.json");
        UpdateService.EnsureNoReparse(path);if(File.Exists(path))snapshot.Receipt=File.ReadAllText(path);
        using(var registration=Registry.CurrentUser.OpenSubKey(RegistryPath)) {
            if(snapshot.Receipt!=null&&registration!=null&&string.Equals(Convert.ToString(registration.GetValue("InstallLocation")),snapshot.Root,StringComparison.OrdinalIgnoreCase)) {
                snapshot.Registered=true;snapshot.DisplayVersion=registration.GetValue("DisplayVersion",null);snapshot.HadVersion=snapshot.DisplayVersion!=null;
                if(snapshot.HadVersion)snapshot.Kind=registration.GetValueKind("DisplayVersion");
            }
        }
        return snapshot;
    }
    public static void Restore(string root,object captured) {
        var snapshot=captured as Snapshot;if(snapshot==null)return;
        if(!string.Equals(snapshot.Root,Path.GetFullPath(root),StringComparison.OrdinalIgnoreCase))throw new IOException("Metadata snapshot belongs to another folder.");
        string path=Path.Combine(root,"setup-installation.json");UpdateService.EnsureNoReparse(path);
        if(snapshot.Receipt!=null){string temp=path+".restore.tmp";File.WriteAllText(temp,snapshot.Receipt);if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}
        if(snapshot.Registered)using(var registration=Registry.CurrentUser.OpenSubKey(RegistryPath,true)) {
            if(registration==null||!string.Equals(Convert.ToString(registration.GetValue("InstallLocation")),snapshot.Root,StringComparison.OrdinalIgnoreCase))throw new IOException("Installation registration changed during update.");
            if(snapshot.HadVersion)registration.SetValue("DisplayVersion",snapshot.DisplayVersion,snapshot.Kind);else registration.DeleteValue("DisplayVersion",false);
        }
    }
    public static void Apply(string root,string version,Dictionary<string,string> sha256) {
        string receipt=Path.Combine(root,"setup-installation.json");
        if(!File.Exists(receipt))return; // Portable packages have no Windows registration.
        UpdateService.EnsureNoReparse(receipt);
        var serializer=new JavaScriptSerializer();
        var values=serializer.Deserialize<Dictionary<string,object>>(File.ReadAllText(receipt));
        if(values==null||!values.ContainsKey("product")||Convert.ToString(values["product"])!="FC27PadLock"||
           !values.ContainsKey("installRoot")||!string.Equals(Path.GetFullPath(Convert.ToString(values["installRoot"])),Path.GetFullPath(root),StringComparison.OrdinalIgnoreCase))throw new IOException("Installation receipt does not belong to this folder.");
        // version is the original folder identity used by Uninstall.ps1. Updating in place keeps it.
        values["displayVersion"]=version;
        values["files"]=new List<string>(sha256.Keys).ToArray();values["sha256"]=sha256;
        values["updatedUtc"]=DateTime.UtcNow.ToString("o");values["status"]="installed";
        using(var registration=Registry.CurrentUser.OpenSubKey(RegistryPath,true)) {
            if(registration!=null&&string.Equals(Convert.ToString(registration.GetValue("InstallLocation")),Path.GetFullPath(root),StringComparison.OrdinalIgnoreCase))registration.SetValue("DisplayVersion",version);
        }
        string temp=receipt+".update.tmp";File.WriteAllText(temp,serializer.Serialize(values));File.Replace(temp,receipt,null);
    }
    public static void Tests(Action<string,bool> check) {
        string root=Path.Combine(Path.GetTempPath(),"padlock-metadata-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);string path=Path.Combine(root,"setup-installation.json");
        string original=new JavaScriptSerializer().Serialize(new{product="FC27PadLock",installRoot=root,version="0.1.0-preview.1",extension="retained"});
        try {
            File.WriteAllText(path,original);object snapshot=Capture(root);
            Apply(root,"0.1.0-preview.2",new Dictionary<string,string>{{"VERSION",new string('a',64)}});
            var updated=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(path));
            check("update retains installation folder identity",Convert.ToString(updated["version"])=="0.1.0-preview.1");
            check("update records new display version",Convert.ToString(updated["displayVersion"])=="0.1.0-preview.2");
            check("update preserves receipt extensions",Convert.ToString(updated["extension"])=="retained");
            Restore(root,snapshot);check("update restores exact receipt on rollback",File.ReadAllText(path)==original);
        } finally {UpdateService.EnsureNoReparse(root);Directory.Delete(root,true);}
    }
}
