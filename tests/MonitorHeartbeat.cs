using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using System.Collections.Generic;

// Exercise the sampler without creating UI, observer hooks, or game input.
class MonitorHeartbeat {
    static int Main(string[] args) {
        if(args.Length!=2)return 3;
        var type=Assembly.LoadFile(Path.GetFullPath(args[0])).GetType("PadLockMonitor");
        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
        var sample=FormatterServices.GetUninitializedObject(type);
        string directory=Path.Combine(Path.GetFullPath(args[1]),"monitor-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        type.GetField("directory",flags).SetValue(sample,directory);
        type.GetField("startedUtc",flags).SetValue(sample,DateTime.UtcNow);
        type.GetField("gamePids",flags).SetValue(sample,new uint[0]);
        type.GetField("events",flags).SetValue(sample,new Queue<string>());
        type.GetField("hooks",flags).SetValue(sample,new Thread(delegate(){}));
        try {
            type.GetMethod("Sample",flags).Invoke(sample,null);
            type.GetMethod("Sample",flags).Invoke(sample,null);
            if(!File.Exists(Path.Combine(directory,"monitor-status.json"))||!File.Exists(Path.Combine(directory,"monitor-events.jsonl")))throw new Exception("Sampler did not write heartbeat/events");
            Console.WriteLine("Heartbeat saved twice without guard; hooksInstalled=false; inputSent=false");
            return 0;
        }catch(Exception error){Console.Error.WriteLine(error.InnerException??error);return 1;}
    }
}
