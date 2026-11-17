using System;
using System.Collections;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

public class BehaviorChecks
{
    static int count;
    static Assembly app;
    static void Check(bool ok, string message) { if (!ok) throw new Exception("FAIL: " + message); count++; }
    static object Call(object obj, string method, params object[] args)
    {
        foreach (var candidate in obj.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) {
            if (candidate.Name != method || candidate.GetParameters().Length != args.Length) continue;
            bool matches = true; var parameters = candidate.GetParameters();
            for (int i=0; i<args.Length; i++) if (args[i] != null && !parameters[i].ParameterType.IsInstanceOfType(args[i])) matches=false;
            if (matches) return candidate.Invoke(obj,args);
        }
        throw new MissingMethodException(method);
    }
    static object Field(object obj, string name)
    { return obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(obj); }
    // Catches discarded sessions, two visible chats, delayed first click, accidental click after drag,
    // lost preferences, and windows restored outside the usable display.
    [STAThread] public static int Main() { return Run(AppDomain.CurrentDomain.BaseDirectory); }
    public static int Run(string root)
    {
        string result = Path.Combine(root, "test-results.txt");
        Form bubble = null;
        string temp = Path.Combine(Path.GetTempPath(), "aibubble-check-" + Guid.NewGuid().ToString("N"));
        try {
            Application.EnableVisualStyles();
            app = Assembly.LoadFrom(Path.Combine(root, "AIBubbleWin.exe"));
            Type type = app.GetType("MuseBubbleWin.BubbleForm", true);
            Check(type.GetMethod("SelectProvider") != null, "assistant selection must be available");
            bubble = (Form)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { true }, null);
            bubble.Show(); Application.DoEvents();
            Call(bubble, "SelectProvider", "claude"); Application.DoEvents();
            IDictionary chats = (IDictionary)Field(bubble, "chats");
            Form claude = (Form)chats["claude"];
            Check(claude.Visible, "Claude opens on selection");
            Rectangle work=Screen.FromControl(bubble).WorkingArea;
            int expectedLeft=bubble.Left-claude.Width-8;
            if(expectedLeft<work.Left)expectedLeft=bubble.Right+8;
            expectedLeft=Math.Max(work.Left,Math.Min(expectedLeft,work.Right-claude.Width));
            Check(claude.Left==expectedLeft,"chat opens eight pixels beside bubble");
            claude.Location=new Point(work.Left+20,work.Top+20);
            Call(bubble,"HideChat");Call(bubble,"ShowChat");
            Check(claude.Left==expectedLeft,"reopened chat returns beside bubble rather than an old distant position");
            Call(bubble, "SelectProvider", "chatgpt"); Application.DoEvents();
            Form gpt = (Form)chats["chatgpt"];
            Check(gpt.Visible && !claude.Visible, "switch shows only selected assistant");
            Call(bubble, "SelectProvider", "claude"); Application.DoEvents();
            Check(Object.ReferenceEquals(chats["claude"], claude), "switch retains the same chat instance");
            Check(!gpt.Visible && claude.Visible, "switching back hides ChatGPT");
            Call(bubble, "HideChat");
            Call(bubble, "OnMouseDown", bubble, new MouseEventArgs(MouseButtons.Left,1,20,20,0));
            Call(bubble, "OnMouseUp", bubble, new MouseEventArgs(MouseButtons.Left,1,20,20,0));
            Check(claude.Visible, "single click opens synchronously without timer");
            Call(bubble, "OnMouseDown", bubble, new MouseEventArgs(MouseButtons.Left,1,20,20,0));
            Call(bubble, "OnMouseUp", bubble, new MouseEventArgs(MouseButtons.Left,1,20,20,0));
            Check(!claude.Visible, "clicking the bubble again hides the open chat");
            Call(bubble, "OnMouseDown", bubble, new MouseEventArgs(MouseButtons.Left,1,20,20,0));
            Call(bubble, "OnMouseUp", bubble, new MouseEventArgs(MouseButtons.Left,1,20,20,0));
            Check(claude.Visible, "clicking the bubble a third time reopens the chat");
            Call(bubble, "HideChat");
            object bubbleGesture=Field(bubble,"gesture");
            Call(bubble,"OnMouseDown",bubble,new MouseEventArgs(MouseButtons.Left,1,20,20,0));
            Call(bubbleGesture,"Move",new Point(Cursor.Position.X+100,Cursor.Position.Y+100));
            Call(bubble,"OnMouseUp",bubble,new MouseEventArgs(MouseButtons.Left,1,20,20,0));
            Check(!claude.Visible,"releasing a dragged bubble does not open chat");
            Call(bubble, "ShowChat"); Application.DoEvents();
            Point dragBubbleStart = bubble.Location; Point dragChatStart = claude.Location;
            Cursor.Position = new Point(dragBubbleStart.X + 20, dragBubbleStart.Y + 20);
            Call(bubble, "OnMouseDown", bubble, new MouseEventArgs(MouseButtons.Left,1,20,20,0));
            Cursor.Position = new Point(Cursor.Position.X - 60, Cursor.Position.Y - 40);
            Call(bubble, "OnMouseMove", bubble, new MouseEventArgs(MouseButtons.Left,1,20,20,0));
            Call(bubble, "OnMouseUp", bubble, new MouseEventArgs(MouseButtons.Left,1,20,20,0));
            Check(bubble.Left - dragBubbleStart.X == -60 && bubble.Top - dragBubbleStart.Y == -40, "bubble follows the drag");
            Type geomCalc = app.GetType("MuseBubbleWin.WindowGeometry", true);
            Rectangle dragTarget = new Rectangle(dragChatStart.X - 60, dragChatStart.Y - 40, claude.Width, claude.Height);
            Rectangle dragExpected = (Rectangle)geomCalc.GetMethod("Clamp").Invoke(null, new object[]{ dragTarget, Screen.FromRectangle(dragTarget).WorkingArea });
            Check(claude.Location == dragExpected.Location, "open chat moves together with the bubble");
            Call(bubble, "HideChat");
            Call(bubble, "SetChatSize", 600, 720);
            Check(claude.Width == Math.Max(Math.Min(600,work.Width), claude.MinimumSize.Width) && claude.Height == Math.Min(720,work.Height), "size preset resizes selected window");
            Form same = (Form)chats["claude"];
            Check(!same.Visible, "resizing a hidden chat does not open it");
            Call(bubble, "ShowChat");
            Check(same.Visible && same.Size == new Size(Math.Max(Math.Min(600,work.Width), same.MinimumSize.Width),Math.Min(720,work.Height)), "reopen retains resized window");
            Call(bubble, "HideAll");
            Check(!gpt.Visible && !claude.Visible && !bubble.Visible, "hide all hides each assistant and bubble");

            Type gestureType = app.GetType("MuseBubbleWin.DragGesture", true);
            object gesture = Activator.CreateInstance(gestureType);
            Call(gesture, "Begin", new Point(100,100));
            Check(!(bool)Call(gesture, "Move", new Point(101,101)), "small pointer jitter does not drag");
            Check(!(bool)Call(gesture, "End"), "jitter release counts as click");
            Call(gesture, "Begin", new Point(100,100));
            Check((bool)Call(gesture, "Move", new Point(140,150)), "movement beyond threshold drags");
            Check((bool)Call(gesture, "End"), "drag release suppresses click");

            Type settingsType = app.GetType("MuseBubbleWin.Settings", true);
            object settings = Activator.CreateInstance(settingsType, new object[] {temp});
            settingsType.GetField("Selected").SetValue(settings, "chatgpt");
            settingsType.GetField("Hover").SetValue(settings, false);
            settingsType.GetField("X").SetValue(settings, -850);
            Call(settings, "Save");
            object restored = Activator.CreateInstance(settingsType, new object[] {temp});
            Call(restored, "Load");
            Check((string)Field(restored, "Selected") == "chatgpt", "last assistant persists across restart");
            Check(!(bool)Field(restored, "Hover"), "hover preference persists");
            Check((int)Field(restored, "X") == -850, "negative monitor coordinates persist");
            Type geometry = app.GetType("MuseBubbleWin.WindowGeometry", true);
            Rectangle bounded = (Rectangle)geometry.GetMethod("Clamp").Invoke(null,new object[]{
                new Rectangle(2000,2000,900,800), new Rectangle(-1000,0,800,600)});
            Check(bounded == new Rectangle(-1000,0,800,600), "oversized offscreen windows fit negative-coordinate monitor");
            Rectangle ordinary = (Rectangle)geometry.GetMethod("Clamp").Invoke(null,new object[]{
                new Rectangle(20,30,400,500), new Rectangle(0,0,1920,1080)});
            Check(ordinary == new Rectangle(20,30,400,500), "valid saved geometry is preserved");
            
            Call(bubble,"ShowBubble");
            Call(bubble,"SelectProvider","muse");
            Form muse=(Form)chats["muse"];
            Control bar=(Control)Field(muse,"themeBar");
            Control browser=(Control)Field(muse,"web");
            ((Control)Field(muse,"loading")).Visible=false;
            browser.Visible=true;muse.PerformLayout();
            string captures=Path.Combine(root,"verification");
            Directory.CreateDirectory(captures);
            foreach(string theme in new[]{"dark","light"}) {
                Call(bubble,"SetTheme",theme);
                foreach(float scale in new[]{1f,1.25f,1.5f}) {
                    Size original=muse.Size;
                    muse.Scale(new SizeF(scale,scale));muse.PerformLayout();Application.DoEvents();
                    Control title=(Control)Field(bar,"Title");
                    Check(title.Width>40,"title remains readable "+theme+" "+scale);
                    Rectangle webBounds=muse.RectangleToClient(browser.Parent.RectangleToScreen(browser.Bounds));
                    Check(webBounds.Width>0 && webBounds.Top>=bar.Bottom && webBounds.Right<=muse.ClientSize.Width &&
                        webBounds.Bottom<=muse.ClientSize.Height,"browser remains inside resized frame "+theme+" "+scale);
                    using(var bitmap=new Bitmap(muse.Width,muse.Height)) {
                        muse.DrawToBitmap(bitmap,muse.ClientRectangle);
                        bitmap.Save(Path.Combine(captures,"frame-"+theme+"-"+scale.ToString(System.Globalization.CultureInfo.InvariantCulture)+".png"));
                    }
                    muse.Scale(new SizeF(1/scale,1/scale));muse.Size=original;
                }
            }
            Call(bubble,"SetTheme","dark");
            Call(bubble,"ShowPanel");Application.DoEvents();
            Form panel=(Form)Field(bubble,"panel");
            using(var bitmap=new Bitmap(panel.Width,panel.Height)) {
                panel.DrawToBitmap(bitmap,panel.ClientRectangle);bitmap.Save(Path.Combine(captures,"picker.png"));
            }
            bool initialPin=muse.TopMost;
            Call(muse,"SetPinned",!initialPin);
            Check(muse.TopMost!=initialPin,"pin changes actual window state");
            Call(muse,"SetPinned",initialPin);
            Point hitPoint=muse.PointToScreen(new Point(2,muse.Height/2));
            long packed=((long)(ushort)hitPoint.Y<<16)|(ushort)hitPoint.X;
            Message hit=Message.Create(muse.Handle,0x84,IntPtr.Zero,new IntPtr(packed));
            object[] hitArgs={hit};
            muse.GetType().GetMethod("WndProc",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(muse,hitArgs);
            Check(((Message)hitArgs[0]).Result==new IntPtr(10),"left edge exposes native resize hit target");

            File.WriteAllText(result, "PASS " + count + " behavioral assertions\r\n");
            Console.WriteLine(File.ReadAllText(result));
            return 0;
        } catch (Exception ex) {
            File.WriteAllText(result, ex.ToString()); Console.WriteLine(ex); return 1;
        } finally {
            if (bubble != null) { Call(bubble,"ExitApp"); bubble.Dispose(); }
            if (Directory.Exists(temp)) Directory.Delete(temp,true);
        }
    }
}