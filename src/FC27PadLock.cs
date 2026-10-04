using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Web.Script.Serialization;

namespace FC27PadLock {
    static class Native {
        public delegate IntPtr Hook(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SetWindowsHookEx(int type, Hook callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
        [DllImport("kernel32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("user32.dll")] public static extern bool PostThreadMessage(uint id, uint message, UIntPtr wparam, IntPtr lparam);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
        [DllImport("user32.dll")] public static extern bool PeekMessage(out Message msg, IntPtr window, uint min, uint max, uint remove);
        [DllImport("user32.dll")] public static extern int GetMessage(out Message msg, IntPtr window, uint min, uint max);
        [DllImport("user32.dll")] public static extern bool TranslateMessage(ref Message msg);
        [DllImport("user32.dll")] public static extern IntPtr DispatchMessage(ref Message msg);
        [StructLayout(LayoutKind.Sequential)] public struct Message { public IntPtr window; public uint message; public UIntPtr wparam; public IntPtr lparam; public uint time; public Point point; public uint privateData; }
        [StructLayout(LayoutKind.Sequential)] public struct Keyboard { public uint key, scan, flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] public struct Mouse { public Point point; public uint data, flags, time; public UIntPtr extra; }
    }
    // Access to sets is confined to the hook thread. Injected events never change normal-input state.
    sealed class Policy {
        public volatile bool Armed=true;
        volatile ToggleShortcut configuredShortcut=ToggleShortcut.Emergency;
        public string ShortcutLabel {get{return configuredShortcut.Label;}}
        public void ConfigureShortcut(ToggleShortcut shortcut){if(shortcut!=null)configuredShortcut=shortcut;}
        public long NormalBlocked, InjectedPassthrough;
        readonly HashSet<int> held=new HashSet<int>(), blocked=new HashSet<int>(), allowed=new HashSet<int>();
        readonly HashSet<int> mouseBlocked=new HashSet<int>(), mouseAllowed=new HashSet<int>();
        bool Alt { get { return held.Contains(0x12)||held.Contains(0xa4)||held.Contains(0xa5); } }
        bool Ctrl { get { return held.Contains(0x11)||held.Contains(0xa2)||held.Contains(0xa3); } }
        bool Win { get { return held.Contains(0x5b)||held.Contains(0x5c); } }
        bool Shift {get{return held.Contains(0x10)||held.Contains(0xa0)||held.Contains(0xa1);}}
        bool Block() { Interlocked.Increment(ref NormalBlocked); return true; }
        public bool Key(int key, bool down, bool game, bool injected) {
            if(injected) { Interlocked.Increment(ref InjectedPassthrough); return false; }
            if(down) held.Add(key); else held.Remove(key);
            if(!down) { if(blocked.Remove(key)) return Block(); allowed.Remove(key); return false; }
            if(blocked.Contains(key)) return Block();
            if(allowed.Contains(key)) return false;
            int modifiers=(Ctrl?1:0)|(Alt?2:0)|(Shift?4:0)|(Win?8:0);
            if((key==0x7b&&Ctrl&&Alt)||configuredShortcut.Matches(key,modifiers)) { Armed=!Armed; blocked.Add(key); return Block(); }
            bool escape=key==0x12||key==0xa4||key==0xa5||key==0x5b||key==0x5c||((key==9||key==0x10||key==0xa0||key==0xa1)&&Alt)||Win;
            if(Armed&&game&&!escape) { blocked.Add(key); return Block(); }
            allowed.Add(key); return false;
        }
        public bool Mouse(int message,int button,bool foreground,bool pointed,bool injected) {
            if(injected) { Interlocked.Increment(ref InjectedPassthrough); return false; }
            bool down=message==0x201||message==0x204||message==0x207||message==0x20b;
            bool up=message==0x202||message==0x205||message==0x208||message==0x20c;
            if(up) { if(mouseBlocked.Remove(button)) return Block(); mouseAllowed.Remove(button); return false; }
            if(down) {
                if(mouseBlocked.Contains(button)) return Block();
                if(mouseAllowed.Contains(button)) return false;
                if(Armed&&(foreground||pointed)) { mouseBlocked.Add(button); return Block(); }
                mouseAllowed.Add(button); return false;
            }
            if(Armed&&(foreground||((message==0x20a||message==0x20e)&&pointed))) return Block();
            return false;
        }
    }
    sealed class Guard : IDisposable {
        readonly Policy policy=new Policy();
        readonly Native.Hook keyCallback,mouseCallback;
        readonly Thread hookThread,monitor;
        readonly ManualResetEvent ready=new ManualResetEvent(false),finished=new ManualResetEvent(false);
        readonly EventWaitHandle stop,pause,resume;
        readonly string dir;
        readonly Stopwatch elapsed=Stopwatch.StartNew();
        readonly int trialSeconds;
        readonly DateTime started=DateTime.UtcNow;
        volatile uint[] gamePids=new uint[0];
        volatile bool stopping,hooksInstalled;
        uint threadId;
        IntPtr keyHook,mouseHook;
        Exception error;
        System.Threading.Timer watchdog;
        bool watchdogDrained=true;
        public Guard(string prefix,string directory,int trial) {
            dir=directory; trialSeconds=trial;
            stop=new EventWaitHandle(false,EventResetMode.AutoReset,prefix+"Stop");
            pause=new EventWaitHandle(false,EventResetMode.AutoReset,prefix+"Pause");
            resume=new EventWaitHandle(false,EventResetMode.AutoReset,prefix+"Resume");
            keyCallback=KeyHook; mouseCallback=MouseHook;
            hookThread=new Thread(RunHooks){IsBackground=true,Name="FC27PadLock hooks"};
            monitor=new Thread(Monitor){IsBackground=true,Name="FC27PadLock status"};
        }
        public void Run() {
            Directory.CreateDirectory(dir); FindGame(); // Detection precedes hook installation, including trial starts.
            hookThread.Start();
            if(!ready.WaitOne(10000)) { RequestStop(); throw new Exception("Hook startup timeout"); }
            if(error!=null) throw error;
            watchdog=new System.Threading.Timer(Watchdog,null,0,100);
            monitor.Start();
            finished.WaitOne();
            stopping=true;
            StopWatchdog();
            monitor.Join(2000);
            WriteState();
            if(error!=null) throw error;
        }
        void Watchdog(object ignored) {
            // No UI, disk I/O, process scanning or lock acquisition on this independent deadline path.
            if(stopping) { Native.PostThreadMessage(threadId,0x12,UIntPtr.Zero,IntPtr.Zero); return; }
            if(stop.WaitOne(0)||(trialSeconds>0&&elapsed.Elapsed.TotalSeconds>=trialSeconds)) { RequestStop(); return; }
            if(pause.WaitOne(0)) policy.Armed=false;
            if(resume.WaitOne(0)) policy.Armed=true;
        }
        void StopWatchdog() {
            var timer=Interlocked.Exchange(ref watchdog,null);
            if(timer==null) return;
            var complete=new ManualResetEvent(false);
            if(!timer.Dispose(complete)||complete.WaitOne(2000)) complete.Dispose();
            else watchdogDrained=false; // Retain callback-owned handles until process exit on timeout.
        }
        void RequestStop() { stopping=true; policy.Armed=false; if(threadId!=0) Native.PostThreadMessage(threadId,0x12,UIntPtr.Zero,IntPtr.Zero); }
        void RunHooks() {
            try {
                threadId=Native.GetCurrentThreadId(); Native.Message msg;
                Native.PeekMessage(out msg,IntPtr.Zero,0,0,0); // Establish queue before publishing ready.
                if(stopping) return;
                keyHook=Native.SetWindowsHookEx(13,keyCallback,Native.GetModuleHandle(null),0);
                int ke=Marshal.GetLastWin32Error();
                mouseHook=Native.SetWindowsHookEx(14,mouseCallback,Native.GetModuleHandle(null),0);
                int me=Marshal.GetLastWin32Error();
                if(keyHook==IntPtr.Zero||mouseHook==IntPtr.Zero) throw new Exception("Hook registration failed: keyboard="+ke+" mouse="+me);
                hooksInstalled=true; ready.Set();
                int result;
                while((result=Native.GetMessage(out msg,IntPtr.Zero,0,0))>0) { Native.TranslateMessage(ref msg); Native.DispatchMessage(ref msg); }
                if(result<0) throw new Exception("Hook message loop failed");
            } catch(Exception ex) { error=ex; }
            finally { policy.Armed=false; if(keyHook!=IntPtr.Zero) Native.UnhookWindowsHookEx(keyHook); if(mouseHook!=IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook); hooksInstalled=false; ready.Set(); finished.Set(); }
        }
        bool IsGame(IntPtr window) {
            uint pid; if(window==IntPtr.Zero) return false; Native.GetWindowThreadProcessId(window,out pid);
            uint[] ids=gamePids; for(int i=0;i<ids.Length;i++) if(ids[i]==pid) return true; return false;
        }
        void FindGame() {
            var ids=new List<uint>();
            foreach(string name in new[]{"FC27","FC27_Trial","FC27_Showcase"}) foreach(var p in Process.GetProcessesByName(name)) using(p) { try { ids.Add((uint)p.Id); } catch {} }
            gamePids=ids.ToArray();
            Dictionary<string,object> settings;object value;ToggleShortcut shortcut;
            if(AppSettings.TryRead(out settings)&&settings.TryGetValue("toggleHotkey",out value)&&ToggleShortcut.TryParse(Convert.ToString(value),out shortcut))policy.ConfigureShortcut(shortcut);
        }
        IntPtr KeyHook(int code,IntPtr message,IntPtr data) {
            if(code>=0) {
                int msg=message.ToInt32();
                if(msg==0x100||msg==0x104||msg==0x101||msg==0x105) {
                    var k=(Native.Keyboard)Marshal.PtrToStructure(data,typeof(Native.Keyboard));
                    bool injected=(k.flags&0x12)!=0;
                    if(policy.Key((int)k.key,msg==0x100||msg==0x104,!injected&&IsGame(Native.GetForegroundWindow()),injected)) return new IntPtr(1);
                }
            }
            return Native.CallNextHookEx(keyHook,code,message,data);
        }
        IntPtr MouseHook(int code,IntPtr message,IntPtr data) {
            if(code>=0) {
                var m=(Native.Mouse)Marshal.PtrToStructure(data,typeof(Native.Mouse)); int msg=message.ToInt32();
                int button=msg==0x201||msg==0x202?1:msg==0x204||msg==0x205?2:msg==0x207||msg==0x208?3:(int)(m.data>>16)+3;
                bool injected=(m.flags&3)!=0;
                if(policy.Mouse(msg,button,!injected&&IsGame(Native.GetForegroundWindow()),!injected&&msg!=0x200&&IsGame(Native.WindowFromPoint(m.point)),injected)) return new IntPtr(1);
            }
            return Native.CallNextHookEx(mouseHook,code,message,data);
        }
        void Monitor() { while(!stopping&&!finished.WaitOne(500)) { try { FindGame(); WriteState(); } catch {} } }
        void WriteState() {
            try {
                var state=new { version="0.1.0-preview.1",settingsVersion=AppSettings.SchemaVersion,toggleHotkey=policy.ShortcutLabel,emergencyHotkey=ToggleShortcut.Emergency.Label,pid=Process.GetCurrentProcess().Id,startedUtc=started.ToString("o"),updatedUtc=DateTime.UtcNow.ToString("o"),armed=policy.Armed,stopped=finished.WaitOne(0),gamePids=gamePids,
                    gameForeground=IsGame(Native.GetForegroundWindow()),hooksInstalled=hooksInstalled,hookThreadAlive=hookThread.IsAlive,hookHealth="registration and thread state only; silent hook removal cannot be excluded",
                    normalBlocked=Interlocked.Read(ref policy.NormalBlocked),injectedPassthrough=Interlocked.Read(ref policy.InjectedPassthrough),rawInputGuaranteed=false,actualMatchVerified=false,
                    trialSeconds=trialSeconds,remainingTrialSeconds=trialSeconds>0?(double?)Math.Max(0,trialSeconds-elapsed.Elapsed.TotalSeconds):null,
                    scope="FC27 foreground normal LL events; background FC27 button/wheel; all injected events pass",physicalInputGuaranteed=false };
                string path=Path.Combine(dir,"status.json"),temp=path+".tmp";
                File.WriteAllText(temp,new JavaScriptSerializer().Serialize(state));
                if(File.Exists(path)) File.Replace(temp,path,null); else File.Move(temp,path);
            } catch {}
        }
        public void Dispose() {
            RequestStop(); StopWatchdog();
            if(hookThread.IsAlive) hookThread.Join(2000);
            if(!watchdogDrained||hookThread.IsAlive) return;
            if(monitor.IsAlive&&!monitor.Join(2000)) return; // Never dispose handles still in use.
            stop.Dispose(); pause.Dispose(); resume.Dispose(); ready.Dispose(); finished.Dispose();
        }
    }
    static class Program {
        [STAThread] static int Main(string[] args) {
            string prefix="Local\\FC27PadLock-"+WindowsIdentity.GetCurrent().User.Value+"-";
            if(args.Length>0&&(args[0]=="--stop"||args[0]=="--pause"||args[0]=="--resume")) {
                if(args.Length!=1) return 3;
                string name=args[0].Substring(2); name=char.ToUpperInvariant(name[0])+name.Substring(1);
                try { using(var ev=EventWaitHandle.OpenExisting(prefix+name)) ev.Set(); return 0; } catch(WaitHandleCannotBeOpenedException) { return 2; }
            }
            if(args.Length>0&&args[0]=="--self-test") {
                if(args.Length>2) return 3;
                try { return SelfTest(args.Length>1?args[1]:Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"self-test.json")); } catch { return 1; }
            }
            int trial=0;
            if(args.Length>0 && (args.Length!=2||args[0]!="--trial"||!int.TryParse(args[1],out trial)||trial<1||trial>3600)) return 3;
            bool fresh; using(var mutex=new Mutex(true,prefix+"Instance",out fresh)) {
                if(!fresh) return 2;
                string dir=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FC27PadLock");
                try { using(var guard=new Guard(prefix,dir,trial)) guard.Run(); return 0; }
                catch(Exception ex) { Directory.CreateDirectory(dir); File.WriteAllText(Path.Combine(dir,"error.txt"),ex.ToString()); return 1; }
            }
        }
        static int SelfTest(string path) {
            var cases=new List<object>(); int failures=0;
            Action<string,bool> check=delegate(string name,bool ok){cases.Add(new{name=name,passed=ok});if(!ok)failures++;};
            var p=new Policy();
            check("normal game key down",p.Key(87,true,true,false));
            check("blocked repeat after focus loss",p.Key(87,true,false,false));
            check("injected up cannot clear normal blocked pair",!p.Key(87,false,true,true));
            check("normal paired up after focus loss",p.Key(87,false,false,false));
            check("orphan up passes",!p.Key(66,false,true,false));
            check("background key down passes",!p.Key(65,true,false,false));
            check("allowed repeat after game focus passes",!p.Key(65,true,true,false));
            check("allowed paired release passes",!p.Key(65,false,true,false));
            check("Alt down passes",!p.Key(0xa4,true,true,false));
            check("Alt Shift Tab passes both shift variants",!p.Key(0xa0,true,true,false)&&!p.Key(0xa1,true,true,false)&&!p.Key(0x10,true,true,false)&&!p.Key(9,true,true,false));
            check("Alt letter remains blocked",p.Key(69,true,true,false)); p.Key(69,false,true,false);
            check("Alt Shift Tab releases pass",!p.Key(9,false,false,false)&&!p.Key(0xa0,false,false,false)&&!p.Key(0xa1,false,false,false)&&!p.Key(0x10,false,false,false));p.Key(0xa4,false,false,false);
            check("Win down passes",!p.Key(0x5b,true,true,false));
            check("Win escape chord passes",!p.Key(68,true,true,false));p.Key(68,false,false,false);p.Key(0x5b,false,false,false);
            check("injected game key passes",!p.Key(70,true,true,true));
            check("normal same key still blocked",p.Key(70,true,true,false));p.Key(70,false,true,false);
            check("normal foreground move blocked",p.Mouse(0x200,0,true,false,false));
            check("background move passes",!p.Mouse(0x200,0,false,true,false));
            check("injected foreground move passes",!p.Mouse(0x200,0,true,true,true));
            check("background FC click blocked",p.Mouse(0x201,1,false,true,false));
            check("injected up preserves mouse pair",!p.Mouse(0x202,1,true,true,true));
            check("mouse release after focus loss blocked",p.Mouse(0x202,1,false,false,false));
            check("allowed click preserves release over focus",!p.Mouse(0x204,2,false,false,false)&&!p.Mouse(0x205,2,true,true,false));
            check("background FC horizontal wheel blocked",p.Mouse(0x20e,0,false,true,false));
            check("other app wheel passes",!p.Mouse(0x20a,0,false,false,false));
            p.Key(0xa2,true,true,false);p.Key(0xa4,true,true,false);p.Key(0x7b,true,true,false);
            check("Ctrl Alt F12 toggles off once",!p.Armed&&p.Key(0x7b,true,true,false)&&!p.Armed);
            p.Key(0x7b,false,true,false);p.Key(0xa4,false,true,false);p.Key(0xa2,false,true,false);
            check("paused normal key and mouse pass",!p.Key(72,true,true,false)&&!p.Mouse(0x200,0,true,true,false));p.Key(72,false,true,false);
            p.Armed=true;p.Key(83,true,true,false);p.Mouse(0x20b,4,true,true,false);p.Armed=false;
            check("pause preserves suppressed releases",p.Key(83,false,false,false)&&p.Mouse(0x20c,4,false,false,false));
            check("injected counters and normal counters",p.InjectedPassthrough==4&&p.NormalBlocked>0);
            p.Key(0xa2,true,false,false);p.Key(0xa4,true,false,false);p.Key(0x7b,true,false,false);
            check("emergency chord resumes in background",p.Armed);
            ToggleShortcut shortcut;
            check("parse custom shortcut",ToggleShortcut.TryParse("shift+ctrl+F10",out shortcut)&&shortcut.Label=="Ctrl+Shift+F10");
            var custom=new Policy();custom.ConfigureShortcut(shortcut);custom.Key(0xa2,true,true,false);custom.Key(0xa0,true,true,false);
            check("custom shortcut toggles",custom.Key(0x79,true,true,false)&&!custom.Armed);
            check("custom repeat does not toggle",custom.Key(0x79,true,true,false)&&!custom.Armed);
            custom.Key(0x79,false,false,false);custom.Key(0xa0,false,false,false);custom.Key(0xa4,true,false,false);
            check("emergency remains after customization",custom.Key(0x7b,true,false,false)&&custom.Armed);
            check("single modifier rejected",!ToggleShortcut.TryParse("Ctrl+F10",out shortcut));
            check("typing keys rejected",!ToggleShortcut.TryParse("Ctrl+Alt+A",out shortcut));
            check("duplicate modifiers rejected",!ToggleShortcut.TryParse("Ctrl+Ctrl+F10",out shortcut));
            check("invalid function key rejected",!ToggleShortcut.TryParse("Ctrl+Alt+F25",out shortcut));
            check("blank shortcut rejected",!ToggleShortcut.TryParse("",out shortcut));
            check("extended function key allowed",ToggleShortcut.TryParse("Ctrl+Win+F24",out shortcut)&&shortcut.Key==0x87);
            File.WriteAllText(path,new JavaScriptSerializer().Serialize(new{failures=failures,testCount=cases.Count,tests=cases,hooksInstalled=false,rawInputGuaranteed=false,actualMatchVerified=false,inputSent=false}));
            return failures==0?0:1;
        }
    }
}
