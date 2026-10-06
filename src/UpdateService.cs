using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

static class UpdateService {
    const string Repository="https://github.com/anyscape-maruo/fc27-pad-lock";
    const long MaximumZip=100L*1024*1024,MaximumExpanded=200L*1024*1024;
    public static readonly string[] Allowed={
        "Manual.html","docs/MANUAL.ja.html","docs/MANUAL.en.html","docs/SETTINGS.ja.html","docs/SETTINGS.en.html","docs/TROUBLESHOOTING.ja.html","docs/COMPATIBILITY.ja.html","docs/PRIVACY.ja.html","docs/OVERLAY.ja.html","docs/MONITOR.ja.html","docs/REPORTING.ja.html","Start.cmd","Install.ps1","Uninstall.ps1","README.md","LICENSE","VERSION","CHANGELOG.md","SECURITY.md",
        "build/FC27PadLock.exe","build/FC27PadLockControl.exe","build/FC27PadLockOverlay.exe","build/FC27PadLockMonitor.exe","build/FC27PadLockUpdater.exe",
        "assets/PadLock.ico","assets/PadLock.png","docs/MANUAL.ja.md","docs/MANUAL.en.md","docs/TROUBLESHOOTING.ja.md","docs/COMPATIBILITY.ja.md","docs/PRIVACY.ja.md","docs/OVERLAY.ja.md","docs/overlay-preview.png","docs/SETTINGS.ja.md","docs/SETTINGS.en.md","docs/MONITOR.ja.md","docs/REPORTING.ja.md","config/app-settings.example.json","config/app-settings.schema.json"
    };
    static readonly string[] Required={"VERSION","LICENSE","Uninstall.ps1","build/FC27PadLock.exe","build/FC27PadLockControl.exe","build/FC27PadLockOverlay.exe","build/FC27PadLockMonitor.exe","build/FC27PadLockUpdater.exe"};
    public sealed class Release { public string Version,ZipUrl,ChecksumUrl,ZipName,Digest; }
    public sealed class Pending { public string ZipPath,ChecksumPath,StageRoot,TargetRoot,Version,Mode; }
    static readonly Regex VersionPattern=new Regex(@"^(\d+)\.(\d+)\.(\d+)(?:-([A-Za-z0-9.]+))?$",RegexOptions.CultureInvariant);
    public static bool ValidVersion(string version){return version!=null&&VersionPattern.IsMatch(version);}
    public static int CompareVersions(string left,string right){
        Match a=VersionPattern.Match(left??""),b=VersionPattern.Match(right??"");if(!a.Success||!b.Success)throw new InvalidDataException("Invalid release version.");
        for(int i=1;i<=3;i++){long x,y;if(!long.TryParse(a.Groups[i].Value,out x)||!long.TryParse(b.Groups[i].Value,out y))throw new InvalidDataException("Version number is too large.");int n=x.CompareTo(y);if(n!=0)return n;}
        string ap=a.Groups[4].Value,bp=b.Groups[4].Value;if(ap==bp)return 0;if(ap.Length==0)return 1;if(bp.Length==0)return -1;
        string[] aa=ap.Split('.'),bb=bp.Split('.');for(int i=0;i<Math.Min(aa.Length,bb.Length);i++){long x,y;bool xn=long.TryParse(aa[i],out x),yn=long.TryParse(bb[i],out y);int n=xn&&yn?x.CompareTo(y):xn?-1:yn?1:string.CompareOrdinal(aa[i],bb[i]);if(n!=0)return n;}return aa.Length.CompareTo(bb.Length);
    }
    static string Value(Dictionary<string,object> item,string key){object value;return item.TryGetValue(key,out value)?Convert.ToString(value,CultureInfo.InvariantCulture):null;}
    public static Release SelectRelease(string json,string current){
        object[] releases=new JavaScriptSerializer{MaxJsonLength=2*1024*1024}.Deserialize<object[]>(json);Release best=null;
        foreach(object item in releases??new object[0]){
            var release=item as Dictionary<string,object>;object flag;if(release==null||!release.TryGetValue("draft",out flag)||!(flag is bool)||(bool)flag)continue;
            if(!release.TryGetValue("prerelease",out flag)||!(flag is bool))continue;if((bool)flag&&current.IndexOf('-')<0)continue;
            string tag=Value(release,"tag_name"),version=tag!=null&&tag.StartsWith("v",StringComparison.Ordinal)?tag.Substring(1):tag;
            if(!ValidVersion(version)||CompareVersions(version,current)<=0||(best!=null&&CompareVersions(version,best.Version)<=0))continue;
            string zipName="FC27PadLock-v"+version+"-win-x64.zip",zip=null,sums=null,digest=null;object assets;
            if(!release.TryGetValue("assets",out assets)||!(assets is IEnumerable))continue;
            foreach(object assetItem in (IEnumerable)assets){var asset=assetItem as Dictionary<string,object>;if(asset==null)continue;string name=Value(asset,"name"),url=Value(asset,"browser_download_url");string expected=Repository+"/releases/download/"+Uri.EscapeDataString(tag)+"/"+name;
                if(url!=expected)continue;if(name==zipName){zip=url;digest=Value(asset,"digest");}else if(name=="SHA256SUMS.txt")sums=url;
            }
            if(zip!=null&&sums!=null)best=new Release{Version=version,ZipUrl=zip,ChecksumUrl=sums,ZipName=zipName,Digest=digest};
        }return best;
    }
    static bool TrustedResponse(Uri uri){string host=uri.Host;return uri.Scheme=="https"&&(host=="api.github.com"||host=="github.com"||host=="release-assets.githubusercontent.com"||host=="objects.githubusercontent.com");}
    static void Download(string url,string path,long limit){
        ServicePointManager.SecurityProtocol|=(SecurityProtocolType)3072;
        var request=(HttpWebRequest)WebRequest.Create(url);request.UserAgent="FC27-Pad-Lock/"+ProductVersion.Value;request.Accept="application/vnd.github+json";request.Timeout=20000;request.ReadWriteTimeout=20000;
        using(var response=(HttpWebResponse)request.GetResponse()){
            if(!TrustedResponse(response.ResponseUri)||response.ContentLength>limit)throw new InvalidDataException("Untrusted or oversized update response.");
            using(var source=response.GetResponseStream())using(var target=new FileStream(path,FileMode.CreateNew,FileAccess.Write)){byte[] buffer=new byte[81920];long total=0;int count;while((count=source.Read(buffer,0,buffer.Length))>0){total+=count;if(total>limit)throw new InvalidDataException("Update download exceeds the size limit.");target.Write(buffer,0,count);}}
        }
    }
    public static string Hash(string path){using(var sha=SHA256.Create())using(var file=File.OpenRead(path))return BitConverter.ToString(sha.ComputeHash(file)).Replace("-","").ToLowerInvariant();}
    public static string ExpectedHash(string sums,string name){
        string result=null;foreach(string line in (sums??"").Split(new[]{'\r','\n'},StringSplitOptions.RemoveEmptyEntries)){var match=Regex.Match(line,@"^([a-fA-F0-9]{64})[ \t]+\*?([^\r\n]+)$");if(match.Success&&match.Groups[2].Value==name){if(result!=null)throw new InvalidDataException("Duplicate archive checksum.");result=match.Groups[1].Value.ToLowerInvariant();}}
        if(result==null)throw new InvalidDataException("Archive checksum is missing.");return result;
    }
    public static bool GameRunning(){foreach(string name in new[]{"FC27","FC27_Trial","FC27_Showcase"}){Process[] games=Process.GetProcessesByName(name);try{if(games.Length>0)return true;}finally{foreach(var process in games)process.Dispose();}}return false;}
    public static bool DevelopmentRoot(string root){return Directory.Exists(Path.Combine(root,".git"))||File.Exists(Path.Combine(root,".git"))||File.Exists(Path.Combine(root,"build","build.ps1"));}
    public static Pending Check(string target,string mode){
        if(DevelopmentRoot(target))return null;
        string updates=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FC27PadLock","updates");Directory.CreateDirectory(updates);
        string work=Path.Combine(updates,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(work);
        try{
        string index=Path.Combine(work,"releases.json");Download("https://api.github.com/repos/anyscape-maruo/fc27-pad-lock/releases?per_page=30",index,2*1024*1024);
        var release=SelectRelease(File.ReadAllText(index),ProductVersion.Value);if(release==null){CleanWork(updates,work);return null;}
        string zip=Path.Combine(work,release.ZipName),sums=Path.Combine(work,"SHA256SUMS.txt");Download(release.ChecksumUrl,sums,128*1024);Download(release.ZipUrl,zip,MaximumZip);
        string expected=ExpectedHash(File.ReadAllText(sums),release.ZipName),actual=Hash(zip);if(expected!=actual)throw new InvalidDataException("The downloaded archive checksum does not match.");
        if(!string.IsNullOrEmpty(release.Digest)&&release.Digest!="sha256:"+actual)throw new InvalidDataException("GitHub archive digest does not match.");
        string stage=Path.Combine(work,"payload");Extract(zip,release.Version,stage);
        return new Pending{ZipPath=zip,ChecksumPath=sums,StageRoot=stage,TargetRoot=target,Version=release.Version,Mode=mode=="--background"?"--background":"--panel"};
        }catch{CleanWork(updates,work);throw;}
    }
    static void CleanWork(string updates,string work){try{string absolute=Path.GetFullPath(work),prefix=Path.GetFullPath(updates).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;if(absolute.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)&&Regex.IsMatch(Path.GetFileName(absolute),"^[a-f0-9]{32}$")){EnsureNoReparse(absolute);Directory.Delete(absolute,true);}}catch{}}
    public static string Relative(string name,string version){
        string prefix="FC27PadLock-v"+version+"-win-x64/";
        if(!ValidVersion(version)||!name.StartsWith(prefix,StringComparison.Ordinal))throw new InvalidDataException("Unexpected update archive root.");
        string relative=name.Substring(prefix.Length);if(Array.IndexOf(Allowed,relative)<0)throw new InvalidDataException("Unexpected or unsafe update path: "+name);return relative;
    }
    public static void EnsureNoReparse(string path){for(var current=new DirectoryInfo(Path.GetFullPath(path));current!=null;current=current.Parent)if(current.Exists&&(current.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("Update path contains a junction or symbolic link.");if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("Update file is a symbolic link.");}
    public static void Extract(string zip,string version,string root){
        if(Directory.Exists(root))throw new IOException("Update staging directory already exists.");EnsureNoReparse(Path.GetDirectoryName(root));
        var seen=new HashSet<string>(StringComparer.OrdinalIgnoreCase);long expanded=0;
        using(var archive=ZipFile.OpenRead(zip)){
            if(archive.Entries.Count>128)throw new InvalidDataException("Too many update entries.");
            foreach(var entry in archive.Entries){
                int type=(entry.ExternalAttributes>>16)&0xF000;if(type==0xA000||(entry.ExternalAttributes&0x400)!=0)throw new InvalidDataException("Linked ZIP entries are forbidden.");
                if(entry.FullName.EndsWith("/",StringComparison.Ordinal)){string prefix="FC27PadLock-v"+version+"-win-x64/";if(entry.FullName!=prefix&&entry.FullName!=prefix+"build/"&&entry.FullName!=prefix+"docs/"&&entry.FullName!=prefix+"assets/"&&entry.FullName!=prefix+"config/")throw new InvalidDataException("Unsafe ZIP directory.");continue;}
                string relative=Relative(entry.FullName,version);if(!seen.Add(relative))throw new InvalidDataException("Duplicate update entry.");expanded+=entry.Length;
                if(entry.Length<0||entry.Length>32L*1024*1024||expanded>MaximumExpanded)throw new InvalidDataException("Expanded update exceeds the size limit.");
            }
            foreach(string required in Required)if(!seen.Contains(required))throw new InvalidDataException("Required update component missing: "+required);
            Directory.CreateDirectory(root);
            foreach(var entry in archive.Entries){if(entry.FullName.EndsWith("/",StringComparison.Ordinal))continue;string relative=Relative(entry.FullName,version),path=Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar));Directory.CreateDirectory(Path.GetDirectoryName(path));using(var source=entry.Open())using(var destination=new FileStream(path,FileMode.CreateNew,FileAccess.Write)){byte[] buffer=new byte[81920];long total=0;int count;while((count=source.Read(buffer,0,buffer.Length))>0){total+=count;if(total>entry.Length)throw new InvalidDataException("ZIP expanded length mismatch.");destination.Write(buffer,0,count);}if(total!=entry.Length)throw new InvalidDataException("ZIP expanded length mismatch.");}}
        }
        if(File.ReadAllText(Path.Combine(root,"VERSION")).Trim().TrimStart('\uFEFF')!=version)throw new InvalidDataException("Update VERSION does not match release.");
    }
    public static bool StartInstaller(Pending pending){
        if(GameRunning())return false;EnsureNoReparse(pending.TargetRoot);EnsureNoReparse(pending.StageRoot);
        string work=Path.GetDirectoryName(pending.ZipPath),manifest=Path.Combine(work,"pending.json"),helper=Path.Combine(work,"updater-"+Guid.NewGuid().ToString("N")+".exe");
        File.WriteAllText(manifest,new JavaScriptSerializer().Serialize(pending));
        // Run a separate copy so the installed updater can itself be replaced.
        File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"FC27PadLockUpdater.exe"),helper,false);
        using(var process=Process.Start(new ProcessStartInfo(helper,"--apply \""+manifest+"\""){UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden})){}
        return true;
    }
    public static void PureTests(Action<string,bool> check){
        check("update newer preview",CompareVersions("0.1.0-preview.10","0.1.0-preview.2")>0);check("update stable follows preview",CompareVersions("0.1.0","0.1.0-preview.2")>0);check("update prevents downgrade",CompareVersions("0.0.9","0.1.0-preview.1")<0);
        string prefix="FC27PadLock-v0.1.1-win-x64/";check("update accepts required exe path",Relative(prefix+"build/FC27PadLock.exe","0.1.1")=="build/FC27PadLock.exe");
        foreach(string bad in new[]{prefix+"../VERSION",prefix+"build\\FC27PadLock.exe",prefix+"build//FC27PadLock.exe",prefix+"status.json",prefix+"build/FC27PadLock.exe.","/VERSION"}){bool rejected=false;try{Relative(bad,"0.1.1");}catch(InvalidDataException){rejected=true;}check("update rejects "+bad,rejected);}
        string hash=new string('a',64);check("update exact archive hash",ExpectedHash(hash+"  release.zip","release.zip")==hash);bool missing=false;try{ExpectedHash(hash+"  other.zip","release.zip");}catch(InvalidDataException){missing=true;}check("update rejects unrelated checksum",missing);
        bool duplicate=false;try{ExpectedHash(hash+"  release.zip\n"+hash+"  release.zip","release.zip");}catch(InvalidDataException){duplicate=true;}check("update rejects duplicate checksum",duplicate);
        var json=new JavaScriptSerializer();Func<string,bool,bool,string,object> release=delegate(string version,bool draft,bool preview,string download){return new{tag_name="v"+version,draft=draft,prerelease=preview,assets=new[]{new{name="FC27PadLock-v"+version+"-win-x64.zip",browser_download_url=download??Repository+"/releases/download/v"+version+"/FC27PadLock-v"+version+"-win-x64.zip"},new{name="SHA256SUMS.txt",browser_download_url=Repository+"/releases/download/v"+version+"/SHA256SUMS.txt"}}};};
        check("update ignores draft",SelectRelease(json.Serialize(new[]{release("0.1.1",true,false,null)}),"0.1.0")==null);check("stable ignores preview channel",SelectRelease(json.Serialize(new[]{release("0.2.0-preview.1",false,true,null)}),"0.1.0")==null);check("preview allows preview channel",SelectRelease(json.Serialize(new[]{release("0.1.0-preview.2",false,true,null)}),"0.1.0-preview.1")!=null);check("update rejects foreign download",SelectRelease(json.Serialize(new[]{release("0.1.1",false,false,"https://example.com/file.zip")}),"0.1.0")==null);
    }
}
