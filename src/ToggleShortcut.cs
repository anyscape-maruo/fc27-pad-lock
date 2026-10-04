using System;
using System.Collections.Generic;

sealed class ToggleShortcut {
    public readonly int Key,Modifiers;
    public readonly string Label;
    ToggleShortcut(int key,int modifiers,string label){Key=key;Modifiers=modifiers;Label=label;}
    public static readonly ToggleShortcut Emergency=new ToggleShortcut(0x7b,3,"Ctrl+Alt+F12");
    public bool Matches(int key,int modifiers){return Key==key&&Modifiers==modifiers;}
    public static bool TryParse(string text,out ToggleShortcut shortcut){
        shortcut=null;if(string.IsNullOrWhiteSpace(text))return false;string[] parts=text.Split('+');if(parts.Length<3||parts.Length>5)return false;
        int mods=0;for(int i=0;i<parts.Length-1;i++){
            int flag;switch(parts[i].Trim().ToLowerInvariant()){case "ctrl":case "control":flag=1;break;case "alt":flag=2;break;case "shift":flag=4;break;case "win":case "windows":flag=8;break;default:return false;}
            if((mods&flag)!=0)return false;mods|=flag;
        }
        int bitCount=0;for(int bit=1;bit<=8;bit<<=1)if((mods&bit)!=0)bitCount++;if(bitCount<2)return false;
        string keyText=parts[parts.Length-1].Trim().ToUpperInvariant();int fn;if(keyText.Length<2||keyText[0]!='F'||!int.TryParse(keyText.Substring(1),out fn)||fn<1||fn>24)return false;
        var names=new List<string>();if((mods&1)!=0)names.Add("Ctrl");if((mods&2)!=0)names.Add("Alt");if((mods&4)!=0)names.Add("Shift");if((mods&8)!=0)names.Add("Win");names.Add("F"+fn);
        shortcut=new ToggleShortcut(0x6f+fn,mods,string.Join("+",names.ToArray()));return true;
    }
}
