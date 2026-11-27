using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
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
        public int ChatWidth, ChatHeight;
        public Dictionary<string,ChatState> Windows;
    }
    public sealed class Settings
    {
        public int X=int.MinValue,Y=int.MinValue,Size=84;
        public double Opacity=1;
        public string Theme="dark",Selected="muse";
        public bool Hover=true;
        public Dictionary<string,ChatState> Windows=new Dictionary<string,ChatState>();
        public Size ChatSize = new Size(440, 640);
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
                var legacy = Window(Selected);
                ChatSize = new Size(
                    Math.Max(360, Math.Min(5000, d.ChatWidth > 0 ? d.ChatWidth : legacy.Width)),
                    Math.Max(460, Math.Min(5000, d.ChatHeight > 0 ? d.ChatHeight : legacy.Height)));
            } catch(Exception ex) { System.Diagnostics.Debug.WriteLine(ex.Message); }
        }
        public void Save()
        {
            try {
                Directory.CreateDirectory(Folder);
                string file=Path.Combine(Folder,"settings.json"),tmp=file+".tmp";
                var data=new SettingsData { X=X,Y=Y,Size=Size,Opacity=Opacity,Theme=Theme,
                    Selected=Selected,Hover=Hover,Windows=Windows, ChatWidth=ChatSize.Width, ChatHeight=ChatSize.Height };
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
            var palette = new ApplePalette(light);
            var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var body = new RectangleF(size * .10f, size * .075f, size * .80f, size * .80f);
                float corner = body.Width * .30f;
                for (int pass = 6; pass >= 1; pass--)
                {
                    float spread = pass * size * .013f;
                    var shadow = RectangleF.Inflate(body, spread, spread);
                    shadow.Offset(0, size * .035f);
                    using (var path = AppleDrawing.Rounded(shadow, corner + spread))
                    using (var fill = new SolidBrush(Color.FromArgb(5 + (6 - pass) * 2, Color.Black)))
                        g.FillPath(fill, path);
                }

                using (var path = AppleDrawing.Rounded(body, corner))
                {
                    using (var fill = new LinearGradientBrush(body, palette.Raised, palette.Surface, 90f))
                        g.FillPath(fill, path);

                    var state = g.Save();
                    g.SetClip(path, CombineMode.Intersect);
                    var highlight = new RectangleF(body.X, body.Y, body.Width, body.Height * .48f);
                    using (var shine = new LinearGradientBrush(highlight, Color.FromArgb(light ? 100 : 23, Color.White),
                        Color.FromArgb(0, Color.White), 90f))
                        g.FillRectangle(shine, highlight);
                    g.Restore(state);

                    using (var rim = new Pen(light ? palette.Border : Color.FromArgb(80, 82, 89)))
                        g.DrawPath(rim, path);
                }

                float cx = body.X + body.Width / 2, cy = body.Y + body.Height / 2;
                float radius = size * .228f, waist = radius * .24f;
                using (var sparkle = new GraphicsPath())
                using (var ink = new SolidBrush(palette.Ink))
                {
                    sparkle.AddBezier(cx, cy - radius, cx + waist, cy - waist, cx + waist, cy - waist, cx + radius, cy);
                    sparkle.AddBezier(cx + radius, cy, cx + waist, cy + waist, cx + waist, cy + waist, cx, cy + radius);
                    sparkle.AddBezier(cx, cy + radius, cx - waist, cy + waist, cx - waist, cy + waist, cx - radius, cy);
                    sparkle.AddBezier(cx - radius, cy, cx - waist, cy - waist, cx - waist, cy - waist, cx, cy - radius);
                    sparkle.CloseFigure();
                    g.FillPath(ink, sparkle);
                }
            }
            return bitmap;
        }
    }
}