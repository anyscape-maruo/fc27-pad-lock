using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

// A read-only display helper. It never sends game input or guard control commands.
class PadLockOverlay : Form {
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr handle,out uint pid);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr handle,out Rect rect);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr handle,IntPtr after,int x,int y,int cx,int cy,uint flags);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr handle,int index);
    [DllImport("user32.dll",EntryPoint="SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr handle,int index,IntPtr value);
    [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr handle,uint color,byte alpha,uint flags);
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left,Top,Right,Bottom; }
    sealed class DisplayState { public int Pid; public int[] Games; public DateTime Started,Updated; public bool Armed,Hooks,ThreadAlive,Stopped; public string ToggleHotkey="Ctrl+Alt+F12",EmergencyHotkey="Ctrl+Alt+F12"; }
    sealed class Preferences { public bool Visible=true; public string Position="top-right",Layout="detailed"; }
    sealed class MonitorState {public int Pid;public DateTime Updated;public DateTime? Started,WarningUtc;public string Health,WarningKind;public long? KeyboardBlocked,KeyboardPassed,MouseBlocked,MousePassed;}
    sealed class ViewSettings {public double Opacity=0.94,Scale=1;public int Margin=18;public string DesiredHotkey="Ctrl+Alt+F12";public HashSet<string> Widgets=new HashSet<string>(WidgetNames);}
    static readonly string[] WidgetNames={"protection","keyboard","mouse","monitor","warning","shortcut"};
    readonly string directory;
    readonly string settingsPath;
    readonly Dictionary<string,EventWaitHandle> signals=new Dictionary<string,EventWaitHandle>();
    readonly System.Windows.Forms.Timer timer;
    NotifyIcon tray;
    Icon trayIcon;
    Preferences preferences;
    bool armed;
    string monitorLine;
    bool monitorWarning;
    MonitorState monitor;
    string language;
    ViewSettings view=new ViewSettings();
    string actualHotkey="Ctrl+Alt+F12",emergencyHotkey="Ctrl+Alt+F12";
    static readonly string[] Corners={"top-left","top-right","bottom-left","bottom-right"};
    static readonly string[] Commands={"Show","Hide","Toggle","Quit","Standalone","TopLeft","TopRight","BottomLeft","BottomRight","LanguageJa","LanguageEn","LayoutCompact","LayoutDetailed","SettingsChanged"};
    const int CardWidth=338,CardHeight=220;
    static Size CardSize(string layout){return layout=="compact"?new Size(218,104):new Size(CardWidth,CardHeight);}
    static int BodyHeight(string layout,ViewSettings v,bool pending){int y=31;if(v.Widgets.Contains("protection"))y+=24;if(v.Widgets.Contains("monitor"))y+=23;if(layout=="compact"){if(v.Widgets.Contains("shortcut")){y+=20;if(pending)y+=17;}return Math.Max(54,y+7);}if(v.Widgets.Contains("keyboard")||v.Widgets.Contains("mouse"))y+=18;if(v.Widgets.Contains("keyboard"))y+=21;if(v.Widgets.Contains("mouse"))y+=21;if(v.Widgets.Contains("warning"))y+=20;if(v.Widgets.Contains("shortcut")){y+=18;if(pending)y+=17;}return Math.Min(CardHeight,Math.Max(60,y+26));}
    static bool PendingShortcut(string desired,string actual){return !string.Equals((desired??"").Replace(" ",""),(actual??"").Replace(" ",""),StringComparison.OrdinalIgnoreCase);}
    static double NumberSetting(Dictionary<string,object> d,string key,double fallback,double min,double max){object value;double number;return d.TryGetValue(key,out value)&&value!=null&&double.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out number)&&!double.IsNaN(number)&&!double.IsInfinity(number)?Math.Max(min,Math.Min(max,number)):fallback;}
    static ViewSettings ParseViewSettings(string json){
        var result=new ViewSettings();try{
            var d=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);if(d==null)return result;object value;
            result.Opacity=NumberSetting(d,"overlayOpacity",0.94,0.25,1);result.Scale=NumberSetting(d,"overlayScale",1,0.75,1.5);result.Margin=(int)Math.Round(NumberSetting(d,"overlayMargin",18,8,80));
            if(d.TryGetValue("toggleHotkey",out value)&&!string.IsNullOrWhiteSpace(Convert.ToString(value)))result.DesiredHotkey=Convert.ToString(value).Trim();
            if(d.TryGetValue("overlayWidgets",out value)&&value is System.Collections.IEnumerable&&!(value is string)){result.Widgets.Clear();foreach(object item in (System.Collections.IEnumerable)value){string name=Convert.ToString(item);if(Array.IndexOf(WidgetNames,name)>=0)result.Widgets.Add(name);}}
        }catch{}return result;
    }
    static Size ScaledSize(Size logical,double scale){return new Size((int)Math.Ceiling(logical.Width*scale),(int)Math.Ceiling(logical.Height*scale));}
    protected override bool ShowWithoutActivation { get { return true; } }
    protected override CreateParams CreateParams { get { var p=base.CreateParams;p.ExStyle|=0x08000000|0x00000020|0x00000080|0x00000008|0x00080000;return p; } }
    protected override void WndProc(ref Message m) { if(m.Msg==0x84){m.Result=new IntPtr(-1);return;}if(m.Msg==0x21){m.Result=new IntPtr(3);return;}base.WndProc(ref m); }
    static string Prefix(string sid) { return "Local\\FC27PadLockOverlay-"+sid+"-"; }
    static string PositionEvent(string corner) { switch(corner){case "top-left":return "TopLeft";case "top-right":return "TopRight";case "bottom-left":return "BottomLeft";case "bottom-right":return "BottomRight";default:return null;} }
    static string ParseCommand(string[] args) {
        if(args.Length==0)return "Start";
        if(args.Length==2&&args[0]=="--position")return PositionEvent(args[1]);
        if(args.Length==2&&args[0]=="--language")return args[1]=="ja"?"LanguageJa":args[1]=="en"?"LanguageEn":null;
        if(args.Length==2&&args[0]=="--layout")return args[1]=="compact"?"LayoutCompact":args[1]=="detailed"?"LayoutDetailed":null;
        if(args.Length==2&&args[0]=="--opacity"){int opacity;if(int.TryParse(args[1],NumberStyles.Integer,CultureInfo.InvariantCulture,out opacity)&&opacity>=25&&opacity<=100)return "Opacity:"+opacity;return null;}
        if(args.Length!=1)return null;
        switch(args[0]){case "--standalone":return "Standalone";case "--show":return "Show";case "--hide":return "Hide";case "--toggle":return "Toggle";case "--quit":return "Quit";default:return null;}
    }
    static Preferences ParsePreferences(string json) {
        try{var d=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);if(d!=null&&d["visible"] is bool&&PositionEvent(Convert.ToString(d["position"]))!=null){object layout;string value=d.TryGetValue("layout",out layout)?Convert.ToString(layout):"detailed";return new Preferences{Visible=(bool)d["visible"],Position=Convert.ToString(d["position"]),Layout=value=="compact"?"compact":"detailed"};}}catch{}return new Preferences();
    }
    static DisplayState ParseState(string json) {
        try {
            var d=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);int pid;DateTime started,updated;
            if(d==null||!(d["armed"] is bool)||!(d["hooksInstalled"] is bool)||!(d["hookThreadAlive"] is bool)||!(d["stopped"] is bool)||!int.TryParse(Convert.ToString(d["pid"]),out pid)||pid<=0||
               !DateTime.TryParse(Convert.ToString(d["startedUtc"]),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out started)||
               !DateTime.TryParse(Convert.ToString(d["updatedUtc"]),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out updated)||started.Kind!=DateTimeKind.Utc||updated.Kind!=DateTimeKind.Utc||started>updated)return null;
            var list=d["gamePids"] as System.Collections.IEnumerable;if(list==null||d["gamePids"] is string)return null;var games=new List<int>();
            foreach(var item in list){int game;if(!int.TryParse(Convert.ToString(item),out game)||game<=0)return null;games.Add(game);}
            object hotkey,emergency;return new DisplayState{Pid=pid,Games=games.ToArray(),Started=started,Updated=updated,Armed=(bool)d["armed"],Hooks=(bool)d["hooksInstalled"],ThreadAlive=(bool)d["hookThreadAlive"],Stopped=(bool)d["stopped"],ToggleHotkey=d.TryGetValue("toggleHotkey",out hotkey)&&!string.IsNullOrWhiteSpace(Convert.ToString(hotkey))?Convert.ToString(hotkey):"Ctrl+Alt+F12",EmergencyHotkey=d.TryGetValue("emergencyHotkey",out emergency)&&!string.IsNullOrWhiteSpace(Convert.ToString(emergency))?Convert.ToString(emergency):"Ctrl+Alt+F12"};
        }catch{return null;}
    }
    static bool Fresh(DisplayState s,DateTime now) { return s!=null&&(now-s.Updated).TotalSeconds>=0&&(now-s.Updated).TotalSeconds<5; }
    static bool Eligible(Preferences p,DisplayState s,DateTime now) { return p.Visible&&Fresh(s,now)&&!s.Stopped&&s.Hooks&&s.ThreadAlive; }
    static bool Matches(DisplayState s,int pid,string name,DateTime started,bool exited) { return s!=null&&!exited&&s.Pid==pid&&name=="FC27PadLock"&&s.Started>=started&&(s.Started-started).TotalSeconds<=10; }
    static bool GameName(string name) { return name=="FC27"||name=="FC27_Trial"||name=="FC27_Showcase"; }
    static Point Locate(Rectangle bounds,Size size,string corner,int margin=18) {
        bool right=corner=="top-right"||corner=="bottom-right",bottom=corner=="bottom-left"||corner=="bottom-right";
        return new Point(Math.Max(bounds.Left,right?bounds.Right-size.Width-margin:bounds.Left+margin),Math.Max(bounds.Top,bottom?bounds.Bottom-size.Height-margin:bounds.Top+margin));
    }
    PadLockOverlay(string prefix,string command) {
        directory=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FC27PadLock");settingsPath=Path.Combine(directory,"overlay-settings.json");
        try{preferences=ParsePreferences(File.ReadAllText(settingsPath));}catch{preferences=new Preferences();}
        foreach(string name in Commands)signals.Add(name,new EventWaitHandle(false,EventResetMode.AutoReset,prefix+name));
        language=TextCatalog.CurrentLanguage;
        FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;StartPosition=FormStartPosition.Manual;ClientSize=CardSize(preferences.Layout);BackColor=Color.FromArgb(19,25,35);Opacity=0.94;DoubleBuffered=true;
        IntPtr hiddenHandle=Handle; // Create an invisible window so Close raises FormClosed even before first display.
        Apply(command);
        timer=new System.Windows.Forms.Timer{Interval=250};timer.Tick+=delegate{TickDisplay();};timer.Start();
    }
    void Save(bool updateLayout=false){try{Directory.CreateDirectory(directory);var values=new Dictionary<string,object>();try{var existing=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(settingsPath));if(existing!=null)values=existing;}catch{}values["visible"]=preferences.Visible;values["position"]=preferences.Position;if(updateLayout||!values.ContainsKey("layout"))values["layout"]=preferences.Layout;string temp=settingsPath+".tmp";File.WriteAllText(temp,new JavaScriptSerializer().Serialize(values));if(File.Exists(settingsPath))File.Replace(temp,settingsPath,null);else File.Move(temp,settingsPath);}catch{}}
    void Apply(string command) {
        if(command=="Quit"){Close();return;}
        if(command=="Standalone")AddStandaloneTray();
        if(command=="LanguageJa"||command=="LanguageEn"){TextCatalog.SetLanguage(command=="LanguageJa"?"ja":"en");language=TextCatalog.CurrentLanguage;RefreshTray();return;}
        if(command.StartsWith("Opacity:",StringComparison.Ordinal)){TextCatalog.SetSetting("overlayOpacity",int.Parse(command.Substring(8),CultureInfo.InvariantCulture)/100.0);return;}
        if(command=="LayoutCompact"||command=="LayoutDetailed"){preferences.Layout=command=="LayoutCompact"?"compact":"detailed";ClientSize=CardSize(preferences.Layout);}
        if(command=="Show")preferences.Visible=true;else if(command=="Hide")preferences.Visible=false;else if(command=="Toggle")preferences.Visible=!preferences.Visible;
        else for(int i=0;i<Corners.Length;i++)if(command==PositionEvent(Corners[i]))preferences.Position=Corners[i];
        if(command!="Start"&&command!="Standalone")Save(command=="LayoutCompact"||command=="LayoutDetailed");
    }
    void TrayAction(string command){try{Apply(command);}catch{}}
    void AddStandaloneTray(){
        if(tray!=null)return;
        try{trayIcon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);}catch{trayIcon=(Icon)SystemIcons.Shield.Clone();}
        if(trayIcon==null)trayIcon=(Icon)SystemIcons.Shield.Clone();
        tray=new NotifyIcon{Icon=trayIcon,Text=TextCatalog.Translate(language,"overlay_title"),Visible=true};
        tray.DoubleClick+=delegate{TrayAction("Toggle");};RefreshTray();
    }
    void RefreshTray(){
        if(tray==null)return;
        var old=tray.ContextMenuStrip;
        var menu=new ContextMenuStrip();
        menu.Items.Add(TextCatalog.Translate(language,"show"),null,delegate{TrayAction("Show");});
        menu.Items.Add(TextCatalog.Translate(language,"hide"),null,delegate{TrayAction("Hide");});
        menu.Items.Add(TextCatalog.Translate(language,"toggle"),null,delegate{TrayAction("Toggle");});
        var position=new ToolStripMenuItem(TextCatalog.Translate(language,"position"));
        for(int i=0;i<Corners.Length;i++){string command=PositionEvent(Corners[i]);position.DropDownItems.Add(TextCatalog.Translate(language,Corners[i]),null,delegate{TrayAction(command);});}
        menu.Items.Add(position);
        var layouts=new ToolStripMenuItem(TextCatalog.Translate(language,"layout"));
        layouts.DropDownItems.Add(TextCatalog.Translate(language,"compact"),null,delegate{TrayAction("LayoutCompact");});layouts.DropDownItems.Add(TextCatalog.Translate(language,"detailed"),null,delegate{TrayAction("LayoutDetailed");});menu.Items.Add(layouts);
        var languages=new ToolStripMenuItem(TextCatalog.Translate(language,"language"));languages.DropDownItems.Add("日本語",null,delegate{TrayAction("LanguageJa");});languages.DropDownItems.Add("English",null,delegate{TrayAction("LanguageEn");});menu.Items.Add(languages);
        var opacity=new ToolStripMenuItem(TextCatalog.Translate(language,"opacity"));foreach(int value in new[]{25,50,75,94,100}){string command="Opacity:"+value;opacity.DropDownItems.Add(value+"%",null,delegate{TrayAction(command);});}menu.Items.Add(opacity);
        menu.Items.Add(new ToolStripSeparator());menu.Items.Add(TextCatalog.Translate(language,"overlay_quit"),null,delegate{TrayAction("Quit");});
        tray.ContextMenuStrip=menu;tray.Text=TextCatalog.Translate(language,"overlay_title");if(old!=null)old.Dispose();
    }
    void TickDisplay(){
        try {
            foreach(var entry in signals)if(entry.Value.WaitOne(0)){if(entry.Key=="Quit"){Close();return;}Apply(entry.Key);}
            string currentLanguage=TextCatalog.CurrentLanguage;if(language!=currentLanguage){language=currentLanguage;RefreshTray();}
            try{view=ParseViewSettings(File.ReadAllText(Path.Combine(directory,"app-settings.json")));}catch{view=new ViewSettings();}ApplyOpacity(view.Opacity);
            var state=ParseState(File.ReadAllText(Path.Combine(directory,"status.json")));
            if(!Eligible(preferences,state,DateTime.UtcNow)){Hide();return;}
            using(var guard=Process.GetProcessById(state.Pid))if(!Matches(state,guard.Id,guard.ProcessName,guard.StartTime.ToUniversalTime(),guard.HasExited)){Hide();return;}
            actualHotkey=state.ToggleHotkey;emergencyHotkey=state.EmergencyHotkey;ClientSize=ScaledSize(new Size(preferences.Layout=="compact"?218:CardWidth,BodyHeight(preferences.Layout,view,PendingShortcut(view.DesiredHotkey,actualHotkey))),view.Scale);
            IntPtr foreground=GetForegroundWindow();uint pid;GetWindowThreadProcessId(foreground,out pid);bool target=false;foreach(int id in state.Games)if((uint)id==pid)target=true;
            if(!target){Hide();return;}
            using(var game=Process.GetProcessById((int)pid))if(!GameName(game.ProcessName)||game.HasExited){Hide();return;}
            Rect nativeBounds;if(!GetWindowRect(foreground,out nativeBounds)){Hide();return;}
            Rectangle bounds=Rectangle.Intersect(Rectangle.FromLTRB(nativeBounds.Left,nativeBounds.Top,nativeBounds.Right,nativeBounds.Bottom),Screen.FromHandle(foreground).Bounds);
            if(bounds.Width<ClientSize.Width+2*view.Margin||bounds.Height<ClientSize.Height+2*view.Margin){Hide();return;}
            armed=state.Armed;Location=Locate(bounds,ClientSize,preferences.Position,view.Margin);if(!Visible)Show();
            // Some fullscreen transitions can demote a topmost tool window. Reassert without activation.
            SetWindowPos(Handle,new IntPtr(-1),0,0,0,0,0x0013);
            ReadMonitorState();
            Invalidate();
        } catch { Hide(); }
    }
    void ApplyOpacity(double value){
        if(Math.Abs(Opacity-value)>0.0001)Opacity=value;
        // Form.Opacity=1 can remove WS_EX_LAYERED. Keep it at 100% so WS_EX_TRANSPARENT remains click-through.
        long styles=GetWindowLongPtr(Handle,-20).ToInt64();if((styles&0x00080000)==0)SetWindowLongPtr(Handle,-20,new IntPtr(styles|0x00080000|0x00000020));
        SetLayeredWindowAttributes(Handle,0,(byte)Math.Round(255*value),2);
    }
    static long? Count(Dictionary<string,object> d,string key){object value;long count;return d.TryGetValue(key,out value)&&value!=null&&long.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),out count)&&count>=0?(long?)count:null;}
    static MonitorState ParseMonitor(string json){
        try{
            var d=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);int pid;DateTime updated,started,warningUtc;object value;
            if(d==null||!int.TryParse(Convert.ToString(d["pid"]),out pid)||pid<=0||!DateTime.TryParse(Convert.ToString(d["updatedUtc"]),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out updated)||updated.Kind!=DateTimeKind.Utc)return null;
            DateTime? beginning=null;if(d.TryGetValue("startedUtc",out value)&&value!=null){if(!DateTime.TryParse(Convert.ToString(value),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out started)||started.Kind!=DateTimeKind.Utc||started>updated)return null;beginning=started;}
            DateTime? warning=null;if(d.TryGetValue("lastWarningUtc",out value)&&DateTime.TryParse(Convert.ToString(value),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out warningUtc)&&warningUtc.Kind==DateTimeKind.Utc)warning=warningUtc;
            return new MonitorState{Pid=pid,Updated=updated,Started=beginning,WarningUtc=warning,Health=d.TryGetValue("observerHealth",out value)?Convert.ToString(value):null,WarningKind=d.TryGetValue("lastWarningKind",out value)?Convert.ToString(value):null,KeyboardBlocked=Count(d,"normalKeyboardBlocked"),KeyboardPassed=Count(d,"normalKeyboardPassed"),MouseBlocked=Count(d,"normalMouseBlocked"),MousePassed=Count(d,"normalMousePassed")};
        }catch{return null;}
    }
    static bool MonitorMatches(MonitorState state,int pid,string name,DateTime started,bool exited){return state!=null&&!exited&&state.Pid==pid&&name=="FC27PadLockMonitor"&&state.Updated>=started&&(!state.Started.HasValue||(state.Started.Value>=started&&(state.Started.Value-started).TotalSeconds<=10));}
    static bool MonitorFresh(MonitorState state,DateTime now){return state!=null&&(now-state.Updated).TotalSeconds>=0&&(now-state.Updated).TotalSeconds<5;}
    static string MonitorKey(string health){return health=="observing"?"monitor_observing":health=="registration_failed"?"monitor_failed":health=="thread_stopped"?"monitor_stopped":"monitor_waiting";}
    static string WarningKey(string kind){
        switch(kind){case "guard_stopped":return "warning_guard_stopped";case "guard_heartbeat_stale":return "warning_guard_stale";case "guard_hook_registration_inactive":return "warning_guard_inactive";case "guard_status_unavailable":case "guard_process_unconfirmed":return "warning_guard_unknown";case "normal_non_escape_chain_passed_guard_reported_armed":return "warning_normal_passed";case "raw_ll_mismatch_suspected":return "warning_raw_mismatch";case "observer_registration_failed":return "warning_observer_failed";case "observer_thread_stopped":return "warning_observer_stopped";default:return "warning_other";}
    }
    static bool RecentWarning(MonitorState state,DateTime now){return state!=null&&!string.IsNullOrEmpty(state.WarningKind)&&state.WarningUtc.HasValue&&(now-state.WarningUtc.Value).TotalSeconds>=0&&(now-state.WarningUtc.Value).TotalSeconds<60;}
    void ReadMonitorState(){
        monitor=null;monitorLine="monitor_waiting";monitorWarning=false;
        try{
            var parsed=ParseMonitor(File.ReadAllText(Path.Combine(directory,"monitor-status.json")));
            if(!MonitorFresh(parsed,DateTime.UtcNow)){if(parsed!=null){monitorLine="monitor_stale";monitorWarning=true;}return;}
            using(var process=Process.GetProcessById(parsed.Pid))if(!MonitorMatches(parsed,process.Id,process.ProcessName,process.StartTime.ToUniversalTime(),process.HasExited))return;
            monitor=parsed;monitorLine=MonitorKey(parsed.Health);monitorWarning=parsed.Health!="observing"||RecentWarning(parsed,DateTime.UtcNow);
        }catch{}
    }
    static string Number(long? number){return number.HasValue?number.Value.ToString("N0",CultureInfo.InvariantCulture):"—";}
    static void DrawText(Graphics g,string text,float size,FontStyle style,Color color,RectangleF area){using(var font=new Font("Yu Gothic UI",size,style))using(var brush=new SolidBrush(color))using(var format=new StringFormat{Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap})g.DrawString(text,font,brush,area,format);}
    static void PaintCard(Graphics g,Rectangle box,bool active,string monitoring,bool warning,MonitorState data,string layout,string locale,DateTime now,ViewSettings widgets=null,string hotkey="Ctrl+Alt+F12",string emergency="Ctrl+Alt+F12") {
        if(widgets==null)widgets=new ViewSettings();
        g.SmoothingMode=SmoothingMode.AntiAlias;g.Clear(Color.FromArgb(19,25,35));Color muted=Color.FromArgb(159,177,198),amber=Color.FromArgb(247,200,103),white=Color.FromArgb(231,237,245);
        using(var pen=new Pen(Color.FromArgb(67,82,102),1))g.DrawRectangle(pen,box.Left,box.Top,box.Width-1,box.Height-1);
        using(var dot=new SolidBrush(active?Color.FromArgb(56,214,148):Color.FromArgb(247,186,69)))g.FillEllipse(dot,14,16,9,9);
        DrawText(g,"PAD LOCK",10,FontStyle.Bold,white,new RectangleF(31,9,box.Width-44,22));
        int y=31;
        if(widgets.Widgets.Contains("protection")){DrawText(g,TextCatalog.Translate(locale,active?"protection_active":"protection_paused"),11,FontStyle.Bold,active?Color.FromArgb(122,238,184):amber,new RectangleF(14,y,box.Width-28,23));y+=24;}
        if(widgets.Widgets.Contains("monitor")){DrawText(g,TextCatalog.Translate(locale,monitoring),8.5f,FontStyle.Regular,warning?amber:muted,new RectangleF(14,y,box.Width-28,21));y+=23;}
        string shortcut=TextCatalog.Translate(locale,"shortcut")+": "+hotkey;if(!string.Equals(hotkey,emergency,StringComparison.OrdinalIgnoreCase))shortcut+=" / "+TextCatalog.Translate(locale,"emergency")+": "+emergency;
        if(layout=="compact"){if(widgets.Widgets.Contains("shortcut")){DrawText(g,shortcut,8,FontStyle.Regular,PendingShortcut(widgets.DesiredHotkey,hotkey)?amber:muted,new RectangleF(14,y,box.Width-28,19));if(PendingShortcut(widgets.DesiredHotkey,hotkey))DrawText(g,TextCatalog.Translate(locale,"shortcut_pending"),7.5f,FontStyle.Regular,amber,new RectangleF(14,y+20,box.Width-28,18));}return;}
        if(widgets.Widgets.Contains("keyboard")||widgets.Widgets.Contains("mouse")){DrawText(g,TextCatalog.Translate(locale,"counts_since_start"),8,FontStyle.Regular,muted,new RectangleF(14,y,box.Width-28,19));y+=18;}
        string keyboard=TextCatalog.Translate(locale,"keyboard")+"   "+TextCatalog.Translate(locale,"blocked")+" "+Number(data==null?null:data.KeyboardBlocked)+"   "+TextCatalog.Translate(locale,"passed")+" "+Number(data==null?null:data.KeyboardPassed);
        string mouse=TextCatalog.Translate(locale,"mouse")+"   "+TextCatalog.Translate(locale,"blocked")+" "+Number(data==null?null:data.MouseBlocked)+"   "+TextCatalog.Translate(locale,"passed")+" "+Number(data==null?null:data.MousePassed);
        if(widgets.Widgets.Contains("keyboard")){DrawText(g,keyboard,9,FontStyle.Regular,white,new RectangleF(14,y,box.Width-28,22));y+=21;}if(widgets.Widgets.Contains("mouse")){DrawText(g,mouse,9,FontStyle.Regular,white,new RectangleF(14,y,box.Width-28,22));y+=21;}
        string note=RecentWarning(data,now)?TextCatalog.Translate(locale,WarningKey(data.WarningKind)):TextCatalog.Translate(locale,"no_recent_note");
        if(widgets.Widgets.Contains("warning")){DrawText(g,note,8,FontStyle.Regular,RecentWarning(data,now)?amber:muted,new RectangleF(14,y,box.Width-28,19));y+=20;}
        if(widgets.Widgets.Contains("shortcut")){DrawText(g,shortcut,8,FontStyle.Regular,muted,new RectangleF(14,y,box.Width-28,19));y+=18;if(PendingShortcut(widgets.DesiredHotkey,hotkey)){DrawText(g,TextCatalog.Translate(locale,"shortcut_pending"),7.5f,FontStyle.Regular,amber,new RectangleF(14,y,box.Width-28,18));y+=17;}}
        DrawText(g,TextCatalog.Translate(locale,"arrival_unknown"),7.5f,FontStyle.Regular,muted,new RectangleF(14,y+4,box.Width-28,18));
    }
    protected override void OnPaint(PaintEventArgs e){var saved=e.Graphics.Save();try{e.Graphics.ScaleTransform((float)view.Scale,(float)view.Scale);var logical=new Rectangle(0,0,preferences.Layout=="compact"?218:CardWidth,BodyHeight(preferences.Layout,view,PendingShortcut(view.DesiredHotkey,actualHotkey)));PaintCard(e.Graphics,logical,armed,monitorLine??"monitor_waiting",monitorWarning,monitor,preferences.Layout,language,DateTime.UtcNow,view,actualHotkey,emergencyHotkey);}finally{e.Graphics.Restore(saved);}}
    protected override void Dispose(bool disposing){if(disposing){if(timer!=null)timer.Dispose();if(tray!=null){tray.Visible=false;var menu=tray.ContextMenuStrip;tray.Dispose();tray=null;if(menu!=null)menu.Dispose();}if(trayIcon!=null){trayIcon.Dispose();trayIcon=null;}foreach(var item in signals.Values)item.Dispose();}base.Dispose(disposing);}
    static int SelfTest(string path){
        var tests=new List<object>();int failures=0;Action<string,bool> check=delegate(string name,bool ok){tests.Add(new{name=name,passed=ok});if(!ok)failures++;};
        check("default",ParseCommand(new string[0])=="Start");foreach(string cmd in new[]{"show","hide","toggle","quit"})check("command "+cmd,ParseCommand(new[]{"--"+cmd})!=null);
        check("standalone mode",ParseCommand(new[]{"--standalone"})=="Standalone");check("standalone extra argument rejected",ParseCommand(new[]{"--standalone","extra"})==null);
        check("Japanese language CLI",ParseCommand(new[]{"--language","ja"})=="LanguageJa");check("English language CLI",ParseCommand(new[]{"--language","en"})=="LanguageEn");check("invalid language CLI",ParseCommand(new[]{"--language","fr"})==null);
        check("compact layout CLI",ParseCommand(new[]{"--layout","compact"})=="LayoutCompact");check("detailed layout CLI",ParseCommand(new[]{"--layout","detailed"})=="LayoutDetailed");check("invalid layout CLI",ParseCommand(new[]{"--layout","other"})==null);
        check("opacity CLI",ParseCommand(new[]{"--opacity","94"})=="Opacity:94");check("opacity minimum CLI",ParseCommand(new[]{"--opacity","25"})=="Opacity:25");check("opacity maximum CLI",ParseCommand(new[]{"--opacity","100"})=="Opacity:100");check("invalid opacity CLI",ParseCommand(new[]{"--opacity","24"})==null&&ParseCommand(new[]{"--opacity","101"})==null);
        foreach(string corner in Corners)check("corner "+corner,ParseCommand(new[]{"--position",corner})==PositionEvent(corner));
        check("invalid position",ParseCommand(new[]{"--position","center"})==null);check("invalid args",ParseCommand(new[]{"--show","extra"})==null);
        check("separate prefix",Prefix("test")=="Local\\FC27PadLockOverlay-test-");
        check("default prefs",ParsePreferences("{}").Visible&&ParsePreferences("{}").Position=="top-right");
        var prefs=ParsePreferences("{\"visible\":false,\"position\":\"bottom-left\"}");check("stored prefs",!prefs.Visible&&prefs.Position=="bottom-left");
        check("default detailed layout",prefs.Layout=="detailed");check("saved compact layout",ParsePreferences("{\"visible\":true,\"position\":\"top-left\",\"layout\":\"compact\"}").Layout=="compact");
        DateTime now=new DateTime(2026,10,5,0,0,0,DateTimeKind.Utc);var d=new Dictionary<string,object>{{"pid",123},{"gamePids",new[]{456}},{"armed",true},{"hooksInstalled",true},{"hookThreadAlive",true},{"stopped",false},{"startedUtc",now.AddSeconds(-2).ToString("o")},{"updatedUtc",now.ToString("o")}};var j=new JavaScriptSerializer();var s=ParseState(j.Serialize(d));
        check("valid state",s!=null&&s.Games[0]==456);check("malformed state",ParseState("{")==null);check("missing fields",ParseState("{}")==null);
        check("legacy actual shortcut fallback",s.ToggleHotkey=="Ctrl+Alt+F12"&&s.EmergencyHotkey=="Ctrl+Alt+F12");d["toggleHotkey"]="F8";check("actual shortcut from guard",ParseState(j.Serialize(d)).ToggleHotkey=="F8");
        check("fresh",Fresh(s,now.AddSeconds(4)));check("stale",!Fresh(s,now.AddSeconds(5)));check("future",!Fresh(s,now.AddSeconds(-1)));
        check("eligible guard",Eligible(new Preferences(),s,now));check("hidden preference",!Eligible(new Preferences{Visible=false},s,now));
        check("missing state hidden",!Eligible(new Preferences(),null,now));s.Hooks=false;check("unregistered hooks hidden",!Eligible(new Preferences(),s,now));s.Hooks=true;
        s.ThreadAlive=false;check("dead hook thread hidden",!Eligible(new Preferences(),s,now));s.ThreadAlive=true;s.Stopped=true;check("stopped guard hidden",!Eligible(new Preferences(),s,now));s.Stopped=false;
        s.Armed=false;check("paused guard still display eligible",Eligible(new Preferences(),s,now));s.Armed=true;
        check("matching guard",Matches(s,123,"FC27PadLock",now.AddSeconds(-3),false));check("reused PID",!Matches(s,123,"FC27PadLock",now.AddSeconds(-1),false));check("exited guard",!Matches(s,123,"FC27PadLock",now.AddSeconds(-3),true));
        check("game names",GameName("FC27")&&GameName("FC27_Trial")&&GameName("FC27_Showcase")&&!GameName("Other"));
        var bounds=new Rectangle(100,200,800,600);check("top left",Locate(bounds,new Size(184,66),"top-left")==new Point(118,218));check("bottom right",Locate(bounds,new Size(184,66),"bottom-right")==new Point(698,716));
        var monitorData=new Dictionary<string,object>{{"pid",789},{"updatedUtc",now.ToString("o")},{"observerHealth","observing"},{"normalKeyboardBlocked",12},{"normalKeyboardPassed",null},{"normalMouseBlocked",34},{"normalMousePassed","invalid"},{"lastWarningKind","guard_stopped"},{"lastWarningUtc",now.AddSeconds(-3).ToString("o")}};
        var parsed=ParseMonitor(j.Serialize(monitorData));check("monitor stats parsed",parsed!=null&&parsed.KeyboardBlocked==12&&parsed.MouseBlocked==34);check("null and invalid stats safe",parsed.KeyboardPassed==null&&parsed.MousePassed==null&&Number(parsed.KeyboardPassed)=="—");
        check("fresh monitor",MonitorFresh(parsed,now));check("stale monitor",!MonitorFresh(parsed,now.AddSeconds(5)));check("future monitor",!MonitorFresh(parsed,now.AddSeconds(-1)));
        check("matching legacy monitor",MonitorMatches(parsed,789,"FC27PadLockMonitor",now.AddSeconds(-3),false));check("reused legacy monitor rejected",!MonitorMatches(parsed,789,"FC27PadLockMonitor",now.AddSeconds(1),false));
        parsed.Started=now.AddSeconds(-2);check("strict monitor reused PID rejected",!MonitorMatches(parsed,789,"FC27PadLockMonitor",now.AddSeconds(-1),false));
        check("recent warning",RecentWarning(parsed,now));check("old warning expired",!RecentWarning(parsed,now.AddSeconds(60)));check("warning friendly mapping",WarningKey("raw_ll_mismatch_suspected")=="warning_raw_mismatch");
        check("Japanese OS default",TextCatalog.DefaultLanguage("ja-JP")=="ja");check("other OS English default",TextCatalog.DefaultLanguage("de-DE")=="en");check("Japanese text",TextCatalog.Translate("ja","keyboard")=="キーボード");check("English text",TextCatalog.Translate("en","keyboard")=="Keyboard");check("supported locale list",TextCatalog.LanguageCodes.Length==2&&TextCatalog.IsSupported("ja")&&TextCatalog.IsSupported("en")&&!TextCatalog.IsSupported("fr"));
        check("opacity default",ParseViewSettings("{}").Opacity==0.94);check("opacity lower clamp",ParseViewSettings("{\"overlayOpacity\":0.1}").Opacity==0.25);check("opacity upper clamp",ParseViewSettings("{\"overlayOpacity\":2}").Opacity==1);check("invalid opacity default",ParseViewSettings("{\"overlayOpacity\":null}").Opacity==0.94);
        check("default widget set",ParseViewSettings("{}").Widgets.Count==6);var selected=ParseViewSettings("{\"overlayWidgets\":[\"shortcut\",\"unknown\"]}");check("unknown widget ignored",selected.Widgets.Count==1&&selected.Widgets.Contains("shortcut"));check("empty widgets supported",ParseViewSettings("{\"overlayWidgets\":[]}").Widgets.Count==0);
        check("actual desired shortcut comparison",!PendingShortcut("ctrl + alt + f12","Ctrl+Alt+F12")&&PendingShortcut("F8","Ctrl+Alt+F12"));check("detailed height bounded",BodyHeight("detailed",new ViewSettings(),true)<=220);check("hidden widgets reduce height",BodyHeight("detailed",selected,false)<BodyHeight("detailed",new ViewSettings(),false));
        check("scale and margin defaults",ParseViewSettings("{}").Scale==1&&ParseViewSettings("{}").Margin==18);check("scale upper clamp",ParseViewSettings("{\"overlayScale\":4}").Scale==1.5);check("scale lower clamp",ParseViewSettings("{\"overlayScale\":0}").Scale==0.75);check("margin clamps",ParseViewSettings("{\"overlayMargin\":0}").Margin==8&&ParseViewSettings("{\"overlayMargin\":200}").Margin==80);check("scaled card dimensions",ScaledSize(new Size(338,202),1.5)==new Size(507,303));check("custom corner margin",Locate(bounds,new Size(184,66),"bottom-right",40)==new Point(676,694));
        File.WriteAllText(path,j.Serialize(new{failures=failures,testCount=tests.Count,tests=tests,hooksInstalled=false,inputSent=false,guardIpcSent=false,overlayIpcSent=false,uiStarted=false,processOperations=false,settingsWritten=false}));return failures==0?0:1;
    }
    [STAThread]static int Main(string[] args){
        // Diagnostics execute before IPC, WindowsIdentity, Process inspection and any window creation.
        if(args.Length>0&&args[0]=="--self-test"){if(args.Length!=2||!Path.IsPathRooted(args[1]))return 3;try{return SelfTest(args[1]);}catch{return 1;}}
        if(args.Length>0&&args[0]=="--render-preview"){
            if(args.Length<2||args.Length>5||!Path.IsPathRooted(args[1]))return 3;string locale=args.Length>2?args[2]:TextCatalog.DefaultLanguage(CultureInfo.CurrentUICulture.Name),layout=args.Length>3?args[3]:"detailed";if(!TextCatalog.IsSupported(locale)||(layout!="detailed"&&layout!="compact")||(args.Length==5&&args[4]!="pending"))return 3;
            try{bool pending=args.Length==5;var widgets=new ViewSettings();if(pending)widgets.DesiredHotkey="F8";Size size=new Size(layout=="compact"?218:CardWidth,BodyHeight(layout,widgets,pending));var sample=new MonitorState{KeyboardBlocked=137,KeyboardPassed=0,MouseBlocked=4098,MousePassed=64,Health="observing"};using(var bitmap=new Bitmap(size.Width,size.Height))using(var graphics=Graphics.FromImage(bitmap)){PaintCard(graphics,new Rectangle(Point.Empty,size),true,"monitor_observing",false,sample,layout,locale,DateTime.UtcNow,widgets);bitmap.Save(args[1],System.Drawing.Imaging.ImageFormat.Png);}return 0;}catch{return 1;}
        }
        string command=ParseCommand(args);if(command==null)return 3;
        if(command.StartsWith("Opacity:",StringComparison.Ordinal)){try{TextCatalog.SetSetting("overlayOpacity",int.Parse(command.Substring(8),CultureInfo.InvariantCulture)/100.0);}catch{return 1;}command="SettingsChanged";}
        string prefix=Prefix(WindowsIdentity.GetCurrent().User.Value);bool fresh;using(var mutex=new Mutex(true,prefix+"Instance",out fresh)){
            if(!fresh||command=="Quit"){
                if(command=="Start")return 0;
                var deadline=Stopwatch.StartNew();
                do{try{using(var signal=EventWaitHandle.OpenExisting(prefix+command))signal.Set();return 0;}catch(WaitHandleCannotBeOpenedException){if(fresh)return 2;Thread.Sleep(50);}catch{return 2;}}while(deadline.ElapsedMilliseconds<1000);
                return 2;
            }
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            try{using(var overlay=new PadLockOverlay(prefix,command))using(var context=new ApplicationContext()){overlay.FormClosed+=delegate{context.ExitThread();};Application.Run(context);}return 0;}catch{return 1;}
        }
    }
}
