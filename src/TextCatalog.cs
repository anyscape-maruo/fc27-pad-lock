using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Web.Script.Serialization;

static class TextCatalog {
    static readonly string SettingsPath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FC27PadLock","app-settings.json");
    static readonly object CacheLock=new object();
    static DateTime cachedStamp=DateTime.MinValue;
    static string cachedLanguage;
    public static string[] LanguageCodes { get { return new[]{"ja","en"}; } }
    public static string[] LanguageNames { get { return new[]{"日本語","English"}; } }
    static readonly Dictionary<string,string[]> Words=new Dictionary<string,string[]>{
        {"ui_tagline",new[]{"コントローラーに集中。\n保護と状態を、ここで。","Controller first.\nProtection at a glance."}},
        {"ui_game_guidance",new[]{"最初の画面からコントローラーで操作。\nAlt+Tabで他アプリへ移動。解除キーで一時解除できます。","Use your controller from the first game screen.\nAlt+Tab switches apps. Use the release key to pause protection."}},
        {"ui_dashboard",new[]{"ダッシュボード","Dashboard"}},
        {"ui_display",new[]{"表示・オーバーレイ","Appearance & overlay"}},
        {"ui_input",new[]{"入力・解除キー","Input & release key"}},
        {"ui_resident",new[]{"常駐・通知","Startup & notifications"}},
        {"ui_support",new[]{"サポート","Support"}},
        {"ui_version",new[]{"FC27 Pad Lock\nPreview 0.1.0","FC27 Pad Lock\nPreview 0.1.0"}},
        {"close_hint_short",new[]{"×で通知領域へ","Close to the system tray"}},
        {"ui_live_status",new[]{"現在の保護状態","CURRENT PROTECTION"}},
        {"ui_observation",new[]{"FC27への入力観測","FC27 INPUT OBSERVATION"}},
        {"ui_quick_actions",new[]{"クイック操作","QUICK ACTIONS"}},
        {"ui_theme",new[]{"アプリのテーマ","App theme"}},
        {"ui_dark",new[]{"ダーク","Dark"}},
        {"ui_light",new[]{"ライト","Light"}},
        {"ui_system",new[]{"Windowsに合わせる","Match Windows"}},
        {"ui_scale",new[]{"オーバーレイの大きさ","Overlay size"}},
        {"ui_margin",new[]{"画面の端からの余白","Distance from screen edge"}},
        {"ui_percent",new[]{"%","%"}},
        {"ui_pixels",new[]{"px","px"}},
        {"ui_reset_visual",new[]{"表示設定を初期値に戻す","Restore appearance defaults"}},
        {"ui_emergency_hint",new[]{"緊急解除：Ctrl + Alt + F12\nこのキーは設定を変えても使えます。","Emergency release: Ctrl + Alt + F12\nThis key remains available after changing settings."}},
        {"ui_manual_actions",new[]{"試験・停止操作","MANUAL TEST & STOP"}},
        {"ui_autostart",new[]{"Windowsへのログイン時に保護を起動","Start protection when signing in to Windows"}},
        {"ui_panel_login",new[]{"ログイン時にこのパネルも表示する","Also show this panel at sign-in"}},
        {"ui_notifications",new[]{"監視で新しい注意を検知したら通知する","Notify when the monitor records a new warning"}},
        {"ui_cooldown",new[]{"通知の最小間隔","Minimum time between notifications"}},
        {"ui_seconds",new[]{"秒","seconds"}},
        {"ui_resident_hint",new[]{"×はパネルを隠すだけです。\n通知領域のアイコンから、いつでも戻れます。","Closing the panel keeps protection running.\nReopen it from the system tray icon at any time."}},
        {"ui_manual",new[]{"マニュアルを開く","Open manual"}},
        {"ui_game_tools",new[]{"ゲームの操作","GAME TOOLS"}},
        {"startup_conflict",new[]{"同名の自動起動ショートカットは別のアプリを参照しています。既存の設定は変更しませんでした。","The startup shortcut belongs to another application. Its existing settings were preserved."}},
        {"control_title",new[]{"FC27 パッド保護","FC27 Pad Lock"}},
        {"overlay_title",new[]{"FC27 状態表示","FC27 Status Overlay"}},
        {"protection_active",new[]{"保護稼働中","Protection running"}},
        {"protection_paused",new[]{"一時解除中","Protection paused"}},
        {"protection_stopped",new[]{"保護停止中","Protection stopped"}},
        {"monitor_observing",new[]{"入力監視：観測中","Input monitor: observing"}},
        {"monitor_waiting",new[]{"入力監視：確認待ち","Input monitor: unavailable"}},
        {"monitor_stale",new[]{"入力監視：更新が途絶","Input monitor: stale"}},
        {"monitor_failed",new[]{"入力監視：登録に失敗","Input monitor: registration failed"}},
        {"monitor_stopped",new[]{"入力監視：停止","Input monitor: stopped"}},
        {"counts_since_start",new[]{"観測開始からの累計 / チェーン結果","Since monitor start / hook-chain result"}},
        {"keyboard",new[]{"キーボード","Keyboard"}},
        {"mouse",new[]{"マウス","Mouse"}},
        {"blocked",new[]{"遮断","Blocked"}},
        {"passed",new[]{"通過","Passed"}},
        {"recent_note",new[]{"最近の注意","Recent notice"}},
        {"no_recent_note",new[]{"最近の注意なし","No recent notice"}},
        {"arrival_unknown",new[]{"ゲーム内部への到達は未確認","Game receipt is unverified"}},
        {"show",new[]{"表示をON","Show overlay"}},
        {"hide",new[]{"表示をOFF","Hide overlay"}},
        {"toggle",new[]{"表示を切り替え","Toggle overlay"}},
        {"position",new[]{"表示位置","Position"}},
        {"top-left",new[]{"左上","Top left"}},
        {"top-right",new[]{"右上","Top right"}},
        {"bottom-left",new[]{"左下","Bottom left"}},
        {"bottom-right",new[]{"右下","Bottom right"}},
        {"layout",new[]{"表示情報量","Layout"}},
        {"compact",new[]{"コンパクト","Compact"}},
        {"detailed",new[]{"詳細表示","Detailed"}},
        {"language",new[]{"言語","Language"}},
        {"overlay_quit",new[]{"オーバーレイだけ終了","Exit overlay only"}},
        {"warning_guard_stopped",new[]{"入力ガードが停止しています","Input guard has stopped"}},
        {"warning_guard_stale",new[]{"入力ガードの状態更新が途絶","Guard heartbeat is stale"}},
        {"warning_guard_inactive",new[]{"入力ガードの登録またはスレッド停止","Guard hooks or thread are inactive"}},
        {"warning_guard_unknown",new[]{"入力ガードの状態が未確認","Guard state is unconfirmed"}},
        {"warning_normal_passed",new[]{"保護稼働の記録中に通常入力が通過","Normal input passed while guard reported active"}},
        {"warning_raw_mismatch",new[]{"Raw Inputとフックの観測数に差","Raw Input and hook counts differ"}},
        {"warning_observer_failed",new[]{"入力監視のフック登録に失敗","Monitor hook registration failed"}},
        {"warning_observer_stopped",new[]{"入力監視のスレッドが停止","Monitor thread has stopped"}},
        {"warning_other",new[]{"入力監視で注意を記録","Monitor recorded a notice"}},
        {"enable",new[]{"保護を有効","Enable protection"}},
        {"pause",new[]{"一時解除","Pause protection"}},
        {"stop",new[]{"保護を停止","Stop protection"}},
        {"launch_game",new[]{"FC27を起動","Launch FC27"}},
        {"focus_game",new[]{"FC27へ戻る","Return to FC27"}},
        {"trial",new[]{"30秒試験","30-second trial"}}
        ,{"shortcut",new[]{"解除キー","Release key"}}
        ,{"shortcut_pending",new[]{"設定した解除キーは反映待ち","Requested release key is pending"}}
        ,{"emergency",new[]{"緊急","Emergency"}}
        ,{"opacity",new[]{"不透明度","Opacity"}}
        ,{"tab_control",new[]{"操作・診断","Controls & diagnostics"}}
        ,{"tab_settings",new[]{"設定","Settings"}}
        ,{"open_panel",new[]{"操作パネルを開く","Open control panel"}}
        ,{"exit_all",new[]{"すべて終了して保護解除","Exit all and release protection"}}
        ,{"overlay_visible",new[]{"ゲーム内に状態を表示","Show in-game status overlay"}}
        ,{"widgets",new[]{"表示する情報","Visible information"}}
        ,{"save",new[]{"保存","Save"}}
        ,{"diagnostic_save",new[]{"診断レポートを保存","Save diagnostic report"}}
        ,{"github_report",new[]{"GitHubで報告する","Report on GitHub"}}
        ,{"diagnostic_hint",new[]{"診断ZIPを報告に添付できます。アプリから投稿・送信はしません。","Attach the diagnostic ZIP to your report. The app does not submit or send it."}}
        ,{"usage",new[]{"最初の画面からコントローラーで操作してください。\n他アプリへはAlt+Tab。Raw Input等の全入力経路は遮断保証しません。\nウィンドウ／ボーダーレス向け。排他的全画面の表示は未保証です。","Use your controller from the first game screen.\nUse Alt+Tab for other apps. Blocking all paths, including Raw Input, is not guaranteed.\nOverlay supports windowed/borderless mode. Exclusive fullscreen is not guaranteed."}}
        ,{"close_hint",new[]{"×はパネルを隠します。完全終了は通知領域のメニューから。","The close button hides this panel. Use the tray menu to exit all helpers."}}
        ,{"hotkey_hint",new[]{"2つ以上の修飾キー＋F1～F24。緊急Ctrl+Alt+F12は常に残ります。","Use at least two modifiers plus F1–F24. Emergency Ctrl+Alt+F12 stays available."}}
        ,{"invalid_hotkey",new[]{"Ctrl/Alt/Shift/Winの2つ以上＋F1～F24を入力してください。","Enter at least two of Ctrl/Alt/Shift/Win plus F1–F24."}}
        ,{"status_unknown",new[]{"状態を確認できません","Status unavailable"}}
        ,{"status_stale",new[]{"状態の更新が遅れています","Status update is stale"}}
        ,{"protection_starting",new[]{"保護を起動しています…","Starting protection…"}}
        ,{"protection_start_failed",new[]{"保護の起動を確認できません","Protection startup unconfirmed"}}
        ,{"guard_inactive",new[]{"入力ガードが動作していません","Input guard is inactive"}}
        ,{"monitor_stop_pending",new[]{"入力監視の終了待ちです。少し待ってからやり直してください。","Waiting for the input monitor to stop. Try again shortly."}}
        ,{"ui_auto_updates",new[]{"GitHubの新しいリリースへ自動更新する（FC27終了後）","Automatically install new GitHub releases after FC27 exits"}}
        ,{"update_check_now",new[]{"更新を確認","Check for updates"}}
        ,{"update_waiting",new[]{"起動後に更新を確認します","Checking for updates after startup"}}
        ,{"update_checking",new[]{"更新を確認・ダウンロードしています…","Checking and downloading updates…"}}
        ,{"update_current",new[]{"利用可能な新しいリリースはありません","No newer release is available"}}
        ,{"update_wait_game",new[]{"更新を準備しました。FC27終了後に適用します","Update ready; it will install after FC27 exits"}}
        ,{"update_installing",new[]{"更新を適用しています。完了後に再起動します","Installing update; the app will restart afterward"}}
        ,{"update_disabled",new[]{"自動更新はオフです","Automatic updates are off"}}
        ,{"update_development",new[]{"開発フォルダーでは自動更新しません","Automatic updates are disabled in a development folder"}}
        ,{"update_failed",new[]{"更新できませんでした。次回の確認を待つか再確認してください","Update failed; wait for the next check or check again"}}
        ,{"trial_remaining",new[]{"試験：残り {0} 秒","Trial: {0} seconds remaining"}}
        ,{"current_shortcut",new[]{"現在の解除キー","Current release key"}}
        ,{"monitor_counts",new[]{"通常入力 {0}件 ／ チェーン遮断 {1}件 ／ 通過 {2}件","Normal input {0} / chain blocked {1} / passed {2}"}}
        ,{"missing_component",new[]{"{0} がありません。配布フォルダーを確認してください。","{0} is missing. Check your distribution folder."}}
        ,{"guard_not_running",new[]{"入力ガードは起動していません。先に保護を有効にしてください。","Input guard is not running. Enable protection first."}}
        ,{"guard_stop_pending",new[]{"保護の終了待ちです。少し待ってからやり直してください。","Waiting for protection to stop. Try again shortly."}}
        ,{"guard_start_failed",new[]{"保護の起動を確認できませんでした。状態とログを確認してください。","Could not confirm protection startup. Check status and logs."}}
        ,{"steam_failed",new[]{"Steamを起動できませんでした。インストールを確認してください。","Could not launch Steam. Check its installation."}}
        ,{"stop_failed",new[]{"停止通知に失敗しました。緊急Ctrl+Alt+F12で解除してください。","Stop notification failed. Release protection with emergency Ctrl+Alt+F12."}}
        ,{"diagnostic_saved",new[]{"診断レポートを保存しました。ZIPをGitHubの報告に添付してください。","Diagnostic report saved. Attach the ZIP to your GitHub report."}}
        ,{"protection",new[]{"保護状態","Protection state"}}
        ,{"monitor",new[]{"入力監視","Input monitor"}}
        ,{"warning",new[]{"最近の注意","Recent notice"}}
    };
    public static bool IsSupported(string code){return code=="ja"||code=="en";}
    public static string DefaultLanguage(string culture){return culture!=null&&(culture=="ja"||culture.StartsWith("ja-",StringComparison.OrdinalIgnoreCase))?"ja":"en";}
    public static string Translate(string code,string key){string[] texts;return Words.TryGetValue(key,out texts)?texts[code=="ja"?0:1]:key;}
    public static string CurrentLanguage {
        get { lock(CacheLock){DateTime stamp=DateTime.MinValue;try{stamp=File.GetLastWriteTimeUtc(SettingsPath);}catch{}if(cachedLanguage!=null&&stamp==cachedStamp)return cachedLanguage;string language=DefaultLanguage(CultureInfo.CurrentUICulture.Name);try{var d=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(SettingsPath));object value;if(d!=null&&d.TryGetValue("language",out value)&&IsSupported(Convert.ToString(value)))language=Convert.ToString(value);}catch{}cachedLanguage=language;cachedStamp=stamp;return language;} }
    }
    public static string T(string key){return Translate(CurrentLanguage,key);}
    public static void SetLanguage(string code){if(!IsSupported(code))throw new ArgumentException("Supported languages: ja, en.");SetSetting("language",code);}
    internal static void SetSetting(string key,object value){
        AppSettings.Set(key,value);lock(CacheLock){cachedLanguage=null;}
    }
}
