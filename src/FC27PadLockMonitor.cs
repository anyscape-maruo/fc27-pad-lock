using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// Read-only observer: every hook callback returns CallNextHookEx unchanged.
class PadLockMonitor : ApplicationContext {
    static class Native {
        public delegate IntPtr Hook(int code,IntPtr message,IntPtr data);
        [DllImport("user32.dll",SetLastError=true)]public static extern IntPtr SetWindowsHookEx(int type,Hook callback,IntPtr module,uint thread);
        [DllImport("user32.dll")]public static extern bool UnhookWindowsHookEx(IntPtr handle);
        [DllImport("user32.dll")]public static extern IntPtr CallNextHookEx(IntPtr handle,int code,IntPtr message,IntPtr data);
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]public static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")]public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")]public static extern bool PostThreadMessage(uint id,uint msg,UIntPtr wp,IntPtr lp);
        [DllImport("user32.dll")]public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]public static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
        [DllImport("user32.dll")]public static extern bool RegisterRawInputDevices([In]RawDevice[] devices,uint count,uint size);
        [DllImport("user32.dll")]public static extern uint GetRawInputData(IntPtr input,uint command,IntPtr data,ref uint size,uint headerSize);
        [StructLayout(LayoutKind.Sequential)]public struct Key{public uint key,scan,flags,time;public UIntPtr extra;}
        [StructLayout(LayoutKind.Sequential)]public struct Mouse{public Point point;public uint data,flags,time;public UIntPtr extra;}
        [StructLayout(LayoutKind.Sequential)]public struct RawDevice{public ushort page,usage;public uint flags;public IntPtr target;}
        [StructLayout(LayoutKind.Sequential)]public struct RawHeader{public uint type,size;public IntPtr device;public UIntPtr wp;}
    }
    sealed class GuardState {public int Pid;public DateTime Started,Updated;public bool Armed,Stopped,Hooks,ThreadAlive;}
    sealed class Warning {public readonly string Kind,Description;public readonly DateTime Utc;public Warning(string kind,string description,DateTime utc){Kind=kind;Description=description;Utc=utc;}}
    sealed class Policy {
        // Transient pairing/modifier state only; key values never appear in logs or status.
        readonly HashSet<int> held=new HashSet<int>(),outside=new HashSet<int>();
        bool Alt{get{return held.Contains(0x12)||held.Contains(0xa4)||held.Contains(0xa5);}}
        bool Ctrl{get{return held.Contains(0x11)||held.Contains(0xa2)||held.Contains(0xa3);}}
        bool Win{get{return held.Contains(0x5b)||held.Contains(0x5c);}}
        public bool NonEscape(int key,bool down,bool game,bool injected){
            if(injected)return false;
            if(!down){held.Remove(key);outside.Remove(key);return false;}
            bool previouslyHeld=held.Contains(key);held.Add(key);if(!game&&!previouslyHeld)outside.Add(key);
            if(!game||outside.Contains(key))return false;
            bool escape=key==0x12||key==0xa4||key==0xa5||key==0x5b||key==0x5c||Win||((key==9||key==0x10||key==0xa0||key==0xa1)&&Alt)||(key==0x7b&&Ctrl&&Alt);
            return !escape;
        }
    }
    sealed class RawWindow:NativeWindow {
        readonly PadLockMonitor owner;
        public RawWindow(PadLockMonitor value){owner=value;CreateHandle(new CreateParams{Parent=new IntPtr(-3)});}
        protected override void WndProc(ref Message m){if(m.Msg==0xff)owner.Raw(m.LParam);base.WndProc(ref m);}
    }
    readonly DateTime startedUtc=DateTime.UtcNow;
    readonly string directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FC27PadLock");
    readonly EventWaitHandle quit,standalone;
    readonly Thread hooks;
    readonly Thread sampler;
    readonly ManualResetEvent shutdown=new ManualResetEvent(false);
    readonly ManualResetEvent ready=new ManualResetEvent(false);
    readonly Native.Hook keyCallback,mouseCallback;
    readonly Policy policy=new Policy();
    readonly System.Windows.Forms.Timer timer;
    readonly Queue<string> events=new Queue<string>();
    NotifyIcon tray;Icon icon;
    IntPtr keyHook,mouseHook;uint threadId;
    volatile bool stopping,hookRegistered,rawRegistered;
    volatile uint[] gamePids=new uint[0];
    string hookError,lastWarning="なし",previousHealth="",previousDiagnostic="";
    GuardState lastGoodGuard;
    string previousObserverHealth="";
    volatile Warning latestWarning;
    long warningSerial,shownWarningSerial;
    DateTime lastBalloonUtc=DateTime.MinValue;
    long normalObserved,normalBlocked,normalPassed,injectedObserved,injectedBlocked,injectedPassed,nonEscapePassed,keyObserved,mouseObserved,rawKey,rawMouse;
    long normalKeyboardObserved,normalKeyboardBlocked,normalKeyboardPassed,normalMouseObserved,normalMouseBlocked,normalMousePassed;
    long previousNonEscape,previousInjected,previousRawKey,previousRawMouse,previousKey,previousMouse;
    int sampleCount,scanTicks;
    bool eventsDirty;
    DateTime lastDiagnostic=DateTime.MinValue;
    DateTime lastInjectedDiagnostic=DateTime.MinValue;
    static string Prefix(string sid){return "Local\\FC27PadLockMonitor-"+sid+"-";}
    static string ParseMode(string[] args){if(args.Length==0)return "Start";if(args.Length!=1)return null;switch(args[0]){case "--standalone":return "Standalone";case "--quit":return "Quit";default:return null;}}
    static GuardState ParseState(string json){try{var d=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);int pid;DateTime started,updated;if(d==null||!(d["armed"] is bool)||!(d["stopped"] is bool)||!(d["hooksInstalled"] is bool)||!(d["hookThreadAlive"] is bool)||!int.TryParse(Convert.ToString(d["pid"]),out pid)||pid<=0||!DateTime.TryParse(Convert.ToString(d["startedUtc"]),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out started)||!DateTime.TryParse(Convert.ToString(d["updatedUtc"]),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out updated)||started.Kind!=DateTimeKind.Utc||updated.Kind!=DateTimeKind.Utc||started>updated)return null;return new GuardState{Pid=pid,Started=started,Updated=updated,Armed=(bool)d["armed"],Stopped=(bool)d["stopped"],Hooks=(bool)d["hooksInstalled"],ThreadAlive=(bool)d["hookThreadAlive"]};}catch{return null;}}
    static bool Matches(GuardState s,int pid,string name,DateTime started,bool exited){return s!=null&&!exited&&pid==s.Pid&&name=="FC27PadLock"&&s.Started>=started&&(s.Started-started).TotalSeconds<=10;}
    static string Health(GuardState s,DateTime now,int processState){
        if(s==null)return "guard_status_unavailable";
        if(s.Stopped||processState==0)return "guard_stopped";
        if(processState<0)return "guard_process_unconfirmed";
        double age=(now-s.Updated).TotalSeconds;if(age<0||age>=5)return "guard_heartbeat_stale";
        if(!s.Hooks||!s.ThreadAlive)return "guard_hook_registration_inactive";
        return s.Armed?"guard_armed":"guard_paused";
    }
    static bool RawMismatch(long rawKeyboard,long rawMouseCount,long keyboard,long mouse){return (rawKeyboard>=5&&keyboard==0)||(rawMouseCount>=10&&mouse==0);}
    static string ObserverHealth(bool registered,bool alive){return !alive?"thread_stopped":registered?"observing":"registration_failed";}
    static bool BalloonDue(long serial,long shown,DateTime now,DateTime last,int cooldown=5){return serial>shown&&(now-last).TotalSeconds>=cooldown;}
    static string WarningText(Warning warning){
        switch(warning.Kind){
            case "observer_registration_failed":return "入力監視のフック登録に失敗しました。監視の再起動を確認してください。";
            case "observer_thread_stopped":return "入力監視のスレッドが停止しました。監視の再起動を確認してください。";
            case "guard_stopped":return "入力ガードが停止しています。";
            case "guard_heartbeat_stale":return "入力ガードの状態更新が途絶えています。";
            case "guard_hook_registration_inactive":return "入力ガードのフック登録またはスレッドが稼働していません。";
            case "normal_non_escape_chain_passed_guard_reported_armed":return "ガード稼働の記録中に通常入力のチェーン通過を観測しました。状態更新の遅延も考えられます。";
            case "guard_status_unavailable":return "入力ガードの状態を確認できません。";
            case "guard_process_unconfirmed":return "入力ガードの実プロセスを確認できません。";
            default:return warning.Description;
        }
    }
    PadLockMonitor(string prefix,bool standalone){
        quit=new EventWaitHandle(false,EventResetMode.AutoReset,prefix+"Quit");this.standalone=new EventWaitHandle(false,EventResetMode.AutoReset,prefix+"Standalone");
        keyCallback=Key;mouseCallback=Mouse;hooks=new Thread(RunHooks){IsBackground=true,Name="FC27PadLock read-only observer"};
        FindGame();hooks.Start();ready.WaitOne(5000);
        if(standalone)CreateTray();
        sampler=new Thread(delegate(){do{try{Sample();}catch{lastWarning="監視状態の保存または確認に失敗";}}while(!shutdown.WaitOne(500));}){IsBackground=true,Name="FC27PadLock observer status"};sampler.Start();
        timer=new System.Windows.Forms.Timer{Interval=500};timer.Tick+=delegate{try{if(quit.WaitOne(0)){ExitThread();return;}if(this.standalone.WaitOne(0)&&tray==null)CreateTray();if(tray!=null){tray.Text="FC27 入力監視: "+(previousObserverHealth!="observing"?"監視に異常":previousHealth=="guard_armed"?"ガード稼働中":previousHealth=="guard_paused"?"一時解除中":"確認が必要");long serial=Interlocked.Read(ref warningSerial);Warning warning=latestWarning;DateTime now=DateTime.UtcNow;bool enabled=AppSettings.ReadBool("notificationsEnabled",true);int cooldown=(int)Math.Round(AppSettings.ReadDouble("warningCooldownSeconds",30,5,300));if(!enabled)shownWarningSerial=serial;else if(warning!=null&&BalloonDue(serial,shownWarningSerial,now,lastBalloonUtc,cooldown)){tray.ShowBalloonTip(5000,"FC27 入力監視",WarningText(warning)+"\nゲーム内部の到達・試合失敗を確認したものではありません。",ToolTipIcon.Warning);shownWarningSerial=serial;lastBalloonUtc=now;}}}catch{}};timer.Start();
    }
    bool GameForeground(){uint pid;Native.GetWindowThreadProcessId(Native.GetForegroundWindow(),out pid);foreach(uint id in gamePids)if(id==pid)return true;return false;}
    void FindGame(){var ids=new List<uint>();foreach(string name in new[]{"FC27","FC27_Trial","FC27_Showcase"})foreach(var p in Process.GetProcessesByName(name))using(p){try{ids.Add((uint)p.Id);}catch{}}gamePids=ids.ToArray();}
    IntPtr Key(int code,IntPtr message,IntPtr data){
        IntPtr result=Native.CallNextHookEx(keyHook,code,message,data);
        try{if(code>=0){int msg=message.ToInt32();if(msg==0x100||msg==0x104||msg==0x101||msg==0x105){var k=(Native.Key)Marshal.PtrToStructure(data,typeof(Native.Key));bool injected=(k.flags&0x12)!=0,game=GameForeground(),down=msg==0x100||msg==0x104;bool candidate=policy.NonEscape((int)k.key,down,game,injected);if(game&&down){Interlocked.Increment(ref keyObserved);Count(injected,result!=IntPtr.Zero,candidate,true);}}}}catch{}
        return result;
    }
    IntPtr Mouse(int code,IntPtr message,IntPtr data){
        IntPtr result=Native.CallNextHookEx(mouseHook,code,message,data);
        try{if(code>=0&&GameForeground()){int msg=message.ToInt32();bool included=msg==0x200||msg==0x201||msg==0x204||msg==0x207||msg==0x20b||msg==0x20a||msg==0x20e;if(included){var m=(Native.Mouse)Marshal.PtrToStructure(data,typeof(Native.Mouse));bool injected=(m.flags&3)!=0;Interlocked.Increment(ref mouseObserved);Count(injected,result!=IntPtr.Zero,msg!=0x200,false);}}}catch{}
        return result;
    }
    void Count(bool injected,bool blocked,bool candidate,bool keyboard){
        if(injected){Interlocked.Increment(ref injectedObserved);if(blocked)Interlocked.Increment(ref injectedBlocked);else Interlocked.Increment(ref injectedPassed);return;}
        Interlocked.Increment(ref normalObserved);
        if(keyboard){Interlocked.Increment(ref normalKeyboardObserved);if(blocked)Interlocked.Increment(ref normalKeyboardBlocked);else Interlocked.Increment(ref normalKeyboardPassed);}
        else{Interlocked.Increment(ref normalMouseObserved);if(blocked)Interlocked.Increment(ref normalMouseBlocked);else Interlocked.Increment(ref normalMousePassed);}
        if(blocked)Interlocked.Increment(ref normalBlocked);else{Interlocked.Increment(ref normalPassed);if(candidate)Interlocked.Increment(ref nonEscapePassed);}
    }
    void Raw(IntPtr input){try{if(!GameForeground())return;uint size=(uint)Marshal.SizeOf(typeof(Native.RawHeader));IntPtr memory=Marshal.AllocHGlobal((int)size);try{uint got=Native.GetRawInputData(input,0x10000005,memory,ref size,(uint)Marshal.SizeOf(typeof(Native.RawHeader)));if(got==uint.MaxValue||got<(uint)Marshal.SizeOf(typeof(Native.RawHeader)))return;var h=(Native.RawHeader)Marshal.PtrToStructure(memory,typeof(Native.RawHeader));if(h.type==0)Interlocked.Increment(ref rawMouse);if(h.type==1)Interlocked.Increment(ref rawKey);}finally{Marshal.FreeHGlobal(memory);}}catch{}}
    void RunHooks(){RawWindow window=null;try{
        threadId=Native.GetCurrentThreadId();window=new RawWindow(this);
        var devices=new[]{new Native.RawDevice{page=1,usage=2,flags=0x100,target=window.Handle},new Native.RawDevice{page=1,usage=6,flags=0x100,target=window.Handle}};
        rawRegistered=Native.RegisterRawInputDevices(devices,(uint)devices.Length,(uint)Marshal.SizeOf(typeof(Native.RawDevice)));
        keyHook=Native.SetWindowsHookEx(13,keyCallback,Native.GetModuleHandle(null),0);mouseHook=Native.SetWindowsHookEx(14,mouseCallback,Native.GetModuleHandle(null),0);
        hookRegistered=keyHook!=IntPtr.Zero&&mouseHook!=IntPtr.Zero;if(!hookRegistered)hookError="observer_hook_registration_failed";ready.Set();Application.Run();
    }catch(Exception e){hookError=e.GetType().Name;ready.Set();}finally{if(keyHook!=IntPtr.Zero)Native.UnhookWindowsHookEx(keyHook);if(mouseHook!=IntPtr.Zero)Native.UnhookWindowsHookEx(mouseHook);hookRegistered=false;rawRegistered=false;if(window!=null)window.DestroyHandle();}}
    void CreateTray(){try{icon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);}catch{}if(icon==null)icon=(Icon)SystemIcons.Shield.Clone();var menu=new ContextMenuStrip();menu.Items.Add("監視状態・観測数",null,delegate{ShowReport();});menu.Items.Add("ログ保存場所を開く",null,delegate{try{Directory.CreateDirectory(directory);Process.Start(new ProcessStartInfo(directory){UseShellExecute=true});}catch{}});menu.Items.Add("監視だけ終了",null,delegate{ExitThread();});tray=new NotifyIcon{Icon=icon,Text="FC27 入力監視",ContextMenuStrip=menu,Visible=true};tray.DoubleClick+=delegate{ShowReport();};}
    void ShowReport(){try{MessageBox.Show("FC27前面時だけ集計しています。入力の文字・キー番号・座標は保存しません。\n\n状態: "+previousHealth+"\n通常入力の観測: "+Interlocked.Read(ref normalObserved)+"\nチェーン遮断: "+Interlocked.Read(ref normalBlocked)+"\nチェーン通過: "+Interlocked.Read(ref normalPassed)+"\n注入入力の通過: "+Interlocked.Read(ref injectedPassed)+"\n最近の注意: "+lastWarning+"\n\nゲーム内部への到達・試合失敗は判定できません。","FC27 入力監視");}catch{}}
    void Log(string kind,string description,bool warning){DateTime now=DateTime.UtcNow;events.Enqueue(new JavaScriptSerializer().Serialize(new{utc=now.ToString("o"),kind=kind,description=description,actualMatchFailureDetected=false}));while(events.Count>1000)events.Dequeue();eventsDirty=true;if(warning){lastWarning=description;latestWarning=new Warning(kind,description,now);Interlocked.Increment(ref warningSerial);}}
    void Sample(){
        if(++scanTicks%2==0)FindGame();DateTime now=DateTime.UtcNow;GuardState guard=null;try{guard=ParseState(File.ReadAllText(Path.Combine(directory,"status.json")));}catch{}
        // Atomic file replacement can briefly conflict with a reader; use the last valid heartbeat until its normal expiry.
        if(guard!=null)lastGoodGuard=guard;else guard=lastGoodGuard;
        int processState=0;if(guard!=null){try{using(var p=Process.GetProcessById(guard.Pid))processState=Matches(guard,p.Id,p.ProcessName,p.StartTime.ToUniversalTime(),p.HasExited)?1:0;}catch(ArgumentException){processState=0;}catch{processState=-1;}}
        string health=Health(guard,now,processState);if(health!=previousHealth){Log(health,health,health!="guard_armed"&&health!="guard_paused");previousHealth=health;}
        string observerHealth=ObserverHealth(hookRegistered,hooks.IsAlive);if(observerHealth!=previousObserverHealth){Log("observer_"+observerHealth,observerHealth,observerHealth!="observing");previousObserverHealth=observerHealth;}
        long candidate=Interlocked.Read(ref nonEscapePassed),injected=Interlocked.Read(ref injectedPassed);
        string diagnostic=candidate>previousNonEscape?(health=="guard_armed"?"normal_non_escape_chain_passed_guard_reported_armed":"normal_chain_passed_guard_not_armed"):"";
        if(diagnostic!=""&&(diagnostic!=previousDiagnostic||(now-lastDiagnostic).TotalSeconds>=5)){Log(diagnostic,diagnostic,diagnostic=="normal_non_escape_chain_passed_guard_reported_armed");lastDiagnostic=now;previousDiagnostic=diagnostic;}
        if(injected>previousInjected&&(now-lastInjectedDiagnostic).TotalSeconds>=5){Log("injected_chain_passed_out_of_scope","注入入力のチェーン通過を観測しました。現在のガードでは対象外です。",false);lastInjectedDiagnostic=now;}
        previousNonEscape=candidate;previousInjected=injected;
        if(++sampleCount%10==0){long rk=Interlocked.Read(ref rawKey),rm=Interlocked.Read(ref rawMouse),lk=Interlocked.Read(ref keyObserved),lm=Interlocked.Read(ref mouseObserved);if(rawRegistered&&RawMismatch(rk-previousRawKey,rm-previousRawMouse,lk-previousKey,lm-previousMouse))Log("raw_ll_mismatch_suspected","Raw InputとLL観測数に差があります。ゲーム到達や失敗の証拠ではありません。",true);previousRawKey=rk;previousRawMouse=rm;previousKey=lk;previousMouse=lm;}
        Warning currentWarning=latestWarning;
        Directory.CreateDirectory(directory);var state=new{version="0.1.0-preview.1",startedUtc=startedUtc.ToString("o"),pid=Process.GetCurrentProcess().Id,updatedUtc=now.ToString("o"),guardHealth=health,observerHealth=observerHealth,observerHooksRegistered=hookRegistered,observerThreadAlive=hooks.IsAlive,observerError=hookError,rawInputObservationEnabled=rawRegistered,gameForeground=GameForeground(),normalKeyboardObserved=Interlocked.Read(ref normalKeyboardObserved),normalKeyboardBlocked=Interlocked.Read(ref normalKeyboardBlocked),normalKeyboardPassed=Interlocked.Read(ref normalKeyboardPassed),normalMouseObserved=Interlocked.Read(ref normalMouseObserved),normalMouseBlocked=Interlocked.Read(ref normalMouseBlocked),normalMousePassed=Interlocked.Read(ref normalMousePassed),normalObserved=Interlocked.Read(ref normalObserved),normalChainBlocked=Interlocked.Read(ref normalBlocked),normalChainPassed=Interlocked.Read(ref normalPassed),nonEscapeChainPassed=candidate,injectedObserved=Interlocked.Read(ref injectedObserved),injectedChainBlocked=Interlocked.Read(ref injectedBlocked),injectedChainPassed=injected,rawKeyboardReports=Interlocked.Read(ref rawKey),rawMouseReports=Interlocked.Read(ref rawMouse),lastWarning=lastWarning,lastWarningKind=currentWarning==null?null:currentWarning.Kind,lastWarningUtc=currentWarning==null?null:currentWarning.Utc.ToString("o"),actualMatchFailureDetected=false,gameInternalArrivalKnown=false,scope="FC27 foreground only; hook-chain result is not game receipt",hookHealth="registration/thread state only; silent hook removal cannot be excluded",typedContentStored=false,coordinatesStored=false,deviceIdsStored=false};
        AtomicWrite(Path.Combine(directory,"monitor-status.json"),new JavaScriptSerializer().Serialize(state));if(eventsDirty){AtomicWrite(Path.Combine(directory,"monitor-events.jsonl"),string.Join(Environment.NewLine,events.ToArray())+Environment.NewLine);eventsDirty=false;}
    }
    static void AtomicWrite(string path,string contents){string temp=path+".tmp";File.WriteAllText(temp,contents);if(File.Exists(path))File.Replace(temp,path,null);else File.Move(temp,path);}
    protected override void ExitThreadCore(){if(stopping)return;stopping=true;timer.Stop();shutdown.Set();Native.PostThreadMessage(threadId,0x12,UIntPtr.Zero,IntPtr.Zero);hooks.Join(2000);sampler.Join(2000);if(tray!=null){tray.Visible=false;var menu=tray.ContextMenuStrip;tray.Dispose();if(menu!=null)menu.Dispose();}if(icon!=null)icon.Dispose();timer.Dispose();quit.Dispose();standalone.Dispose();if(!hooks.IsAlive)ready.Dispose();if(!sampler.IsAlive)shutdown.Dispose();base.ExitThreadCore();}
    static int SelfTest(string path){var tests=new List<object>();int failures=0;Action<string,bool> check=delegate(string name,bool ok){tests.Add(new{name=name,passed=ok});if(!ok)failures++;};check("default",ParseMode(new string[0])=="Start");check("standalone",ParseMode(new[]{"--standalone"})=="Standalone");check("quit",ParseMode(new[]{"--quit"})=="Quit");check("invalid",ParseMode(new[]{"--bad"})==null);check("extra",ParseMode(new[]{"--quit","extra"})==null);check("separate prefix",Prefix("test")=="Local\\FC27PadLockMonitor-test-");
        var p=new Policy();check("normal game down candidate",p.NonEscape(65,true,true,false));p.NonEscape(65,false,false,false);check("outside down excluded",!p.NonEscape(66,true,false,false));check("outside held repeat excluded",!p.NonEscape(66,true,true,false));p.NonEscape(66,false,true,false);check("new game down candidate",p.NonEscape(66,true,true,false));check("injected excluded",!p.NonEscape(67,true,true,true));check("Alt excluded",!p.NonEscape(0xa4,true,true,false));check("Alt Tab excluded",!p.NonEscape(9,true,true,false));check("Alt Shift excluded",!p.NonEscape(0xa0,true,true,false));check("Alt letter remains candidate",p.NonEscape(68,true,true,false));p.NonEscape(0xa4,false,true,false);check("Win excluded",!p.NonEscape(0x5b,true,true,false));check("Win chord excluded",!p.NonEscape(69,true,true,false));p.NonEscape(0x5b,false,true,false);p.NonEscape(0xa2,true,true,false);p.NonEscape(0xa4,true,true,false);check("emergency F12 excluded",!p.NonEscape(0x7b,true,true,false));
        DateTime now=new DateTime(2026,10,5,0,0,0,DateTimeKind.Utc);var j=new JavaScriptSerializer();var d=new Dictionary<string,object>{{"pid",123},{"armed",true},{"stopped",false},{"hooksInstalled",true},{"hookThreadAlive",true},{"startedUtc",now.AddSeconds(-2).ToString("o")},{"updatedUtc",now.ToString("o")}};var s=ParseState(j.Serialize(d));check("valid state",s!=null);check("invalid state",ParseState("{}")==null);check("armed health",Health(s,now,1)=="guard_armed");check("stale health",Health(s,now.AddSeconds(5),1)=="guard_heartbeat_stale");check("future health",Health(s,now.AddSeconds(-1),1)=="guard_heartbeat_stale");check("stopped process",Health(s,now,0)=="guard_stopped");check("unknown process",Health(s,now,-1)=="guard_process_unconfirmed");s.Armed=false;check("paused",Health(s,now,1)=="guard_paused");s.Armed=true;s.Hooks=false;check("inactive hooks",Health(s,now,1)=="guard_hook_registration_inactive");s.Hooks=true;check("matching process",Matches(s,123,"FC27PadLock",now.AddSeconds(-3),false));check("PID reuse",!Matches(s,123,"FC27PadLock",now.AddSeconds(-1),false));check("raw keyboard mismatch",RawMismatch(5,0,0,0));check("raw mouse mismatch",RawMismatch(0,10,0,0));check("no mismatch with LL",!RawMismatch(5,10,1,1));
        check("observer observing",ObserverHealth(true,true)=="observing");check("observer registration failed",ObserverHealth(false,true)=="registration_failed");check("observer thread stopped",ObserverHealth(true,false)=="thread_stopped");
        check("new warning balloon due",BalloonDue(1,0,now,now.AddSeconds(-5)));check("balloon cooldown",!BalloonDue(2,1,now,now.AddSeconds(-4)));check("same warning not repeated",!BalloonDue(1,1,now,now.AddSeconds(-30)));
        check("configured warning cooldown",!BalloonDue(2,1,now,now.AddSeconds(-20),30)&&BalloonDue(2,1,now,now.AddSeconds(-30),30));
        check("observer warning has explicit text",WarningText(new Warning("observer_registration_failed","registration_failed",now)).Contains("登録に失敗"));
        File.WriteAllText(path,j.Serialize(new{testCount=tests.Count,failures=failures,tests=tests,hooksInstalled=false,inputSent=false,ipcSent=false,uiStarted=false,processOperations=false,actualMatchFailureDetected=false}));return failures==0?0:1;
    }
    [STAThread]static int Main(string[] args){if(args.Length>0&&args[0]=="--self-test"){if(args.Length!=2||!Path.IsPathRooted(args[1]))return 3;try{return SelfTest(args[1]);}catch{return 1;}}string mode=ParseMode(args);if(mode==null)return 3;string prefix=Prefix(WindowsIdentity.GetCurrent().User.Value);bool fresh;using(var mutex=new Mutex(true,prefix+"Instance",out fresh)){if(!fresh||mode=="Quit"){if(mode=="Start")return 0;var deadline=Stopwatch.StartNew();do{try{using(var ev=EventWaitHandle.OpenExisting(prefix+mode))ev.Set();return 0;}catch(WaitHandleCannotBeOpenedException){if(fresh)return 2;Thread.Sleep(50);}catch{return 2;}}while(deadline.ElapsedMilliseconds<1000);return 2;}Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);try{Application.Run(new PadLockMonitor(prefix,mode=="Standalone"));return 0;}catch{return 1;}}}
}
