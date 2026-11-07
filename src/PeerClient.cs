using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;

namespace LanDrop
{
    public class PeerException : Exception
    {
        public int Status { get; private set; }

        public PeerException(int status, string message) : base(message)
        {
            Status = status;
        }

        public PeerException(int status, string message, Exception inner) : base(message, inner)
        {
            Status = status;
        }
    }

    public static class PeerClient
    {
        static PeerClient()
        {
            ServicePointManager.Expect100Continue = false;
            ServicePointManager.DefaultConnectionLimit = 64;
        }

        public static Dictionary<string, object> Hello(string host, int port)
        {
            HttpWebRequest request = CreateRequest(NetUtil.BuildUrl(host, port, "/api/hello"), "GET", null);
            return ReadJson(request);
        }

        public static Dictionary<string, object> Pair(string host, int port, string code)
        {
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["code"] = code;
            HttpWebRequest request = CreateRequest(NetUtil.BuildUrl(host, port, "/api/pair"), "POST", null);
            WriteJsonBody(request, body);
            return ReadJson(request);
        }

        public static Dictionary<string, object> List(string host, int port, string key, string path, bool hidden)
        {
            string url = NetUtil.BuildUrl(host, port, "/api/fs/list?path=" + TextUtil.UrlEncode(path) + "&hidden=" + (hidden ? "1" : "0"));
            HttpWebRequest request = CreateRequest(url, "GET", key);
            return ReadJson(request);
        }

        public static Dictionary<string, object> Tree(string host, int port, string key, string path)
        {
            string url = NetUtil.BuildUrl(host, port, "/api/fs/tree?path=" + TextUtil.UrlEncode(path));
            HttpWebRequest request = CreateRequest(url, "GET", key);
            return ReadJson(request);
        }

        public static Dictionary<string, object> Mkdir(string host, int port, string key, string path, bool unique)
        {
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["path"] = path;
            body["unique"] = unique;
            HttpWebRequest request = CreateRequest(NetUtil.BuildUrl(host, port, "/api/fs/mkdir"), "POST", key);
            WriteJsonBody(request, body);
            return ReadJson(request);
        }

        public static Dictionary<string, object> Rename(string host, int port, string key, string path, string newName)
        {
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["path"] = path;
            body["newName"] = newName;
            HttpWebRequest request = CreateRequest(NetUtil.BuildUrl(host, port, "/api/fs/rename"), "POST", key);
            WriteJsonBody(request, body);
            return ReadJson(request);
        }

        public static Dictionary<string, object> Delete(string host, int port, string key, List<string> paths)
        {
            Dictionary<string, object> body = new Dictionary<string, object>();
            body["paths"] = paths;
            HttpWebRequest request = CreateRequest(NetUtil.BuildUrl(host, port, "/api/fs/delete"), "POST", key);
            WriteJsonBody(request, body);
            return ReadJson(request);
        }

        public static Dictionary<string, object> Download(string host, int port, string key, string remotePath,
            string localFile, Action<long> progress, Func<bool> canceled)
        {
            string url = NetUtil.BuildUrl(host, port, "/api/fs/download?path=" + TextUtil.UrlEncode(remotePath) + "&key=" + TextUtil.UrlEncode(key));
            HttpWebRequest request = CreateRequest(url, "GET", key);
            request.Timeout = 30000;
            request.ReadWriteTimeout = 10 * 60 * 1000;
            string temp = Path.Combine(Path.GetDirectoryName(localFile), ".landrop-" + Guid.NewGuid().ToString("N") + ".part");
            try
            {
                using (HttpWebResponse response = GetResponse(request))
                using (Stream input = response.GetResponseStream())
                using (FileStream output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024))
                {
                    FsUtil.CopyStream(input, output, response.ContentLength, progress, canceled);
                }
                if (File.Exists(localFile)) File.Delete(localFile);
                File.Move(temp, localFile);
                Dictionary<string, object> result = new Dictionary<string, object>();
                result["ok"] = true;
                result["path"] = localFile;
                result["size"] = new FileInfo(localFile).Length;
                return result;
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                throw;
            }
        }

