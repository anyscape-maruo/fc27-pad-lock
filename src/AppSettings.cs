using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Web.Script.Serialization;

// A versioned, additive configuration store. Unknown fields are retained on updates.
static class AppSettings {
    public const int SchemaVersion=1;
    static readonly string PathName=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FC27PadLock","app-settings.json");
    public static bool TryRead(out Dictionary<string,object> values){
        try { if(!File.Exists(PathName)){values=new Dictionary<string,object>();return true;}values=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(PathName));return values!=null; }
        catch { values=null;return false; }
    }
    public static string ReadString(string key,string fallback){Dictionary<string,object> d;object value;return TryRead(out d)&&d.TryGetValue(key,out value)&&value is string?(string)value:fallback;}
    public static bool ReadBool(string key,bool fallback){Dictionary<string,object> d;object value;return TryRead(out d)&&d.TryGetValue(key,out value)&&value is bool?(bool)value:fallback;}
    public static double ReadDouble(string key,double fallback,double min,double max){Dictionary<string,object> d;object value;double number;if(!TryRead(out d)||!d.TryGetValue(key,out value)||!double.TryParse(Convert.ToString(value,CultureInfo.InvariantCulture),NumberStyles.Float,CultureInfo.InvariantCulture,out number)||double.IsNaN(number)||double.IsInfinity(number))return fallback;return Math.Max(min,Math.Min(max,number));}
    public static string[] ReadStringList(string key,string[] fallback){Dictionary<string,object> d;object value;if(!TryRead(out d)||!d.TryGetValue(key,out value)||value is string||!(value is IEnumerable))return fallback;var list=new List<string>();foreach(object item in (IEnumerable)value)if(item is string)list.Add((string)item);return list.ToArray();}
    public static void Set(string key,object value){
        if(string.IsNullOrWhiteSpace(key))throw new ArgumentException("A settings key is required.");
        using(var mutex=new Mutex(false,"Local\\FC27PadLockSettings-"+WindowsIdentity.GetCurrent().User.Value)){
            bool held=false;try{
                try{held=mutex.WaitOne(3000);}catch(AbandonedMutexException){held=true;}
                if(!held)throw new IOException("Settings are busy.");
                Dictionary<string,object> d;if(!TryRead(out d))throw new IOException("Settings are unreadable; existing data was preserved.");
                d[key]=value;if(!d.ContainsKey("schemaVersion"))d["schemaVersion"]=SchemaVersion;Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathName));
                string temp=PathName+".tmp";File.WriteAllText(temp,new JavaScriptSerializer().Serialize(d));if(File.Exists(PathName))File.Replace(temp,PathName,null);else File.Move(temp,PathName);
            }finally{if(held)mutex.ReleaseMutex();}
        }
    }
}
