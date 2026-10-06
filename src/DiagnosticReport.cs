using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Web.Script.Serialization;

static class DiagnosticReport {
    public const string PublicVersion=ProductVersion.Value;
    static readonly string[] Widgets={"protection","keyboard","mouse","monitor","warning","shortcut"};
    static readonly HashSet<string> EventKinds=new HashSet<string>(new[]{"guard_status_unavailable","guard_stopped","guard_process_unconfirmed","guard_heartbeat_stale","guard_hook_registration_inactive","guard_armed","guard_paused","observer_observing","observer_registration_failed","observer_thread_stopped","normal_non_escape_chain_passed_guard_reported_armed","normal_chain_passed_guard_not_armed","injected_chain_passed_out_of_scope","raw_ll_mismatch_suspected"});
    static Dictionary<string,object> Parse(string json){try{return new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json)??new Dictionary<string,object>();}catch{return new Dictionary<string,object>();}}
    static void Bool(Dictionary<string,object> src,Dictionary<string,object> dst,string name){object v;if(src.TryGetValue(name,out v)&&v is bool)dst[name]=v;}
    static void Number(Dictionary<string,object> src,Dictionary<string,object> dst,string name){object v;double n;if(src.TryGetValue(name,out v)&&v!=null&&double.TryParse(Convert.ToString(v,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out n)&&!double.IsNaN(n)&&!double.IsInfinity(n)&&n>=0&&n<=9007199254740991)dst[name]=n;}
    static void Enum(Dictionary<string,object> src,Dictionary<string,object> dst,string name,string[] allowed){object v;if(src.TryGetValue(name,out v)&&v is string&&Array.IndexOf(allowed,(string)v)>=0)dst[name]=v;}
    static void Utc(Dictionary<string,object> src,Dictionary<string,object> dst,string name){object v;DateTime utc;if(src.TryGetValue(name,out v)&&DateTime.TryParse(Convert.ToString(v),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out utc)&&utc.Kind==DateTimeKind.Utc)dst[name]=utc.ToString("o");}
    static void Shortcut(Dictionary<string,object> src,Dictionary<string,object> dst,string name){object v;ToggleShortcut key;if(src.TryGetValue(name,out v)&&v is string&&ToggleShortcut.TryParse((string)v,out key))dst[name]=key.Label;}
    public static Dictionary<string,object> SanitizeStatus(string json,bool monitor){
        var src=Parse(json);var dst=new Dictionary<string,object>();
        foreach(string name in new[]{"startedUtc","updatedUtc"})Utc(src,dst,name);
        foreach(string name in new[]{"armed","stopped","hooksInstalled","hookThreadAlive","gameForeground","rawInputGuaranteed","physicalInputGuaranteed","observerHooksRegistered","observerThreadAlive","rawInputObservationEnabled","typedContentStored","coordinatesStored","deviceIdsStored"})Bool(src,dst,name);
        foreach(string name in new[]{"normalBlocked","injectedPassthrough","trialSeconds","remainingTrialSeconds","normalObserved","normalChainBlocked","normalChainPassed","nonEscapeChainPassed","injectedObserved","injectedChainBlocked","injectedChainPassed","rawKeyboardReports","rawMouseReports","normalKeyboardObserved","normalKeyboardBlocked","normalKeyboardPassed","normalMouseObserved","normalMouseBlocked","normalMousePassed"})Number(src,dst,name);
        Enum(src,dst,"guardHealth",new[]{"guard_status_unavailable","guard_stopped","guard_process_unconfirmed","guard_heartbeat_stale","guard_hook_registration_inactive","guard_armed","guard_paused"});Enum(src,dst,"observerHealth",new[]{"observing","registration_failed","thread_stopped"});
        object kind;if(src.TryGetValue("lastWarningKind",out kind)&&kind is string&&EventKinds.Contains((string)kind))dst["lastWarningKind"]=kind;Utc(src,dst,"lastWarningUtc");
        Shortcut(src,dst,"toggleHotkey");Shortcut(src,dst,"emergencyHotkey");
        dst["actualMatchFailureDetected"]=false;dst["gameInternalArrivalKnown"]=false;return dst;
    }
    public static Dictionary<string,object> SanitizeSettings(string json,bool overlay){
        var src=Parse(json);var dst=new Dictionary<string,object>();
        if(overlay){Bool(src,dst,"visible");Enum(src,dst,"position",new[]{"top-left","top-right","bottom-left","bottom-right"});Enum(src,dst,"layout",new[]{"compact","detailed"});return dst;}
        Enum(src,dst,"language",new[]{"ja","en"});Shortcut(src,dst,"toggleHotkey");object value;
        if(src.TryGetValue("schemaVersion",out value)&&Convert.ToString(value)=="1")dst["schemaVersion"]=1;
        double opacity;if(src.TryGetValue("overlayOpacity",out value)&&value!=null&&double.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out opacity)&&!double.IsNaN(opacity)&&!double.IsInfinity(opacity))dst["overlayOpacity"]=Math.Max(0.25,Math.Min(1,opacity));
        if(src.TryGetValue("overlayWidgets",out value)&&value is IEnumerable&&!(value is string)){var names=new List<string>();foreach(object item in (IEnumerable)value)if(item is string&&Array.IndexOf(Widgets,(string)item)>=0&&!names.Contains((string)item))names.Add((string)item);dst["overlayWidgets"]=names.ToArray();}
        return dst;
    }
    public static string SanitizeEvents(string text){var queue=new Queue<string>();using(var reader=new StringReader(text??"")){string line;while((line=reader.ReadLine())!=null){if(line.Length>4096)continue;var src=Parse(line);object kind;if(!src.TryGetValue("kind",out kind)||!(kind is string)||!EventKinds.Contains((string)kind))continue;var dst=new Dictionary<string,object>{{"kind",kind},{"actualMatchFailureDetected",false}};Utc(src,dst,"utc");if(!dst.ContainsKey("utc"))continue;queue.Enqueue(new JavaScriptSerializer().Serialize(dst));while(queue.Count>200)queue.Dequeue();}}return string.Join(Environment.NewLine,queue.ToArray());}
    public static string ErrorType(string text){if(string.IsNullOrWhiteSpace(text))return null;string first=text.Split(new[]{'\r','\n'},2)[0];int colon=first.IndexOf(':');if(colon>=0)first=first.Substring(0,colon);string[] allowed={"System.Exception","System.InvalidOperationException","System.IO.IOException","System.IO.FileNotFoundException","System.UnauthorizedAccessException","System.TimeoutException","System.ComponentModel.Win32Exception","System.ArgumentException","System.ObjectDisposedException"};return Array.IndexOf(allowed,first)>=0?first:"UnclassifiedError";}
    public static string BuildReport(string guard,string monitor,string settings,string overlay,string error){return new JavaScriptSerializer().Serialize(new{publicVersion=PublicVersion,settingsSchema=1,guard=SanitizeStatus(guard,false),monitor=SanitizeStatus(monitor,true),settings=SanitizeSettings(settings,false),overlay=SanitizeSettings(overlay,true),errorType=ErrorType(error),privacy=new{typedInput=false,keyCodes=false,coordinates=false,deviceIds=false,userNames=false,personalPaths=false},actualMatchFailureDetected=false});}
    static string VersionLabel(string value){Version parsed;return Version.TryParse(value,out parsed)?parsed.ToString():"unavailable";}
    public static string BuildManifest(string osVersion,string platform,bool os64,bool process64,string clrVersion,DateTime generatedUtc){return new JavaScriptSerializer().Serialize(new{publicVersion=PublicVersion,settingsSchema=1,generatedUtc=generatedUtc.ToUniversalTime().ToString("o"),osVersion=VersionLabel(osVersion),platform=Array.IndexOf(new[]{"Win32NT","Win32Windows","Win32S","WinCE","Unix","MacOSX","Xbox"},platform)>=0?platform:"Unknown",is64BitOperatingSystem=os64,is64BitProcess=process64,clrVersion=VersionLabel(clrVersion),files=new[]{"manifest.json","report.json","events.jsonl"}});}
    static string Read(string directory,string file,int limit){try{using(var reader=new StreamReader(Path.Combine(directory,file))){char[] buffer=new char[limit];int length=reader.ReadBlock(buffer,0,buffer.Length);return new string(buffer,0,length);}}catch{return null;}}
    static void Entry(ZipArchive archive,string name,string text){using(var writer=new StreamWriter(archive.CreateEntry(name,CompressionLevel.Optimal).Open(),new UTF8Encoding(false)))writer.Write(text);}
    public static void Save(string output,string directory){
        if(!Path.IsPathRooted(output))throw new ArgumentException("An absolute report path is required.");
        string report=BuildReport(Read(directory,"status.json",131072),Read(directory,"monitor-status.json",131072),Read(directory,"app-settings.json",131072),Read(directory,"overlay-settings.json",131072),Read(directory,"error.txt",1024));
        string events=SanitizeEvents(Read(directory,"monitor-events.jsonl",1048576));
        string manifest=BuildManifest(Environment.OSVersion.Version.ToString(),Environment.OSVersion.Platform.ToString(),Environment.Is64BitOperatingSystem,Environment.Is64BitProcess,Environment.Version.ToString(),DateTime.UtcNow);
        // The UI's SaveFileDialog handles overwrite consent. A temporary file keeps an earlier report intact on errors.
        string temp=output+"."+Guid.NewGuid().ToString("N")+".tmp";try{using(var stream=new FileStream(temp,FileMode.CreateNew,FileAccess.Write,FileShare.None))using(var archive=new ZipArchive(stream,ZipArchiveMode.Create)){Entry(archive,"manifest.json",manifest);Entry(archive,"report.json",report);Entry(archive,"events.jsonl",events);}if(File.Exists(output))File.Replace(temp,output,null);else File.Move(temp,output);}finally{try{if(File.Exists(temp))File.Delete(temp);}catch{}}
    }
    public static int PureTests(Action<string,bool> check){
        string personal="C:\\Users\\Alice\\secret",secret="typed-password-123",device="VID_054C&PID_0DF2";var j=new JavaScriptSerializer();var malicious=new Dictionary<string,object>{{"armed",true},{"updatedUtc","2026-10-05T00:00:00.0000000Z"},{"normalBlocked",12},{"path",personal},{"lastWarning",secret},{"deviceId",device},{"observerError",personal},{"lastWarningKind",personal},{"toggleHotkey",secret}};
        string source=j.Serialize(malicious);string report=BuildReport(source,source,j.Serialize(new{language="en",overlayWidgets=new[]{"keyboard",personal},unknown=secret,toggleHotkey=secret}),j.Serialize(new{visible=true,position="top-left",path=personal}),"System.IO.IOException: "+personal+" "+secret);
        check("diagnostic excludes personal path",!report.Contains("Alice")&&!report.Contains("Users"));check("diagnostic excludes typed contents",!report.Contains(secret));check("diagnostic excludes device ID",!report.Contains(device));check("unknown settings excluded",!report.Contains("unknown"));check("safe error type only",report.Contains("System.IO.IOException"));check("invalid error text excluded",ErrorType(personal)=="UnclassifiedError");
        string events=SanitizeEvents(j.Serialize(new{utc="2026-10-05T00:00:00.0000000Z",kind="guard_stopped",description=personal+secret,deviceId=device,actualMatchFailureDetected=true})+"\n"+j.Serialize(new{utc="2026-10-05T00:00:00Z",kind=secret}));check("event descriptions removed",!events.Contains("description")&&!events.Contains(secret)&&!events.Contains("Alice"));check("event match failure never asserted",events.Contains("\"actualMatchFailureDetected\":false"));
        var many=new StringBuilder();for(int i=0;i<220;i++)many.AppendLine(j.Serialize(new{utc="2026-10-05T00:00:00.0000000Z",kind="guard_stopped"}));check("event tail bounded",SanitizeEvents(many.ToString()).Split(new[]{Environment.NewLine},StringSplitOptions.RemoveEmptyEntries).Length==200);
        check("known shortcut normalized",Convert.ToString(SanitizeSettings("{\"toggleHotkey\":\"alt+ctrl+F8\"}",false)["toggleHotkey"])=="Ctrl+Alt+F8");string manifest=BuildManifest(personal,secret,true,true,device,new DateTime(2026,10,5,0,0,0,DateTimeKind.Utc));check("manifest excludes untrusted environment strings",!manifest.Contains("Alice")&&!manifest.Contains(secret)&&!manifest.Contains(device));return 11;
    }
}
