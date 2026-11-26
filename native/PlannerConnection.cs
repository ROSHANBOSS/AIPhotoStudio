using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;

namespace AIPhotoStudio
{
    public static partial class Planner
    {
        private static string RequestJson(Settings config, Uri uri, byte[] body, int timeout, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested();
            var request = (HttpWebRequest)WebRequest.Create(uri);
            request.Method = body == null ? "GET" : "POST";
            request.Accept = "application/json";
            request.AllowAutoRedirect = false;
            request.UseDefaultCredentials = false;
            request.Credentials = null;
            request.CookieContainer = null;
            request.Timeout = request.ReadWriteTimeout = timeout;
            if (Loopback(uri)) request.Proxy = null;
            if (!String.IsNullOrEmpty(config.Key))
            {
                if (config.Kind == "gemini") request.Headers["x-goog-api-key"] = config.Key;
                else request.Headers[HttpRequestHeader.Authorization] = "Bearer " + config.Key;
            }
            try
            {
                using (cancel.Register(request.Abort))
                using (var deadline = new Timer(delegate { request.Abort(); }, null, timeout, Timeout.Infinite))
                {
                    if (body != null)
                    {
                        request.ContentType = "application/json; charset=utf-8";
                        request.ContentLength = body.Length;
                        using (var output = request.GetRequestStream()) output.Write(body, 0, body.Length);
                    }
                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var stream = response.GetResponseStream())
                    using (var buffer = new MemoryStream())
                    {
                        int status = (int)response.StatusCode;
                        if (status >= 300 && status < 400) throw new InvalidOperationException("服务要求重定向。请在设置中直接确认新的 API 根地址。");
                        if (status < 200 || status >= 300) throw new InvalidOperationException("服务返回 HTTP " + status + "。");
                        const int limit = 512 * 1024;
                        if (response.ContentLength > limit) throw new InvalidDataException("服务响应过大。");
                        byte[] chunk = new byte[8192];
                        int read;
                        while ((read = stream.Read(chunk, 0, chunk.Length)) > 0)
                        {
                            cancel.ThrowIfCancellationRequested();
                            if (buffer.Length + read > limit) throw new InvalidDataException("服务响应过大。");
                            buffer.Write(chunk, 0, read);
                        }
                        return new UTF8Encoding(false, true).GetString(buffer.ToArray());
                    }
                }
            }
            catch (WebException e)
            {
                var response = e.Response as HttpWebResponse;
                if (response != null)
                {
                    int status = (int)response.StatusCode;
                    response.Dispose();
                    if (body == null && (status == 404 || status == 405))
                        throw new InvalidOperationException("服务返回 HTTP " + status + "。请核对模型 ID 与地址，或确认该服务提供模型查询接口。");
                    throw new InvalidOperationException("服务返回 HTTP " + status + "，请检查模型、授权和额度。");
                }
                cancel.ThrowIfCancellationRequested();
                throw new InvalidOperationException("AI 服务连接失败或超时，请检查服务地址和本机网络。");
            }
        }

        internal static string CheckConnection(Settings config, CancellationToken cancel)
        {
            VerifySettings(config);
            if (config.Kind == "rules") return "本地规则可离线使用。";
            var root = new UriBuilder(EndpointUri(config));
            bool gemini = config.Kind == "gemini", retrieve = config.Kind == "openai";
            const string chat = "/chat/completions";
            if (gemini) root.Path = root.Path.Substring(0, root.Path.LastIndexOf(":generateContent", StringComparison.Ordinal));
            else root.Path = root.Path.Substring(0, root.Path.Length - chat.Length) + "/models" +
                (retrieve ? "/" + Uri.EscapeDataString(config.Model) : "");
            string text = RequestJson(config, root.Uri, null, 12000, cancel);
            Dictionary<string, object> info;
            try { info = Json().Deserialize<Dictionary<string, object>>(text); }
            catch (Exception e)
            {
                if (!(e is ArgumentException || e is InvalidOperationException)) throw;
                throw new InvalidDataException("模型接口没有返回可读取的 JSON。");
            }
            if (info == null) throw new InvalidDataException("模型接口返回了空结果。");
            object value;
            bool found;
            if (gemini)
            {
                string prefix = "models/" + config.Model;
                string name = info.TryGetValue("name", out value) ? value as string : null;
                object alias, revision;
                found = String.Equals(name,prefix,StringComparison.Ordinal) ||
                    (name != null && name.StartsWith("models/",StringComparison.Ordinal) &&
                     (info.TryGetValue("baseModelId",out alias) && Object.Equals(alias,config.Model) ||
                      info.TryGetValue("version",out revision) && revision is string && name==prefix+"-"+(string)revision));
                if (!info.TryGetValue("supportedGenerationMethods", out value) || !(value is IList) ||
                    !((IList)value).Cast<object>().Contains("generateContent")) throw new InvalidOperationException("该模型未列出文本生成能力。");
            }
            else if (retrieve) found = info.TryGetValue("id", out value) && Object.Equals(value, config.Model);
            else
            {
                if (!info.TryGetValue("data", out value) || !(value is IList)) throw new InvalidDataException("服务未返回兼容的模型列表。");
                found = ((IList)value).OfType<Dictionary<string, object>>().Any(model => {
                    object id; return model.TryGetValue("id", out id) && Object.Equals(id, config.Model);
                });
            }
            if (!found) throw new InvalidOperationException("服务已响应，返回的模型信息中没有填写的模型 ID。");
            return "已确认服务可访问，模型 " + config.Model + " 可见。";
        }
    }
}
