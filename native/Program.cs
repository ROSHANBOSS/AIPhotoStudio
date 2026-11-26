using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AIPhotoStudio
{
    internal static class Program
    {
        internal const string Version = "0.5.0";
#if STABLE
        internal const string Channel = "stable";
#else
        internal const string Channel = "development";
#endif
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            string dataRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AIPhotoStudio", "Preview", "Data");
#if STABLE
            dataRoot = Path.Combine(dataRoot, "0.5");
#endif
            if (args.Length == 2 && args[0] == "--data-root" && Path.IsPathRooted(args[1])) dataRoot = Path.GetFullPath(args[1]);
            else if (args.Length != 0) { MessageBox.Show("使用 AIPhotoStudio.exe [--data-root 绝对目录] 启动。\nUsage: AIPhotoStudio.exe [--data-root absolute-directory]"); return 2; }
            try
            {
                using (var workspace = new StudioWorkspace(StudioWorkspace.ChannelDirectory(dataRoot, Channel)))
                using (var window = new StudioWindow(workspace, dataRoot))
                {
                    Application.Run(window);
                    return window.StartupFailed ? 1 : 0;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.GetBaseException().Message, "AIPhotoStudio 启动失败 / Startup failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 1;
            }
        }
    }

    internal sealed class StudioWindow : Form
    {
        private const string Home = "https://studio.aiphoto.invalid/index.html";
        private readonly WebView2 view;
        private readonly StudioBridge bridge;
        private readonly string userData;
        private bool closing;
        private bool english;
        internal void ApplyLanguage(bool useEnglish)
        {
            english=useEnglish;
            Text="AIPhotoStudio"+(Program.Channel=="development"?(english?" · Development":" · 开发版"):"");
        }
        internal bool StartupFailed { get; private set; }

        internal StudioWindow(StudioWorkspace workspace, string dataRoot)
        {
            ApplyLanguage(workspace.ReadLanguage().language=="en");
            ClientSize = new Size(1340, 840);
            MinimumSize = new Size(900, 620);
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Color.FromArgb(30, 32, 33);
            Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            userData = Path.Combine(StudioWorkspace.ChannelDirectory(dataRoot, Program.Channel), "Cache", "WebView2");
            view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
            Controls.Add(view);
            bridge = new StudioBridge(this, workspace, dataRoot);
            Shown += async delegate
            {
                try { await InitializeView(); }
                catch (Exception ex)
                {
                    if (closing) return;
                    StartupFailed = true;
                    string hint = ex is FileNotFoundException && !File.Exists(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ui","index.html")) ?
                        "请重新解压完整便携包，或重新运行安装程序。" : "请检查 Microsoft Edge WebView2 Runtime 和本账户的数据目录是否可用。";
                    if(english)hint=ex is FileNotFoundException?"Extract the complete portable package or run Setup again.":"Check the Microsoft Edge WebView2 Runtime and access to your data folder.";
                    string reason=ex.GetBaseException().Message;
                    MessageBox.Show(this, (english?"The editor could not start. ":"桌面界面启动失败。")+hint+"\n\n"+(english?NativeMessages.Translate(reason):reason), Text);
                    Close();
                }
            };
            FormClosing += delegate { closing = true; bridge.Stop(); };
        }

        private async Task InitializeView()
        {
            string folder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ui");
            if (!File.Exists(Path.Combine(folder, "index.html"))) throw new FileNotFoundException("程序目录缺少 ui/index.html，请从完整安装包启动。");
            var environment = await CoreWebView2Environment.CreateAsync(null, userData, null);
            await view.EnsureCoreWebView2Async(environment);
            var core = view.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.SetVirtualHostNameToFolderMapping("studio.aiphoto.invalid", folder, CoreWebView2HostResourceAccessKind.Deny);
            core.NavigationStarting += delegate(object sender, CoreWebView2NavigationStartingEventArgs e) { if (e.Uri != Home) e.Cancel = true; };
            core.NewWindowRequested += delegate(object sender, CoreWebView2NewWindowRequestedEventArgs e) { e.Handled = true; };
            core.PermissionRequested += delegate(object sender, CoreWebView2PermissionRequestedEventArgs e) { e.State = CoreWebView2PermissionState.Deny; };
            core.DownloadStarting += delegate(object sender, CoreWebView2DownloadStartingEventArgs e) { e.Cancel = true; };
            core.WebMessageReceived += async delegate(object sender, CoreWebView2WebMessageReceivedEventArgs e)
            {
                if (closing || e.Source != Home) return;
                string response = await bridge.Receive(e.WebMessageAsJson);
                if (!closing && !IsDisposed && core.Source == Home) core.PostWebMessageAsJson(response);
            };
            core.Navigate(Home);
        }
    }
}
