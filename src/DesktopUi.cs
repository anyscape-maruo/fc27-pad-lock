using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

static class DesktopUi {
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr window,int attribute,ref int value,int size);
    public static Color Background,Card,Text,Muted,Border,Accent,Hover;
    public static bool Dark;
    public static void SetTheme(string theme){
        Dark=theme!="light";
        if(theme=="system")try{Dark=Convert.ToInt32(Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize","AppsUseLightTheme",0))==0;}catch{}
        Background=Dark?Color.FromArgb(14,22,36):Color.FromArgb(241,245,249);
        Card=Dark?Color.FromArgb(22,33,51):Color.White;
        Text=Dark?Color.FromArgb(229,239,247):Color.FromArgb(24,38,58);
        Muted=Dark?Color.FromArgb(147,165,187):Color.FromArgb(91,109,129);
        Border=Dark?Color.FromArgb(42,58,79):Color.FromArgb(218,228,237);
        Accent=Dark?Color.FromArgb(71,214,224):Color.FromArgb(0,119,138);
        Hover=Dark?Color.FromArgb(34,54,75):Color.FromArgb(224,242,246);
    }
    public static GraphicsPath Rounded(Rectangle r,int radius){var p=new GraphicsPath();int d=radius*2;p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;}
    public static void Apply(Control control){
        control.ForeColor=Text;
        control.BackColor=control is Form?Background:control is CardPanel?Card:control.Parent==null?Background:control.Parent.BackColor;
        if(control is TextBox||control is ComboBox||control is CheckedListBox||control is NumericUpDown){control.BackColor=Card;control.ForeColor=Text;}
        if(control is Label&&Convert.ToString(control.Tag)=="muted")control.ForeColor=Muted;
        foreach(Control child in control.Controls)Apply(child);control.Invalidate();
        if(control is Form&&control.IsHandleCreated)try{int dark=Dark?1:0;DwmSetWindowAttribute(control.Handle,20,ref dark,4);}catch{}
    }
    public static void SaveBitmap(Form form,string path){form.Show();Application.DoEvents();using(var bitmap=new Bitmap(form.Width,form.Height)){form.DrawToBitmap(bitmap,new Rectangle(Point.Empty,bitmap.Size));bitmap.Save(path,System.Drawing.Imaging.ImageFormat.Png);}form.Hide();}
    public static Point Clamp(Point point,Size size,Rectangle area){return new Point(Math.Max(area.Left,Math.Min(point.X,area.Right-size.Width)),Math.Max(area.Top,Math.Min(point.Y,area.Bottom-size.Height)));}
}

class CardPanel : Panel {
    public CardPanel(){DoubleBuffered=true;Padding=new Padding(24);}
    protected override void OnPaintBackground(PaintEventArgs e){e.Graphics.Clear(Parent==null?DesktopUi.Background:Parent.BackColor);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;using(var path=DesktopUi.Rounded(new Rectangle(0,0,Width-1,Height-1),14)){using(var b=new SolidBrush(DesktopUi.Card))e.Graphics.FillPath(b,path);using(var p=new Pen(DesktopUi.Border))e.Graphics.DrawPath(p,path);}}
}
class ModernButton : Button {
    bool hover;public bool Selected,Primary;
    public ModernButton(){FlatStyle=FlatStyle.Flat;FlatAppearance.BorderSize=0;Cursor=Cursors.Hand;TextAlign=ContentAlignment.MiddleLeft;Padding=new Padding(16,0,12,0);Height=42;SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);}
    protected override void OnMouseEnter(EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
    protected override void OnMouseLeave(EventArgs e){hover=false;Invalidate();base.OnMouseLeave(e);}
    protected override void OnPaint(PaintEventArgs e){e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;Color bg=Primary?DesktopUi.Accent:Selected||hover?DesktopUi.Hover:Parent.BackColor;Color fg=Primary?(DesktopUi.Dark?DesktopUi.Background:Color.White):Selected?DesktopUi.Accent:DesktopUi.Text;using(var p=DesktopUi.Rounded(new Rectangle(0,0,Width-1,Height-1),8))using(var b=new SolidBrush(bg))e.Graphics.FillPath(b,p);TextRenderer.DrawText(e.Graphics,Text,Font,new Rectangle(Padding.Left,0,Width-Padding.Horizontal,Height),Enabled?fg:DesktopUi.Muted,TextFormatFlags.VerticalCenter|TextFormatFlags.Left|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);if(Focused){using(var pen=new Pen(DesktopUi.Accent))e.Graphics.DrawRectangle(pen,3,3,Width-7,Height-7);}}
}
class ModernChoice : ComboBox {
    public ModernChoice(){DropDownStyle=ComboBoxStyle.DropDownList;FlatStyle=FlatStyle.Flat;DrawMode=DrawMode.OwnerDrawFixed;ItemHeight=25;}
    protected override void OnDrawItem(DrawItemEventArgs e){using(var brush=new SolidBrush((e.State&DrawItemState.Selected)!=0?DesktopUi.Hover:DesktopUi.Card))e.Graphics.FillRectangle(brush,e.Bounds);string text=e.Index>=0?Convert.ToString(Items[e.Index]):Text;TextRenderer.DrawText(e.Graphics,text,Font,new Rectangle(e.Bounds.X+8,e.Bounds.Y,e.Bounds.Width-12,e.Bounds.Height),DesktopUi.Text,TextFormatFlags.VerticalCenter|TextFormatFlags.Left|TextFormatFlags.EndEllipsis|TextFormatFlags.NoPrefix);}
}
class TrayFlyout : Form {
    public TrayFlyout(){FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;KeyPreview=true;StartPosition=FormStartPosition.Manual;ClientSize=new Size(330,435);Font=new Font("Yu Gothic UI",10);BackColor=DesktopUi.Background;Deactivate+=delegate{Hide();};KeyDown+=delegate(object sender,KeyEventArgs e){if(e.KeyCode==Keys.Escape)Hide();};}
    protected override void OnSizeChanged(EventArgs e){base.OnSizeChanged(e);if(Width>20&&Height>20)using(var path=DesktopUi.Rounded(new Rectangle(0,0,Width,Height),14))Region=new Region(path);}
    public void Present(Point cursor){Location=DesktopUi.Clamp(new Point(cursor.X-Width,cursor.Y-Height),Size,Screen.FromPoint(cursor).WorkingArea);Show();Activate();}
}

static class StartupRegistration {
    static string ShortcutPath {get{return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup),"FC27 Pad Lock.lnk");}}
    static object Shortcut(out object shell){shell=Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell",true));return shell.GetType().InvokeMember("CreateShortcut",BindingFlags.InvokeMethod,null,shell,new object[]{ShortcutPath});}
    static object Get(object item,string name){return item.GetType().InvokeMember(name,BindingFlags.GetProperty,null,item,null);}
    static void Set(object item,string name,object value){item.GetType().InvokeMember(name,BindingFlags.SetProperty,null,item,new[]{value});}
    public static bool OwnedTarget(string actual,string expected){try{return string.Equals(Path.GetFullPath(actual),Path.GetFullPath(expected),StringComparison.OrdinalIgnoreCase);}catch{return false;}}
    public static bool Enabled(string executable){if(!File.Exists(ShortcutPath))return false;object shell=null,item=null;try{item=Shortcut(out shell);return OwnedTarget(Convert.ToString(Get(item,"TargetPath")),executable);}finally{if(item!=null)Marshal.FinalReleaseComObject(item);if(shell!=null)Marshal.FinalReleaseComObject(shell);}}
    public static void Update(string executable,bool enabled){object shell=null,item=null;try{item=Shortcut(out shell);if(File.Exists(ShortcutPath)&&!OwnedTarget(Convert.ToString(Get(item,"TargetPath")),executable))throw new IOException(TextCatalog.T("startup_conflict"));if(enabled){Set(item,"TargetPath",executable);Set(item,"Arguments","--background");Set(item,"WorkingDirectory",Path.GetDirectoryName(executable));Set(item,"IconLocation",executable+",0");Set(item,"WindowStyle",7);item.GetType().InvokeMember("Save",BindingFlags.InvokeMethod,null,item,null);}else if(File.Exists(ShortcutPath))File.Delete(ShortcutPath);}finally{if(item!=null)Marshal.FinalReleaseComObject(item);if(shell!=null)Marshal.FinalReleaseComObject(shell);}}
}
