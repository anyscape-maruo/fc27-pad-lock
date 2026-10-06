using System;
using System.IO;
using System.Reflection;
using System.Web.Script.Serialization;
using System.Collections.Generic;

// Redirect both stores before any read/write; never touches the user's settings.
class SettingsPersistence {
    static void Require(bool condition,string name){if(!condition)throw new Exception(name);}
    static int Main(string[] args) {
        if(args.Length!=1||!Path.IsPathRooted(args[0]))return 3;
        string directory=Path.Combine(args[0],"settings-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path=Path.Combine(directory,"app-settings.json");
        typeof(AppSettings).GetField("PathName",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,path);
        typeof(TextCatalog).GetField("SettingsPath",BindingFlags.NonPublic|BindingFlags.Static).SetValue(null,path);
        try {
            File.WriteAllText(path,"{\"overlayOpacity\":0.48,\"extensionSettings\":{\"retained\":true}}");
            AppSettings.Set("uiTheme","light");
            TextCatalog.SetLanguage("en");
            Require(AppSettings.ReadDouble("overlayOpacity",0.94,0.25,1)==0.48,"opacity survives language/theme updates");
            Require(AppSettings.ReadString("uiTheme","")=="light","theme survives language update");
            Require(TextCatalog.CurrentLanguage=="en","saved language readback");
            var data=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(path));
            Require(data.ContainsKey("extensionSettings"),"unknown extension fields retained");
            Require(Convert.ToInt32(data["schemaVersion"])==1,"schema saved");
            AppSettings.Set("overlayOpacity",0.65);
            TextCatalog.SetSetting("overlayOpacity",0.48);
            Require(AppSettings.ReadDouble("overlayOpacity",0.94,0.25,1)==0.48,"both setting routes persist same field");
            Require(!File.Exists(path+".tmp"),"atomic write finished");
            File.WriteAllText(path,"{broken");
            bool rejected=false;try{TextCatalog.SetLanguage("ja");}catch(IOException){rejected=true;}
            Require(rejected&&File.ReadAllText(path)=="{broken","unreadable settings preserved");
            File.WriteAllText(Path.Combine(args[0],"settings-persistence-result.json"),"{\"failures\":0,\"checks\":8,\"userSettingsWritten\":false}");
            return 0;
        } catch(Exception error){Console.Error.WriteLine(error.Message);return 1;}
    }
}
