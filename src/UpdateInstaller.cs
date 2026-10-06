using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Principal;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

static class UpdateInstaller {
    static string StatusPath { get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FC27PadLock","update-status.json");} }
    static bool ComponentRunning(string kind){
        string name="Local\\FC27PadLock"+kind+"-"+WindowsIdentity.GetCurrent().User.Value+"-Instance";
        Mutex instance;try{instance=Mutex.OpenExisting(name);}catch(WaitHandleCannotBeOpenedException){return false;}
        using(instance){bool acquired=false;try{try{acquired=instance.WaitOne(0);}catch(AbandonedMutexException){acquired=true;}return !acquired;}finally{if(acquired)instance.ReleaseMutex();}}
    }
    static bool ComponentsRunning(){foreach(string kind in new[]{"UI","","Overlay","Monitor"})if(ComponentRunning(kind))return true;return false;}
    static void Record(string version,string result,string error,string backup,bool rollbackComplete){try{Directory.CreateDirectory(Path.GetDirectoryName(StatusPath));File.WriteAllText(StatusPath,new JavaScriptSerializer().Serialize(new{version=version,result=result,error=error,backupRoot=backup,rollbackComplete=rollbackComplete,updatedUtc=DateTime.UtcNow.ToString("o")}));}catch{}}
    static bool HealthyHeartbeat(Dictionary<string,object> state,int pid,DateTime processStarted,DateTime now){
        try{
            DateTime started,updated;int savedPid;
            return state!=null&&int.TryParse(Convert.ToString(state["pid"]),out savedPid)&&savedPid==pid&&
                DateTime.TryParse(Convert.ToString(state["startedUtc"]),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out started)&&started.Kind==DateTimeKind.Utc&&
                DateTime.TryParse(Convert.ToString(state["updatedUtc"]),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out updated)&&updated.Kind==DateTimeKind.Utc&&
                started>=processStarted&&(started-processStarted).TotalSeconds<=10&&updated>=started&&now>=updated&&(now-updated).TotalSeconds<5&&
                state["armed"] is bool&&(bool)state["armed"]&&state["hooksInstalled"] is bool&&(bool)state["hooksInstalled"]&&
                state["hookThreadAlive"] is bool&&(bool)state["hookThreadAlive"]&&state["stopped"] is bool&&!(bool)state["stopped"];
        }catch{return false;}
    }
    static bool GuardReady(string root){
        try{
            var state=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(StatusPath),"status.json")));
            using(var guard=Process.GetProcessById(Convert.ToInt32(state["pid"])))return !guard.HasExited&&guard.ProcessName=="FC27PadLock"&&
                string.Equals(Path.GetFullPath(guard.MainModule.FileName),Path.GetFullPath(Path.Combine(root,"build","FC27PadLock.exe")),StringComparison.OrdinalIgnoreCase)&&
                HealthyHeartbeat(state,guard.Id,guard.StartTime.ToUniversalTime(),DateTime.UtcNow);
        }catch{return false;}
    }
    static void Restart(string root,string mode){
        using(var process=Process.Start(new ProcessStartInfo(Path.Combine(root,"build","FC27PadLockControl.exe"),mode){UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden})){
            if(process==null)throw new IOException("The control panel did not start.");
            var wait=Stopwatch.StartNew();while(wait.ElapsedMilliseconds<20000){
                if(process.HasExited)throw new IOException("The control panel exited before protection was ready.");
                if(ComponentRunning("UI")&&ComponentRunning("")&&GuardReady(root))return;
                Thread.Sleep(100);
            }
            throw new IOException("Restart did not confirm a fresh, armed protection heartbeat.");
        }
    }
    static void Signal(string kind,string command){
        try{using(var signal=EventWaitHandle.OpenExisting("Local\\FC27PadLock"+kind+"-"+WindowsIdentity.GetCurrent().User.Value+"-"+command))signal.Set();}catch(WaitHandleCannotBeOpenedException){}
    }
    static bool StopComponents(){
        Signal("UI","Quit"); // The UI first fences its asynchronous guard-start worker.
        var wait=Stopwatch.StartNew();while(ComponentRunning("UI")&&wait.ElapsedMilliseconds<15000)Thread.Sleep(100);
        if(ComponentRunning("UI"))return false;
        // Also handle orphaned components when the UI failed during startup/shutdown.
        Signal("","Stop");Signal("Overlay","Quit");Signal("Monitor","Quit");
        wait.Restart();while(ComponentsRunning()&&wait.ElapsedMilliseconds<15000)Thread.Sleep(100);
        return !ComponentsRunning();
    }
    static bool Recover(bool shutdownRequested,bool candidateStarted,Func<bool> stopCandidate,Func<bool> restore,Action restart){
        try{
            if(candidateStarted&&!stopCandidate())return false; // Never replace a still-running candidate.
            if(!restore())return false;
            if(shutdownRequested)restart();
            return true;
        }catch{return false;}
    }
    static void Swap(string source,string destination){
        UpdateService.EnsureNoReparse(destination);Directory.CreateDirectory(Path.GetDirectoryName(destination));
        string temporary=destination+".padlock-update-"+Guid.NewGuid().ToString("N")+".tmp";
        try{File.Copy(source,temporary,false);if(File.Exists(destination))File.Replace(temporary,destination,null);else File.Move(temporary,destination);}finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
    static int Apply(string manifest){
        UpdateService.Pending pending=null;string backup=null;bool shutdownRequested=false,candidateStarted=false,rollbackComplete=true,metadataTouched=false;object metadata=null;var modified=new List<string>();var existing=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try{
            UpdateService.EnsureNoReparse(manifest);pending=new JavaScriptSerializer().Deserialize<UpdateService.Pending>(File.ReadAllText(manifest));
            if(pending==null||!UpdateService.ValidVersion(pending.Version)||!Path.IsPathRooted(pending.TargetRoot)||!Path.IsPathRooted(pending.ZipPath)||!Path.IsPathRooted(pending.ChecksumPath)||(pending.Mode!="--panel"&&pending.Mode!="--background"))throw new InvalidDataException("Invalid update plan.");
            string root=Path.GetFullPath(pending.TargetRoot),work=Path.GetDirectoryName(Path.GetFullPath(manifest));
            if(root.TrimEnd(Path.DirectorySeparatorChar)==Path.GetPathRoot(root).TrimEnd(Path.DirectorySeparatorChar)||UpdateService.DevelopmentRoot(root))throw new IOException("This folder cannot be updated automatically.");
            UpdateService.EnsureNoReparse(root);UpdateService.EnsureNoReparse(pending.ZipPath);UpdateService.EnsureNoReparse(pending.ChecksumPath);
            string installedVersion=File.ReadAllText(Path.Combine(root,"VERSION")).Trim().TrimStart('\uFEFF');if(UpdateService.CompareVersions(pending.Version,installedVersion)<=0)throw new InvalidDataException("The queued update is not newer than the installed version.");
            foreach(string app in new[]{"FC27PadLock.exe","FC27PadLockControl.exe","FC27PadLockOverlay.exe","FC27PadLockMonitor.exe"})if(!File.Exists(Path.Combine(root,"build",app)))throw new IOException("Target is not a complete Pad Lock installation.");
            string zipName="FC27PadLock-v"+pending.Version+"-win-x64.zip";
            if(Path.GetFileName(pending.ZipPath)!=zipName||UpdateService.Hash(pending.ZipPath)!=UpdateService.ExpectedHash(File.ReadAllText(pending.ChecksumPath),zipName))throw new InvalidDataException("Update archive verification failed.");
            string verified=Path.Combine(work,"verified-"+Guid.NewGuid().ToString("N"));UpdateService.Extract(pending.ZipPath,pending.Version,verified);
            // Validate every destination before stopping a working guard.
            foreach(string relative in UpdateService.Allowed)UpdateService.EnsureNoReparse(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
            if(UpdateService.GameRunning()){Record(pending.Version,"deferred",null,null,true);return 2;}
            backup=Path.Combine(work,"backup-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(backup);metadata=UpdateInstallMetadata.Capture(root);
            foreach(string relative in UpdateService.Allowed){string source=Path.Combine(verified,relative.Replace('/',Path.DirectorySeparatorChar));if(!File.Exists(source))continue;string destination=Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar));if(File.Exists(destination)){string saved=Path.Combine(backup,relative.Replace('/',Path.DirectorySeparatorChar));Directory.CreateDirectory(Path.GetDirectoryName(saved));File.Copy(destination,saved,false);existing.Add(relative);}}
            // Check once more immediately before graceful shutdown.
            if(UpdateService.GameRunning()){Record(pending.Version,"deferred",null,backup,true);return 2;}
            shutdownRequested=true;
            if(!StopComponents())throw new IOException("Pad Lock components are still running; no files were replaced.");
            foreach(string relative in UpdateService.Allowed){string source=Path.Combine(verified,relative.Replace('/',Path.DirectorySeparatorChar));if(!File.Exists(source))continue;if(UpdateService.GameRunning())throw new IOException("FC27 started during the update; restoring the previous version.");string destination=Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar));Swap(source,destination);modified.Add(relative);}
            var hashes=new Dictionary<string,string>();foreach(string relative in UpdateService.Allowed){string file=Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar));if(File.Exists(file))hashes[relative]=UpdateService.Hash(file);}
            metadataTouched=true;UpdateInstallMetadata.Apply(root,pending.Version,hashes);
            candidateStarted=true;Restart(root,pending.Mode);Record(pending.Version,"installed",null,backup,true);return 0;
        }catch(Exception error){
            rollbackComplete=Recover(shutdownRequested,candidateStarted,StopComponents,delegate{
                bool restored=true;
                if(pending!=null&&backup!=null)restored=RollbackFiles(pending.TargetRoot,backup,modified,existing);
                if(metadataTouched&&pending!=null)try{UpdateInstallMetadata.Restore(pending.TargetRoot,metadata);}catch{restored=false;}
                return restored;
            },delegate{Restart(pending.TargetRoot,pending.Mode);});
            Record(pending==null?null:pending.Version,"failed",error.Message,backup,rollbackComplete);
            MessageBox.Show((rollbackComplete?"自動更新できませんでした。元のファイルを保持しました。\nAutomatic update failed; the previous files were preserved.\n":"自動更新の復元を完了できません。診断ログとバックアップを確認してください。\nUpdate rollback is incomplete; check the update log and backup.\n")+error.Message,"FC27 Pad Lock",MessageBoxButtons.OK,MessageBoxIcon.Warning);return 1;
        }
    }
    static bool RollbackFiles(string root,string backup,List<string> modified,HashSet<string> existing){bool complete=true;for(int i=modified.Count-1;i>=0;i--){string relative=modified[i],destination=Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar));try{if(existing.Contains(relative))Swap(Path.Combine(backup,relative.Replace('/',Path.DirectorySeparatorChar)),destination);else{UpdateService.EnsureNoReparse(destination);File.Delete(destination);}}catch{complete=false;}}return complete;}
    static void FixtureZip(string path,string version,string extra,bool duplicate,bool linked){using(var archive=ZipFile.Open(path,ZipArchiveMode.Create)){foreach(string relative in new[]{"VERSION","LICENSE","Uninstall.ps1","build/FC27PadLock.exe","build/FC27PadLockControl.exe","build/FC27PadLockOverlay.exe","build/FC27PadLockMonitor.exe","build/FC27PadLockUpdater.exe"}){var entry=archive.CreateEntry("FC27PadLock-v"+version+"-win-x64/"+relative);if(linked&&relative=="LICENSE")entry.ExternalAttributes=unchecked((int)0xA1FF0000);using(var writer=new StreamWriter(entry.Open()))writer.Write(relative=="VERSION"?version:"fixture");}if(extra!=null){var entry=archive.CreateEntry(extra);using(var writer=new StreamWriter(entry.Open()))writer.Write("unexpected");}if(duplicate){var entry=archive.CreateEntry("FC27PadLock-v"+version+"-win-x64/LICENSE");using(var writer=new StreamWriter(entry.Open()))writer.Write("duplicate");}}}
    static void FileTests(Action<string,bool> check){
        string root=Path.Combine(Path.GetTempPath(),"fc27-updater-self-test-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try{
            string zip=Path.Combine(root,"valid.zip"),stage=Path.Combine(root,"valid");FixtureZip(zip,"0.1.1",null,false,false);UpdateService.Extract(zip,"0.1.1",stage);check("update extracts validated complete archive",File.ReadAllText(Path.Combine(stage,"VERSION")).Trim()=="0.1.1");
            for(int i=0;i<3;i++){string badZip=Path.Combine(root,"bad"+i+".zip");FixtureZip(badZip,"0.1.1",i==0?"FC27PadLock-v0.1.1-win-x64/../outside.txt":null,i==1,i==2);bool rejected=false;try{UpdateService.Extract(badZip,"0.1.1",Path.Combine(root,"bad"+i));}catch(InvalidDataException){rejected=true;}check(i==0?"archive traversal rejected before extraction":i==1?"archive duplicate rejected before extraction":"archive symbolic link rejected before extraction",rejected&&!Directory.Exists(Path.Combine(root,"bad"+i)));}
            string destination=Path.Combine(root,"target"),backup=Path.Combine(root,"backup");Directory.CreateDirectory(destination);Directory.CreateDirectory(backup);File.WriteAllText(Path.Combine(destination,"VERSION"),"old");File.Copy(Path.Combine(destination,"VERSION"),Path.Combine(backup,"VERSION"));
            var changed=new List<string>();var existing=new HashSet<string>(StringComparer.OrdinalIgnoreCase){"VERSION"};Swap(Path.Combine(stage,"VERSION"),Path.Combine(destination,"VERSION"));changed.Add("VERSION");Swap(Path.Combine(stage,"LICENSE"),Path.Combine(destination,"LICENSE"));changed.Add("LICENSE");check("transaction replaces files with staged content",File.ReadAllText(Path.Combine(destination,"VERSION")).Trim()=="0.1.1");
            bool failure=false;try{Swap(Path.Combine(stage,"missing-file"),Path.Combine(destination,"Uninstall.ps1"));}catch(IOException){failure=true;}check("transaction rollback after mid-update failure",failure&&RollbackFiles(destination,backup,changed,existing)&&File.ReadAllText(Path.Combine(destination,"VERSION"))=="old"&&!File.Exists(Path.Combine(destination,"LICENSE")));
        }finally{UpdateService.EnsureNoReparse(root);Directory.Delete(root,true);}
    }
    static void RecoveryTests(Action<string,bool> check){
        string calls="";
        Func<bool> stop=delegate{calls+="stop;";return true;};Func<bool> restore=delegate{calls+="restore;";return true;};Action restart=delegate{calls+="restart;";};
        check("partial shutdown attempts protection recovery",Recover(true,false,stop,restore,restart)&&calls=="restore;restart;");
        calls="";check("failed candidate stops before restoring and restarting",Recover(true,true,stop,restore,restart)&&calls=="stop;restore;restart;");
        calls="";check("running candidate prevents unsafe rollback",!Recover(true,true,delegate{calls+="stop;";return false;},restore,restart)&&calls=="stop;");
        calls="";check("failed file restoration prevents mixed-version restart",!Recover(true,true,stop,delegate{calls+="restore;";return false;},restart)&&calls=="stop;restore;");
        calls="";check("recovery restart failure is reported incomplete",!Recover(true,false,stop,restore,delegate{throw new IOException("fixture startup failure");})&&calls=="restore;");
        calls="";check("pre-shutdown error does not launch components",Recover(false,false,stop,restore,restart)&&calls=="restore;");
        DateTime now=new DateTime(2026,10,7,0,0,0,DateTimeKind.Utc),started=now.AddSeconds(-2);
        var state=new Dictionary<string,object>{{"pid",42},{"startedUtc",started.ToString("o")},{"updatedUtc",now.ToString("o")},{"armed",true},{"hooksInstalled",true},{"hookThreadAlive",true},{"stopped",false}};
        check("restart accepts fresh armed matching guard",HealthyHeartbeat(state,42,started,now));
        check("restart rejects stale heartbeat",!HealthyHeartbeat(state,42,started,now.AddSeconds(6)));
        check("restart rejects unrelated PID",!HealthyHeartbeat(state,43,started,now));
        check("restart rejects reused PID",!HealthyHeartbeat(state,42,now,now));
        state["armed"]=false;check("restart rejects paused guard",!HealthyHeartbeat(state,42,started,now));state["armed"]=true;
        state["hooksInstalled"]=false;check("restart rejects missing hooks",!HealthyHeartbeat(state,42,started,now));state["hooksInstalled"]=true;
        state["hookThreadAlive"]=false;check("restart rejects dead hook thread",!HealthyHeartbeat(state,42,started,now));
    }
    static int SelfTest(string path){var tests=new List<object>();int failures=0;Action<string,bool> check=delegate(string name,bool passed){tests.Add(new{name=name,passed=passed});if(!passed)failures++;};UpdateService.PureTests(check);FileTests(check);RecoveryTests(check);UpdateInstallMetadata.Tests(check);File.WriteAllText(path,new JavaScriptSerializer().Serialize(new{version=ProductVersion.Value,failures=failures,testCount=tests.Count,tests=tests,hooksInstalled=false,inputSent=false,ipcSent=false,processOperations=false,networkAccess=false,updateApplied=false,temporaryFilesWritten=true}));return failures==0?0:1;}
    [STAThread]static int Main(string[] args){
        if(args.Length==2&&args[0]=="--self-test"&&Path.IsPathRooted(args[1])){try{return SelfTest(args[1]);}catch{return 1;}}
        if(args.Length!=2||args[0]!="--apply"||!Path.IsPathRooted(args[1]))return 3;
        bool fresh;using(var mutex=new Mutex(true,"Local\\FC27PadLockUpdater-"+WindowsIdentity.GetCurrent().User.Value+"-Instance",out fresh)){if(!fresh)return 2;return Apply(args[1]);}
    }
}
