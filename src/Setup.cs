using System;
using System.IO;
using System.IO.Compression;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

internal static class Setup {
    internal const string Version=ProductVersion.Value;
    const string Resource="FC27PadLock.Payload.zip";
    const string RegistryKey="Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\FC27PadLock";
    static readonly HashSet<string> Allowed=new HashSet<string>(StringComparer.OrdinalIgnoreCase){
        "Manual.html","docs/MANUAL.ja.html","docs/MANUAL.en.html","docs/SETTINGS.ja.html","docs/SETTINGS.en.html","docs/TROUBLESHOOTING.ja.html","docs/COMPATIBILITY.ja.html","docs/PRIVACY.ja.html","docs/OVERLAY.ja.html","docs/MONITOR.ja.html","docs/REPORTING.ja.html","Start.cmd","Install.ps1","Uninstall.ps1","README.md","LICENSE","VERSION","CHANGELOG.md","SECURITY.md",
        "build/FC27PadLock.exe","build/FC27PadLockControl.exe","build/FC27PadLockOverlay.exe","build/FC27PadLockMonitor.exe","build/FC27PadLockUpdater.exe",
        "assets/PadLock.ico","assets/PadLock.png","docs/MANUAL.ja.md","docs/MANUAL.en.md","docs/TROUBLESHOOTING.ja.md","docs/TROUBLESHOOTING.en.md",
        "docs/COMPATIBILITY.ja.md","docs/COMPATIBILITY.en.md","docs/PRIVACY.ja.md","docs/PRIVACY.en.md","docs/OVERLAY.ja.md","docs/OVERLAY.en.md","docs/overlay-preview.png",
        "docs/SETTINGS.ja.md","docs/SETTINGS.en.md","docs/MONITOR.ja.md","docs/REPORTING.ja.md","config/app-settings.example.json","config/app-settings.schema.json"};
    static readonly string[] Required={"build/FC27PadLock.exe","build/FC27PadLockControl.exe","build/FC27PadLockOverlay.exe","build/FC27PadLockMonitor.exe","build/FC27PadLockUpdater.exe","Uninstall.ps1","LICENSE","VERSION"};
    internal static string AppRoot {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FC27PadLock","App");}}
    internal static string Root {get{return Path.Combine(AppRoot,Version);}}
    static bool Under(string path,string root){return Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);}
    internal static string Relative(string name) {
        if(String.IsNullOrEmpty(name)||name.IndexOf('\\')>=0||name.IndexOf(':')>=0||name.StartsWith("/")||Path.IsPathRooted(name))throw new InvalidDataException("Unsafe ZIP path.");
        string[] parts=name.Split('/');foreach(string p in parts)if(p==".."||p=="."||p.Length==0||p.TrimEnd(' ','.')!=p)throw new InvalidDataException("Unsafe ZIP path segment.");
        if(parts.Length<2||parts[0]!="FC27PadLock-v"+Version+"-win-x64")throw new InvalidDataException("Unexpected payload root.");
        string relative=String.Join("/",parts,1,parts.Length-1);
        if(!Allowed.Contains(relative))throw new InvalidDataException("Unexpected payload file: "+relative);
        if(!Under(Path.Combine(Root,relative.Replace('/',Path.DirectorySeparatorChar)),Root))throw new InvalidDataException("ZIP path escape.");
        return relative;
    }
    static void ValidateDirectory(string name){ string prefix="FC27PadLock-v"+Version+"-win-x64/"; if(name==prefix)return; if(!name.StartsWith(prefix,StringComparison.Ordinal))throw new InvalidDataException("Unsafe ZIP directory."); string relative=name.Substring(prefix.Length); if(relative!="build/"&&relative!="assets/"&&relative!="docs/"&&relative!="config/")throw new InvalidDataException("Unexpected ZIP directory."); }
    static Dictionary<string,byte[]> Payload() {
        var result=new Dictionary<string,byte[]>(StringComparer.OrdinalIgnoreCase);
        using(Stream stream=Assembly.GetExecutingAssembly().GetManifestResourceStream(Resource)){
            if(stream==null)throw new InvalidDataException("Embedded payload missing.");
            using(var zip=new ZipArchive(stream,ZipArchiveMode.Read))foreach(var entry in zip.Entries){
                if(entry.FullName.EndsWith("/")) { ValidateDirectory(entry.FullName); continue; }
                string relative=Relative(entry.FullName);
                if(entry.Length>32*1024*1024||result.ContainsKey(relative))throw new InvalidDataException("Invalid payload size or duplicate.");
                using(Stream input=entry.Open())using(var output=new MemoryStream()){input.CopyTo(output);if(output.Length!=entry.Length)throw new InvalidDataException("Payload length mismatch.");result.Add(relative,output.ToArray());}
            }
        }
        foreach(string required in Required)if(!result.ContainsKey(required))throw new InvalidDataException("Missing payload: "+required);
        if(System.Text.Encoding.UTF8.GetString(result["VERSION"]).Trim().TrimStart('\uFEFF')!=Version)throw new InvalidDataException("Payload version mismatch.");
        return result;
    }
    static string Hash(byte[] data){using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(data)).Replace("-","").ToLowerInvariant();}
    static bool Running(string name) {
        try{using(var mutex=Mutex.OpenExisting(name)){bool held=false;try{try{held=mutex.WaitOne(0);}catch(AbandonedMutexException){held=true;}return !held;}finally{if(held)mutex.ReleaseMutex();}}}catch(WaitHandleCannotBeOpenedException){return false;}
    }
    static void CheckIdle(){string sid=WindowsIdentity.GetCurrent().User.Value;foreach(string kind in new[]{"","UI","Overlay","Monitor"})if(Running("Local\\FC27PadLock"+kind+"-"+sid+"-Instance"))throw new InvalidOperationException("Close FC27 Pad Lock from its tray menu before installing. / タスクトレイからアプリを終了してからインストールしてください。");}
    static dynamic ShortcutShell(){return Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));}
    static void CheckShortcut(dynamic shell,string path){if(!File.Exists(path))return;dynamic link=shell.CreateShortcut(path);string target=link.TargetPath;if(String.IsNullOrEmpty(target)||!Under(target,AppRoot))throw new IOException("Shortcut belongs to another application: "+path);}
    static void Link(dynamic shell,string path,string target,string args){dynamic link=shell.CreateShortcut(path);link.TargetPath=target;link.Arguments=args;link.WorkingDirectory=Root;link.IconLocation=Path.Combine(Root,"assets","PadLock.ico");link.Save();}
    internal static void Install(bool startup,bool desktop,bool start){
        CheckIdle();var payload=Payload();
        if(Directory.Exists(Root))throw new IOException("This version directory already exists. Preserve it and uninstall first, or use a new version. / 同じバージョンが既に存在します。先にアンインストールしてください。");
        string desktopPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),"FC27 Pad Lock.lnk");
        string startMenuPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),"FC27 Pad Lock.lnk");
        string startupPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),"FC27 Pad Lock.lnk");
        dynamic shell=ShortcutShell();CheckShortcut(shell,startMenuPath);if(desktop)CheckShortcut(shell,desktopPath);if(startup)CheckShortcut(shell,startupPath);
        using(var key=Registry.CurrentUser.OpenSubKey(RegistryKey))if(key!=null){string previous=key.GetValue("InstallLocation","") as string;if(String.IsNullOrEmpty(previous)||!Under(previous,AppRoot))throw new IOException("An unrelated Installed Apps entry exists.");}
        // Refuse directory junctions along the deployment path.
        for(string p=Root;!String.IsNullOrEmpty(p);p=Path.GetDirectoryName(p)){if(Directory.Exists(p)&&(File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new IOException("Installation path contains a reparse point.");}
        Directory.CreateDirectory(Root);
        var hashes=new Dictionary<string,string>();foreach(var item in payload)hashes.Add(item.Key,Hash(item.Value));
        string control=Path.Combine(Root,"build","FC27PadLockControl.exe");
        var receipt=new{schemaVersion=1,product="FC27PadLock",version=Version,status="installing",installRoot=Root,appRoot=AppRoot,controlPath=control,desktopShortcut=desktop?desktopPath:null,startupShortcut=startup?startupPath:null,startMenuShortcut=startMenuPath,registryKey="HKCU:\\"+RegistryKey,files=new List<string>(hashes.Keys).ToArray(),sha256=hashes,installedUtc=DateTime.UtcNow.ToString("o")};
        string receiptPath=Path.Combine(Root,"setup-installation.json");File.WriteAllText(receiptPath,new JavaScriptSerializer().Serialize(receipt));
        foreach(var item in payload){string path=Path.Combine(Root,item.Key.Replace('/',Path.DirectorySeparatorChar));Directory.CreateDirectory(Path.GetDirectoryName(path));using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write))file.Write(item.Value,0,item.Value.Length);}
        Link(shell,startMenuPath,control,"--panel");if(desktop)Link(shell,desktopPath,control,"--panel");if(startup)Link(shell,startupPath,control,"--background");
        using(var key=Registry.CurrentUser.CreateSubKey(RegistryKey)){key.SetValue("DisplayName","FC27 Pad Lock");key.SetValue("DisplayVersion",Version);key.SetValue("Publisher","anyscape-maruo");key.SetValue("InstallLocation",Root);key.SetValue("DisplayIcon",Path.Combine(Root,"assets","PadLock.ico"));key.SetValue("UninstallString","powershell.exe -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File \""+Path.Combine(Root,"Uninstall.ps1")+"\"");key.SetValue("NoModify",1,RegistryValueKind.DWord);key.SetValue("NoRepair",1,RegistryValueKind.DWord);}
        var completed=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(receiptPath));completed["status"]="installed";File.WriteAllText(receiptPath,new JavaScriptSerializer().Serialize(completed));
        if(start)Process.Start(new ProcessStartInfo(control,"--panel"){UseShellExecute=true,WorkingDirectory=Root});
    }
    static int SelfTest(string path){var tests=new List<object>();int failures=0;Action<string,bool> check=delegate(string n,bool ok){tests.Add(new{name=n,passed=ok});if(!ok)failures++;};string root="FC27PadLock-v"+Version+"-win-x64/";
        check("allowed relative",Relative(root+"build/FC27PadLock.exe")=="build/FC27PadLock.exe");
        foreach(string bad in new[]{"/LICENSE","C:/LICENSE",root+"../LICENSE",root+"build/../../LICENSE",root+"build\\FC27PadLock.exe",root+"src/Setup.cs",root+"status.json",root+"build/FC27PadLock.exe.",root+"build//FC27PadLock.exe"}){bool rejected=false;try{Relative(bad);}catch(InvalidDataException){rejected=true;}check("reject "+bad,rejected);}
        bool goodDirectory=true;try{ValidateDirectory(root);ValidateDirectory(root+"build/");}catch{goodDirectory=false;}check("allow manifest directories",goodDirectory);bool badDirectory=false;try{ValidateDirectory(root+"../");}catch(InvalidDataException){badDirectory=true;}check("reject unsafe directory",badDirectory);
        check("install arguments",Mode(new[]{"--install","--no-start"})=="InstallNoStart");check("invalid arguments",Mode(new[]{"--install","--bad"})==null);check("no default install",Mode(new string[0])=="UI");check("nonabsolute report rejected",Mode(new[]{"--self-test","relative.json"})==null);
        try{var payload=Payload();check("embedded manifest",payload.Count>=Required.Length);}catch(Exception e){check("embedded manifest "+e.Message,false);}
        File.WriteAllText(path,new JavaScriptSerializer().Serialize(new{version=Version,failures=failures,testCount=tests.Count,tests=tests,hooksInstalled=false,inputSent=false,installPerformed=false,uiStarted=false}));return failures==0?0:1;
    }
    static string Mode(string[] args){if(args.Length==0)return "UI";if(args.Length==2&&args[0]=="--self-test"&&Path.IsPathRooted(args[1]))return "SelfTest";if(args.Length==1&&args[0]=="--install")return "Install";if(args.Length==2&&args[0]=="--install"&&args[1]=="--no-start")return "InstallNoStart";return null;}
    [STAThread]static int Main(string[] args){string mode=Mode(args);if(mode==null)return 3;if(mode=="SelfTest"){try{return SelfTest(args[1]);}catch{return 1;}}try{if(mode=="Install"||mode=="InstallNoStart"){Install(true,true,mode=="Install");return 0;}Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);Application.Run(new SetupForm());return 0;}catch(Exception e){if(mode=="UI")MessageBox.Show(e.Message,"FC27 Pad Lock Setup",MessageBoxButtons.OK,MessageBoxIcon.Error);else Trace.WriteLine(e.ToString());return 1;}}
}
internal sealed class SetupForm:Form {
    ComboBox language=new ComboBox();CheckBox startup=new CheckBox(),desktop=new CheckBox();Button install=new Button();Label intro=new Label();
    public SetupForm(){Text="FC27 Pad Lock Setup";ClientSize=new Size(500,245);FormBorderStyle=FormBorderStyle.FixedDialog;MaximizeBox=false;StartPosition=FormStartPosition.CenterScreen;language.Items.AddRange(new object[]{"日本語","English"});language.DropDownStyle=ComboBoxStyle.DropDownList;language.SetBounds(24,18,180,28);language.SelectedIndexChanged+=delegate{Translate();};intro.SetBounds(24,58,450,55);startup.SetBounds(24,118,450,25);desktop.SetBounds(24,148,450,25);startup.Checked=desktop.Checked=true;install.SetBounds(280,194,192,32);install.Click+=delegate{try{install.Enabled=false;Setup.Install(startup.Checked,desktop.Checked,true);MessageBox.Show(language.SelectedIndex==0?"インストールしました。ゲームは起動していません。":"Installed. The game has not been launched.",Text);Close();}catch(Exception e){MessageBox.Show(e.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Error);install.Enabled=true;}};Controls.AddRange(new Control[]{language,intro,startup,desktop,install});language.SelectedIndex=System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName=="ja"?0:1;}
    void Translate(){bool ja=language.SelectedIndex==0;intro.Text=ja?"ユーザー専用にインストールします（管理者権限不要）。\n更新時は先にタスクトレイからアプリを終了してください。":"Installs for the current user. No administrator rights required.\nClose the app from its tray menu before updating.";startup.Text=ja?"Windowsログイン時に常駐する":"Start in the system tray at Windows sign-in";desktop.Text=ja?"デスクトップにアプリのショートカットを作成":"Create an application shortcut on the desktop";install.Text=ja?"インストール":"Install";}
}
