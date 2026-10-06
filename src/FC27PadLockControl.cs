using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

class PadLockControl : ApplicationContext {
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr window,int command);
    sealed class Snapshot {
        public int Pid,TrialSeconds;
        public bool Armed,HooksInstalled,ThreadAlive,Stopped;
        public DateTime StartedUtc,UpdatedUtc;
        public double? Remaining;
        public string ToggleHotkey="Ctrl+Alt+F12",EmergencyHotkey="Ctrl+Alt+F12";
    }
    readonly Form panel;
    readonly Label status;
    readonly Label monitorStatus;
    readonly NotifyIcon tray;
    readonly System.Windows.Forms.Timer timer;
    readonly EventWaitHandle open,launch,quit,detach;
    readonly Icon appIcon;
    readonly CheckBox overlayVisible;
    readonly ComboBox overlayPosition;
    readonly ComboBox languageChoice,overlayLayout;
    readonly TrackBar opacitySlider;
    readonly TextBox shortcutInput;
    readonly Label actualShortcut,opacityLabel;
    readonly CheckedListBox widgets;
    readonly Dictionary<Control,string> localized=new Dictionary<Control,string>();
    static readonly string[] WidgetNames={"protection","keyboard","mouse","monitor","warning","shortcut"};
    static readonly string[] Positions={"top-left","top-right","bottom-left","bottom-right"};
    string currentLanguage;
    readonly string overlayPath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"FC27PadLockOverlay.exe");
    readonly string monitorPath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"FC27PadLockMonitor.exe");
    readonly string guardPath=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"FC27PadLock.exe");
    readonly string statePath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FC27PadLock","status.json");
    readonly string guardPrefix;
    volatile bool exiting;
    bool detaching,preview,protectionBusy,pendingGameLaunch;
    bool protectionStartFailed;
    readonly object protectionSync=new object();
    readonly List<Control> protectionActions=new List<Control>();
    Snapshot lastGoodSnapshot;
    string lastGoodMonitorJson;
    readonly List<Panel> pages=new List<Panel>();
    readonly List<ModernButton> navigation=new List<ModernButton>();
    readonly ComboBox themeChoice;
    readonly CheckBox startupChoice,panelOnLogin,notificationsChoice;
    readonly CheckBox automaticUpdates;
    readonly Label updateStatus;
    readonly string startupMode;
    bool updateBusy,updateHandoff;
    UpdateService.Pending pendingUpdate;
    DateTime nextUpdateCheck=DateTime.UtcNow.AddSeconds(3),nextUpdateApply=DateTime.MinValue,updateHandoffStarted;
    string updateStatusKey="update_waiting";
    readonly NumericUpDown cooldownChoice,scaleChoice,marginChoice;
    TrayFlyout flyout;
    DateTime notificationStarted=DateTime.UtcNow,lastNotified=DateTime.MinValue,lastBalloon=DateTime.MinValue;
    string previewLanguage;
    bool refreshingOverlay;
    static string OverlayPrefix(string sid) { return "Local\\FC27PadLockOverlay-"+sid+"-"; }
    static string MonitorPrefix(string sid) { return "Local\\FC27PadLockMonitor-"+sid+"-"; }
    static string Prefix(string sid,bool ui) { return "Local\\FC27PadLock"+(ui?"UI":"")+"-"+sid+"-"; }
    static string CommandEvent(string prefix,string command) {
        switch(command) { case "--stop":return prefix+"Stop";case "--pause":return prefix+"Pause";case "--resume":return prefix+"Resume";default:throw new ArgumentException("Unsupported guard command."); }
    }
    static string ParseMode(string[] args) {
        if(args.Length==0)return "--panel";
        if(args.Length!=1)return null;
        switch(args[0]) { case "--panel":case "--background":case "--launch":case "--quit":case "--detach":return args[0];default:return null; }
    }
    static bool AutoEnableOnStartup(string mode){return mode=="--panel"||mode=="--background"||mode=="--launch";}
    static string ExistingInstanceCommand(string mode){return mode=="--launch"?"Launch":mode=="--quit"?"Quit":mode=="--detach"?"Detach":"Open";}
    static string ProtectionStatusKey(Snapshot state,bool live,bool fresh){return state==null?"status_unknown":!live?"protection_stopped":!fresh?"status_stale":state.Stopped?"protection_stopped":!state.HooksInstalled||!state.ThreadAlive?"guard_inactive":state.Armed?"protection_active":"protection_paused";}
    static bool GuardReady(Snapshot state,int pid,bool live,DateTime now){return state!=null&&state.Pid==pid&&live&&state.Armed&&state.HooksInstalled&&state.ThreadAlive&&!state.Stopped&&IsFresh(state,now);}
    static bool WaitForMonitorExit(Mutex instance,int timeout){bool acquired=false;try{try{acquired=instance.WaitOne(timeout);}catch(AbandonedMutexException){acquired=true;}return acquired;}finally{if(acquired)instance.ReleaseMutex();}}
    static bool IsFresh(Snapshot s,DateTime now) {
        if(s==null)return false;
        double age=(now-s.UpdatedUtc).TotalSeconds;
        return age>=0&&age<5&&s.StartedUtc<=s.UpdatedUtc;
    }
    static bool MatchesProcess(Snapshot s,int pid,string name,DateTime startedUtc,bool exited) {
        // State captures startup just after process creation; a reused PID with a later start fails.
        return s!=null&&!exited&&pid==s.Pid&&name=="FC27PadLock"&&s.StartedUtc>=startedUtc&&(s.StartedUtc-startedUtc).TotalSeconds<=10;
    }
    static Snapshot ParseState(string json) {
        try {
            var d=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);
            if(d==null||!(d["armed"] is bool)||!(d["hooksInstalled"] is bool)||!(d["hookThreadAlive"] is bool)||!(d["stopped"] is bool))return null;
            int pid,trial;DateTime started,updated;
            if(!int.TryParse(Convert.ToString(d["pid"],CultureInfo.InvariantCulture),out pid)||pid<=0||
               !int.TryParse(Convert.ToString(d["trialSeconds"],CultureInfo.InvariantCulture),out trial)||trial<0||trial>3600||
               !DateTime.TryParse(Convert.ToString(d["startedUtc"]),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out started)||
               !DateTime.TryParse(Convert.ToString(d["updatedUtc"]),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out updated)||
               started.Kind!=DateTimeKind.Utc||updated.Kind!=DateTimeKind.Utc||started>updated)return null;
            double? remaining=null;
            if(trial>0) { double value;if(!double.TryParse(Convert.ToString(d["remainingTrialSeconds"],CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out value)||double.IsNaN(value)||double.IsInfinity(value)||value<0||value>trial)return null;remaining=value; }
            object actual,emergency;return new Snapshot{Pid=pid,TrialSeconds=trial,Armed=(bool)d["armed"],HooksInstalled=(bool)d["hooksInstalled"],ThreadAlive=(bool)d["hookThreadAlive"],Stopped=(bool)d["stopped"],StartedUtc=started,UpdatedUtc=updated,Remaining=remaining,ToggleHotkey=d.TryGetValue("toggleHotkey",out actual)&&actual is string?(string)actual:"Ctrl+Alt+F12",EmergencyHotkey=d.TryGetValue("emergencyHotkey",out emergency)&&emergency is string?(string)emergency:"Ctrl+Alt+F12"};
        } catch { return null; }
    }
    PadLockControl(string prefix,string mode,bool renderOnly=false,string renderLanguage=null) {
        preview=renderOnly;previewLanguage=renderLanguage;
        startupMode=mode=="--background"?"--background":"--panel";
        guardPrefix=Prefix(WindowsIdentity.GetCurrent().User.Value,false);
        if(!preview){open=new EventWaitHandle(false,EventResetMode.AutoReset,prefix+"Open");launch=new EventWaitHandle(false,EventResetMode.AutoReset,prefix+"Launch");quit=new EventWaitHandle(false,EventResetMode.AutoReset,prefix+"Quit");detach=new EventWaitHandle(false,EventResetMode.AutoReset,prefix+"Detach");}
        try{appIcon=Icon.ExtractAssociatedIcon(Application.ExecutablePath);}catch{appIcon=(Icon)SystemIcons.Shield.Clone();}if(appIcon==null)appIcon=(Icon)SystemIcons.Shield.Clone();
        DesktopUi.SetTheme(preview?"dark":AppSettings.ReadString("uiTheme","dark"));
        panel=new Form{Icon=appIcon,ClientSize=new Size(1000,640),MinimumSize=new Size(920,620),StartPosition=FormStartPosition.CenterScreen,Font=new Font("Yu Gothic UI",10),AutoScaleMode=AutoScaleMode.Dpi};
        panel.Shown+=delegate{if(!preview){Rectangle area=Screen.FromControl(panel).WorkingArea;panel.MinimumSize=new Size(Math.Min(panel.MinimumSize.Width,area.Width),Math.Min(panel.MinimumSize.Height,area.Height));if(panel.Width>area.Width||panel.Height>area.Height)panel.Size=new Size(Math.Min(panel.Width,area.Width),Math.Min(panel.Height,area.Height));panel.Location=DesktopUi.Clamp(panel.Location,panel.Size,area);}};
        var side=new Panel{Dock=DockStyle.Left,Width=218,Padding=new Padding(18),AutoScroll=true};panel.Controls.Add(side);
        var logo=new Label{Text="PAD LOCK",Left=24,Top=30,Width=174,Height=35,Font=new Font("Segoe UI",19,FontStyle.Bold)};side.Controls.Add(logo);
        AddLabel(side,"ui_tagline",24,69,174,54);
        string[] categories={"ui_dashboard","ui_display","ui_input","ui_resident","ui_support"};
        for(int i=0;i<categories.Length;i++){int index=i;var button=new ModernButton{Left=12,Top=150+i*54,Width=192,Height=46};localized[button]=categories[i];button.Click+=delegate{ShowPage(index);};navigation.Add(button);side.Controls.Add(button);var page=new Panel{Left=242,Top=24,Width=732,Height=588,Anchor=AnchorStyles.Top|AnchorStyles.Bottom|AnchorStyles.Left|AnchorStyles.Right,AutoScroll=true};pages.Add(page);panel.Controls.Add(page);}
        AddLabel(side,"ui_version",24,544,176,38);AddLabel(side,"close_hint_short",24,586,176,35);
        Panel dashboard=pages[0],display=pages[1],input=pages[2],resident=pages[3],support=pages[4];
        var stateCard=Card(dashboard,0,0,720,160);AddLabel(stateCard,"ui_live_status",24,18,670,26);
        status=new Label{Left=24,Top=50,Width=665,Height=45,Font=new Font("Yu Gothic UI",23,FontStyle.Bold)};stateCard.Controls.Add(status);
        actualShortcut=new Label{Left=24,Top=106,Width=665,Height=46,Font=new Font("Yu Gothic UI",10)};stateCard.Controls.Add(actualShortcut);
        var observerCard=Card(dashboard,0,178,720,136);AddLabel(observerCard,"ui_observation",24,18,664,25);monitorStatus=new Label{Left=24,Top=55,Width=664,Height=68};observerCard.Controls.Add(monitorStatus);
        var actionsCard=Card(dashboard,0,332,720,152);AddLabel(actionsCard,"ui_quick_actions",24,18,664,26);
        AddButton(actionsCard,"enable",24,61,delegate{QueueProtection(false,0);},205,true);AddButton(actionsCard,"pause",251,61,delegate{Command("--pause");},205);AddButton(actionsCard,"focus_game",478,61,FocusGame,216);
        var guideCard=Card(dashboard,0,502,720,80);AddLabel(guideCard,"ui_game_guidance",24,14,670,58);
        var displayCard=Card(display,0,0,720,440);AddLabel(displayCard,"ui_display",24,18,670,34);
        AddLabel(displayCard,"language",24,73,180,28);languageChoice=Choice(displayCard,244,69,420);languageChoice.Items.AddRange(TextCatalog.LanguageNames);
        AddLabel(displayCard,"ui_theme",24,118,180,28);themeChoice=Choice(displayCard,244,114,420);
        overlayVisible=new CheckBox{Left=24,Top=159,Width=640,Height=29};localized[overlayVisible]="overlay_visible";displayCard.Controls.Add(overlayVisible);
        AddLabel(displayCard,"position",24,206,180,28);overlayPosition=Choice(displayCard,244,202,420);
        AddLabel(displayCard,"layout",24,251,180,28);overlayLayout=Choice(displayCard,244,247,420);
        AddLabel(displayCard,"opacity",24,298,180,28);opacitySlider=new TrackBar{Left=234,Top=290,Width=365,Height=42,Minimum=25,Maximum=100,TickFrequency=25};opacityLabel=new Label{Left=607,Top=298,Width=70,Height=28};displayCard.Controls.Add(opacitySlider);displayCard.Controls.Add(opacityLabel);
        AddLabel(displayCard,"ui_scale",24,347,180,28);scaleChoice=Number(displayCard,244,342,75,150,100);scaleChoice.Increment=5;AddLabel(displayCard,"ui_percent",372,347,100,28);
        AddLabel(displayCard,"ui_margin",24,391,180,28);marginChoice=Number(displayCard,244,387,8,80,18);AddLabel(displayCard,"ui_pixels",372,391,100,28);
        var widgetCard=Card(display,0,458,720,197);AddLabel(widgetCard,"widgets",24,18,400,28);widgets=new CheckedListBox{Left=24,Top=59,Width=670,Height=79,CheckOnClick=true,MultiColumn=true,ColumnWidth=220,IntegralHeight=false,BorderStyle=BorderStyle.None};widgetCard.Controls.Add(widgets);AddButton(widgetCard,"ui_reset_visual",24,145,ResetVisuals,300);
        var inputCard=Card(input,0,0,720,260);AddLabel(inputCard,"ui_input",24,18,670,34);AddLabel(inputCard,"shortcut",24,78,190,28);shortcutInput=new TextBox{Left=220,Top=74,Width=335};inputCard.Controls.Add(shortcutInput);AddButton(inputCard,"save",569,69,SaveShortcut,124,true);AddLabel(inputCard,"hotkey_hint",24,125,670,67);AddLabel(inputCard,"ui_emergency_hint",24,192,670,52);
        var testCard=Card(input,0,278,720,160);AddLabel(testCard,"ui_manual_actions",24,18,670,30);AddButton(testCard,"trial",24,67,delegate{QueueProtection(false,30);},305);AddButton(testCard,"stop",351,67,delegate{Command("--stop");},342);
        var residentCard=Card(resident,0,0,720,495);AddLabel(residentCard,"ui_resident",24,18,670,34);
        startupChoice=Check(residentCard,"ui_autostart",24,77);panelOnLogin=Check(residentCard,"ui_panel_login",24,124);notificationsChoice=Check(residentCard,"ui_notifications",24,171);
        AddLabel(residentCard,"ui_cooldown",24,229,300,28);cooldownChoice=Number(residentCard,374,225,5,300,30);AddLabel(residentCard,"ui_seconds",504,229,150,28);AddLabel(residentCard,"ui_resident_hint",24,284,665,58);
        automaticUpdates=Check(residentCard,"ui_auto_updates",24,355);updateStatus=new Label{Left=24,Top=397,Width=665,Height=42};residentCard.Controls.Add(updateStatus);AddButton(residentCard,"update_check_now",24,447,delegate{CheckForUpdate(true);},300);
        var supportCard=Card(support,0,0,720,265);AddLabel(supportCard,"ui_support",24,18,670,34);AddLabel(supportCard,"diagnostic_hint",24,73,670,60);AddButton(supportCard,"diagnostic_save",24,155,SaveDiagnostic,315,true);AddButton(supportCard,"github_report",361,155,ReportOnGitHub,333);AddButton(supportCard,"ui_manual",24,210,OpenManual,315);
        var gameCard=Card(support,0,283,720,135);AddLabel(gameCard,"ui_game_tools",24,18,670,30);AddButton(gameCard,"launch_game",24,66,delegate{QueueProtection(true,0);},315);AddButton(gameCard,"focus_game",361,66,FocusGame,333);
        var exitCard=Card(support,0,436,720,155);AddLabel(exitCard,"close_hint",24,21,665,60);AddButton(exitCard,"exit_all",24,95,ExitThread,670);
        var limitsCard=Card(support,0,609,720,160);AddLabel(limitsCard,"usage",24,22,670,128);
        panel.FormClosing+=delegate(object sender,FormClosingEventArgs e){if(!exiting){e.Cancel=true;panel.Hide();}};
        tray=new NotifyIcon{Icon=appIcon,Visible=!preview};tray.DoubleClick+=delegate{UiAction(OpenPanel);};tray.MouseUp+=delegate(object sender,MouseEventArgs e){if(e.Button==MouseButtons.Right||e.Button==MouseButtons.Left)ShowFlyout();};
        LoadPreferences();ApplyLanguage();if(preview){overlayVisible.Checked=true;for(int i=0;i<widgets.Items.Count;i++)widgets.SetItemChecked(i,true);}ShowPage(0);
        languageChoice.SelectedIndexChanged+=delegate{if(!refreshingOverlay&&languageChoice.SelectedIndex>=0)UiAction(delegate{TextCatalog.SetLanguage(TextCatalog.LanguageCodes[languageChoice.SelectedIndex]);ApplyLanguage();});};
        themeChoice.SelectedIndexChanged+=delegate{if(!refreshingOverlay&&themeChoice.SelectedIndex>=0)UiAction(delegate{AppSettings.Set("uiTheme",new[]{"dark","light","system"}[themeChoice.SelectedIndex]);ApplyTheme();});};
        overlayVisible.CheckedChanged+=delegate{if(!refreshingOverlay)UiAction(delegate{OverlayCommand(overlayVisible.Checked?"--show":"--hide");});};
        overlayPosition.SelectedIndexChanged+=delegate{if(!refreshingOverlay&&overlayPosition.SelectedIndex>=0)UiAction(delegate{OverlayCommand("--position "+Positions[overlayPosition.SelectedIndex]);});};
        overlayLayout.SelectedIndexChanged+=delegate{if(!refreshingOverlay&&overlayLayout.SelectedIndex>=0)UiAction(delegate{OverlayCommand("--layout "+(overlayLayout.SelectedIndex==0?"compact":"detailed"));});};
        opacitySlider.Scroll+=delegate{if(!refreshingOverlay)UiAction(delegate{AppSettings.Set("overlayOpacity",opacitySlider.Value/100.0);opacityLabel.Text=opacitySlider.Value+"%";});};
        widgets.ItemCheck+=delegate(object sender,ItemCheckEventArgs e){if(!refreshingOverlay)UiAction(delegate{var selected=new List<string>();for(int i=0;i<WidgetNames.Length;i++)if((i==e.Index?e.NewValue:widgets.GetItemCheckState(i))==CheckState.Checked)selected.Add(WidgetNames[i]);AppSettings.Set("overlayWidgets",selected.ToArray());});};
        BindNumber(scaleChoice,"overlayScale",100);BindNumber(marginChoice,"overlayMargin",1);BindNumber(cooldownChoice,"warningCooldownSeconds",1);
        panelOnLogin.CheckedChanged+=delegate{if(!refreshingOverlay)UiAction(delegate{AppSettings.Set("startPanelOnLogin",panelOnLogin.Checked);});};
        notificationsChoice.CheckedChanged+=delegate{if(!refreshingOverlay)UiAction(delegate{AppSettings.Set("notificationsEnabled",notificationsChoice.Checked);});};
        automaticUpdates.CheckedChanged+=delegate{if(!refreshingOverlay)UiAction(delegate{AppSettings.Set("automaticUpdates",automaticUpdates.Checked);if(automaticUpdates.Checked)nextUpdateCheck=DateTime.UtcNow;RefreshUpdateStatus();});};
        startupChoice.CheckedChanged+=delegate{if(!refreshingOverlay)UiAction(delegate{try{StartupRegistration.Update(Application.ExecutablePath,startupChoice.Checked);}finally{refreshingOverlay=true;try{startupChoice.Checked=StartupRegistration.Enabled(Application.ExecutablePath);}finally{refreshingOverlay=false;}}});};
        timer=new System.Windows.Forms.Timer{Interval=500};timer.Tick+=delegate{try{if(detach.WaitOne(0)){detaching=true;ExitThread();return;}if(quit.WaitOne(0)){ExitThread();return;}if(open.WaitOne(0))UiAction(OpenPanel);if(launch.WaitOne(0))UiAction(delegate{QueueProtection(true,0);});if(currentLanguage!=TextCatalog.CurrentLanguage)ApplyLanguage();RefreshStatus();UpdateTick();}catch{if(!exiting){status.Text=T("status_unknown");status.ForeColor=DesktopUi.Muted;}}};
        if(!preview){LoadUpdateHistory();timer.Start();RefreshStatus();UiAction(delegate{OverlayCommand("");});if(mode=="--panel"||(mode=="--background"&&AppSettings.ReadBool("startPanelOnLogin",false)))UiAction(OpenPanel);if(AutoEnableOnStartup(mode))QueueProtection(mode=="--launch",0);}
        else {status.Text=T("protection_active");status.ForeColor=DesktopUi.Accent;monitorStatus.Text=T("monitor_observing")+"\n"+string.Format(T("monitor_counts"),"1,420","1,387","33");actualShortcut.Text=T("current_shortcut")+": Ctrl+Alt+F12  /  "+T("emergency")+": Ctrl+Alt+F12";}
    }
    string T(string key){return preview?TextCatalog.Translate(previewLanguage??"ja",key):TextCatalog.T(key);}
    static CardPanel Card(Control parent,int x,int y,int width,int height){var card=new CardPanel{Left=x,Top=y,Width=width,Height=height,Anchor=AnchorStyles.Top|AnchorStyles.Left|AnchorStyles.Right};parent.Controls.Add(card);return card;}
    static ComboBox Choice(Control parent,int x,int y,int width){var box=new ModernChoice{Left=x,Top=y,Width=width,DropDownStyle=ComboBoxStyle.DropDownList,FlatStyle=FlatStyle.Flat};parent.Controls.Add(box);return box;}
    static NumericUpDown Number(Control parent,int x,int y,int min,int max,int value){var number=new NumericUpDown{Left=x,Top=y,Width=112,Minimum=min,Maximum=max,Value=value};parent.Controls.Add(number);return number;}
    CheckBox Check(Control parent,string key,int x,int y){var box=new CheckBox{Left=x,Top=y,Width=650,Height=30};localized[box]=key;parent.Controls.Add(box);return box;}
    void BindNumber(NumericUpDown number,string key,double divisor){number.ValueChanged+=delegate{if(!refreshingOverlay)UiAction(delegate{AppSettings.Set(key,(double)number.Value/divisor);});};}
    void ShowPage(int index){for(int i=0;i<pages.Count;i++){pages[i].Visible=i==index;navigation[i].Selected=i==index;navigation[i].Invalidate();}}
    void ApplyTheme(){DesktopUi.SetTheme(preview?"dark":AppSettings.ReadString("uiTheme","dark"));DesktopUi.Apply(panel);if(flyout!=null)DesktopUi.Apply(flyout);}
    void OpenManual(){string root=Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).FullName;string path=Path.Combine(root,"docs",currentLanguage=="en"?"MANUAL.en.html":"MANUAL.ja.html");Process.Start(new ProcessStartInfo(File.Exists(path)?path:"https://github.com/anyscape-maruo/fc27-pad-lock#readme"){UseShellExecute=true});}
    void ResetVisuals(){AppSettings.Set("uiTheme","dark");AppSettings.Set("overlayOpacity",OverlayPreferences.DefaultOpacity);AppSettings.Set("overlayScale",1.0);AppSettings.Set("overlayMargin",18);AppSettings.Set("overlayWidgets",WidgetNames);OverlayCommand("--position top-right");OverlayCommand("--layout "+OverlayPreferences.DefaultLayout);LoadPreferences();ApplyLanguage();RefreshOverlaySettings();}
    void ShowFlyout(){if(flyout!=null){flyout.Dispose();flyout=null;}flyout=CreateFlyout();flyout.Present(Cursor.Position);}
    TrayFlyout CreateFlyout(){var f=new TrayFlyout();var snapshot=preview?null:State();string shortcut=preview?"Ctrl+Alt+F12":snapshot!=null&&Running(snapshot)&&IsFresh(snapshot,DateTime.UtcNow)?snapshot.ToggleHotkey:T("status_unknown");var header=new Label{Text="PAD LOCK",Left=22,Top=20,Width=286,Height=31,Font=new Font("Segoe UI",16,FontStyle.Bold)};f.Controls.Add(header);var stateLabel=new Label{Text=status.Text,Left=24,Top=62,Width=282,Height=27,ForeColor=status.ForeColor};f.Controls.Add(stateLabel);var keyLabel=new Label{Text=T("shortcut")+"  "+shortcut,Left=24,Top=91,Width=282,Height=30};f.Controls.Add(keyLabel);string[] keys={"open_panel","tab_settings","enable","pause","toggle","exit_all"};Action[] actions={OpenPanel,delegate{OpenPanel();ShowPage(1);},delegate{QueueProtection(false,0);},delegate{Command("--pause");},delegate{OverlayCommand("--toggle");},ExitThread};for(int i=0;i<keys.Length;i++){int index=i;var button=new ModernButton{Text=T(keys[i]),Left=12,Top=136+i*47,Width=306,Height=42,Primary=i==0,Tag=keys[i],Enabled=!protectionBusy||!IsProtectionAction(keys[i])};button.Click+=delegate{f.Hide();UiAction(actions[index]);};f.Controls.Add(button);}DesktopUi.Apply(f);stateLabel.ForeColor=status.ForeColor;return f;}
    static int RenderPreview(string path,string language){Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);using(var app=new PadLockControl("preview-","--panel",true,language)){string stem=Path.Combine(Path.GetDirectoryName(path),Path.GetFileNameWithoutExtension(path));Directory.CreateDirectory(Path.GetDirectoryName(path));app.panel.ClientSize=new Size(1000,640);app.ShowPage(0);DesktopUi.SaveBitmap(app.panel,path);string[] names={"dashboard","display","input","resident","support"};for(int i=1;i<5;i++){app.ShowPage(i);DesktopUi.SaveBitmap(app.panel,stem+"."+names[i]+".png");}using(var f=app.CreateFlyout())DesktopUi.SaveBitmap(f,stem+".tray.png");app.detaching=true;app.ExitThread();}return 0;}
    void AddLabel(Control parent,string key,int x,int y,int width,int height){var label=new Label{Left=x,Top=y,Width=width,Height=height,UseMnemonic=false};localized[label]=key;parent.Controls.Add(label);}
    static bool IsProtectionAction(string key){return key=="enable"||key=="pause"||key=="trial"||key=="stop"||key=="launch_game";}
    void AddButton(Control parent,string key,int x,int y,Action action,int width=182,bool primary=false){var button=new ModernButton{Left=x,Top=y,Width=width,Height=40,Primary=primary};localized[button]=key;if(IsProtectionAction(key))protectionActions.Add(button);button.Click+=delegate{UiAction(action);};parent.Controls.Add(button);}
    void LoadPreferences(){refreshingOverlay=true;try{languageChoice.SelectedIndex=(preview?previewLanguage:TextCatalog.CurrentLanguage)=="en"?1:0;shortcutInput.Text=preview?"Ctrl+Alt+F12":AppSettings.ReadString("toggleHotkey","Ctrl+Alt+F12");opacitySlider.Value=preview?(int)(OverlayPreferences.DefaultOpacity*100):(int)Math.Round(AppSettings.ReadDouble("overlayOpacity",OverlayPreferences.DefaultOpacity,0.25,1)*100);opacityLabel.Text=opacitySlider.Value+"%";scaleChoice.Value=preview?100:(decimal)(AppSettings.ReadDouble("overlayScale",1,.75,1.5)*100);marginChoice.Value=preview?18:(decimal)AppSettings.ReadDouble("overlayMargin",18,8,80);cooldownChoice.Value=preview?30:(decimal)AppSettings.ReadDouble("warningCooldownSeconds",30,5,300);panelOnLogin.Checked=!preview&&AppSettings.ReadBool("startPanelOnLogin",false);notificationsChoice.Checked=preview||AppSettings.ReadBool("notificationsEnabled",true);automaticUpdates.Checked=preview||AppSettings.ReadBool("automaticUpdates",true);startupChoice.Checked=!preview&&StartupRegistration.Enabled(Application.ExecutablePath);}finally{refreshingOverlay=false;}}
    void ApplyLanguage(){
        currentLanguage=preview?(previewLanguage??"ja"):TextCatalog.CurrentLanguage;panel.Text=T("control_title")+"  ·  "+ProductVersion.Value;foreach(var item in localized)item.Key.Text=item.Value=="ui_version"?"FC27 Pad Lock\n"+ProductVersion.Value:T(item.Value);
        refreshingOverlay=true;try{int position=overlayPosition.SelectedIndex<0?1:overlayPosition.SelectedIndex;overlayPosition.Items.Clear();foreach(string name in Positions)overlayPosition.Items.Add(T(name));overlayPosition.SelectedIndex=position;int layout=overlayLayout.SelectedIndex<0?0:overlayLayout.SelectedIndex;overlayLayout.Items.Clear();overlayLayout.Items.Add(T("compact"));overlayLayout.Items.Add(T("detailed"));overlayLayout.SelectedIndex=layout;languageChoice.SelectedIndex=currentLanguage=="ja"?0:1;bool[] checkedItems=new bool[WidgetNames.Length];for(int i=0;i<widgets.Items.Count&&i<checkedItems.Length;i++)checkedItems[i]=widgets.GetItemChecked(i);widgets.Items.Clear();for(int i=0;i<WidgetNames.Length;i++)widgets.Items.Add(T(WidgetNames[i]),checkedItems[i]);}finally{refreshingOverlay=false;}
        refreshingOverlay=true;try{int theme=Array.IndexOf(new[]{"dark","light","system"},preview?"dark":AppSettings.ReadString("uiTheme","dark"));themeChoice.Items.Clear();foreach(string key in new[]{"ui_dark","ui_light","ui_system"})themeChoice.Items.Add(T(key));themeChoice.SelectedIndex=theme<0?0:theme;}finally{refreshingOverlay=false;}
        tray.Text=T("control_title");ApplyTheme();
    }
    void SaveShortcut(){ToggleShortcut shortcut;if(!ToggleShortcut.TryParse(shortcutInput.Text,out shortcut))throw new ArgumentException(TextCatalog.T("invalid_hotkey"));AppSettings.Set("toggleHotkey",shortcut.Label);shortcutInput.Text=shortcut.Label;RefreshStatus();}
    void SaveDiagnostic(){using(var dialog=new SaveFileDialog{Filter="ZIP (*.zip)|*.zip",FileName="FC27-Pad-Lock-diagnostic-"+DateTime.Now.ToString("yyyyMMdd-HHmm")+".zip",OverwritePrompt=true,Title=TextCatalog.T("diagnostic_save")})if(dialog.ShowDialog(panel)==DialogResult.OK){DiagnosticReport.Save(dialog.FileName,Path.GetDirectoryName(statePath));MessageBox.Show(panel,TextCatalog.T("diagnostic_saved"),TextCatalog.T("control_title"));}}
    void ReportOnGitHub(){Process.Start(new ProcessStartInfo("https://github.com/anyscape-maruo/fc27-pad-lock/issues/new?template=bug_report.md"){UseShellExecute=true});}
    void UiAction(Action action) { if(preview)return;try{action();}catch(Exception e){if(!exiting)MessageBox.Show(panel,e.Message,TextCatalog.T("control_title"));} }
    void QueueProtection(bool launchGame,int seconds){
        if(preview||exiting)return;
        if(protectionBusy){if(launchGame)pendingGameLaunch=true;return;}
        SetProtectionBusy(true);protectionStartFailed=false;
        // Force a UI handle even for tray-only startup so completion returns to this thread.
        IntPtr handle=panel.Handle;
        RefreshStatus();
        var worker=new BackgroundWorker();
        worker.DoWork+=delegate{if(launchGame)LaunchGame();else StartGuard(seconds);};
        worker.RunWorkerCompleted+=delegate(object sender,RunWorkerCompletedEventArgs result){
            worker.Dispose();if(exiting)return;
            SetProtectionBusy(false);protectionStartFailed=result.Error!=null;RefreshStatus();
            if(result.Error!=null){OpenPanel();MessageBox.Show(panel,result.Error.Message,T("control_title"));}
            bool launchNext=pendingGameLaunch&&result.Error==null;pendingGameLaunch=false;if(launchNext)QueueProtection(true,0);
        };
        worker.RunWorkerAsync();
    }
    void SetProtectionBusy(bool busy){protectionBusy=busy;foreach(var button in protectionActions)button.Enabled=!busy;if(flyout!=null&&!flyout.IsDisposed)foreach(Control button in flyout.Controls)if(IsProtectionAction(Convert.ToString(button.Tag)))button.Enabled=!busy;}
    string UpdateRoot(){return Directory.GetParent(AppDomain.CurrentDomain.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)).FullName;}
    void RefreshUpdateStatus(){if(updateStatus==null)return;string key=preview?"update_current":!AppSettings.ReadBool("automaticUpdates",true)?"update_disabled":UpdateService.DevelopmentRoot(UpdateRoot())?"update_development":updateStatusKey;updateStatus.Text="v"+ProductVersion.Value+" — "+T(key)+(pendingUpdate==null?"":" (v"+pendingUpdate.Version+")");updateStatus.ForeColor=key=="update_failed"?Color.DarkOrange:DesktopUi.Muted;}
    void LoadUpdateHistory(){try{var report=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(statePath),"update-status.json")));DateTime updated;if(Convert.ToString(report["result"])=="failed"&&DateTime.TryParse(Convert.ToString(report["updatedUtc"]),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out updated)&&updated.ToUniversalTime().AddHours(6)>DateTime.UtcNow){nextUpdateCheck=updated.ToUniversalTime().AddHours(6);updateStatusKey="update_failed";}}catch{}}
    void CheckForUpdate(bool manual){
        if(preview||exiting||updateBusy||updateHandoff||protectionBusy||pendingUpdate!=null)return;
        if(!manual&&!AppSettings.ReadBool("automaticUpdates",true))return;
        if(UpdateService.DevelopmentRoot(UpdateRoot())){updateStatusKey="update_development";nextUpdateCheck=DateTime.UtcNow.AddHours(6);RefreshUpdateStatus();return;}
        updateBusy=true;updateStatusKey="update_checking";nextUpdateCheck=DateTime.UtcNow.AddHours(6);RefreshUpdateStatus();string root=UpdateRoot();
        var worker=new BackgroundWorker();worker.DoWork+=delegate(object sender,DoWorkEventArgs result){result.Result=UpdateService.Check(root,startupMode);};
        worker.RunWorkerCompleted+=delegate(object sender,RunWorkerCompletedEventArgs result){worker.Dispose();if(exiting)return;updateBusy=false;if(result.Error!=null){updateStatusKey="update_failed";}else{pendingUpdate=(UpdateService.Pending)result.Result;updateStatusKey=pendingUpdate==null?"update_current":"update_wait_game";}RefreshUpdateStatus();};worker.RunWorkerAsync();
    }
    void UpdateTick(){
        if(exiting||protectionBusy)return;
        if(updateHandoff){
            if((DateTime.UtcNow-updateHandoffStarted).TotalSeconds>=120){updateHandoff=false;pendingUpdate=null;updateStatusKey="update_failed";nextUpdateCheck=DateTime.UtcNow.AddHours(6);RefreshUpdateStatus();return;}
            try{var report=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(statePath),"update-status.json")));DateTime updated;if(report!=null&&DateTime.TryParse(Convert.ToString(report["updatedUtc"]),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out updated)&&updated.ToUniversalTime()>=updateHandoffStarted){string result=Convert.ToString(report["result"]);if(result=="deferred"){updateHandoff=false;nextUpdateApply=DateTime.UtcNow.AddMinutes(1);updateStatusKey="update_wait_game";}else if(result=="failed"){updateHandoff=false;pendingUpdate=null;updateStatusKey="update_failed";nextUpdateCheck=DateTime.UtcNow.AddHours(6);}}}catch{}
        }else if(AppSettings.ReadBool("automaticUpdates",true)&&pendingUpdate!=null&&DateTime.UtcNow>=nextUpdateApply&&!UpdateService.GameRunning()){
            try{updateHandoffStarted=DateTime.UtcNow;if(UpdateService.StartInstaller(pendingUpdate)){updateHandoff=true;updateStatusKey="update_installing";}}catch{pendingUpdate=null;updateStatusKey="update_failed";nextUpdateCheck=DateTime.UtcNow.AddHours(6);}
        }else if(DateTime.UtcNow>=nextUpdateCheck)CheckForUpdate(false);
        RefreshUpdateStatus();
    }
    Snapshot State(){try{var snapshot=ParseState(File.ReadAllText(statePath));if(snapshot!=null)lastGoodSnapshot=snapshot;}catch{}return lastGoodSnapshot;}
    bool Running(Snapshot state){
        if(state==null)return false;
        try{using(var p=Process.GetProcessById(state.Pid))return MatchesProcess(state,p.Id,p.ProcessName,p.StartTime.ToUniversalTime(),p.HasExited);}catch{return false;}
    }
    void RefreshStatus(){
        var state=State();bool live=Running(state),fresh=IsFresh(state,DateTime.UtcNow);
        bool armed=live&&state.Armed&&state.HooksInstalled&&state.ThreadAlive&&!state.Stopped;
        string text=TextCatalog.T(protectionBusy?"protection_starting":protectionStartFailed?"protection_start_failed":ProtectionStatusKey(state,live,fresh));
        if(live&&state.TrialSeconds>0)text+=" / "+string.Format(TextCatalog.T("trial_remaining"),Math.Ceiling(state.Remaining.Value));
        status.Text=text;status.ForeColor=protectionBusy||protectionStartFailed||!live||!fresh||!state.HooksInstalled||!state.ThreadAlive||state.Stopped?Color.DarkOrange:armed?DesktopUi.Accent:DesktopUi.Muted;
        string tooltip="FC27: "+text;tray.Text=tooltip.Length>63?tooltip.Substring(0,63):tooltip;
        RefreshOverlaySettings();
        RefreshMonitorStatus();
        RefreshUpdateStatus();
        string desired=AppSettings.ReadString("toggleHotkey","Ctrl+Alt+F12");
        string actual=live&&fresh?state.ToggleHotkey:TextCatalog.T("status_unknown");
        actualShortcut.Text=TextCatalog.T("current_shortcut")+": "+actual+" / "+TextCatalog.T("emergency")+": Ctrl+Alt+F12";
        if(live&&fresh&&ShortcutPending(desired,state.ToggleHotkey))actualShortcut.Text+="\n"+TextCatalog.T("shortcut_pending")+": "+desired;
    }
    static bool ShortcutPending(string desired,string actual){ToggleShortcut parsed;return !ToggleShortcut.TryParse(desired,out parsed)||!string.Equals(parsed.Label,actual,StringComparison.OrdinalIgnoreCase);}
    void StartMonitor(string command){
        if(!File.Exists(monitorPath))throw new Exception(string.Format(TextCatalog.T("missing_component"),"FC27PadLockMonitor.exe"));
        lock(protectionSync){if(exiting)return;using(var process=Process.Start(new ProcessStartInfo(monitorPath,command){UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden})){}}
    }
    void StopMonitor(){try{using(var signal=EventWaitHandle.OpenExisting(MonitorPrefix(WindowsIdentity.GetCurrent().User.Value)+"Quit"))signal.Set();}catch(WaitHandleCannotBeOpenedException){}}
    void RefreshMonitorStatus(){
        try{
            string json;try{json=File.ReadAllText(Path.Combine(Path.GetDirectoryName(statePath),"monitor-status.json"));}catch(IOException){if(lastGoodMonitorJson==null)throw;json=lastGoodMonitorJson;}
            var d=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);
            DateTime updated=DateTime.Parse(d["updatedUtc"].ToString(),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind);
            double age=(DateTime.UtcNow-updated.ToUniversalTime()).TotalSeconds;
            if(age<0||age>=5)throw new Exception();
            using(var process=Process.GetProcessById(Convert.ToInt32(d["pid"])))if(process.ProcessName!="FC27PadLockMonitor"||process.HasExited||updated.ToUniversalTime()<process.StartTime.ToUniversalTime())throw new Exception();
            lastGoodMonitorJson=json;
            string health=Convert.ToString(d["observerHealth"]);
            monitorStatus.Text=health=="observing"?TextCatalog.T("monitor_observing")+"\n"+string.Format(TextCatalog.T("monitor_counts"),SafeCount(d,"normalObserved"),SafeCount(d,"normalChainBlocked"),SafeCount(d,"normalChainPassed")):TextCatalog.T(health=="registration_failed"?"monitor_failed":health=="thread_stopped"?"monitor_stopped":"monitor_waiting");
            monitorStatus.ForeColor=health=="observing"?DesktopUi.Muted:Color.DarkOrange;NotifyWarning(d);
        }catch{monitorStatus.Text=TextCatalog.T("monitor_waiting");monitorStatus.ForeColor=Color.DarkOrange;}
    }
    void NotifyWarning(Dictionary<string,object> data){
        object value;DateTime utc;if(!data.TryGetValue("lastWarningUtc",out value)||!DateTime.TryParse(Convert.ToString(value),CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out utc))return;utc=utc.ToUniversalTime();if(utc<=notificationStarted||utc<=lastNotified||utc>DateTime.UtcNow)return;lastNotified=utc;
        if(!NotificationDue(utc,notificationStarted,lastBalloon,DateTime.UtcNow,AppSettings.ReadDouble("warningCooldownSeconds",30,5,300),AppSettings.ReadBool("notificationsEnabled",true)))return;
        string kind=data.TryGetValue("lastWarningKind",out value)?Convert.ToString(value):"other";string key=kind=="guard_stopped"?"warning_guard_stopped":kind=="guard_heartbeat_stale"?"warning_guard_stale":kind=="guard_hook_registration_inactive"?"warning_guard_inactive":kind=="normal_non_escape_chain_passed_guard_reported_armed"?"warning_normal_passed":kind=="raw_ll_mismatch_suspected"?"warning_raw_mismatch":kind=="observer_registration_failed"?"warning_observer_failed":kind=="observer_thread_stopped"?"warning_observer_stopped":"warning_other";
        lastBalloon=DateTime.UtcNow;tray.ShowBalloonTip(5000,T("control_title"),T(key),ToolTipIcon.Warning);
    }
    static bool NotificationDue(DateTime warningUtc,DateTime started,DateTime last,DateTime now,double cooldown,bool enabled){return enabled&&warningUtc>started&&warningUtc<=now&&(now-last).TotalSeconds>=cooldown;}
    static string SafeCount(Dictionary<string,object> d,string key){object v;long value;return d.TryGetValue(key,out v)&&v!=null&&long.TryParse(Convert.ToString(v),out value)&&value>=0?value.ToString("N0",CultureInfo.InvariantCulture):"—";}
    void RefreshOverlaySettings(){
        refreshingOverlay=true;
        try{
            string json=null;try{json=File.ReadAllText(Path.Combine(Path.GetDirectoryName(statePath),"overlay-settings.json"));}catch{}
            var settings=OverlayPreferences.Parse(json);overlayVisible.Checked=settings.Visible;
            overlayPosition.SelectedIndex=Array.IndexOf(Positions,settings.Position);
            overlayLayout.SelectedIndex=settings.Layout=="compact"?0:1;
        }finally{refreshingOverlay=false;}
        refreshingOverlay=true;try{if(!opacitySlider.Capture)opacitySlider.Value=(int)Math.Round(AppSettings.ReadDouble("overlayOpacity",OverlayPreferences.DefaultOpacity,0.25,1)*100);opacityLabel.Text=opacitySlider.Value+"%";string[] chosen=AppSettings.ReadStringList("overlayWidgets",WidgetNames);for(int i=0;i<WidgetNames.Length;i++)widgets.SetItemChecked(i,Array.IndexOf(chosen,WidgetNames[i])>=0);}finally{refreshingOverlay=false;}
    }
    void OverlayCommand(string command){
        if(!File.Exists(overlayPath))throw new Exception(string.Format(TextCatalog.T("missing_component"),"FC27PadLockOverlay.exe"));
        using(var process=Process.Start(new ProcessStartInfo(overlayPath,command){UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden})){}
    }
    void StopOverlay(){try{using(var signal=EventWaitHandle.OpenExisting(OverlayPrefix(WindowsIdentity.GetCurrent().User.Value)+"Quit"))signal.Set();}catch(WaitHandleCannotBeOpenedException){}}
    void Command(string command){
        try { using(var ev=EventWaitHandle.OpenExisting(CommandEvent(guardPrefix,command)))ev.Set(); }
        catch(WaitHandleCannotBeOpenedException) { if(command!="--stop")throw new Exception(TextCatalog.T("guard_not_running")); }
    }
    void StartGuard(int seconds){
        if(exiting)return;
        var current=State();
        if(Running(current)){
            if(seconds==0&&current.TrialSeconds==0){Command("--resume");WaitForGuard(current.Pid);StartMonitor("");return;}
            Command("--stop");
            var wait=Stopwatch.StartNew();while(Running(current)&&wait.ElapsedMilliseconds<2500)Thread.Sleep(50);
            if(Running(current))throw new Exception(TextCatalog.T("guard_stop_pending"));
        }
        if(!File.Exists(guardPath))throw new Exception(string.Format(TextCatalog.T("missing_component"),"FC27PadLock.exe"));
        StopMonitorAndWait(); // New guard hooks would precede an older observer; re-register observation afterward.
        int newPid;lock(protectionSync){if(exiting)return;using(var p=Process.Start(new ProcessStartInfo(guardPath,seconds==0?"":"--trial "+seconds){UseShellExecute=true,WindowStyle=ProcessWindowStyle.Hidden})){newPid=p.Id;}}
        WaitForGuard(newPid);
        StartMonitor("");
    }
    void WaitForGuard(int pid){
        var deadline=Stopwatch.StartNew();bool ready=false;
        while(!exiting&&deadline.ElapsedMilliseconds<5000){var snapshot=State();if(GuardReady(snapshot,pid,Running(snapshot),DateTime.UtcNow)){ready=true;break;}Thread.Sleep(100);}
        if(exiting)return;
        if(!ready)throw new Exception(TextCatalog.T("guard_start_failed"));
    }
    void StopMonitorAndWait(){
        // The mutex identifies the current session's observer even if its saved PID is stale.
        Mutex instance;try{instance=Mutex.OpenExisting(MonitorPrefix(WindowsIdentity.GetCurrent().User.Value)+"Instance");}catch(WaitHandleCannotBeOpenedException){return;}
        using(instance){StopMonitor();if(!WaitForMonitorExit(instance,6000))throw new Exception(TextCatalog.T("monitor_stop_pending"));}
    }
    void LaunchGame(){
        StartGuard(0);
        try { lock(protectionSync){if(exiting)return;using(var p=Process.Start(new ProcessStartInfo("steam://rungameid/4080220"){UseShellExecute=true})){}} }
        catch(Exception e){throw new Exception(TextCatalog.T("steam_failed")+"\n"+e.Message);}
    }
    void FocusGame(){foreach(var p in Process.GetProcessesByName("FC27"))using(p){if(p.MainWindowHandle!=IntPtr.Zero){ShowWindow(p.MainWindowHandle,9);SetForegroundWindow(p.MainWindowHandle);break;}}}
    void OpenPanel(){panel.Show();ShowWindow(panel.Handle,9);panel.Activate();}
    static void Cleanup(Action action) { try{action();}catch{} }
    protected override void ExitThreadCore(){
        lock(protectionSync){exiting=true;}
        Exception stopError=null;
        try{if(!detaching&&!preview)Command("--stop");}catch(Exception e){stopError=e;}
        finally {
            if(!detaching&&!preview){Cleanup(StopOverlay);Cleanup(StopMonitor);}
            Cleanup(delegate{timer.Stop();});Cleanup(delegate{tray.Visible=false;tray.Dispose();});
            Cleanup(delegate{panel.Close();panel.Dispose();});Cleanup(delegate{timer.Dispose();});
            Cleanup(delegate{open.Dispose();});Cleanup(delegate{launch.Dispose();});Cleanup(delegate{quit.Dispose();});Cleanup(delegate{detach.Dispose();});Cleanup(delegate{if(flyout!=null)flyout.Dispose();});
            Cleanup(delegate{appIcon.Dispose();});
            if(stopError!=null)Cleanup(delegate{MessageBox.Show(TextCatalog.T("stop_failed")+"\n"+stopError.Message,TextCatalog.T("control_title"));});
            base.ExitThreadCore();
        }
    }
    static int SelfTest(string path) {
        var tests=new List<object>();int failed=0;
        Action<string,bool> check=delegate(string name,bool passed){tests.Add(new{name=name,passed=passed});if(!passed)failed++;};
        check("default panel",ParseMode(new string[0])=="--panel");
        foreach(string mode in new[]{"--panel","--background","--launch","--quit","--detach"})check("valid "+mode,ParseMode(new[]{mode})==mode);
        check("unknown argument rejected",ParseMode(new[]{"--oops"})==null);
        check("extra argument rejected",ParseMode(new[]{"--launch","extra"})==null);
        foreach(string mode in new[]{"--panel","--background","--launch"})check("fresh startup enables protection "+mode,AutoEnableOnStartup(mode));
        check("quit cannot enable protection",!AutoEnableOnStartup("--quit"));check("detach cannot enable protection",!AutoEnableOnStartup("--detach"));
        check("existing panel preserves pause",ExistingInstanceCommand("--panel")=="Open");check("existing background preserves pause",ExistingInstanceCommand("--background")=="Open");check("explicit game launch resumes",ExistingInstanceCommand("--launch")=="Launch");
        check("enable action serialized",IsProtectionAction("enable"));check("pause action serialized",IsProtectionAction("pause"));check("stop action serialized",IsProtectionAction("stop"));check("trial action serialized",IsProtectionAction("trial"));check("launch action serialized",IsProtectionAction("launch_game"));check("exit available while starting",!IsProtectionAction("exit_all"));
        check("guard prefix",Prefix("S-1-5-21-test",false)=="Local\\FC27PadLock-S-1-5-21-test-");
        check("UI prefix",Prefix("S-1-5-21-test",true)=="Local\\FC27PadLockUI-S-1-5-21-test-");
        check("overlay prefix is isolated",OverlayPrefix("S-1-5-21-test")=="Local\\FC27PadLockOverlay-S-1-5-21-test-");
        check("monitor prefix is isolated",MonitorPrefix("S-1-5-21-test")=="Local\\FC27PadLockMonitor-S-1-5-21-test-");
        check("stop name",CommandEvent("sample-","--stop")=="sample-Stop");
        check("pause name",CommandEvent("sample-","--pause")=="sample-Pause");
        check("resume name",CommandEvent("sample-","--resume")=="sample-Resume");
        bool invalid=false;try{CommandEvent("sample-","--wrong");}catch(ArgumentException){invalid=true;}check("invalid IPC command rejected",invalid);
        DateTime now=new DateTime(2026,10,5,0,0,0,DateTimeKind.Utc);
        var d=new Dictionary<string,object>{{"pid",123},{"trialSeconds",0},{"armed",true},{"hooksInstalled",true},{"hookThreadAlive",true},{"stopped",false},{"startedUtc",now.AddSeconds(-2).ToString("o")},{"updatedUtc",now.ToString("o")},{"remainingTrialSeconds",null}};
        var serializer=new JavaScriptSerializer();var s=ParseState(serializer.Serialize(d));
        check("valid state",s!=null&&s.Armed&&s.Pid==123);
        check("fresh armed guard ready",GuardReady(s,123,true,now));check("stale guard not ready",!GuardReady(s,123,true,now.AddSeconds(5)));check("unrelated guard not ready",!GuardReady(s,124,true,now));check("absent guard not ready",!GuardReady(s,123,false,now));
        check("active status",ProtectionStatusKey(s,true,true)=="protection_active");check("stale status overrides armed",ProtectionStatusKey(s,true,false)=="status_stale");check("dead process overrides armed",ProtectionStatusKey(s,false,true)=="protection_stopped");
        s.Armed=false;check("paused guard not ready",!GuardReady(s,123,true,now));check("paused status",ProtectionStatusKey(s,true,true)=="protection_paused");s.Armed=true;
        s.HooksInstalled=false;check("inactive guard not ready",!GuardReady(s,123,true,now));check("inactive hooks not called paused",ProtectionStatusKey(s,true,true)=="guard_inactive");s.HooksInstalled=true;
        s.ThreadAlive=false;check("dead hook thread not ready",!GuardReady(s,123,true,now));check("dead hook thread inactive status",ProtectionStatusKey(s,true,true)=="guard_inactive");s.ThreadAlive=true;
        s.Stopped=true;check("stopped guard not ready",!GuardReady(s,123,true,now));check("stopped status",ProtectionStatusKey(s,true,true)=="protection_stopped");s.Stopped=false;
        using(var fixture=new Mutex(false))using(var held=new ManualResetEvent(false))using(var release=new ManualResetEvent(false)){var holder=new Thread(delegate(){fixture.WaitOne();held.Set();release.WaitOne();fixture.ReleaseMutex();});holder.Start();held.WaitOne();try{check("running monitor wait times out independently of saved PID",!WaitForMonitorExit(fixture,10));}finally{release.Set();holder.Join();}check("exited monitor mutex available",WaitForMonitorExit(fixture,100));check("monitor wait releases acquired mutex",WaitForMonitorExit(fixture,100));}
        check("malformed state rejected",ParseState("{invalid")==null);
        check("missing state rejected",ParseState("{}")==null);
        check("fresh state",IsFresh(s,now.AddSeconds(4)));
        check("stale state",!IsFresh(s,now.AddSeconds(5)));
        check("future timestamp rejected",!IsFresh(s,now.AddSeconds(-1)));
        check("matching process",MatchesProcess(s,123,"FC27PadLock",now.AddSeconds(-3),false));
        check("PID reused rejected",!MatchesProcess(s,123,"FC27PadLock",now.AddSeconds(-1),false));
        check("different PID rejected",!MatchesProcess(s,124,"FC27PadLock",now.AddSeconds(-3),false));
        check("other process name rejected",!MatchesProcess(s,123,"OtherApp",now.AddSeconds(-3),false));
        check("exited process rejected",!MatchesProcess(s,123,"FC27PadLock",now.AddSeconds(-3),true));
        check("unrelated old process rejected",!MatchesProcess(s,123,"FC27PadLock",now.AddMinutes(-1),false));
        d["armed"]="true";check("incorrect boolean rejected",ParseState(serializer.Serialize(d))==null);d["armed"]=true;
        d["pid"]=-1;check("invalid PID rejected",ParseState(serializer.Serialize(d))==null);d["pid"]=123;
        d["startedUtc"]=now.AddSeconds(1).ToString("o");check("reversed timestamps rejected",ParseState(serializer.Serialize(d))==null);d["startedUtc"]=now.AddSeconds(-2).ToString("o");
        d["trialSeconds"]=30;d["remainingTrialSeconds"]=29.5;s=ParseState(serializer.Serialize(d));check("valid trial state",s!=null&&s.Remaining==29.5);
        d["remainingTrialSeconds"]=31;check("trial remaining out of range rejected",ParseState(serializer.Serialize(d))==null);
        d["remainingTrialSeconds"]=null;check("trial missing remaining rejected",ParseState(serializer.Serialize(d))==null);
        check("startup target same path",StartupRegistration.OwnedTarget(@"C:\app\Control.exe",@"C:\app\Control.exe"));
        check("foreign startup rejected",!StartupRegistration.OwnedTarget(@"C:\other\Control.exe",@"C:\app\Control.exe"));
        check("flyout clamp right bottom",DesktopUi.Clamp(new Point(980,680),new Size(330,435),new Rectangle(0,0,1000,700))==new Point(670,265));
        check("flyout clamp negative origin",DesktopUi.Clamp(new Point(-2200,-100),new Size(330,435),new Rectangle(-1920,0,1920,1080))==new Point(-1920,0));
        check("old warning not notified",!NotificationDue(now.AddSeconds(-2),now,now.AddMinutes(-1),now,30,true));
        check("fresh warning notified",NotificationDue(now,now.AddSeconds(-1),now.AddMinutes(-1),now,30,true));
        check("notification cooldown",!NotificationDue(now,now.AddSeconds(-1),now.AddSeconds(-5),now,30,true));
        check("notification off",!NotificationDue(now,now.AddSeconds(-1),now.AddMinutes(-1),now,30,false));
        check("future warning rejected",!NotificationDue(now.AddSeconds(1),now.AddSeconds(-1),now.AddMinutes(-1),now,30,true));
        DiagnosticReport.PureTests(check);
        UpdateService.PureTests(check);
        check("canonical shortcut already applied",!ShortcutPending("alt+ctrl+F8","Ctrl+Alt+F8"));check("different shortcut pending",ShortcutPending("Ctrl+Shift+F8","Ctrl+Alt+F12"));check("invalid shortcut rejected",ShortcutPending("F8","Ctrl+Alt+F12"));
        File.WriteAllText(path,serializer.Serialize(new{failures=failed,testCount=tests.Count,tests=tests,hooksInstalled=false,inputSent=false,ipcSent=false,processOperations=false,steamLaunched=false,settingsWritten=false,diagnosticZipSaved=false}));
        return failed==0?0:1;
    }
    [STAThread]static int Main(string[] args){
        // This branch precedes identity lookup, Mutex/event creation, UI startup and all live operations.
        if(args.Length>0&&args[0]=="--preview-ui"){if((args.Length!=2&&args.Length!=4)||!Path.IsPathRooted(args[1])||(args.Length==4&&(args[2]!="--language"||!TextCatalog.IsSupported(args[3]))))return 3;try{return RenderPreview(args[1],args.Length==4?args[3]:"ja");}catch{return 1;}}
        if(args.Length>0&&args[0]=="--self-test") { if(args.Length!=2||!Path.IsPathRooted(args[1]))return 3;try{return SelfTest(args[1]);}catch{return 1;} }
        if(args.Length>0&&args[0]=="--diagnostic"){if(args.Length!=2||!Path.IsPathRooted(args[1])||File.Exists(args[1]))return 3;try{DiagnosticReport.Save(args[1],Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FC27PadLock"));return 0;}catch{return 1;}}
        string mode=ParseMode(args);if(mode==null)return 3;
        string prefix=Prefix(WindowsIdentity.GetCurrent().User.Value,true);
        bool fresh;using(var mutex=new Mutex(true,prefix+"Instance",out fresh)){
            if(!fresh||mode=="--quit"||mode=="--detach"){
                string name=ExistingInstanceCommand(mode);
                try{using(var ev=EventWaitHandle.OpenExisting(prefix+name))ev.Set();return 0;}catch{return 2;}
            }
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            try{Application.Run(new PadLockControl(prefix,mode));return 0;}
            catch(Exception ex){MessageBox.Show(ex.Message,TextCatalog.T("control_title"));return 1;}
        }
    }
}
