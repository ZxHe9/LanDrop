using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace LanDrop
{
    public static class Log
    {
        private static readonly object Sync = new object();
        private static string _path;

        public static void Init(string dataDir)
        {
            try
            {
                _path = Path.Combine(dataDir, "landrop.log");
            }
            catch { }
        }

        public static void Info(string message)
        {
            Write("INFO", message);
        }

        public static void Warn(string message)
        {
            Write("WARN", message);
        }

        public static void Error(string message)
        {
            Write("ERROR", message);
        }

        public static void Error(string message, Exception ex)
        {
            Write("ERROR", message + " :: " + (ex == null ? "" : ex.ToString()));
        }

        private static void Write(string level, string message)
        {
            if (string.IsNullOrEmpty(_path)) return;
            try
            {
                lock (Sync)
                {
                    FileInfo fi = new FileInfo(_path);
                    if (fi.Exists && fi.Length > 2 * 1024 * 1024)
                    {
                        string old = _path + ".1";
                        try { if (File.Exists(old)) File.Delete(old); } catch { }
                        try { File.Move(_path, old); } catch { }
                    }
                    File.AppendAllText(_path,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " [" + level + "] " + message + Environment.NewLine,
                        Encoding.UTF8);
                }
            }
            catch { }
        }
    }

    public static class Json
    {
        public static string Serialize(object value)
        {
            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            ser.RecursionLimit = 100;
            return ser.Serialize(value);
        }

        public static Dictionary<string, object> ParseObject(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, object>();
            JavaScriptSerializer ser = new JavaScriptSerializer();
            ser.MaxJsonLength = int.MaxValue;
            ser.RecursionLimit = 100;
            object value = ser.DeserializeObject(json);
            Dictionary<string, object> dict = value as Dictionary<string, object>;
            if (dict == null) throw new InvalidDataException("JSON 格式不正确");
            return dict;
        }

        public static string GetString(Dictionary<string, object> dict, string key, string fallback)
        {
            if (dict == null || !dict.ContainsKey(key) || dict[key] == null) return fallback;
            return Convert.ToString(dict[key], CultureInfo.InvariantCulture);
        }

        public static long GetLong(Dictionary<string, object> dict, string key, long fallback)
        {
            if (dict == null || !dict.ContainsKey(key) || dict[key] == null) return fallback;
            try { return Convert.ToInt64(dict[key], CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public static bool GetBool(Dictionary<string, object> dict, string key, bool fallback)
        {
            if (dict == null || !dict.ContainsKey(key) || dict[key] == null) return fallback;
            try { return Convert.ToBoolean(dict[key], CultureInfo.InvariantCulture); }
            catch { return fallback; }
        }

        public static Dictionary<string, object> GetDict(Dictionary<string, object> dict, string key)
        {
            if (dict == null || !dict.ContainsKey(key)) return null;
            return dict[key] as Dictionary<string, object>;
        }

        public static List<object> GetList(Dictionary<string, object> dict, string key)
        {
            List<object> result = new List<object>();
            if (dict == null || !dict.ContainsKey(key) || dict[key] == null) return result;
            object value = dict[key];
            object[] array = value as object[];
            if (array != null) { result.AddRange(array); return result; }
            System.Collections.ArrayList arrayList = value as System.Collections.ArrayList;
            if (arrayList != null)
            {
                foreach (object item in arrayList) result.Add(item);
                return result;
            }
            List<object> list = value as List<object>;
            if (list != null) return list;
            return result;
        }
    }

    public class FsEntry
    {
        public string Name;
        public string Rel;
        public string FullPath;
        public bool IsDir;
        public long Size;
        public DateTime Modified;
        public bool Hidden;
    }

    public static class FsUtil
    {
        public static string NormalizePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return "";
            path = path.Trim().Trim('"');
            if (path == "此电脑" || path == "This PC") return "";
            path = Environment.ExpandEnvironmentVariables(path);
            if (path.Length == 2 && path[1] == ':') path += "\\";
            return Path.GetFullPath(path);
        }

        public static string GetHomePath()
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop)) return desktop;
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        public static string SafeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("名称不能为空");
            name = name.Trim();
            if (name == "." || name == "..") throw new ArgumentException("名称不合法");
            if (name.IndexOf('\\') >= 0 || name.IndexOf('/') >= 0 || name.IndexOf(':') >= 0)
                throw new ArgumentException("名称中不能包含路径分隔符");
            foreach (char c in Path.GetInvalidFileNameChars())
            {
                if (name.IndexOf(c) >= 0) throw new ArgumentException("名称中包含非法字符");
            }
            if (name.Length > 240) throw new ArgumentException("名称过长");
            return name;
        }

        public static string UniqueFile(string dir, string fileName)
        {
            string path = Path.Combine(dir, fileName);
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
            string baseName = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            if (string.IsNullOrEmpty(baseName))
            {
                baseName = fileName;
                ext = "";
            }
            for (int i = 1; i < 10000; i++)
            {
                path = Path.Combine(dir, string.Format("{0} ({1}){2}", baseName, i, ext));
                if (!File.Exists(path) && !Directory.Exists(path)) return path;
            }
            return Path.Combine(dir, string.Format("{0} ({1}){2}", baseName, Guid.NewGuid().ToString("N").Substring(0, 6), ext));
        }

        public static string UniqueDir(string dir, string dirName)
        {
            string path = Path.Combine(dir, dirName);
            if (!Directory.Exists(path) && !File.Exists(path)) return path;
            for (int i = 1; i < 10000; i++)
            {
                path = Path.Combine(dir, string.Format("{0} ({1})", dirName, i));
                if (!Directory.Exists(path) && !File.Exists(path)) return path;
            }
            return Path.Combine(dir, dirName + " (" + Guid.NewGuid().ToString("N").Substring(0, 6) + ")");
        }

        public static bool IsHidden(string path, FileAttributes attrs)
        {
            if ((attrs & FileAttributes.Hidden) != 0) return true;
            if ((attrs & FileAttributes.System) != 0) return true;
            return false;
        }

        public static string Iso(DateTime dt)
        {
            return dt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
        }

        public static string FormatBytes(long bytes)
        {
            string[] units = new string[] { "B", "KB", "MB", "GB", "TB" };
            double value = bytes;
            int index = 0;
            while (value >= 1024 && index < units.Length - 1)
            {
                value /= 1024.0;
                index++;
            }
            if (index == 0) return ((long)value).ToString(CultureInfo.InvariantCulture) + " " + units[index];
            return value.ToString(index == 1 ? "0.0" : "0.00", CultureInfo.InvariantCulture) + " " + units[index];
        }

        public static List<FsEntry> Walk(string root)
        {
            List<FsEntry> result = new List<FsEntry>();
            Stack<string> stack = new Stack<string>();
            stack.Push(root);
            int guard = 0;
            while (stack.Count > 0)
            {
                guard++;
                if (guard > 200000) throw new IOException("目录项目过多，已停止扫描");
                string dir = stack.Pop();
                DirectoryInfo di = new DirectoryInfo(dir);
                FileSystemInfo[] items;
                try { items = di.GetFileSystemInfos(); }
                catch { continue; }
                foreach (FileSystemInfo item in items)
                {
                    try
                    {
                        if ((item.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        FsEntry entry = new FsEntry();
                        entry.FullPath = item.FullName;
                        entry.Name = item.Name;
                        entry.Rel = GetRelative(root, item.FullName);
                        entry.IsDir = (item.Attributes & FileAttributes.Directory) != 0;
                        entry.Hidden = IsHidden(item.FullName, item.Attributes);
                        if (entry.IsDir)
                        {
                            entry.Size = 0;
                            entry.Modified = item.LastWriteTime;
                            stack.Push(item.FullName);
                        }
                        else
                        {
                            FileInfo fi = item as FileInfo;
                            entry.Size = fi == null ? 0 : fi.Length;
                            entry.Modified = item.LastWriteTime;
                        }
                        result.Add(entry);
                    }
                    catch { }
                }
            }
            return result;
        }

        public static string GetRelative(string root, string full)
        {
            string rootFull = Path.GetFullPath(root);
            if (!rootFull.EndsWith("\\", StringComparison.Ordinal)) rootFull += "\\";
            string fullPath = Path.GetFullPath(full);
            if (fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                return fullPath.Substring(rootFull.Length);
            return Path.GetFileName(fullPath);
        }

        public static string GetDriveLabel(string root)
        {
            try
            {
                DriveInfo di = new DriveInfo(Path.GetPathRoot(root));
                if (string.IsNullOrEmpty(di.VolumeLabel)) return di.DriveType == DriveType.Fixed ? "本地磁盘" : "";
                return di.VolumeLabel;
            }
            catch { return ""; }
        }

        public static bool DeletePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return false;
            string full = Path.GetFullPath(path);
            string root = Path.GetPathRoot(full);
            if (string.Equals(full.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("不能删除磁盘根目录");
            if (Directory.Exists(full))
            {
                Directory.Delete(full, true);
                return true;
            }
            if (File.Exists(full))
            {
                File.SetAttributes(full, FileAttributes.Normal);
                File.Delete(full);
                return true;
            }
            return false;
        }

        public static void CopyStream(Stream input, Stream output, long total, Action<long> progress, Func<bool> canceled)
        {
            byte[] buffer = new byte[256 * 1024];
            long done = 0;
            while (true)
            {
                if (canceled != null && canceled()) throw new OperationCanceledException();
                int read = input.Read(buffer, 0, buffer.Length);
                if (read <= 0) break;
                output.Write(buffer, 0, read);
                done += read;
                if (progress != null) progress(done);
            }
            output.Flush();
        }
    }

    public static class TextUtil
    {
        private const string CodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        public static string RandomCode()
        {
            byte[] bytes = new byte[8];
            using (System.Security.Cryptography.RandomNumberGenerator rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < bytes.Length; i++)
            {
                if (i == 4) sb.Append('-');
                sb.Append(CodeAlphabet[bytes[i] % CodeAlphabet.Length]);
            }
            return sb.ToString();
        }

        public static string RandomKey()
        {
            byte[] bytes = new byte[24];
            using (System.Security.Cryptography.RandomNumberGenerator rng = System.Security.Cryptography.RandomNumberGenerator.Create())
            {
                rng.GetBytes(bytes);
            }
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < bytes.Length; i++) sb.Append(bytes[i].ToString("x2"));
            return sb.ToString();
        }

        public static string NormalizeCode(string code)
        {
            if (code == null) return "";
            return code.Replace("-", "").Replace(" ", "").Trim().ToUpperInvariant();
        }

        public static bool FixedEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            if (a.Length != b.Length) return false;
            int diff = 0;
            for (int i = 0; i < a.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        public static string UrlEncode(string value)
        {
            return Uri.EscapeDataString(value ?? "");
        }

        public static string HtmlEncode(string value)
        {
            return (value ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        }

        public static string ContentType(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".txt": case ".log": case ".ini": case ".cfg": return "text/plain; charset=utf-8";
                case ".html": case ".htm": return "text/html; charset=utf-8";
                case ".css": return "text/css; charset=utf-8";
                case ".js": return "application/javascript; charset=utf-8";
                case ".json": return "application/json; charset=utf-8";
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                case ".svg": return "image/svg+xml";
                case ".ico": return "image/x-icon";
                case ".pdf": return "application/pdf";
                case ".mp4": return "video/mp4";
                case ".mp3": return "audio/mpeg";
                case ".zip": return "application/zip";
                case ".7z": return "application/x-7z-compressed";
                case ".rar": return "application/vnd.rar";
                case ".doc": return "application/msword";
                case ".docx": return "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
                case ".xls": return "application/vnd.ms-excel";
                case ".xlsx": return "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
                case ".ppt": return "application/vnd.ms-powerpoint";
                case ".pptx": return "application/vnd.openxmlformats-officedocument.presentationml.presentation";
                default: return "application/octet-stream";
            }
        }
    }

    public static class NetUtil
    {
        public static List<string> GetLocalAddresses()
        {
            List<string> result = new List<string>();
            try
            {
                NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
                foreach (NetworkInterface nic in nics)
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    IPInterfaceProperties props = nic.GetIPProperties();
                    foreach (UnicastIPAddressInformation info in props.UnicastAddresses)
                    {
                        if (info.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        string ip = info.Address.ToString();
                        if (ip.StartsWith("127.") || ip.StartsWith("169.254.")) continue;
                        if (!result.Contains(ip)) result.Add(ip);
                    }
                }
            }
            catch { }
            result.Sort(delegate(string a, string b)
            {
                int sa = AddressScore(a);
                int sb = AddressScore(b);
                if (sa != sb) return sa.CompareTo(sb);
                return string.Compare(a, b, StringComparison.Ordinal);
            });
            return result;
        }

        private static int AddressScore(string ip)
        {
            try
            {
                string[] parts = ip.Split('.');
                if (parts.Length != 4) return 9;
                int a = int.Parse(parts[0]);
                int b = int.Parse(parts[1]);
                if (a == 192 && b == 168) return 0;
                if (a == 10) return 1;
                if (a == 172 && b >= 16 && b <= 31) return 2;
                if (a == 192) return 3;
                return 4;
            }
            catch { return 9; }
        }

        public static bool IsPrivateIPv4(string ip)
        {
            try
            {
                string[] parts = ip.Split('.');
                if (parts.Length != 4) return false;
                int a = int.Parse(parts[0]);
                int b = int.Parse(parts[1]);
                if (a == 10) return true;
                if (a == 192 && b == 168) return true;
                if (a == 172 && b >= 16 && b <= 31) return true;
                return false;
            }
            catch { return false; }
        }

        public static bool IsPortOpen(string host, int port, int timeoutMs)
        {
            IPAddress address;
            if (!IPAddress.TryParse(host, out address)) return false;
            TcpClient client = new TcpClient();
            try
            {
                IAsyncResult result = client.BeginConnect(address, port, null, null);
                if (!result.AsyncWaitHandle.WaitOne(timeoutMs)) return false;
                client.EndConnect(result);
                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        public static bool IsLoopback(string remote)
        {
            if (string.IsNullOrEmpty(remote)) return false;
            if (remote == "127.0.0.1" || remote == "::1") return true;
            if (remote.StartsWith("127.")) return true;
            if (remote.StartsWith("::ffff:127.")) return true;
            return false;
        }

        public static string NormalizeHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("IP 地址不能为空");
            host = host.Trim();
            if (host.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) host = host.Substring(7);
            if (host.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) host = host.Substring(8);
            int slash = host.IndexOf('/');
            if (slash >= 0) host = host.Substring(0, slash);
            if (host.IndexOf(':') >= 0 && host.IndexOf(']') < 0)
            {
                int colon = host.IndexOf(':');
                host = host.Substring(0, colon);
            }
            if (host.Length == 0) throw new ArgumentException("IP 地址不正确");
            IPAddress addr;
            if (!IPAddress.TryParse(host, out addr) && host.IndexOf(' ') >= 0)
                throw new ArgumentException("IP 地址不正确");
            return host;
        }

        public static string BuildUrl(string host, int port, string pathAndQuery)
        {
            return "http://" + host + ":" + port.ToString(CultureInfo.InvariantCulture) + pathAndQuery;
        }

        public static List<IPAddress> GetBroadcastAddresses()
        {
            List<IPAddress> result = new List<IPAddress>();
            try
            {
                result.Add(IPAddress.Broadcast);
                foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (UnicastIPAddressInformation info in nic.GetIPProperties().UnicastAddresses)
                    {
                        if (info.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                        byte[] ip = info.Address.GetAddressBytes();
                        byte[] mask = info.IPv4Mask.GetAddressBytes();
                        if (ip.Length != 4 || mask.Length != 4) continue;
                        byte[] bcast = new byte[4];
                        for (int i = 0; i < 4; i++) bcast[i] = (byte)(ip[i] | (byte)~mask[i]);
                        IPAddress addr = new IPAddress(bcast);
                        bool exists = false;
                        foreach (IPAddress item in result) if (item.Equals(addr)) { exists = true; break; }
                        if (!exists) result.Add(addr);
                    }
                }
            }
            catch { }
            return result;
        }
    }
}
