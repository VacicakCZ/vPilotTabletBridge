using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;

namespace VpilotTabletBridge
{
    /// <summary>
    /// Minimal HTTP/1.1 server built directly on TcpListener rather than
    /// System.Net.HttpListener.
    ///
    /// HttpListener (http.sys) refuses to bind any prefix other than strict
    /// loopback (http://localhost/) unless the process runs elevated or a
    /// "netsh http add urlacl" reservation was made first - which would be
    /// an awkward one-time admin step for a home-cockpit plugin that vPilot
    /// normally runs as a regular user. A plain TcpListener has no such
    /// restriction: it can bind 0.0.0.0 on an unprivileged port as a normal
    /// user, which is exactly what's needed so a tablet on the same LAN can
    /// reach this PC.
    /// </summary>
    public class WebServer
    {
        private readonly int _port;
        private readonly MessageStore _store;
        private readonly PluginState _state;
        private readonly Controllers _controllers;
        private readonly Traffic _traffic;
        private readonly Actions _actions;
        private readonly string _qrPage;
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;

        private static readonly string HtmlPage = LoadEmbeddedHtml();

        public WebServer(int port, MessageStore store, PluginState state, Controllers controllers, Traffic traffic, Actions actions, List<string> localIps)
        {
            _port = port;
            _store = store;
            _state = state;
            _controllers = controllers;
            _traffic = traffic;
            _actions = actions;
            _qrPage = BuildQrPage(localIps, port);
        }

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "TabletBridge-Accept" };
            _acceptThread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch { /* already stopped */ }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch
                {
                    break; // listener was stopped
                }

                var worker = new Thread(() => HandleClient(client)) { IsBackground = true };
                worker.Start();
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    stream.ReadTimeout = 5000;
                    stream.WriteTimeout = 5000;

                    string requestLine = ReadLine(stream);
                    if (string.IsNullOrEmpty(requestLine)) return;

                    string[] requestParts = requestLine.Split(' ');
                    string method = requestParts.Length >= 1 ? requestParts[0] : "GET";
                    string path = requestParts.Length >= 2 ? requestParts[1] : "/";
                    int query = path.IndexOf('?');
                    if (query >= 0) path = path.Substring(0, query);

                    int contentLength = 0;
                    string headerLine;
                    while (!string.IsNullOrEmpty(headerLine = ReadLine(stream)))
                    {
                        int colon = headerLine.IndexOf(':');
                        if (colon <= 0) continue;
                        string name = headerLine.Substring(0, colon).Trim();
                        string value = headerLine.Substring(colon + 1).Trim();
                        if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                        {
                            int.TryParse(value, out contentLength);
                        }
                    }

                    string body = "";
                    if (contentLength > 0)
                    {
                        // Cap the body we're willing to read; these are tiny form posts.
                        contentLength = Math.Min(contentLength, 8192);
                        var buffer = new byte[contentLength];
                        int read = 0;
                        while (read < contentLength)
                        {
                            int n = stream.Read(buffer, read, contentLength - read);
                            if (n <= 0) break;
                            read += n;
                        }
                        body = Encoding.UTF8.GetString(buffer, 0, read);
                    }

