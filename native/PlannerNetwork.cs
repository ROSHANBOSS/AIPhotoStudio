using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace AIPhotoStudio
{
    public static partial class Planner
    {
        private const string SystemPrompt = "根据用户指令和当前参数生成编辑建议，仅返回 JSON 对象 label、message、values。只用 allowed 中允许的键和范围内的有限绝对数值，保留其他参数。不得生成工具调用、文件操作或坐标。局部范围由用户选择，无法执行时用空 values 解释。";

        private static Dictionary<string, object> Remote(Settings config, string text, Context c)
        {
            if (Regex.IsMatch(text, @"(?:[a-z]:[\\/]|\\\\|file://|data:[^,]{0,70};base64,|(?:^|\s)/(?:home|Users|Volumes|mnt)/)", Ignore))
                throw new InvalidOperationException("指令含本地路径或内嵌文件，请移除后再发送给模型。");
            var allowed = c.Allowed.OrderBy(k => k, StringComparer.Ordinal).Select(k =>
                new { key = k, name = c.Specs[k].label, min = c.Specs[k].min, max = c.Specs[k].max }).ToArray();
            var input = new {
                instruction = text, allowed = allowed, current = c.Current,
                brightness = c.Analysis,
                history = c.History.Select(g => g.Select(p => new { key = p.Key, before = p.Before, after = p.After }).ToArray()).ToArray()
            };
            string prompt = Json().Serialize(input);
            object body;
            if (config.Kind == "gemini")
            {
                body = new {
                    systemInstruction = new { parts = new[] { new { text = SystemPrompt } } },
                    contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } },
                    generationConfig = new { responseMimeType = "application/json" }
                };
            }
            else
            {
                var chat = new Dictionary<string, object> {
                    {"model",config.Model}, {"messages",new[] {new {role="system",content=SystemPrompt}, new {role="user",content=prompt}}},
                    {"stream",false}
                };
                if (config.Kind == "openai" || config.Kind == "deepseek") chat.Add("response_format", new { type = "json_object" });
                body = chat;
            }
            byte[] bytes = Encoding.UTF8.GetBytes(Json().Serialize(body));
            string responseText = RequestJson(config, EndpointUri(config), bytes, 60000, CancellationToken.None);
            string content;
            try
            {
                var envelope = Json().Deserialize<Dictionary<string, object>>(responseText);
                if (config.Kind == "gemini")
                {
                    var candidate = First(envelope["candidates"]);
                    object finish;
                    if (candidate.TryGetValue("finishReason", out finish) && !Object.Equals(finish, "STOP"))
                        throw new InvalidDataException("服务未返回完整提案。");
                    var parts = (IEnumerable)((Dictionary<string, object>)candidate["content"])["parts"];
                    var normal = new StringBuilder();
                    foreach (object obj in parts)
                    {
                        var part = (Dictionary<string, object>)obj;
                        if (part.ContainsKey("thought") && Object.Equals(part["thought"], true)) continue;
                        if (part.Keys.Any(k=>k!="text" && k!="thought" && k!="thoughtSignature") || !(part["text"] is string))
                            throw new InvalidDataException("服务返回了无法作为参数使用的内容。");
                        normal.Append((string)part["text"]);
                    }
                    content = normal.ToString();
                }
                else
                {
                    var choice = First(envelope["choices"]);
                    object finish;
                    if (choice.TryGetValue("finish_reason", out finish) && finish != null && !Object.Equals(finish, "stop"))
                        throw new InvalidDataException("服务没有正常完成提案。");
                    var message = (Dictionary<string, object>)choice["message"];
                    if ((message.ContainsKey("tool_calls") && message["tool_calls"] != null) || !(message["content"] is string))
                        throw new InvalidDataException("服务未返回文字参数提案。");
                    content = (string)message["content"];
                }
            }
            catch (Exception e)
            {
                if (!(e is KeyNotFoundException || e is InvalidCastException || e is ArgumentException || e is InvalidOperationException || e is NullReferenceException)) throw;
                throw new InvalidDataException("服务响应与所选 API 协议不匹配。");
            }
            var parsed = new ProposalJson(content);
            if (parsed.Values.Count == 0) throw new InvalidOperationException(parsed.Message);
            return Result(c, parsed.Values, parsed.Label, parsed.Message);
        }

        private static Dictionary<string, object> First(object value)
        {
            IList list = value as IList;
            if (list == null || list.Count < 1) throw new InvalidDataException("服务没有给出提案。");
            var item = list[0] as Dictionary<string, object>;
            if (item == null) throw new InvalidDataException("服务响应中缺少提案对象。");
            return item;
        }

        // The bridge accepts exactly two text fields and one numeric parameter object.
        // This small grammar also rejects duplicate names, code fences, comments and
        // JavaScript literals that permissive JSON serializers might otherwise accept.
        private sealed class ProposalJson
        {
            private readonly string input;
            private int position;
            internal readonly Dictionary<string, double> Values = new Dictionary<string, double>(StringComparer.Ordinal);
            internal string Label, Message;
            private static readonly Regex JsonNumber = new Regex(@"\G-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?", RegexOptions.CultureInvariant);
            internal ProposalJson(string data)
            {
                if (String.IsNullOrEmpty(data) || data.Length > 32768) throw Bad();
                input = data;
                bool values = false;
                Object(delegate(string field)
                {
                    switch (field)
                    {
                        case "label": Label = ReadString(); break;
                        case "message": Message = ReadString(); break;
                        case "values":
                            values = true; Object(delegate(string key)
                            {
                                White(); Match m = JsonNumber.Match(input, position); double number;
                                if (!m.Success || !TryDouble(m.Value, out number)) throw Bad();
                                position += m.Length; Values.Add(key, number);
                            }); break;
                        default: throw Bad();
                    }
                });
                White();
                if (position != input.Length || !values || String.IsNullOrWhiteSpace(Label) || Label.Length > 240 ||
                    String.IsNullOrWhiteSpace(Message) || Message.Length > 1000) throw Bad();
            }

            private static InvalidDataException Bad() { return new InvalidDataException("服务提案必须是仅含 label、message 和数值 values 的严格 JSON 对象。"); }
            private void White() { while (position < input.Length && " \r\n\t".IndexOf(input[position]) >= 0) position++; }
            private bool Take(char token)
            {
                White();
                if (position >= input.Length || input[position] != token) return false;
                position++; return true;
            }
            private void Object(Action<string> read)
            {
                if (!Take('{')) throw Bad();
                if (Take('}')) return;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                do
                {
                    string key = ReadString();
                    if (!seen.Add(key) || !Take(':')) throw Bad();
                    read(key);
                    if (Take('}')) return;
                } while (Take(','));
                throw Bad();
            }
            private string ReadString()
            {
                if (!Take('"')) throw Bad();
                var result = new StringBuilder();
                while (position < input.Length)
                {
                    char ch = input[position++];
                    if (ch == '"') return result.ToString();
                    if (ch < 0x20) throw Bad();
                    if (ch != '\\') { result.Append(ch); continue; }
                    if (position >= input.Length) throw Bad();
                    char escape = input[position++];
                    int index = "\"\\/bfnrt".IndexOf(escape);
                    if (index >= 0) result.Append("\"\\/\b\f\n\r\t"[index]);
                    else if (escape == 'u')
                    {
                        int code;
                        if (position + 4 > input.Length || !Int32.TryParse(input.Substring(position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code)) throw Bad();
                        result.Append((char)code); position += 4;
                    }
                    else throw Bad();
                }
                throw Bad();
            }
        }
    }
}
