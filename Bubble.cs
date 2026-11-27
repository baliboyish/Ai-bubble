using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MuseBubbleWin
{
    public class BubbleForm : Form
    {
        readonly Settings settings=new Settings();
        readonly bool previewMode;
        readonly Dictionary<string,ChatForm> chats=new Dictionary<string,ChatForm>();
        readonly DragGesture gesture=new DragGesture();
        readonly Timer hoverOpenTimer=new Timer(), hoverCloseTimer=new Timer(), foregroundTimer=new Timer();
        PanelForm panel; NotifyIcon tray; Icon trayIcon; ContextMenuStrip menu;
        Point dragOffset; IntPtr lastExternal; bool allowExit;
        Point groupPointerStart, groupBubbleStart;
        readonly Dictionary<string,Point> groupStarts=new Dictionary<string,Point>();
        DateTime suppressHoverUntil=DateTime.MinValue;
        public bool Light { get { return settings.Theme=="light"; } }
        internal bool Preview { get { return previewMode; } }
        internal Settings Preferences { get { return settings; } }
        public Provider Selected { get { return Provider.Find(settings.Selected); } }
        public BubbleForm():this(false) {}
        internal BubbleForm(bool previewMode)
        {
            this.previewMode=previewMode;
            if(!previewMode)settings.Load();
            FormBorderStyle=FormBorderStyle.None;ShowInTaskbar=false;TopMost=true;
            StartPosition=FormStartPosition.Manual;Text="AI Bubble";AccessibleName="AI assistant bubble";
            Size=new Size(settings.Size,settings.Size);
            var work=Screen.PrimaryScreen.WorkingArea;
            var position=settings.X==int.MinValue||settings.Y==int.MinValue
                ? new Point(work.Right-Width-24,work.Top+work.Height/3):new Point(settings.X,settings.Y);
            Bounds=WindowGeometry.Clamp(new Rectangle(position,Size),Screen.FromPoint(position).WorkingArea);
            panel=new PanelForm(this);BuildMenu();
            if(!previewMode) {
                tray=new NotifyIcon{Text="AI Bubble",Visible=true,ContextMenuStrip=menu};
                RefreshTrayIcon();tray.DoubleClick+=delegate {ShowBubble();};
            }
            hoverOpenTimer.Interval=350;
            hoverOpenTimer.Tick+=delegate {
                hoverOpenTimer.Stop();
                if(settings.Hover&&!gesture.Active&&Bounds.Contains(Cursor.Position)&&
                    DateTime.UtcNow>suppressHoverUntil&&!menu.Visible)ShowPanel();
            };
            hoverCloseTimer.Interval=300;
            hoverCloseTimer.Tick+=delegate {
                if(panel.Visible&&!Bounds.Contains(Cursor.Position)&&!panel.Bounds.Contains(Cursor.Position))HidePanel();
            };
            hoverCloseTimer.Start();
            foregroundTimer.Interval=400;
            foregroundTimer.Tick+=delegate {
                IntPtr h=Native.GetForegroundWindow();uint pid;
                if(h!=IntPtr.Zero){Native.GetWindowThreadProcessId(h,out pid);
                    if(pid!=(uint)Process.GetCurrentProcess().Id)lastExternal=h;}
            };
            if(!previewMode)foregroundTimer.Start();
            MouseDown+=OnMouseDown;MouseMove+=OnMouseMove;MouseUp+=OnMouseUp;
            MouseEnter+=delegate {if(settings.Hover)hoverOpenTimer.Start();};
            MouseLeave+=delegate {hoverOpenTimer.Stop();};
            MouseWheel+=delegate(object sender,MouseEventArgs e) {
                if((ModifierKeys&Keys.Control)!=0)settings.Opacity=Math.Max(.5,Math.Min(1,settings.Opacity+(e.Delta>0?.05:-.05)));
                else settings.Size=Math.Max(56,Math.Min(160,settings.Size+(e.Delta>0?8:-8)));
                Render();ClampBubble();Persist();
            };
            FormClosing+=delegate(object sender,FormClosingEventArgs e) {
                if(!allowExit){e.Cancel=true;HideAll();}else Persist();
            };
        }
        protected override CreateParams CreateParams {
            get {var cp=base.CreateParams;cp.ExStyle|=0x80000;return cp;}
        }
        protected override void OnShown(EventArgs e){base.OnShown(e);Render();}
        void Render()
        {
            if(!IsHandleCreated)return;
            Size=new Size(settings.Size,settings.Size);
            using(var bitmap=BubbleArtwork.Render(settings.Size,Selected,Light)) {
                IntPtr screen=Native.GetDC(IntPtr.Zero),memory=Native.CreateCompatibleDC(screen);
                IntPtr hb=bitmap.GetHbitmap(Color.FromArgb(0)),old=Native.SelectObject(memory,hb);
                try {
                    var size=new Native.SIZE(bitmap.Width,bitmap.Height);
                    var src=new Native.POINT(0,0);var dst=new Native.POINT(Left,Top);
                    var blend=new Native.BLENDFUNCTION{BlendOp=Native.AC_SRC_OVER,
                        SourceConstantAlpha=(byte)(255*settings.Opacity),AlphaFormat=Native.AC_SRC_ALPHA};
                    Native.UpdateLayeredWindow(Handle,screen,ref dst,ref size,memory,ref src,0,ref blend,Native.ULW_ALPHA);
                } finally {Native.SelectObject(memory,old);Native.DeleteObject(hb);Native.DeleteDC(memory);Native.ReleaseDC(IntPtr.Zero,screen);}
            }
        }
        void RefreshTrayIcon()
        {
            if(tray==null)return;
            using(var bitmap=BubbleArtwork.Render(32,Selected,Light)) {
                IntPtr h=bitmap.GetHicon();Icon icon;
                try {using(var borrowed=Icon.FromHandle(h))icon=(Icon)borrowed.Clone();}
                finally {Native.DestroyIcon(h);}
                tray.Icon=icon;if(trayIcon!=null)trayIcon.Dispose();trayIcon=icon;
            }
        }
        void Add(ToolStripItemCollection items,string text,Action action)
        {var item=new ToolStripMenuItem(text);item.Click+=delegate {action();};items.Add(item);}
        void BuildMenu()
        {
            menu=new ContextMenuStrip{Font=new Font(Ui.FontName,10),Renderer=new AppleMenuRenderer(Light)};
            Add(menu.Items,"Open last assistant",ShowChat);
            var providers=new ToolStripMenuItem("Choose assistant");
            foreach(var p in Provider.All){var captured=p;Add(providers.DropDownItems,p.Name,delegate {SelectProvider(captured.Id);});}
            menu.Items.Add(providers);Add(menu.Items,"Open in browser",OpenMuse);
            var chatSize=new ToolStripMenuItem("Chat window size");
            Add(chatSize.DropDownItems,"Small",delegate {SetChatSize(400,600);});
            Add(chatSize.DropDownItems,"Medium",delegate {SetChatSize(600,720);});
            Add(chatSize.DropDownItems,"Large",delegate {SetChatSize(900,800);});
            menu.Items.Add(chatSize);
            var pin=new ToolStripMenuItem("Keep selected chat on top");
            pin.Click+=delegate {var chat=GetChat(Selected);chat.SetPinned(!chat.TopMost);};
            menu.Items.Add(pin);
            var bubbleSize=new ToolStripMenuItem("Bubble size");
            foreach(int s in new[]{64,84,112}){int captured=s;Add(bubbleSize.DropDownItems,s+" px",
                delegate {settings.Size=captured;Render();ClampBubble();Persist();});}
            menu.Items.Add(bubbleSize);
            var opacity=new ToolStripMenuItem("Bubble opacity");
            foreach(double o in new[]{1.0,.85,.7}){double captured=o;Add(opacity.DropDownItems,(o*100).ToString("0")+"%",
                delegate {settings.Opacity=captured;Render();Persist();});}
            menu.Items.Add(opacity);
            var hover=new ToolStripMenuItem("Show assistant picker on hover");
            hover.Click+=delegate {settings.Hover=!settings.Hover;HidePanel();Persist();};menu.Items.Add(hover);
            var theme=new ToolStripMenuItem("Theme");
            Add(theme.DropDownItems,"Dark",delegate {SetTheme("dark");});
            Add(theme.DropDownItems,"Light",delegate {SetTheme("light");});menu.Items.Add(theme);
            var startup=new ToolStripMenuItem("Launch at Windows sign-in");
            startup.Click+=delegate {
                try {using(var key=Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) {
                    if(IsAutoStart())key.DeleteValue("AIBubbleWin",false);
                    else key.SetValue("AIBubbleWin","\""+Application.ExecutablePath+"\"");
                }}catch(Exception ex){MessageBox.Show("Could not change startup: "+ex.Message,"AI Bubble");}
            };
            menu.Items.Add(startup);Add(menu.Items,"Reset window positions",ResetPositions);
            menu.Items.Add(new ToolStripSeparator());Add(menu.Items,"Show bubble",ShowBubble);
            Add(menu.Items,"Hide to tray",HideAll);Add(menu.Items,"Exit",ExitApp);
            menu.Opening+=delegate {
                HidePanel();hoverOpenTimer.Stop();pin.Checked=settings.Window(Selected.Id).Pinned;
                hover.Checked=settings.Hover;startup.Checked=IsAutoStart();
                foreach(ToolStripMenuItem item in providers.DropDownItems)item.Checked=item.Text==Selected.Name;
            };
            ContextMenuStrip=menu;
        }
        bool IsAutoStart()
        {
            using(var key=Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                return key!=null&&key.GetValue("AIBubbleWin")!=null;
        }
        ChatForm GetChat(Provider provider)
        {
            ChatForm chat;
            if(!chats.TryGetValue(provider.Id,out chat)||chat.IsDisposed) {
                chat=new ChatForm(this,provider);chats[provider.Id]=chat;
                var state=settings.Window(provider.Id);var work=Screen.FromControl(this).WorkingArea;
                Size size = settings.ChatSize;
                var bounds = state.Positioned ? new Rectangle(new Point(state.X, state.Y), size)
                    : new Rectangle(new Point(Left - size.Width - 12, Top), size);
                if(state.Positioned)work=Screen.FromRectangle(bounds).WorkingArea;
                else if(bounds.Left<work.Left)bounds.X=Right+12;
                chat.MinimumSize=new Size(Math.Min(360,work.Width),Math.Min(460,work.Height));
                chat.Bounds=WindowGeometry.Clamp(bounds,work);chat.SetPinned(state.Pinned);

            }
            return chat;
        }
        internal void RememberChat(ChatForm chat)
        {
            if(chat.IsDisposed||chat.WindowState!=FormWindowState.Normal)return;
            var state=settings.Window(chat.Provider.Id);
            state.X=chat.Left;state.Y=chat.Top;state.Width=chat.Width;state.Height=chat.Height;
            state.Pinned=chat.TopMost;state.Positioned=true;Persist();
        }
        internal void RememberChatSize(ChatForm chat)
        {
            if (chat.IsDisposed || chat.WindowState != FormWindowState.Normal) return;
            settings.ChatSize = chat.Size;
            RememberChat(chat);
        }
        public void SelectProvider(string id)
        {settings.Selected=Provider.Find(id).Id;Render();RefreshTrayIcon();Persist();ShowChat();}
        public void ShowChat()
        {
            HidePanel();suppressHoverUntil=DateTime.UtcNow.AddMilliseconds(650);
            var chat=GetChat(Selected);
            foreach(var item in chats.Values)if(item!=chat)item.Hide();
            if(!Visible)ShowBubble();
            PositionChat(chat);
            chat.Show();
            // Initial DPI scaling may change form bounds; anchor again using its final layout.
            PositionChat(chat);
            chat.Activate();if(!previewMode)chat.InitWeb();RememberChat(chat);
        }
        void PositionChat(ChatForm chat)
        {
            var work=Screen.FromControl(this).WorkingArea;
            chat.MinimumSize = new Size(Math.Min(360, work.Width), Math.Min(460, work.Height));
            Size size = settings.ChatSize;
            size.Width = Math.Max(chat.MinimumSize.Width, Math.Min(size.Width, work.Width));
            size.Height = Math.Max(chat.MinimumSize.Height, Math.Min(size.Height, work.Height));
            int x = Left - size.Width - 8;
            if (x < work.Left) x = Right + 8;
            int y = Top + (Height - size.Height) / 2;
            chat.Bounds = WindowGeometry.Clamp(new Rectangle(new Point(x, y), size), work);
        }
        public void HideChat(){foreach(var chat in chats.Values)chat.Hide();}
        bool AnyChatVisible(){foreach(var chat in chats.Values)if(chat.Visible&&!chat.IsDisposed)return true;return false;}
        public void ToggleChat()
        {
            HidePanel();suppressHoverUntil=DateTime.UtcNow.AddMilliseconds(650);
            if(AnyChatVisible())HideChat();else ShowChat();
        }
        void BeginGroupDrag()
        {
            groupPointerStart=Cursor.Position;groupBubbleStart=Location;groupStarts.Clear();
            foreach(var chat in chats.Values)if(chat.Visible&&!chat.IsDisposed)groupStarts[chat.Provider.Id]=chat.Location;
        }
        void GroupDragMove()
        {
            int dx=Cursor.Position.X-groupPointerStart.X,dy=Cursor.Position.Y-groupPointerStart.Y;
            Location=new Point(groupBubbleStart.X+dx,groupBubbleStart.Y+dy);Render();
            foreach(var pair in groupStarts){
                ChatForm chat;if(!chats.TryGetValue(pair.Key,out chat)||!chat.Visible||chat.IsDisposed)continue;
                var target=new Rectangle(pair.Value.X+dx,pair.Value.Y+dy,chat.Width,chat.Height);
                chat.Bounds=WindowGeometry.Clamp(target,Screen.FromRectangle(target).WorkingArea);
            }
        }
        void EndGroupDrag()
        {
            Point before=Location;ClampBubble();
            int cx=Location.X-before.X,cy=Location.Y-before.Y;
            if(cx!=0||cy!=0)foreach(var pair in groupStarts){
                ChatForm chat;if(!chats.TryGetValue(pair.Key,out chat)||!chat.Visible||chat.IsDisposed)continue;
                var target=new Rectangle(chat.Left+cx,chat.Top+cy,chat.Width,chat.Height);
                chat.Bounds=WindowGeometry.Clamp(target,Screen.FromRectangle(target).WorkingArea);
            }
            Render();Persist();
            foreach(var pair in groupStarts){ChatForm chat;if(chats.TryGetValue(pair.Key,out chat)&&!chat.IsDisposed)RememberChat(chat);}
            groupStarts.Clear();
        }
        internal void BeginChatDrag(){HidePanel();hoverOpenTimer.Stop();BeginGroupDrag();}
        internal void ChatDragMove(){GroupDragMove();}
        internal void EndChatDrag(){EndGroupDrag();}
        public void SetChatSize(int width,int height)
        {
            var chat=GetChat(Selected);var work=Screen.FromRectangle(chat.Bounds).WorkingArea;
            chat.Bounds=WindowGeometry.Clamp(new Rectangle(chat.Location,new Size(width,height)),work);RememberChatSize(chat);
        }
        public void ShowPanel()
        {
            if(gesture.Active||menu.Visible)return;panel.ApplyTheme();
            var work=Screen.FromControl(this).WorkingArea;
            var bounds=new Rectangle(Left+(Width-panel.Width)/2,Top-panel.Height-8,panel.Width,panel.Height);
            if(bounds.Top<work.Top)bounds.Y=Bottom+8;
            panel.Bounds=WindowGeometry.Clamp(bounds,work);panel.Show();
        }
        public void HidePanel(){panel.Hide();}
        public void ShowBubble(){Show();ClampBubble();Render();}
        public void HideAll()
        {
            HidePanel();HideChat();hoverOpenTimer.Stop();Hide();
            if(tray!=null)tray.ShowBalloonTip(1500,"AI Bubble","Double-click the tray icon to restore the bubble.",ToolTipIcon.Info);
        }
        public void OpenMuse()
        {
            try {string url=Selected.Url;ChatForm current;
                if(chats.TryGetValue(Selected.Id,out current))url=current.BrowserUrl;
                Process.Start(new ProcessStartInfo(url){UseShellExecute=true});
            }catch(Exception ex){MessageBox.Show("Could not open browser: "+ex.Message,"AI Bubble");}
        }
        public void SetTheme(string theme)
        {
            settings.Theme=theme=="light"?"light":"dark";menu.Renderer=new AppleMenuRenderer(Light);
            panel.ApplyTheme();foreach(var chat in chats.Values)chat.ApplyTheme();Render();RefreshTrayIcon();Persist();
        }
        public void GoBack()
        {
            HidePanel();HideChat();
            if(lastExternal!=IntPtr.Zero){if(Native.IsIconic(lastExternal))Native.ShowWindow(lastExternal,Native.SW_RESTORE);
                Native.SetForegroundWindow(lastExternal);}
        }
        void ResetPositions()
        {
            var work=Screen.PrimaryScreen.WorkingArea;Location=new Point(work.Right-Width-24,work.Top+work.Height/3);
            foreach(var provider in Provider.All){var state=settings.Window(provider.Id);state.Positioned=false;ChatForm chat;
                if(chats.TryGetValue(provider.Id,out chat)){
                    chat.Bounds=WindowGeometry.Clamp(new Rectangle(Left-chat.Width-12,Top,chat.Width,chat.Height),work);RememberChat(chat);
                }}
            Render();Persist();
        }
        void ClampBubble(){Bounds=WindowGeometry.Clamp(Bounds,Screen.FromRectangle(Bounds).WorkingArea);}
        void Persist(){if(previewMode)return;settings.X=Left;settings.Y=Top;settings.Save();}
        void OnMouseDown(object sender,MouseEventArgs e)
        {
            if(e.Button!=MouseButtons.Left)return;
            HidePanel();hoverOpenTimer.Stop();dragOffset=e.Location;gesture.Begin(Cursor.Position);BeginGroupDrag();Capture=true;
        }
        void OnMouseMove(object sender,MouseEventArgs e)
        {
            if(!gesture.Active||e.Button!=MouseButtons.Left)return;
            if(gesture.Move(Cursor.Position))GroupDragMove();
        }
        void OnMouseUp(object sender,MouseEventArgs e)
        {
            if(e.Button!=MouseButtons.Left||!gesture.Active)return;
            bool dragged=gesture.End();Capture=false;suppressHoverUntil=DateTime.UtcNow.AddMilliseconds(650);
            if(dragged)EndGroupDrag();else ToggleChat();
        }
        public void ExitApp(){allowExit=true;Persist();Close();}
        protected override void Dispose(bool disposing)
        {
            if(disposing){
                hoverOpenTimer.Dispose();hoverCloseTimer.Dispose();foregroundTimer.Dispose();
                foreach(var chat in chats.Values)chat.Dispose();
                if(panel!=null)panel.Dispose();if(tray!=null){tray.Visible=false;tray.Dispose();}
                if(trayIcon!=null)trayIcon.Dispose();if(menu!=null)menu.Dispose();
            }base.Dispose(disposing);
        }
    }
    public class PanelForm : Form
    {
        readonly BubbleForm owner2;
        readonly List<ProviderTile> buttons = new List<ProviderTile>();
        ApplePalette palette;
        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams
        {
            get
            {
                var parameters = base.CreateParams;
                parameters.ExStyle |= 0x08000000;
                return parameters;
            }
        }

        public PanelForm(BubbleForm owner)
        {
            owner2 = owner;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            ClientSize = new Size(192, 134);
            Padding = new Padding(8);
            Text = "Choose assistant";
            DoubleBuffered = true;
            int y = Padding.Top;
            foreach (var provider in Provider.All)
            {
                var button = new ProviderTile(provider);
                button.SetBounds(Padding.Left, y, 176, 36);
                button.Click += delegate { owner2.SelectProvider(button.Provider.Id); };
                Controls.Add(button);
                buttons.Add(button);
                y += 41;
            }
            ApplyTheme();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            AppleDrawing.RoundWindow(this, owner2.Light);
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            if (IsHandleCreated) AppleDrawing.RoundWindow(this, owner2.Light);
        }

        public void ApplyTheme()
        {
            palette = new ApplePalette(owner2.Light);
            BackColor = palette.Surface;
            foreach (var button in buttons)
            {
                button.Palette = palette;
                button.Selected = button.Provider.Id == owner2.Selected.Id;
                button.Invalidate();
            }
            AppleDrawing.RoundWindow(this, owner2.Light);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width < 4 || Height < 4) return;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(palette.Border))
            using (var path = AppleDrawing.Rounded(new RectangleF(.5f, .5f, ClientSize.Width - 1, ClientSize.Height - 1),
                14 * this.DeviceScale()))
                e.Graphics.DrawPath(pen, path);
        }
    }

    internal sealed class ProviderTile : Button
    {
        internal readonly Provider Provider;
        internal ApplePalette Palette;
        internal bool Selected;
        bool hovering;

        internal ProviderTile(Provider provider)
        {
            Provider = provider;
            Text = provider.Name;
            AccessibleName = "Open " + provider.Name;
            Cursor = Cursors.Hand;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            Font = new Font(Ui.FontName, 9f);
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque, true);
        }

        protected override void OnMouseEnter(EventArgs e) { hovering = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hovering = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Palette.Surface);
            if (Width < 3 || Height < 3) return;
            float scale = Height / 36f;
            using (var path = AppleDrawing.Rounded(new RectangleF(.5f, .5f, Width - 1, Height - 1), 8 * scale))
            {
                using (var fill = new SolidBrush(Selected ? Palette.PillActive :
                    hovering ? Palette.PillHover : Color.Transparent)) g.FillPath(fill, path);
                using (var pen = new Pen(Focused ? Palette.Accent : Palette.Border)) g.DrawPath(pen, path);
            }

            var state = g.Save();
            g.TranslateTransform(20 * scale, Height / 2f);
            g.ScaleTransform(scale, scale);
            using (var pen = new Pen(Palette.Ink, 1.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                switch (Provider.Id)
                {
                    case "chatgpt":
                        for (int i = 0; i < 6; i++)
                        {
                            g.DrawArc(pen, -3.5f, -8f, 7f, 9f, 177f, 236f);
                            g.RotateTransform(60);
                        }
                        break;
                    case "claude":
                        for (int i = 0; i < 8; i++)
                        {
                            g.DrawLine(pen, 0, -2, 0, i % 2 == 0 ? -8 : -6);
                            g.RotateTransform(45);
                        }
                        break;
                    case "muse":
                        g.DrawLines(pen, new[] { new Point(-7, 6), new Point(-7, -6), new Point(0, 1),
                            new Point(7, -6), new Point(7, 6) });
                        break;
                }
            }
            g.Restore(state);
            TextRenderer.DrawText(g, Text, Font, new Rectangle((int)(36 * scale), 0,
                Math.Max(0, Width - (int)(44 * scale)), Height), Palette.Ink,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }
}