                    Route(stream, method, path, body);
                }
            }
            catch
            {
                // Client disconnected mid-request, timed out, or sent garbage.
                // Nothing useful to do beyond dropping the connection.
            }
        }

        private void Route(NetworkStream stream, string method, string path, string body)
        {
            if (method == "GET" && (path == "/" || path == "/index.html"))
            {
                WriteResponse(stream, "200 OK", "text/html; charset=utf-8", HtmlPage);
                return;
            }

            if (method == "GET" && path == "/qr")
            {
                WriteResponse(stream, "200 OK", "text/html; charset=utf-8", _qrPage);
                return;
            }

            if (method == "GET" && path == "/api/messages")
            {
                WriteResponse(stream, "200 OK", "application/json; charset=utf-8", _store.ToJson());
                return;
            }

            if (method == "GET" && path == "/api/status")
            {
                WriteResponse(stream, "200 OK", "application/json; charset=utf-8", _state.ToJson());
                return;
            }

            if (method == "GET" && path == "/api/controllers")
            {
                WriteResponse(stream, "200 OK", "application/json; charset=utf-8", _controllers.ToJson());
                return;
            }

            if (method == "GET" && path == "/api/traffic")
            {
                WriteResponse(stream, "200 OK", "application/json; charset=utf-8", _traffic.ToJson());
                return;
            }

            if (method == "POST" && path == "/api/connect")
            {
                RunAction(stream, body, form => _actions.Connect(form.Get("callsign"), form.Get("typeCode"), form.Get("selcal")));
                return;
            }

            if (method == "POST" && path == "/api/disconnect")
            {
                RunAction(stream, body, form => _actions.Disconnect());
                return;
            }

            if (method == "POST" && path == "/api/metar")
            {
                RunAction(stream, body, form => _actions.RequestMetar(form.Get("station")));
                return;
            }

            if (method == "POST" && path == "/api/atis")
            {
                RunAction(stream, body, form => _actions.RequestAtis(form.Get("callsign")));
                return;
            }

            if (method == "POST" && path == "/api/settings")
            {
                RunAction(stream, body, form => _actions.SetRememberCallsign(form.Get("rememberCallsign") == "1"));
                return;
            }

            if (method == "POST" && path == "/api/send")
            {
                RunAction(stream, body, form => _actions.Send(form.Get("mode"), form.Get("to"), form.Get("message")));
                return;
            }

            if (method == "POST" && path == "/api/modec")
            {
                RunAction(stream, body, form => _actions.SetModeC(form.Get("on") == "1"));
                return;
            }

            if (method == "POST" && path == "/api/ident")
            {
                RunAction(stream, body, form => _actions.SquawkIdent());
                return;
            }

            WriteResponse(stream, "404 Not Found", "text/plain; charset=utf-8", "Not found");
        }

        private void RunAction(NetworkStream stream, string body, Action<FormData> action)
        {
            try
            {
                action(FormData.Parse(body));
                WriteResponse(stream, "200 OK", "application/json; charset=utf-8", "{\"ok\":true}");
            }
            catch (Exception ex)
            {
                WriteResponse(stream, "400 Bad Request", "application/json; charset=utf-8",
                    "{\"ok\":false,\"error\":" + Json.Str(ex.Message) + "}");
            }
        }

        private static string ReadLine(NetworkStream stream)
        {
            var sb = new StringBuilder();
            int prev = -1;
            int b;
            while ((b = stream.ReadByte()) != -1)
            {
                if (prev == '\r' && b == '\n')
                {
                    sb.Length--; // drop the trailing '\r' already appended
                    return sb.ToString();
                }
                sb.Append((char)b);
                prev = b;
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }

        private static void WriteResponse(NetworkStream stream, string status, string contentType, string body)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            string headers =
                "HTTP/1.1 " + status + "\r\n" +
                "Content-Type: " + contentType + "\r\n" +
                "Content-Length: " + bodyBytes.Length + "\r\n" +
                "Connection: close\r\n" +
                "Cache-Control: no-store\r\n" +
                "\r\n";
            byte[] headerBytes = Encoding.ASCII.GetBytes(headers);
            stream.Write(headerBytes, 0, headerBytes.Length);
            stream.Write(bodyBytes, 0, bodyBytes.Length);
            stream.Flush();
        }

        private static string LoadEmbeddedHtml()
        {
            Assembly asm = typeof(WebServer).Assembly;
            using (Stream s = asm.GetManifestResourceStream("VpilotTabletBridge.index.html"))
            {
                if (s == null) return "<html><body>index.html embedded resource not found.</body></html>";
                using (var reader = new StreamReader(s, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        /// <summary>
        /// Fills the qr.html template's placeholders in once at startup (the
        /// address list and port never change for the life of the process),
        /// rather than redoing the substitution on every request.
        /// </summary>
        private static string BuildQrPage(List<string> localIps, int port)
        {
            Assembly asm = typeof(WebServer).Assembly;
            string template;
            using (Stream s = asm.GetManifestResourceStream("VpilotTabletBridge.qr.html"))
            {
                if (s == null) return "<html><body>qr.html embedded resource not found.</body></html>";
                using (var reader = new StreamReader(s, Encoding.UTF8))
                {
                    template = reader.ReadToEnd();
                }
            }

            string primaryUrl = "http://" + (localIps.Count > 0 ? localIps[0] : "127.0.0.1") + ":" + port + "/";

            string otherAddrsBlock = "";
            if (localIps.Count > 1)
            {
                var sb = new StringBuilder();
                sb.Append("<div class=\"other-addrs\"><div class=\"label\">Other addresses on this PC</div>");
                for (int i = 1; i < localIps.Count; i++)
                {
                    string addr = "http://" + localIps[i] + ":" + port + "/";
                    sb.Append("<a href=\"").Append(addr).Append("\">").Append(addr).Append("</a>");
                }
                sb.Append("</div>");
                otherAddrsBlock = sb.ToString();
            }

            return template
                .Replace("__PRIMARY_URL__", primaryUrl)
                .Replace("__OTHER_ADDRS_BLOCK__", otherAddrsBlock);
        }

        /// <summary>Tiny application/x-www-form-urlencoded parser - no external dependency needed for a handful of form fields.</summary>
        private class FormData
        {
            private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            public static FormData Parse(string body)
            {
                var form = new FormData();
                if (string.IsNullOrEmpty(body)) return form;

                foreach (string pair in body.Split('&'))
                {
                    if (pair.Length == 0) continue;
                    int eq = pair.IndexOf('=');
                    string key = eq >= 0 ? pair.Substring(0, eq) : pair;
                    string value = eq >= 0 ? pair.Substring(eq + 1) : "";
                    key = Uri.UnescapeDataString(key.Replace('+', ' '));
                    value = Uri.UnescapeDataString(value.Replace('+', ' '));
                    form._values[key] = value;
                }
                return form;
            }

            public string Get(string key)
            {
                return _values.TryGetValue(key, out string v) ? v : "";
            }
        }
    }
}
