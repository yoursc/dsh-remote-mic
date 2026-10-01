using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DshRemoteMic
{
    /// <summary>
    /// 内置诊断页的静态服务，**与 WebSocket 共用同一个端口**（<c>WsServer</c> 在握手前按
    /// <c>Upgrade: websocket</c> 分流：带 Upgrade 走 WS，不带就交给这里）。
    ///
    /// 为什么要内置而不是"另起一个 http 服务进程"：
    ///   1. 页面用 ES module，<c>file://</c> 下会被 CORS 拦掉，必须有 http；
    ///   2. 交给 Python/Node 起服务 ⇒ 多一个进程、多一个要记的端口，且没人负责它活着；
    ///   3. 端口本来就要配（WS），共用它就只剩一个配置项，页面还能从 <c>location.host</c>
    ///      自己推出接缝地址 —— 页面里的端口输入框因此可以不必手填。
    ///
    /// 页面来源：**磁盘优先，内嵌兜底**（定案 2026-10-01）：
    ///   · 先在 exe 所在目录**向上若干层**找仓库里的 <c>diagnostics/</c>（要求同时有
    ///     <c>index.html</c> 与 <c>js/app.js</c> 才算数），找到就**每次请求现读现用**
    ///     ⇒ 改页面只需刷新浏览器，**不用重编译**（这正是只要内嵌时最痛的地方）；
    ///   · 找不到（发布出去的单文件 exe、或 exe 被拷到别处）就用 csproj 内嵌的那份；
    ///   · 逐个文件各自回退：磁盘上缺/正被占用/读失败的那一个走内嵌，不会整页 404。
    ///   · <c>--diag-dir &lt;目录&gt;</c> 可显式指定来源，用于 exe 与页面不在同一棵目录树的场合。
    ///
    /// 安全：能取到的路径是启动时枚举好的固定白名单（5 个），请求里的 <c>..</c>、<c>%2e</c>、
    /// 反斜杠只会查不到 ⇒ 404。这条对磁盘来源同样成立 —— 白名单里没有的路径根本不会拼进
    /// 文件系统，所以"改成读文件"并没有引入路径穿越。
    /// </summary>
    internal static class DiagWeb
    {
        private const string Prefix = "DshRemoteMic.diag.";
        private const string DirName = "diagnostics";

        /// <summary>最多向上找几层。exe 在 <c>&lt;仓库&gt;/local-mic/bin/Release/</c>，第 4 层命中。</summary>
        private const int MaxLookup = 6;

        /// <summary>页面真正需要的 5 个文件（adpcm.js / session.js 只服务离线测试，不内嵌）。</summary>
        private static readonly string[] Required =
        {
            "/",
            "/js/app.js",
            "/js/transport.js",
            "/js/wav.js",
            "/js/atvv-consts.js",
        };

        private static readonly HashSet<string> Known = new HashSet<string>(StringComparer.Ordinal);

        private static readonly Dictionary<string, byte[]> Files =
            new Dictionary<string, byte[]>(StringComparer.Ordinal);

        private static readonly List<string> Missing = new List<string>();
        private static readonly object Sync = new object();
        private static bool _embeddedLoaded;

        // 磁盘来源：定位一次（结果缓存），内容每次请求现读 —— 这样改页面不用重启、更不用重编译
        private static string _overrideDir;
        private static string _diskDir;
        private static bool _diskProbed;
        private static string _overrideNote;

        static DiagWeb()
        {
            foreach (var p in Required) Known.Add(Normalize(p));
        }

        /// <summary>托盘菜单用：诊断页地址与 WS 同一个端口。</summary>
        public static string Url(int port)
        {
            return "http://127.0.0.1:" + port + "/";
        }

        /// <summary>
        /// 显式指定页面目录（<c>--diag-dir</c>）。目录里必须有 <c>index.html</c>，
        /// 否则记一条说明并继续用自动查找 + 内嵌 —— 这是个开发期开关，不值得弹框打断启动。
        /// </summary>
        public static void SetSourceDir(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return;
            try
            {
                string full = Path.GetFullPath(dir);
                if (File.Exists(Path.Combine(full, "index.html")))
                {
                    _overrideDir = full;
                    _overrideNote = null;
                }
                else
                {
                    _overrideNote = "--diag-dir 指向的目录里没有 index.html：" + full;
                }
            }
            catch (Exception e)
            {
                _overrideNote = "--diag-dir 无法解析：" + e.Message;
            }
        }

        private static void EnsureEmbedded()
        {
            lock (Sync)
            {
                if (_embeddedLoaded) return;
                _embeddedLoaded = true;

                var asm = Assembly.GetExecutingAssembly();
                foreach (var path in Required)
                {
                    string name = Prefix + ResourceKeyOf(path);
                    try
                    {
                        using (var s = asm.GetManifestResourceStream(name))
                        {
                            if (s == null) { Missing.Add(path + "（资源 " + name + " 不存在）"); continue; }
                            using (var ms = new MemoryStream())
                            {
                                s.CopyTo(ms);
                                Files[Normalize(path)] = ms.ToArray();
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        Missing.Add(path + "（" + e.Message + "）");
                    }
                }
            }
        }

        /// <summary>当前生效的页面目录；没找到返回 null（此时走内嵌）。</summary>
        private static string DiskDir()
        {
            lock (Sync)
            {
                if (_overrideDir != null) return _overrideDir;
                if (_diskProbed) return _diskDir;
                _diskProbed = true;
                _diskDir = FindAlongsideExe();
                return _diskDir;
            }
        }

        /// <summary>
        /// 从 exe 所在目录逐级向上找 <c>diagnostics/</c>。命中条件是该目录同时有
        /// <c>index.html</c> 与 <c>js/app.js</c> —— 免得撞上别的同名目录。
        /// </summary>
        private static string FindAlongsideExe()
        {
            try
            {
                string dir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                for (int i = 0; i < MaxLookup && !string.IsNullOrEmpty(dir); i++)
                {
                    string candidate = Path.Combine(dir, DirName);
                    if (File.Exists(Path.Combine(candidate, "index.html")) &&
                        File.Exists(Path.Combine(candidate, "js", "app.js")))
                    {
                        return candidate;
                    }
                    dir = Path.GetDirectoryName(dir);
                }
            }
            catch { }
            return null;
        }

        /// <summary>URL 路径 → 内嵌资源名（目录分隔 "/" 换成 "."）。</summary>
        private static string ResourceKeyOf(string path)
        {
            string p = Normalize(path);
            if (p.StartsWith("/", StringComparison.Ordinal)) p = p.Substring(1);
            return p.Replace('/', '.');
        }

        /// <summary>URL 路径 → 磁盘相对路径（如 "/js/app.js" ⇒ "js\app.js"）。白名单内调用。</summary>
        private static string RelativeOf(string key)
        {
            string p = key.StartsWith("/", StringComparison.Ordinal) ? key.Substring(1) : key;
            return p.Replace('/', Path.DirectorySeparatorChar);
        }

        private static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return "/index.html";
            int cut = path.IndexOfAny(new[] { '?', '#' });
            if (cut >= 0) path = path.Substring(0, cut);
            if (path == "/" || path == "") return "/index.html";
            return path;
        }

        /// <summary>
        /// 取一个页面文件：磁盘优先，失败退回内嵌。<paramref name="source"/> 记实际来源，
        /// 会作为 <c>X-Diag-Source</c> 响应头发回去 —— curl 一眼能看出改的页面有没有生效。
        /// </summary>
        private static bool TryResolve(string key, out byte[] body, out string source)
        {
            body = null;
            source = "";
            if (!Known.Contains(key)) return false;

            string dir = DiskDir();
            if (dir != null)
            {
                byte[] fromDisk = TryReadFile(Path.Combine(dir, RelativeOf(key)));
                if (fromDisk != null)
                {
                    body = fromDisk;
                    source = "disk";
                    return true;
                }
            }

            EnsureEmbedded();
            byte[] embedded;
            if (Files.TryGetValue(key, out embedded))
            {
                body = embedded;
                source = "embedded";
                return true;
            }
            return false;
        }

        private static byte[] TryReadFile(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                // FileShare.ReadWrite：编辑器/编译器正占着它时也要能读（读不到就退内嵌，不整页 404）
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var ms = new MemoryStream())
                {
                    fs.CopyTo(ms);
                    return ms.ToArray();
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 处理一个非 WebSocket 的 HTTP 请求。无论成功失败都由它回完响应（调用方随后关连接）。
        /// </summary>
        public static async Task ServeAsync(string request, NetworkStream s)
        {
            try
            {
                if (!HostAllowed(request))
                {
                    // 只认本机地址。浏览器里的任意页面都能往 127.0.0.1 发请求（DNS rebinding 就靠这个），
                    // 而 Host 头是它改不掉的东西 —— 认它就能挡住。
                    await WriteAsync(s, 403, "Forbidden", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("403 只接受来自本机的请求"), false, "-").ConfigureAwait(false);
                    return;
                }

                string method, path;
                if (!ParseRequestLine(request, out method, out path))
                {
                    await WriteAsync(s, 400, "Bad Request", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("400 请求行解析失败"), false, "-").ConfigureAwait(false);
                    return;
                }

                if (method != "GET" && method != "HEAD")
                {
                    await WriteAsync(s, 405, "Method Not Allowed", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("405 只支持 GET"), false, "-").ConfigureAwait(false);
                    return;
                }

                // ⚠ MIME 必须按**规范化之后**的路径算："/" 会变成 "/index.html"，
                // 拿原始 "/" 去取扩展名会得到 octet-stream ⇒ 浏览器把首页当文件下载，而不是渲染。
                string key = Normalize(path);

                byte[] body;
                string source;
                if (!TryResolve(key, out body, out source))
                {
                    await WriteAsync(s, 404, "Not Found", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("404 " + path), false, "-").ConfigureAwait(false);
                    return;
                }

                await WriteAsync(s, 200, "OK", ContentTypeOf(key), body, method == "GET", source)
                    .ConfigureAwait(false);
            }
            catch
            {
                // 请求方半路断开等，静默处理 —— 诊断页不是关键路径
            }
        }

        private static bool ParseRequestLine(string request, out string method, out string path)
        {
            method = "";
            path = "";
            if (string.IsNullOrEmpty(request)) return false;

            int eol = request.IndexOf('\n');
            string line = (eol > 0 ? request.Substring(0, eol) : request).Trim();
            var parts = line.Split(' ');
            if (parts.Length < 2) return false;

            method = parts[0].ToUpperInvariant();
            path = parts[1];
            return true;
        }

        private static bool HostAllowed(string request)
        {
            var m = Regex.Match(request, @"^[Hh]ost:\s*(\S+)", RegexOptions.Multiline);
            if (!m.Success) return false;                 // 没有 Host 头 ⇒ HTTP/1.0 或爬虫，一律不认

            string host = m.Groups[1].Value.Trim();
            int colon = host.LastIndexOf(':');
            string name = colon > 0 ? host.Substring(0, colon) : host;    // 去端口

            return name.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase)
                || name.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || name.Equals("[::1]", StringComparison.OrdinalIgnoreCase);
        }

        private static string ContentTypeOf(string path)
        {
            int dot = path.LastIndexOf('.');
            string ext = dot >= 0 ? path.Substring(dot).ToLowerInvariant() : "";
            switch (ext)
            {
                case ".html":
                case ".htm": return "text/html; charset=utf-8";
                case ".js": return "application/javascript; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".json": return "application/json; charset=utf-8";
                case ".svg": return "image/svg+xml";
                default: return "application/octet-stream";
            }
        }

        private static async Task WriteAsync(NetworkStream s, int status, string reason,
            string contentType, byte[] body, bool withBody, string source)
        {
            var sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(status).Append(' ').Append(reason).Append("\r\n");
            sb.Append("Content-Type: ").Append(contentType).Append("\r\n");
            sb.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            sb.Append("Cache-Control: no-store\r\n");
            sb.Append("X-Content-Type-Options: nosniff\r\n");
            // 响应头必须是 ASCII，所以这里只写来源类别，不写路径（路径可能是中文）
            sb.Append("X-Diag-Source: ").Append(source).Append("\r\n");
            sb.Append("Connection: close\r\n\r\n");

            var head = Encoding.ASCII.GetBytes(sb.ToString());
            await s.WriteAsync(head, 0, head.Length).ConfigureAwait(false);
            if (withBody && body.Length > 0) await s.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
            await s.FlushAsync().ConfigureAwait(false);
        }

        /// <summary>
        /// 自检用的一行结论：5 个页面文件是否都取得到，以及**当前用的是哪一份**。
        /// "改了页面没生效"的第一嫌疑就是来源搞错（以为读磁盘、其实走的内嵌），
        /// 所以这里把来源和目录直接打出来。
        /// </summary>
        public static string SelfTest()
        {
            int fromDisk = 0, fromEmbedded = 0;
            var bad = new List<string>();

            foreach (var path in Required)
            {
                byte[] body;
                string source;
                if (TryResolve(Normalize(path), out body, out source))
                {
                    if (source == "disk") fromDisk++; else fromEmbedded++;
                }
                else
                {
                    bad.Add(path);
                }
            }

            string dir = DiskDir();
            string origin = dir != null ? "磁盘 " + dir : "未找到 diagnostics/ 目录，用内嵌";
            var sb = new StringBuilder();
            sb.Append("诊断页自检");
            if (bad.Count > 0)
            {
                sb.Append("失败：缺 ").Append(bad.Count).Append(" 个 —— ").Append(string.Join("；", bad.ToArray()));
            }
            else
            {
                sb.Append("通过：").Append(Required.Length).Append('/').Append(Required.Length)
                  .Append("（磁盘 ").Append(fromDisk).Append(" · 内嵌 ").Append(fromEmbedded).Append('）');
            }
            sb.Append("；来源：").Append(origin);

            EnsureEmbedded();
            if (Missing.Count > 0)
            {
                // 磁盘能补上时不算失败，但内嵌资源名写错这件事本身要看得见 ——
                // 否则发布出去的 exe 一旦找不到目录就是半页 404。
                sb.Append("；⚠ 内嵌缺 ").Append(Missing.Count).Append(" 个：")
                  .Append(string.Join("；", Missing.ToArray()));
            }
            if (_overrideNote != null) sb.Append("；⚠ ").Append(_overrideNote);

            return sb.ToString();
        }
    }
}
