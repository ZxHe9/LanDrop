using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace LanDrop
{
    public class HttpRequest
    {
        public string Method;
        public string RawUrl;
        public string Path;
        public string QueryString;
        public Dictionary<string, string> Headers;
        public Dictionary<string, string> Query;
        public long ContentLength;
        public string RemoteAddress;
        public Stream Body;
        public bool IsHead;

        public HttpRequest()
        {
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public string Header(string name)
        {
            string value;
            return Headers.TryGetValue(name, out value) ? value : null;
        }

        public string QueryValue(string name)
        {
            string value;
            return Query.TryGetValue(name, out value) ? value : null;
        }

        public string ReadBodyText(int maxBytes)
        {
            if (ContentLength <= 0) return "";
            if (ContentLength > maxBytes) throw new InvalidDataException("请求内容过大");
            byte[] data = new byte[(int)ContentLength];
            int offset = 0;
            while (offset < data.Length)
            {
                int read = Body.Read(data, offset, data.Length - offset);
                if (read <= 0) throw new IOException("请求内容不完整");
                offset += read;
            }
            return Encoding.UTF8.GetString(data);
        }

        public Dictionary<string, object> ReadJson(int maxBytes)
        {
            string text = ReadBodyText(maxBytes);
            return Json.ParseObject(text);
        }

        public void CopyBodyTo(Stream output, Action<long> progress)
        {
            byte[] buffer = new byte[256 * 1024];
            long left = ContentLength;
            long done = 0;
            while (left > 0)
            {
                int want = (int)Math.Min(buffer.Length, left);
                int read = Body.Read(buffer, 0, want);
                if (read <= 0) throw new IOException("上传连接中断");
                output.Write(buffer, 0, read);
                left -= read;
                done += read;
                if (progress != null) progress(done);
            }
            output.Flush();
        }
    }

    public class HttpResponse
    {
        public int Status;
        public string ContentType;
        public byte[] BodyBytes;
        public string FilePath;
        public bool HeadOnly;
        public Dictionary<string, string> Headers;

        public HttpResponse()
        {
            Status = 200;
            ContentType = "application/json; charset=utf-8";
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        public void Json(object value)
        {
            BodyBytes = Encoding.UTF8.GetBytes(LanDrop.Json.Serialize(value));
            ContentType = "application/json; charset=utf-8";
        }

        public void Text(string text, string contentType)
        {
            BodyBytes = Encoding.UTF8.GetBytes(text ?? "");
            ContentType = contentType ?? "text/plain; charset=utf-8";
        }

        public void Error(int status, string message)
        {
            Status = status;
            Dictionary<string, object> data = new Dictionary<string, object>();
            data["ok"] = false;
            data["error"] = message;
            Json(data);
        }
    }

    public class HttpServer
    {
        private readonly object _sync = new object();
        private TcpListener _listener;
        private Thread _acceptThread;
        private volatile bool _running;
        private Action<HttpRequest, HttpResponse> _handler;

        public int Port { get; private set; }

        public HttpServer(int port)
        {
            Port = port;
        }

        public void Start(Action<HttpRequest, HttpResponse> handler)
        {
            _handler = handler;
            TcpListener listener = new TcpListener(IPAddress.Any, Port);
            listener.Start(512);
            _listener = listener;
            _running = true;
            _acceptThread = new Thread(AcceptLoop);
            _acceptThread.IsBackground = true;
            _acceptThread.Name = "LanDrop HTTP";
            _acceptThread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            try { if (_acceptThread != null) _acceptThread.Join(1500); } catch { }
        }

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client = null;
                try
                {
                    client = _listener.AcceptTcpClient();
                }
                catch (SocketException)
                {
                    if (!_running) break;
                    continue;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!_running) break;
                    Log.Error("接受连接失败", ex);
                    continue;
                }

                TcpClient captured = client;
                Thread thread = new Thread(delegate() { HandleClient(captured); });
                thread.IsBackground = true;
                thread.Name = "LanDrop Conn";
                thread.Start();
            }
        }

        private void HandleClient(TcpClient client)
        {
            using (client)
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 30000;
                client.SendTimeout = 120000;
                NetworkStream stream = client.GetStream();
                HttpRequest req = null;
                HttpResponse res = new HttpResponse();
                try
                {
                    byte[] leftover;
                    string headerText = ReadHeaderBlock(stream, out leftover);
                    if (headerText == null) return;
                    req = ParseRequest(headerText, client);
                    if (req == null)
                    {
                        res.Error(400, "请求格式不正确");
                    }
                    else
                    {
                        req.Body = new PrefixStream(leftover, stream);
                        res.HeadOnly = req.IsHead;
                        _handler(req, res);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("处理请求失败 " + (req == null ? "" : req.RawUrl), ex);
                    if (res.BodyBytes == null && res.FilePath == null)
                        res.Error(500, "服务器处理请求时出错");
                }

                try
                {
                    WriteResponse(stream, res);
                }
                catch (Exception ex)
                {
                    Log.Warn("写响应失败: " + ex.Message);
                }
            }
        }

        private static string ReadHeaderBlock(NetworkStream stream, out byte[] leftover)
        {
            leftover = new byte[0];
            byte[] buffer = new byte[8192];
            MemoryStream ms = new MemoryStream();
            int searchFrom = 0;
            while (ms.Length < 65536)
            {
                int read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0) return null;
                ms.Write(buffer, 0, read);
                byte[] all = ms.GetBuffer();
                int end = FindHeaderEnd(all, (int)ms.Length, searchFrom);
                if (end >= 0)
                {
                    int headerLength = end;
                    string text = Encoding.UTF8.GetString(all, 0, headerLength);
                    int bodyStart = end + 4;
                    int bodyLength = (int)ms.Length - bodyStart;
                    leftover = new byte[bodyLength];
                    if (bodyLength > 0) Buffer.BlockCopy(all, bodyStart, leftover, 0, bodyLength);
                    return text;
                }
                searchFrom = Math.Max(0, (int)ms.Length - 3);
            }
            return null;
        }

        private static int FindHeaderEnd(byte[] data, int length, int from)
        {
            for (int i = Math.Max(0, from); i <= length - 4; i++)
            {
                if (data[i] == 13 && data[i + 1] == 10 && data[i + 2] == 13 && data[i + 3] == 10)
                    return i;
            }
            return -1;
        }

        private HttpRequest ParseRequest(string headerText, TcpClient client)
        {
            string[] lines = headerText.Split(new string[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0) return null;
            string[] first = lines[0].Split(' ');
            if (first.Length < 2) return null;

            HttpRequest req = new HttpRequest();
            req.Method = first[0].ToUpperInvariant();
            req.IsHead = req.Method == "HEAD";
            req.RawUrl = first[1];
            string target = req.RawUrl;
            if (target.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                Uri uri;
                if (Uri.TryCreate(target, UriKind.Absolute, out uri)) target = uri.PathAndQuery;
            }
            int q = target.IndexOf('?');
            if (q >= 0)
            {
                req.Path = Uri.UnescapeDataString(target.Substring(0, q));
                req.QueryString = target.Substring(q + 1);
                ParseQuery(req.QueryString, req.Query);
            }
            else
            {
                req.Path = Uri.UnescapeDataString(target);
                req.QueryString = "";
            }
            if (string.IsNullOrEmpty(req.Path)) req.Path = "/";

            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (line.Length == 0) continue;
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                req.Headers[name] = value;
            }

            long length = 0;
            string cl = req.Header("Content-Length");
            if (!string.IsNullOrEmpty(cl)) long.TryParse(cl, NumberStyles.Integer, CultureInfo.InvariantCulture, out length);
            if (length < 0 || length > 1024L * 1024L * 1024L * 1024L) return null;
            req.ContentLength = length;

            string transferEncoding = req.Header("Transfer-Encoding");
            if (!string.IsNullOrEmpty(transferEncoding) && transferEncoding.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                return null;

            IPEndPoint ep = client.Client.RemoteEndPoint as IPEndPoint;
            req.RemoteAddress = ep == null ? "" : ep.Address.ToString();
            return req;
        }

        private static void ParseQuery(string query, Dictionary<string, string> target)
        {
            if (string.IsNullOrEmpty(query)) return;
            string[] parts = query.Split('&');
            foreach (string part in parts)
            {
                if (part.Length == 0) continue;
                int eq = part.IndexOf('=');
                string key = eq >= 0 ? part.Substring(0, eq) : part;
                string value = eq >= 0 ? part.Substring(eq + 1) : "";
                try
                {
                    key = Uri.UnescapeDataString(key.Replace("+", " "));
                    value = Uri.UnescapeDataString(value.Replace("+", " "));
                }
                catch { }
                target[key] = value;
            }
        }

        private void WriteResponse(NetworkStream stream, HttpResponse res)
        {
            byte[] body = res.BodyBytes;
            long contentLength = body == null ? 0 : body.Length;
            FileStream file = null;
            if (!string.IsNullOrEmpty(res.FilePath))
            {
                file = new FileStream(res.FilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                contentLength = file.Length;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("HTTP/1.1 ").Append(res.Status).Append(' ').Append(StatusText(res.Status)).Append("\r\n");
            sb.Append("Date: ").Append(DateTime.UtcNow.ToString("r", CultureInfo.InvariantCulture)).Append("\r\n");
            sb.Append("Server: LanDrop/1.0\r\n");
            sb.Append("Connection: close\r\n");
            sb.Append("X-Content-Type-Options: nosniff\r\n");
            if (!res.Headers.ContainsKey("Cache-Control")) sb.Append("Cache-Control: no-store\r\n");
            if (!string.IsNullOrEmpty(res.ContentType))
                sb.Append("Content-Type: ").Append(res.ContentType).Append("\r\n");
            foreach (KeyValuePair<string, string> header in res.Headers)
            {
                if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                sb.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
            }
            sb.Append("Content-Length: ").Append(contentLength.ToString(CultureInfo.InvariantCulture)).Append("\r\n\r\n");

            byte[] head = Encoding.ASCII.GetBytes(sb.ToString());
            try
            {
                stream.Write(head, 0, head.Length);
                if (!res.HeadOnly && res.Status != 204 && res.Status != 304)
                {
                    if (file != null)
                    {
                        FsUtil.CopyStream(file, stream, contentLength, null, null);
                    }
                    else if (body != null && body.Length > 0)
                    {
                        stream.Write(body, 0, body.Length);
                    }
                }
                stream.Flush();
            }
            finally
            {
                if (file != null) file.Dispose();
            }
        }

        private static string StatusText(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 204: return "No Content";
                case 206: return "Partial Content";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 409: return "Conflict";
                case 411: return "Length Required";
                case 413: return "Payload Too Large";
                case 416: return "Range Not Satisfiable";
                case 423: return "Locked";
                case 429: return "Too Many Requests";
                case 500: return "Internal Server Error";
                case 502: return "Bad Gateway";
                case 507: return "Insufficient Storage";
                default: return "Error";
            }
        }
    }

    public class PrefixStream : Stream
    {
        private readonly byte[] _prefix;
        private int _position;
        private readonly Stream _inner;

        public PrefixStream(byte[] prefix, Stream inner)
        {
            _prefix = prefix ?? new byte[0];
            _inner = inner;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position < _prefix.Length)
            {
                int n = Math.Min(count, _prefix.Length - _position);
                Buffer.BlockCopy(_prefix, _position, buffer, offset, n);
                _position += n;
                return n;
            }
            return _inner.Read(buffer, offset, count);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            _inner.Write(buffer, offset, count);
        }

        public override void Flush() { _inner.Flush(); }
        public override bool CanRead { get { return true; } }
        public override bool CanSeek { get { return false; } }
        public override bool CanWrite { get { return true; } }
        public override long Length { get { throw new NotSupportedException(); } }
        public override long Position
        {
            get { throw new NotSupportedException(); }
            set { throw new NotSupportedException(); }
        }
        public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
        public override void SetLength(long value) { throw new NotSupportedException(); }
    }
}
