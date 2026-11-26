using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace AIPhotoStudio
{
    public static partial class Planner
    {
        internal sealed class Settings
        {
            public string Kind { get; set; }
            public string Endpoint { get; set; }
            public string Model { get; set; }
            public string Key { get; set; }
        }
        private static readonly string[] Kinds = { "rules", "openai", "gemini", "deepseek", "compatible", "local" };
        private static readonly string[] Names = { "本地规则", "OpenAI", "Gemini", "DeepSeek", "兼容服务", "本地模型" };
        private static readonly string[] Defaults = { "", "https://api.openai.com/v1", "https://generativelanguage.googleapis.com/v1beta", "https://api.deepseek.com", "", "http://127.0.0.1:11434/v1" };
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AIPhotoStudio/provider/settings/v1");
        private static readonly object SettingsGate = new object();
        private static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = 512 * 1024, RecursionLimit = 24 }; }

        private static string SettingsPath(string channelDir)
        {
            if (String.IsNullOrWhiteSpace(channelDir)) throw new ArgumentException("请指定通道的数据目录。");
            return Path.Combine(WorkspacePaths.Full(channelDir), "Settings", "provider.json");
        }

        internal static Settings ReadSettings(string channelDir)
        {
            lock (SettingsGate)
            {
                string path = WorkspacePaths.ForIO(SettingsPath(channelDir));
                if (!File.Exists(path)) return new Settings { Kind = "rules" };
                try
                {
                    if (new FileInfo(path).Length > 32768) throw new InvalidDataException();
                    var envelope = Json().Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
                    if (envelope==null) throw new InvalidDataException();
                    if (Convert.ToInt32(envelope["version"]) != 1) throw new InvalidDataException();
                    byte[] bytes = ProtectedData.Unprotect(Convert.FromBase64String((string)envelope["protected"]), Entropy, DataProtectionScope.CurrentUser);
                    try
                    {
                        Settings s = Json().Deserialize<Settings>(new UTF8Encoding(false, true).GetString(bytes));
                        VerifySettings(s);
                        return s;
                    }
                    finally { Array.Clear(bytes, 0, bytes.Length); }
                }
                catch (Exception e)
                {
                    if (!(e is ArgumentException || e is InvalidOperationException || e is CryptographicException || e is FormatException ||
                          e is InvalidDataException || e is KeyNotFoundException || e is InvalidCastException || e is OverflowException ||
                          e is IOException || e is UnauthorizedAccessException)) throw;
                    throw new InvalidDataException("当前 Windows 账户无法读取此 AI 配置。请打开配置重新选择本地规则或重新输入服务信息。");
                }
            }
        }

        internal static void SaveSettings(string channelDir, Settings config)
        {
            VerifySettings(config);
            lock (SettingsGate)
            {
                string path = SettingsPath(channelDir);
                string folder=WorkspacePaths.DirectoryOf(path);
                Directory.CreateDirectory(WorkspacePaths.ForIO(folder));
                path=WorkspacePaths.ForIO(path);
                byte[] bytes = Encoding.UTF8.GetBytes(Json().Serialize(config));
                string envelope;
                try { envelope = Json().Serialize(new Dictionary<string, object> { { "version", 1 }, { "protected", Convert.ToBase64String(ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser)) } }); }
                finally { Array.Clear(bytes, 0, bytes.Length); }
                string temp = WorkspacePaths.ForIO(Path.Combine(folder,"p"+Guid.NewGuid().ToString("N").Substring(0,12)+".tmp"));
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] contents = Encoding.UTF8.GetBytes(envelope);
                    file.Write(contents, 0, contents.Length);
                    file.Flush(true);
                }
                try
                {
                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }
            }
        }

        private static void VerifySettings(Settings s)
        {
            if (s == null || Array.IndexOf(Kinds, s.Kind) < 0) throw new ArgumentException("未知的 AI 服务类型。");
            if (s.Kind == "rules") return;
            if (String.IsNullOrWhiteSpace(s.Model) || s.Model.Length > 150 || !Regex.IsMatch(s.Model, @"^[A-Za-z0-9][A-Za-z0-9_.:/-]{0,149}$") || s.Model.Contains(".."))
                throw new ArgumentException("请输入服务提供的模型 ID。");
            if (s.Kind == "gemini" && !Regex.IsMatch(s.Model, @"^[A-Za-z0-9][A-Za-z0-9_.-]{0,149}$"))
                throw new ArgumentException("请输入 Gemini 模型名称，例如控制台 models/ 之后的部分。");
            Uri uri = EndpointUri(s);
            if (String.IsNullOrEmpty(s.Key) && !(s.Kind == "local" || (s.Kind == "compatible" && Loopback(uri))))
                throw new ArgumentException("该服务需要 API Key。");
            if (!String.IsNullOrEmpty(s.Key) && (s.Key.Length > 2048 || Regex.IsMatch(s.Key, @"[^\x21-\x7e]")))
                throw new ArgumentException("API Key 含空白或无效字符。");
        }

        private static bool Loopback(Uri uri)
        {
            string host = uri.Host.Trim('[', ']');
            IPAddress address;
            return String.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) || (IPAddress.TryParse(host, out address) && IPAddress.IsLoopback(address));
        }

        internal static Uri EndpointUri(Settings s)
        {
            Uri baseUri;
            if (String.IsNullOrWhiteSpace(s.Endpoint) || s.Endpoint.Length > 2048 || !Uri.TryCreate(s.Endpoint, UriKind.Absolute, out baseUri) ||
                !String.IsNullOrEmpty(baseUri.UserInfo) || !String.IsNullOrEmpty(baseUri.Query) || !String.IsNullOrEmpty(baseUri.Fragment) ||
                !(baseUri.Scheme == "https" || (baseUri.Scheme == "http" && Loopback(baseUri))))
                throw new ArgumentException("请输入 HTTPS 服务地址，或本机回环 HTTP 地址。地址中不能包含用户名、密码或查询参数。");
            if (s.Kind == "local" && !Loopback(baseUri)) throw new ArgumentException("本地模型地址须指向本机回环接口。");
            UriBuilder b = new UriBuilder(baseUri);
            if (b.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) b.Host = "127.0.0.1";
            string path = b.Path.TrimEnd('/');
            if (s.Kind == "gemini")
            {
                if (path.EndsWith(":generateContent", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Gemini 请填写 API 根地址，模型名称会自动加入请求路径。");
                b.Path = path + "/models/" + Uri.EscapeDataString(s.Model) + ":generateContent";
            }
            else b.Path = path.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ? path : path + "/chat/completions";
            return b.Uri;
        }

        public static string Name(string channelDataDir)
        {
            try
            {
                Settings s = ReadSettings(channelDataDir);
                return Names[Array.IndexOf(Kinds, s.Kind)];
            }
            catch (InvalidDataException) { return "AI 配置待修复"; }
        }

        public static bool Configure(IWin32Window owner, string channelDataDir)
        {
            return Configure(owner,channelDataDir,false);
        }
        internal static bool Configure(IWin32Window owner,string channelDataDir,bool english)
        {
            Func<string,string,string> label=(zh,en)=>english?en:zh;
            Settings original;
            try { original = ReadSettings(channelDataDir); }
            catch (InvalidDataException) { original = new Settings { Kind = "rules" }; }
            using (var dialog = new Form {
                Text = label("AI 服务设置 · AIPhotoStudio","AI provider settings · AIPhotoStudio"), FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                StartPosition = FormStartPosition.CenterParent, ClientSize = new Size(545, 422), AutoScaleMode = AutoScaleMode.Font })
            using (var font = new Font("Microsoft YaHei UI", 9F))
            {
                dialog.Font = font;
                var types = new ComboBox { Left = 110, Top = 18, Width = 408, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = label("AI 服务","AI provider") };
                types.Items.AddRange(english?new[]{"Offline rules","OpenAI","Gemini","DeepSeek","Compatible API","Local model"}:Names);
                var endpoint = new TextBox { Left = 110, Top = 58, Width = 408, AccessibleName = label("API 根地址","API base URL") };
                var model = new TextBox { Left = 110, Top = 98, Width = 408, AccessibleName = label("模型 ID","Model ID") };
                var key = new TextBox { Left = 110, Top = 138, Width = 408, UseSystemPasswordChar = true, AccessibleName = "API Key" };
                var keyHint = new Label { Left = 110, Top = 168, Width = 408, Height = 23 };
                var consent = new CheckBox { Left = 24, Top = 195, Width = 490, Height = 28, Text = label("启用所选服务，允许向上面的地址发送修图请求","Enable this provider and send editing requests to the address above") };
                var explanation = new Label { Left = 24, Top = 235, Width = 495, Height = 62,
                    Text = label("发送内容包括指令、参数、数值历史和亮度统计。原片、缩略图、路径和 EXIF 保留在本机。云服务可能按 API 用量计费。密钥由当前 Windows 账户加密保存。","Requests include instructions, parameters, edit history values and brightness statistics. Images, paths and EXIF data stay on this PC. Your provider may charge for API use. Windows encrypts the key for your account.") };
                var probe = new Button { Left = 24, Top = 301, Width = 104, Height = 30, Text = label("测试连接","Test connection") };
                var status = new Label { Left = 130, Top = 301, Width = 388, Height = 61, AccessibleName = label("连接测试结果","Connection test result") };
                var save = new Button { Left = 350, Top = 374, Width = 80, Height = 30, Text = label("保存","Save") };
                var cancel = new Button { Left = 438, Top = 374, Width = 80, Height = 30, Text = label("取消","Cancel"), DialogResult = DialogResult.Cancel };
                string[] labels = english?new[]{"Provider","API base URL","Model ID","API Key"}:new[]{"服务","API 根地址","模型 ID","API Key"};
                foreach (int i in new[] { 0,1,2,3 }) dialog.Controls.Add(new Label { Left = 24, Top = 22 + 40*i, Width = 84, Text = labels[i] });
                dialog.Controls.AddRange(new Control[] { types, endpoint, model, key, keyHint, consent, explanation, probe, status, save, cancel });
                dialog.AcceptButton = save; dialog.CancelButton = cancel;
                CancellationTokenSource checking = null;
                Action reset = delegate {
                    var previous = checking;
                    checking = null;
                    if (previous != null) previous.Cancel();
                    probe.Enabled = types.SelectedIndex > 0;
                    status.Text = probe.Enabled ? label("使用当前地址和凭据查询模型。照片和修图指令留在本机。","Checks the model using this address and key. Photos and editing instructions stay on this PC.") : label("本地规则可离线使用。","Offline rules are available without a connection.");
                };
                types.SelectedIndexChanged += delegate
                {
                    int i = types.SelectedIndex;
                    bool same = Kinds[i] == original.Kind, active = i != 0;
                    endpoint.Enabled = model.Enabled = key.Enabled = consent.Enabled = active;
                    endpoint.Text = same && active ? original.Endpoint : Defaults[i];
                    model.Text = same && active ? original.Model : "";
                    key.Clear(); consent.Checked = same && active;
                    keyHint.Text = same && !String.IsNullOrEmpty(original.Key) ? label("保持服务和地址不变时，留空可沿用已保存的 Key。","Leave blank to keep the saved key for this provider and address.") : active ? label("模型 ID 请以服务端可用列表为准。","Enter a model ID available from your provider.") : label("本地规则可离线运行。","Offline rules run on this PC.");
                    reset();
                };
                types.SelectedIndex = Array.IndexOf(Kinds, original.Kind);
                endpoint.TextChanged += delegate { consent.Checked = false; reset(); };
                model.TextChanged += delegate { reset(); };
                key.TextChanged += delegate { reset(); };
                dialog.FormClosing += delegate { if (checking != null) { checking.Cancel(); checking = null; } };
                Func<Settings> draft = delegate {
                    string kind = Kinds[types.SelectedIndex];
                    var s = new Settings { Kind = kind };
                    if (kind != "rules")
                    {
                        s.Endpoint = endpoint.Text.Trim(); s.Model = model.Text.Trim(); s.Key = key.Text.Trim();
                        if (s.Key.Length == 0 && kind == original.Kind && String.Equals(s.Endpoint, original.Endpoint, StringComparison.Ordinal)) s.Key = original.Key;
                    }
                    return s;
                };
                probe.Click += async delegate
                {
                    var cts = new CancellationTokenSource();
                    checking = cts;
                    probe.Enabled = false;
                    status.Text = label("正在查询模型…","Checking for the model…");
                    try
                    {
                        var proposed = draft();
                        string result = await Task.Run(() => CheckConnection(proposed, cts.Token));
                        if (checking == cts) status.Text = english?NativeMessages.Translate(result):result;
                    }
                    catch (Exception e) { if (checking == cts) status.Text = english?NativeMessages.Translate(e.GetBaseException().Message,"providerSettings"):e.GetBaseException().Message; }
                    finally
                    {
                        if (checking == cts) { checking = null; probe.Enabled = true; }
                        cts.Dispose();
                    }
                };
                save.Click += delegate
                {
                    try
                    {
                        var s = draft();
                        if (s.Kind != "rules" && !consent.Checked) throw new ArgumentException("请勾选启用服务，或选择本地规则。");
                        SaveSettings(channelDataDir, s);
                        dialog.DialogResult = DialogResult.OK;
                    }
                    catch (Exception e) { MessageBox.Show(dialog, english?NativeMessages.Translate(e.Message,"providerSettings"):e.Message, label("AI 服务设置","AI provider settings"), MessageBoxButtons.OK, MessageBoxIcon.Information); }
                };
                return dialog.ShowDialog(owner) == DialogResult.OK;
            }
        }
    }
}