        public static Dictionary<string, object> Upload(string host, int port, string key, string remoteDir,
            string fileName, string localFile, bool overwrite, Action<long> progress, Func<bool> canceled)
        {
            string url = NetUtil.BuildUrl(host, port, "/api/fs/upload?dir=" + TextUtil.UrlEncode(remoteDir)
                + "&name=" + TextUtil.UrlEncode(fileName) + "&overwrite=" + (overwrite ? "1" : "0"));
            HttpWebRequest request = CreateRequest(url, "PUT", key);
            request.Timeout = 30000;
            request.ReadWriteTimeout = 10 * 60 * 1000;
            request.AllowWriteStreamBuffering = false;
            request.ContentType = "application/octet-stream";
            FileInfo fi = new FileInfo(localFile);
            request.ContentLength = fi.Length;
            try
            {
                using (Stream output = request.GetRequestStream())
                using (FileStream input = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    FsUtil.CopyStream(input, output, fi.Length, progress, canceled);
                }
                return ReadJson(request);
            }
            catch (OperationCanceledException) { throw; }
            catch (WebException ex)
            {
                throw CreateWebException(ex, "上传失败");
            }
            catch (UnauthorizedAccessException ex)
            {
                throw new PeerException(0, "传输中断：源文件无法读取，或目标文件夹没有写入权限。", ex);
            }
            catch (IOException ex)
            {
                throw new PeerException(0, "传输中断：请检查源文件是否可读、目标文件夹是否可写，以及磁盘空间是否充足。", ex);
            }
        }

        private static HttpWebRequest CreateRequest(string url, string method, string key)
        {
            HttpWebRequest request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.Proxy = null;
            request.KeepAlive = false;
            request.AllowAutoRedirect = false;
            request.UserAgent = "LanDrop/1.0";
            request.Accept = "application/json";
            request.Timeout = 15000;
            request.ReadWriteTimeout = 60000;
            request.ServicePoint.Expect100Continue = false;
            request.AutomaticDecompression = DecompressionMethods.None;
            if (!string.IsNullOrEmpty(key)) request.Headers["X-LanDrop-Key"] = key;
            return request;
        }

        private static void WriteJsonBody(HttpWebRequest request, object body)
        {
            byte[] data = Encoding.UTF8.GetBytes(Json.Serialize(body));
            request.ContentType = "application/json; charset=utf-8";
            request.ContentLength = data.Length;
            using (Stream stream = request.GetRequestStream())
            {
                stream.Write(data, 0, data.Length);
            }
        }

        private static Dictionary<string, object> ReadJson(HttpWebRequest request)
        {
            try
            {
                using (HttpWebResponse response = GetResponse(request))
                {
                    return ReadJsonStream(response);
                }
            }
            catch (PeerException) { throw; }
            catch (WebException ex)
            {
                throw CreateWebException(ex, "连接失败");
            }
        }

        private static HttpWebResponse GetResponse(HttpWebRequest request)
        {
            try
            {
                return (HttpWebResponse)request.GetResponse();
            }
            catch (WebException ex)
            {
                HttpWebResponse response = ex.Response as HttpWebResponse;
                if (response != null)
                {
                    int status = (int)response.StatusCode;
                    string message = null;
                    try
                    {
                        using (response)
                        {
                            Dictionary<string, object> data = ReadJsonStream(response);
                            message = Json.GetString(data, "error", null);
                        }
                    }
                    catch { }
                    if (string.IsNullOrEmpty(message)) message = "请求失败（HTTP " + status.ToString() + "）";
                    throw new PeerException(status, message, ex);
                }
                throw new PeerException(0, "无法连接到对方电脑，请确认 LanDrop 正在运行且防火墙已放行。", ex);
            }
        }

        private static Dictionary<string, object> ReadJsonStream(HttpWebResponse response)
        {
            using (Stream stream = response.GetResponseStream())
            using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
            {
                string text = reader.ReadToEnd();
                if (string.IsNullOrWhiteSpace(text)) return new Dictionary<string, object>();
                return Json.ParseObject(text);
            }
        }

        private static PeerException CreateWebException(WebException ex, string prefix)
        {
            HttpWebResponse response = ex.Response as HttpWebResponse;
            if (response != null)
            {
                int status = (int)response.StatusCode;
                string message = null;
                try
                {
                    using (response)
                    {
                        Dictionary<string, object> data = ReadJsonStream(response);
                        message = Json.GetString(data, "error", null);
                    }
                }
                catch { }
                if (string.IsNullOrEmpty(message)) message = prefix + "（HTTP " + status.ToString() + "）";
                return new PeerException(status, message, ex);
            }
            if (ex.Status == WebExceptionStatus.Timeout)
                return new PeerException(0, "连接超时，请检查对方电脑是否在线。", ex);
            return new PeerException(0, "无法连接到对方电脑，请确认 LanDrop 正在运行且在同一 Wi-Fi 下。", ex);
        }
    }
}
