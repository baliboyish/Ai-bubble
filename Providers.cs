using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using System.Web.Script.Serialization;

namespace MuseBubbleWin
{
    public sealed class Provider
    {
        public readonly string Id, Name, Url;
        public readonly Color Accent;
        Provider(string id,string name,string url,string color)
        { Id=id;Name=name;Url=url;Accent=ColorTranslator.FromHtml(color); }
        public static readonly Provider[] All={
            new Provider("chatgpt","ChatGPT","https://chatgpt.com/","#18AC91"),
            new Provider("claude","Claude","https://claude.ai/","#E58B61"),
            new Provider("muse","Muse","https://muse.ai/","#638AFF") };
        public static Provider Find(string id)
        { foreach(var p in All) if(p.Id==id) return p;return All[2]; }
    }
    public sealed class ChatState
    { public bool Positioned,Pinned;public int X,Y,Width=440,Height=640; }
    public sealed class SettingsData
    {
        public int X=int.MinValue,Y=int.MinValue,Size=84;
        public double Opacity=1;
        public string Theme="dark",Selected="muse";
        public bool Hover=true;
        public Dictionary<string,ChatState> Windows;
    }
    public sealed class Settings
    {
        public int X=int.MinValue,Y=int.MinValue,Size=84;
        public double Opacity=1;
        public string Theme="dark",Selected="muse";
        public bool Hover=true;
        public Dictionary<string,ChatState> Windows=new Dictionary<string,ChatState>();
        public readonly string Folder;
        public Settings():this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"AIBubbleWin")) {}
        public Settings(string folder) { Folder=folder; }
        public ChatState Window(string id)
        {
            ChatState state;
            if(!Windows.TryGetValue(id,out state)||state==null) Windows[id]=state=new ChatState();
            return state;
        }
        public void Load()
        {
            string file=Path.Combine(Folder,"settings.json");
            if(!File.Exists(file))return;
            try {
                var d=new JavaScriptSerializer().Deserialize<SettingsData>(File.ReadAllText(file));
                if(d==null)return;
                X=d.X;Y=d.Y;Size=Math.Max(56,Math.Min(160,d.Size));
                Opacity=Double.IsNaN(d.Opacity)?1:Math.Max(.5,Math.Min(1,d.Opacity));
                Theme=d.Theme=="light"?"light":"dark";Selected=Provider.Find(d.Selected).Id;
                Hover=d.Hover;Windows=d.Windows??new Dictionary<string,ChatState>();
                foreach(var p in Provider.All) {
                    var s=Window(p.Id);
                    s.Width=Math.Max(360,Math.Min(5000,s.Width));s.Height=Math.Max(460,Math.Min(5000,s.Height));
                }
            } catch(Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        }
        public void Save()
        {
            try {
                Directory.CreateDirectory(Folder);
                string file=Path.Combine(Folder,"settings.json"),tmp=file+".tmp";
                var data=new SettingsData { X=X,Y=Y,Size=Size,Opacity=Opacity,Theme=Theme,
                    Selected=Selected,Hover=Hover,Windows=Windows };
                File.WriteAllText(tmp,new JavaScriptSerializer().Serialize(data));
                if(File.Exists(file))File.Replace(tmp,file,null);else File.Move(tmp,file);
            } catch(Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        }
    }
    public sealed class DragGesture
    {
        Point origin;bool moved;
        public bool Active { get;private set; }
        public void Begin(Point point) { origin=point;moved=false;Active=true; }
        public bool Move(Point point)
        {
            if(!Active)return false;
            Size threshold=SystemInformation.DragSize;
            if(Math.Abs(point.X-origin.X)>threshold.Width/2||Math.Abs(point.Y-origin.Y)>threshold.Height/2)moved=true;
            return moved;
        }
        public bool End() { bool result=moved;Active=false;moved=false;return result; }
    }
    public static class WindowGeometry
    {
        public static Rectangle Clamp(Rectangle b,Rectangle work)
        {
            int width=Math.Min(b.Width,work.Width),height=Math.Min(b.Height,work.Height);
            return new Rectangle(Math.Max(work.Left,Math.Min(b.X,work.Right-width)),
                Math.Max(work.Top,Math.Min(b.Y,work.Bottom-height)),width,height);
        }
    }
    internal static class BubbleArtwork
    {
        internal static Bitmap Render(int size,Provider selected,bool light)
        {
            var b=new Bitmap(size,size,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using(var g=Graphics.FromImage(b)) {
                g.SmoothingMode=System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var r=new RectangleF(size*.08f,size*.08f,size*.84f,size*.84f);
                for(int i=5;i>0;i--)using(var glow=new Pen(Color.FromArgb(12,selected.Accent),i*size*.025f))g.DrawEllipse(glow,r);
                using(var fill=new System.Drawing.Drawing2D.LinearGradientBrush(r,Color.FromArgb(47,56,98),Color.FromArgb(12,17,38),60f))g.FillEllipse(fill,r);
                var arc=new RectangleF(size*.14f,size*.14f,size*.72f,size*.72f);
                Color[] colors={Provider.All[2].Accent,Provider.All[0].Accent,Provider.All[1].Accent};
                for(int i=0;i<3;i++)using(var pen=new Pen(colors[i],size*.075f)) {
                    pen.StartCap=pen.EndCap=System.Drawing.Drawing2D.LineCap.Round;g.DrawArc(pen,arc,-85+i*120,100);
                }
                using(var font=new Font(Ui.FontName,size*.215f,FontStyle.Bold,GraphicsUnit.Pixel))
                using(var ink=new SolidBrush(Color.White))
                using(var format=new StringFormat{Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Center})
                    g.DrawString("AI",font,ink,new RectangleF(0,0,size,size*.97f),format);
                using(var dot=new SolidBrush(selected.Accent))g.FillEllipse(dot,size*.65f,size*.69f,size*.14f,size*.14f);
            }
            return b;
        }
    }
}