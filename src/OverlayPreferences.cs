using System;
using System.Collections.Generic;
using System.Web.Script.Serialization;

// Control and Overlay use the same defaults, including when settings are absent.
sealed class OverlayPreferences {
    public const double DefaultOpacity=0.50;
    public const string DefaultLayout="compact";
    public bool Visible=true;
    public string Position="top-right",Layout=DefaultLayout;
    public static OverlayPreferences Parse(string json) {
        var result=new OverlayPreferences();
        try {
            var values=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);
            if(values==null)return result;
            object value;
            if(values.TryGetValue("visible",out value)&&value is bool)result.Visible=(bool)value;
            if(values.TryGetValue("position",out value)&&value is string&&
               Array.IndexOf(new[]{"top-left","top-right","bottom-left","bottom-right"},(string)value)>=0)result.Position=(string)value;
            if(values.TryGetValue("layout",out value)&&(string.Equals(value as string,"compact",StringComparison.Ordinal)||string.Equals(value as string,"detailed",StringComparison.Ordinal)))result.Layout=(string)value;
        } catch { }
        return result;
    }
}
