using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace MuseBubbleWin
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            bool singleInstance;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            System.Threading.Mutex instance;
            try {
                instance = new System.Threading.Mutex(true, "Local\\AIBubbleWin", out singleInstance);
                if (!singleInstance) { MessageBox.Show("AI Bubble is already running. Use its tray icon to show it.", "AI Bubble"); instance.Dispose(); return; }
            } catch { instance = null; }
            var bubble = new BubbleForm();
            if (Array.IndexOf(args, "--show-chat") >= 0)
                bubble.Shown += delegate { bubble.ShowChat(); };
            Application.Run(bubble);
            if (instance != null) { instance.ReleaseMutex(); instance.Dispose(); }
        }
    }

    // ---------- Win32 ----------
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int x; public int y; public POINT(int x, int y) { this.x = x; this.y = y; } }
        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int cx; public int cy; public SIZE(int cx, int cy) { this.cx = cx; this.cy = cy; } }
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp; public byte BlendFlags;
            public byte SourceConstantAlpha; public byte AlphaFormat;
        }
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
            ref POINT pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc,
            int crKey, ref BLENDFUNCTION pblend, int dwFlags);
        [DllImport("user32.dll")]
        public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);
        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateCompatibleDC(IntPtr hDC);
        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hDC);
        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hDC, IntPtr hObject);
        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr hObject);
        [DllImport("gdi32.dll")]
        public static extern IntPtr CreateRoundRectRgn(int l, int t, int r, int b, int w, int h);
        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr handle);
        public const int ULW_ALPHA = 0x02;
        public const byte AC_SRC_OVER = 0x00;
        public const byte AC_SRC_ALPHA = 0x01;
        public const int SW_RESTORE = 9;
    }

    public static class Ui
    {
        public static readonly string FontName = Detect();
        static string Detect()
        {
            try
            {
                using (System.Drawing.Text.InstalledFontCollection ifc =
                    new System.Drawing.Text.InstalledFontCollection())
                {
                    foreach (FontFamily f in ifc.Families)
                        if (f.Name == "Inter") return "Inter";
                }
            }
            catch { }
            return "Segoe UI";
        }
    }

    // ---------- Embedded Muse chat (WebView2) ----------
    public class ChatForm : Form
    {
        bool traceEnabled = false;
        bool initializing;
        readonly Button retry = new Button();
        readonly Button browserFallback = new Button();
        public readonly Provider Provider;
        public string BrowserUrl { get { return web != null && web.Source != null ? web.Source.ToString() : Provider.Url; } }
        readonly List<FilePickerBridge> filePickers = new List<FilePickerBridge>();
        // claude.ai shows a bottom-sheet attach menu when the page is narrow. The native
        // file chooser removes that sheet before the selection returns, so attaching fails.
        // Measured at 100% scaling: a ~500 px window shows the sheet; an 880 px window shows
        // the desktop "Upload from computer" dialog, which attaches. Keep Claude at least this wide.
        const int ClaudeMinWidthAt100 = 880;
        bool IsClaude { get { return Provider != null && Provider.Id == "claude"; } }
        static int ScaledWidth(int width)
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return (int)Math.Ceiling(width * g.DpiX / 96f);
        }
        public override Size MinimumSize
        {
            get { return base.MinimumSize; }
            set
            {
                if (IsClaude && value.Width < ScaledWidth(ClaudeMinWidthAt100))
                    value = new Size(ScaledWidth(ClaudeMinWidthAt100), value.Height);
                base.MinimumSize = value;
            }
        }
        protected override void SetBoundsCore(int x, int y, int width, int height, BoundsSpecified specified)
        {
            if (IsClaude && width < ScaledWidth(ClaudeMinWidthAt100)) width = ScaledWidth(ClaudeMinWidthAt100);
            base.SetBoundsCore(x, y, width, height, specified);
        }
        void ConfigureFilePicker(CoreWebView2 core, Form form)
        {
            EventHandler<CoreWebView2NavigationCompletedEventArgs> ready = null;
            ready = async delegate
            {
                core.NavigationCompleted -= ready;
                try
                {
                    var picker = new FilePickerBridge(core, form, TraceFilePicker, providerName: Provider.Name);
                    await picker.EnableAsync();
                    filePickers.Add(picker);
                }
                catch (Exception ex) { TraceFilePicker("Host file chooser failed during initialization: " + ex.Message); }
            };
            // CDP needs a loaded document/renderer; configuring before first navigation can stall.
            core.NavigationCompleted += ready;
        }
        string TracePath { get { Directory.CreateDirectory(owner2.Preferences.Folder); return Path.Combine(owner2.Preferences.Folder, Provider.Id + "-diagnostics.log"); } }
        void Trace(string message)
        {
            if (!traceEnabled) return;
            WriteTrace(message);
        }
        void TraceFilePicker(string message)
        {
            // Keep Claude's chooser lifecycle visible without enabling page/input tracing.
            // Muse retains its existing opt-in diagnostics behavior.
            if (Provider.Id == "claude") WriteTrace(message); else Trace(message);
        }
        void WriteTrace(string message)
        {
            try { File.AppendAllText(TracePath, DateTime.UtcNow.ToString("o") + " " + message + Environment.NewLine); }
            catch (Exception ex) { Debug.WriteLine(ex); }
        }

        async System.Threading.Tasks.Task OpenPopup(CoreWebView2Environment env,
            CoreWebView2NewWindowRequestedEventArgs e, Form parent)
        {
            Trace("NewWindowRequested; userInitiated=" + e.IsUserInitiated);
            CoreWebView2Deferral deferral = e.GetDeferral();
            Form pop = null;
            try
            {
                pop = new Form();
                pop.Text = Provider.Name;
                pop.Size = new Size(880, 660);
                pop.StartPosition = FormStartPosition.CenterParent;
                WebView2 child = new WebView2();
                child.Dock = DockStyle.Fill;
                pop.Controls.Add(child);
                // Blank and cross-origin windows also need their own browsing context.
                // Do not navigate child: the runtime must perform the requested navigation.
                await child.EnsureCoreWebView2Async(env);
                if (Provider.Id == "muse" || Provider.Id == "claude") ConfigureFilePicker(child.CoreWebView2, pop);
                child.CoreWebView2.NewWindowRequested += async delegate(object sender, CoreWebView2NewWindowRequestedEventArgs next)
                { await OpenPopup(env, next, child.FindForm()); };
                child.CoreWebView2.WindowCloseRequested += delegate { child.FindForm().Close(); };
                e.NewWindow = child.CoreWebView2;
                e.Handled = true;
                pop.Show(parent);
                pop.Activate();
                child.Focus();
            }
            catch (Exception ex)
            {
                if (pop != null) pop.Dispose();
                e.Handled = true;
                Trace("Popup creation failed: " + ex);
                MessageBox.Show(parent, "Could not open the "+ Provider.Name + " window: " + ex.Message,
                    Provider.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally { deferral.Complete(); }
        }

        readonly BubbleForm owner2;
        WebView2 web;
        Label loading;
        bool inited;
        AppleTitleBar themeBar;
        bool styleMusePage = true;
        static readonly Color Bg = Color.FromArgb(42, 39, 36);
        static readonly Color BtnBg = Color.FromArgb(58, 53, 47);
        static readonly Color Ivory = Color.FromArgb(245, 240, 230);
        static readonly Color Gold = Color.FromArgb(212, 175, 105);

        public ChatForm(BubbleForm ownerForm) : this(ownerForm, ownerForm.Selected) { }
        public ChatForm(BubbleForm ownerForm, Provider provider)
        {
            owner2 = ownerForm;
            Provider = provider;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = false;
            BackColor = Bg;
            Size = new Size(392, 560);
            StartPosition = FormStartPosition.Manual;

            AutoScaleMode = AutoScaleMode.Dpi;
            AutoScaleDimensions = new SizeF(96, 96);
            Text = Provider.Name + " — AI Bubble";
            Size = new Size(392, 600);
            MinimumSize = new Size(360, 460);
            Padding = new Padding(1);
            themeBar = new AppleTitleBar();
            themeBar.Dock = DockStyle.Top;
            themeBar.Title.Text = Provider.Name;
            themeBar.SetActiveProvider(Provider.Id);
            themeBar.ProviderSelected += id => owner2.SelectProvider(id);
            themeBar.CloseButton.Click += delegate { Hide(); };
            Controls.Add(themeBar);
            themeBar.FullButton.Click += delegate { owner2.OpenMuse(); };
            themeBar.DragBegan += delegate { owner2.BeginChatDrag(); };
            themeBar.DragMoved += delegate { owner2.ChatDragMove(); };
            themeBar.DragEnded += delegate { owner2.EndChatDrag(); };
            themeBar.ThemeButton.Click += delegate { owner2.SetTheme(owner2.Light ? "dark" : "light"); };
            themeBar.PinButton.Click += delegate { SetPinned(!TopMost); };
            ContextMenuStrip diagnostics = new ContextMenuStrip();
            diagnostics.Items.Add("Reload page", null, delegate { if (web.CoreWebView2 != null) web.Reload(); else InitWeb(); });
            diagnostics.Items.Add("Open in browser", null, delegate { owner2.OpenMuse(); });
            diagnostics.Items.Add("Trace attachment click", null, delegate
            {
                if (web.CoreWebView2 == null) return;
                traceEnabled = true;
                Trace("Trace enabled; runtime=" + web.CoreWebView2.Environment.BrowserVersionString);
                EnableAttachmentTrace();
            });
            diagnostics.Items.Add("Open browser DevTools", null, delegate
            {
                if (web.CoreWebView2 != null) web.CoreWebView2.OpenDevToolsWindow();
            });
            ToolStripMenuItem stylePage = new ToolStripMenuItem("Style Muse page");
            stylePage.Checked = true;
            stylePage.CheckOnClick = true;
            stylePage.CheckedChanged += delegate { styleMusePage = stylePage.Checked; ApplyWebTheme(); };
            if (Provider.Id == "muse") diagnostics.Items.Insert(0, stylePage);
            diagnostics.Renderer = new AppleMenuRenderer(owner2.Light);
            themeBar.ContextMenuStrip = diagnostics;
            themeBar.Title.ContextMenuStrip = diagnostics;
            // Docking derives the browser rectangle from the current client area.
            // Anchored absolute bounds can drift outside it after fractional DPI scaling.
            Panel content = new Panel();
            content.Dock = DockStyle.Fill;
            content.Padding = new Padding(0);
            Controls.Add(content);
            // Dock the title bar first, then fill the remaining space with content.
            themeBar.SendToBack();
            loading = new Label();
            loading.Text = "Loading " + Provider.Name + "...";
            loading.ForeColor = Ivory;
            loading.Font = new Font(Ui.FontName, 10);
            loading.Dock = DockStyle.Fill;
            loading.TextAlign = ContentAlignment.MiddleCenter;
            content.Controls.Add(loading);

            web = new WebView2();
            web.Dock = DockStyle.Fill;
            web.Visible = false;
            content.Controls.Add(web);
            var recovery = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 42, Visible = false };
            retry.Text = "Retry"; retry.AutoSize = true;
            browserFallback.Text = "Open in browser"; browserFallback.AutoSize = true;
            retry.Click += delegate {
                recovery.Visible = false; loading.Visible = true;
                if (web.CoreWebView2 != null && inited) web.Reload(); else InitWeb();
            };
            browserFallback.Click += delegate { owner2.OpenMuse(); };
            recovery.Controls.Add(retry); recovery.Controls.Add(browserFallback);
            content.Controls.Add(recovery); recovery.BringToFront();
            retry.Tag = recovery;
            FormClosing += delegate(object sender, FormClosingEventArgs args) {
                if (args.CloseReason == CloseReason.UserClosing) { args.Cancel = true; Hide(); }
            };
            ResizeEnd += delegate { owner2.RememberChat(this); };

            VisibleChanged += delegate { if (Visible) ApplyRegion(); };
            SizeChanged += delegate { if (Visible) ApplyRegion(); };
            Activated += delegate { if (web.Visible) web.Focus(); };
            ApplyTheme();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (web.CoreWebView2 != null && keyData == Keys.F12)
            {
                web.CoreWebView2.OpenDevToolsWindow();
                return true;
            }
            if (web.CoreWebView2 != null && keyData == Keys.F9)
            {
                traceEnabled = true;
                Trace("Trace enabled; runtime=" + web.CoreWebView2.Environment.BrowserVersionString);
                EnableAttachmentTrace();
                return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        async void EnableAttachmentTrace()
        {
            try
            {
                await web.CoreWebView2.ExecuteScriptAsync(@"
(() => {
 if(window.__museAttachmentTrace) return;
 window.__museAttachmentTrace = true;
 const emit = (kind, data) => chrome.webview.postMessage({museAttachmentTrace:true,kind,...data});
 const state = () => ({focused:document.hasFocus(),active:navigator.userActivation.isActive});
 for(const type of ['pointerdown','click']) document.addEventListener(type,e=>{
   const el=e.target instanceof Element ? e.target.closest('button,input,[role=button]') : null;
   emit(type,{...state(),trusted:e.isTrusted,tag:e.target.tagName,button:!!el,
     disabled:el ? !!el.disabled : false,ariaDisabled:el && el.getAttribute('aria-disabled')});
 },true);
 function wrap(obj,name,label) {
   const fn=obj[name]; if(typeof fn!=='function') return;
   obj[name]=function(...args){
     emit(label,{...state(),type:this.type || null});
     try {
       const result=Reflect.apply(fn,this,args);
       if(result && typeof result.then==='function') return result.then(v=>v,e=>{
         emit(label+' rejected',{name:e.name});throw e;
       });
       return result;
     } catch(e){emit(label+' threw',{name:e.name});throw e;}
   };
 }
 wrap(window,'showOpenFilePicker','showOpenFilePicker');
 wrap(window,'open','window.open');
 wrap(HTMLInputElement.prototype,'showPicker','input.showPicker');
 wrap(HTMLElement.prototype,'click','element.click');
 window.addEventListener('error',e=>emit('error',{name:e.error && e.error.name}));
 window.addEventListener('unhandledrejection',e=>emit('unhandledrejection',{name:e.reason && e.reason.name}));
 emit('ready',state());
})();");
                MessageBox.Show(this, "Attachment trace enabled. Click +, then inspect attachment-trace.log beside the app. F12 opens DevTools. Reload to remove page tracing.", "Muse diagnostics");
            }
            catch (Exception ex) { Trace("Trace setup failed: " + ex); }
        }

        public void ApplyTheme()
        {
            var palette = new ApplePalette(owner2.Light);
            BackColor = palette.Surface;
            themeBar.Apply(owner2.Light, TopMost);
            loading.BackColor = palette.Surface;
            loading.ForeColor = palette.Muted;
            web.DefaultBackgroundColor = palette.Surface;
            themeBar.Title.Text = Provider.Name;
            retry.BackColor = browserFallback.BackColor = palette.Raised;
            retry.ForeColor = browserFallback.ForeColor = palette.Ink;
            if (themeBar.ContextMenuStrip != null)
                themeBar.ContextMenuStrip.Renderer = new AppleMenuRenderer(owner2.Light);
            ApplyRegion();
            ApplyWebTheme();
            Invalidate();
        }

        string Embedded(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (stream == null) throw new InvalidOperationException("Missing theme resource: " + name);
                using (var reader = new StreamReader(stream)) return reader.ReadToEnd();
            }
        }

        string BuildWebThemeScript()
        {
            var json = new System.Web.Script.Serialization.JavaScriptSerializer();
            return Embedded("MuseTheme.js").Replace("__MUSEBUBBLE_CSS__", json.Serialize(Embedded("MuseTheme.css")));
        }

        const string ScrollbarScript = @"
(() => {
    if (!document.head || document.getElementById('__aibubble-scroll')) return;
    const style = document.createElement('style');
    style.id = '__aibubble-scroll';
    style.textContent = `
        *, *::before, *::after { scrollbar-width: auto !important; scrollbar-color: auto !important; }
        ::-webkit-scrollbar { width: 6px; height: 6px; }
        ::-webkit-scrollbar-track, ::-webkit-scrollbar-corner { background: transparent; }
        ::-webkit-scrollbar-thumb { background: rgba(142,142,147,0.35); border-radius: 999px; }
        ::-webkit-scrollbar-thumb:hover { background: rgba(142,142,147,0.55); }
    `;
    document.head.appendChild(style);
})();
";

        async void ApplyWebTheme()
        {
            if (web.CoreWebView2 == null) return;
            try
            {
                web.CoreWebView2.Profile.PreferredColorScheme = owner2.Light
                    ? CoreWebView2PreferredColorScheme.Light : CoreWebView2PreferredColorScheme.Dark;
                if (Provider.Id != "muse")
                {
                    await web.CoreWebView2.ExecuteScriptAsync(ScrollbarScript);
                    return;
                }
                string result = await web.CoreWebView2.ExecuteScriptAsync(ScrollbarScript + BuildWebThemeScript() +
                    "\n(()=>{if(!window.__museBubbleTheme)return 'out-of-scope';window.__museBubbleTheme('" +
                    (owner2.Light ? "light" : "dark") + "'," + (styleMusePage ? "true" : "false") +
                    ");return JSON.stringify({theme:document.documentElement.getAttribute('data-musebubble-theme')," +
                    "composers:document.querySelectorAll('[data-musebubble-composer]').length});})();");
                Trace("Page theme applied: " + result);
            }
            catch (Exception ex) { Trace("Page theme failed: " + ex); }
        }

        void ApplyRegion() { AppleDrawing.RoundWindow(this, owner2.Light); }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (Width < 4 || Height < 4) return;
            var palette = new ApplePalette(owner2.Light);
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var pen = new Pen(palette.Border))
            using (var path = AppleDrawing.Rounded(new RectangleF(.5f, .5f, ClientSize.Width - 1, ClientSize.Height - 1), 14 * this.DeviceScale()))
                e.Graphics.DrawPath(pen, path);
        }
        public async void InitWeb()
        {
            if (inited || initializing || owner2.Preview) return;
            initializing = true;
            loading.Text = "Loading " + Provider.Name + "...";
            ((Control)retry.Tag).Visible = false;
            try
            {
                string udf = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "AIBubbleWin", "Profiles", Provider.Id);
                Directory.CreateDirectory(udf);
                CoreWebView2Environment env =
                    await CoreWebView2Environment.CreateAsync(null, udf, null);
                await web.EnsureCoreWebView2Async(env);
                if (IsDisposed) return;
                if (Provider.Id == "muse" || Provider.Id == "claude")
                    ConfigureFilePicker(web.CoreWebView2, this);
                if (Provider.Id == "muse") {
                    await web.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(BuildWebThemeScript());
                }
                web.CoreWebView2.WebMessageReceived += delegate(object sender, CoreWebView2WebMessageReceivedEventArgs args)
                {
                    if (traceEnabled && args.WebMessageAsJson.Contains("\"museAttachmentTrace\":true"))
                        Trace(args.WebMessageAsJson);
                };
                web.CoreWebView2.NewWindowRequested +=
                    async delegate(object sender, CoreWebView2NewWindowRequestedEventArgs args)
                    { await OpenPopup(env, args, this); };
                web.CoreWebView2.NavigationCompleted += delegate(object sender, CoreWebView2NavigationCompletedEventArgs args)
                {
                    if (IsDisposed) return;
                    if (!args.IsSuccess && args.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled) {
                        web.Visible = false; loading.Visible = true;
                        loading.Text = "Could not load " + Provider.Name + ".\r\nCheck your connection, then retry.";
                        ((Control)retry.Tag).Visible = true;
                        return;
                    }
                    loading.Visible = false;
                    web.Visible = true;
                    ((Control)retry.Tag).Visible = false;
                    ApplyWebTheme();
                };
                web.ZoomFactor = 1.0;
                inited = true;
                web.Source = new Uri(Provider.Url);
            }
            catch (Exception ex)
            {
                inited = false;
                if (IsDisposed) return;
                loading.Text = "Could not start " + Provider.Name + ": " + ex.Message;
                ((Control)retry.Tag).Visible = true;
            }
            finally { initializing = false; }
        }
        public void SetPinned(bool pinned)
        {
            TopMost = pinned;
            if (themeBar != null) themeBar.Apply(owner2.Light, pinned);
            owner2.Preferences.Window(Provider.Id).Pinned = pinned;
            if (!owner2.Preview) owner2.Preferences.Save();
        }

        protected override void WndProc(ref Message message)
        {
            base.WndProc(ref message);
            if (message.Msg != 0x84 || (int)message.Result != 1) return;
            long coordinates = message.LParam.ToInt64();
            Point point = PointToClient(new Point((short)(coordinates & 0xFFFF), (short)((coordinates >> 16) & 0xFFFF)));
            int edge = Math.Max(6, Padding.Left);
            bool left=point.X<edge, right=point.X>=ClientSize.Width-edge;
            bool top=point.Y<edge, bottom=point.Y>=ClientSize.Height-edge;
            if (top && left) message.Result=new IntPtr(13);
            else if (top && right) message.Result=new IntPtr(14);
            else if (bottom && left) message.Result=new IntPtr(16);
            else if (bottom && right) message.Result=new IntPtr(17);
            else if (left) message.Result=new IntPtr(10);
            else if (right) message.Result=new IntPtr(11);
            else if (top) message.Result=new IntPtr(12);
            else if (bottom) message.Result=new IntPtr(15);
        }

    }
}