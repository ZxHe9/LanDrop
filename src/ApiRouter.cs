using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace LanDrop
{
    public static class ApiRouter
    {
        private static readonly object PairLock = new object();
        private static readonly Dictionary<string, List<DateTime>> PairAttempts =
            new Dictionary<string, List<DateTime>>(StringComparer.Ordinal);

        public static LanDropApp App { get; set; }

        public static void Handle(HttpRequest req, HttpResponse res)
        {
            try
            {
                if (req.Path == "/" || req.Path == "/index.html" || req.Path == "/app.js"
                    || req.Path == "/style.css" || req.Path == "/logo.png" || req.Path == "/favicon.ico")
                {
                    ServeAsset(req, res);
                    return;
                }

                if (req.Path == "/api/hello")
                {
                    HandleHello(req, res);
                    return;
                }

                if (req.Path == "/api/pair")
                {
                    HandlePair(req, res);
                    return;
                }

                if (!req.Path.StartsWith("/api/", StringComparison.Ordinal))
                {
                    res.Error(404, "页面不存在");
                    return;
                }

                if (IsLocalOnly(req.Path) && !RequireLocal(req, res)) return;
                if (!RequireAuth(req, res)) return;

                switch (req.Path)
                {
                    case "/api/state": HandleState(req, res); break;
                    case "/api/fs/list": HandleList(req, res); break;
                    case "/api/fs/tree": HandleTree(req, res); break;
                    case "/api/fs/download": HandleDownload(req, res); break;
                    case "/api/fs/upload": HandleUpload(req, res); break;
                    case "/api/fs/mkdir": HandleMkdir(req, res); break;
                    case "/api/fs/rename": HandleRename(req, res); break;
                    case "/api/fs/delete": HandleDelete(req, res); break;
                    case "/api/peers": HandlePeers(req, res); break;
                    case "/api/discover": HandleDiscover(req, res); break;
                    case "/api/peers/add": HandlePeerAdd(req, res); break;
                    case "/api/peers/remove": HandlePeerRemove(req, res); break;
                    case "/api/config/name": HandleConfigName(req, res); break;
                    case "/api/config/autostart": HandleAutostart(req, res); break;
                    case "/api/config/open-folder": HandleOpenFolder(req, res); break;
                    case "/api/transfers": HandleTransfers(req, res); break;
                    case "/api/transfer/push": HandleTransferAdd(req, res, true); break;
                    case "/api/transfer/pull": HandleTransferAdd(req, res, false); break;
                    case "/api/transfer/cancel": HandleTransferCancel(req, res); break;
                    case "/api/transfer/clear": HandleTransferClear(req, res); break;
                    case "/api/remote/list": HandleRemoteList(req, res); break;
                    case "/api/remote/mkdir": HandleRemoteMkdir(req, res); break;
                    case "/api/remote/rename": HandleRemoteRename(req, res); break;
                    case "/api/remote/delete": HandleRemoteDelete(req, res); break;
                    case "/api/reveal": HandleReveal(req, res); break;
                    case "/api/open": HandleOpen(req, res); break;
                    default: res.Error(404, "接口不存在"); break;
                }
            }
            catch (PeerException ex)
            {
                res.Error(ex.Status == 401 ? 401 : 502, ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                Log.Warn("没有权限 " + req.Path + " :: " + ex.Message);
                res.Error(403, "没有权限访问：" + ex.Message);
            }
            catch (DirectoryNotFoundException ex)
            {
                res.Error(404, "文件夹不存在：" + ex.Message);
            }
            catch (FileNotFoundException ex)
            {
                res.Error(404, "文件不存在：" + ex.Message);
            }
            catch (PathTooLongException)
            {
                res.Error(400, "路径过长，系统无法处理");
            }
            catch (ArgumentException ex)
            {
                res.Error(400, ex.Message);
            }
            catch (InvalidDataException ex)
            {
                res.Error(400, ex.Message);
            }
            catch (IOException ex)
            {
                Log.Warn("文件操作失败: " + ex.Message);
                res.Error(500, "文件操作失败：" + ex.Message);
            }
            catch (Exception ex)
            {
                Log.Error("接口异常 " + req.Path, ex);
                res.Error(500, "操作失败：" + ex.Message);
            }
        }

        private static bool IsLocalOnly(string path)
        {
            if (path == "/api/state" || path == "/api/peers" || path == "/api/discover"
                || path == "/api/peers/add" || path == "/api/peers/remove"
                || path == "/api/config/name" || path == "/api/config/autostart"
                || path == "/api/config/open-folder" || path == "/api/transfers"
                || path == "/api/reveal" || path == "/api/open") return true;
            if (path.StartsWith("/api/transfer/", StringComparison.Ordinal)) return true;
            if (path.StartsWith("/api/remote/", StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool RequireLocal(HttpRequest req, HttpResponse res)
        {
            if (NetUtil.IsLoopback(req.RemoteAddress)) return true;
            res.Error(403, "此页面只能在本机打开：http://127.0.0.1:" + App.Port.ToString() + "/");
            return false;
        }

        private static bool RequireAuth(HttpRequest req, HttpResponse res)
        {
            if (NetUtil.IsLoopback(req.RemoteAddress)) return true;
            string key = req.Header("X-LanDrop-Key");
            if (string.IsNullOrEmpty(key)) key = req.QueryValue("key");
            if (TextUtil.FixedEquals(key, App.Config.Key)) return true;
            res.Error(401, "连接码无效，请重新配对");
            return false;
        }

        private static void ServeAsset(HttpRequest req, HttpResponse res)
        {
            if (!NetUtil.IsLoopback(req.RemoteAddress))
            {
                res.Status = 403;
                res.Text("<!doctype html><html><meta charset=\"utf-8\"><title>LanDrop</title>"
                    + "<body style=\"font-family:Segoe UI,Microsoft YaHei;background:#0b1220;color:#e7eefc;padding:48px\">"
                    + "<h1>请在本机打开 LanDrop</h1><p>传输界面只允许从本机访问，请双击托盘图标打开。</p></body></html>",
                    "text/html; charset=utf-8");
                return;
            }
            if (req.Method != "GET" && req.Method != "HEAD")
            {
                res.Error(405, "不支持的请求方法");
                return;
            }

            string contentType;
            byte[] data = WebAssets.Get(req.Path, out contentType);
            if (data == null)
            {
                res.Error(404, "页面资源不存在");
                return;
            }
            res.BodyBytes = data;
            res.ContentType = contentType;
            res.Headers["Cache-Control"] = "no-cache";
            res.Headers["Referrer-Policy"] = "no-referrer";
            res.Headers["X-Frame-Options"] = "DENY";
            if (req.Path == "/" || req.Path == "/index.html")
            {
                res.Headers["Content-Security-Policy"] =
                    "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; connect-src 'self'; font-src 'self'; base-uri 'none'; form-action 'none'";
                res.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            }
        }

        private static void HandleHello(HttpRequest req, HttpResponse res)
        {
            Dictionary<string, object> data = new Dictionary<string, object>();
            data["ok"] = true;
            data["app"] = "LanDrop";
            data["version"] = LanDropApp.Version;
            data["id"] = App.Config.Id;
            data["name"] = App.Config.Name;
            data["port"] = App.Port;
            data["home"] = FsUtil.GetHomePath();
            data["os"] = Environment.OSVersion.VersionString;
            res.Json(data);
        }

        private static void HandlePair(HttpRequest req, HttpResponse res)
        {
            if (req.Method != "POST") { res.Error(405, "不支持的请求方法"); return; }
            string ip = req.RemoteAddress ?? "";
            if (IsPairRateLimited(ip))
            {
                res.Error(429, "尝试次数过多，请 5 分钟后再试");
                return;
            }
            Dictionary<string, object> body = req.ReadJson(8192);
            string code = Json.GetString(body, "code", "");
            bool ok = TextUtil.FixedEquals(TextUtil.NormalizeCode(code), TextUtil.NormalizeCode(App.Config.Code));
            if (!ok)
            {
                Thread.Sleep(300);
                res.Error(401, "连接码不正确");
                return;
            }
            lock (PairLock) { PairAttempts.Remove(ip); }
            Dictionary<string, object> data = new Dictionary<string, object>();
            data["ok"] = true;
            data["key"] = App.Config.Key;
            data["id"] = App.Config.Id;
            data["name"] = App.Config.Name;
            data["port"] = App.Port;
            data["home"] = FsUtil.GetHomePath();
            data["version"] = LanDropApp.Version;
            res.Json(data);
        }

        private static bool IsPairRateLimited(string ip)
        {
            lock (PairLock)
            {
                DateTime cutoff = DateTime.UtcNow.AddMinutes(-5);
                List<DateTime> list;
                if (!PairAttempts.TryGetValue(ip, out list)) { list = new List<DateTime>(); PairAttempts[ip] = list; }
                for (int i = list.Count - 1; i >= 0; i--) if (list[i] < cutoff) list.RemoveAt(i);
                if (list.Count >= 10) return true;
                list.Add(DateTime.UtcNow);
                return false;
            }
        }

        private static void HandleState(HttpRequest req, HttpResponse res)
        {
            Dictionary<string, object> data = new Dictionary<string, object>();
            data["ok"] = true;
            Dictionary<string, object> device = new Dictionary<string, object>();
            device["id"] = App.Config.Id;
            device["name"] = App.Config.Name;
            device["code"] = App.Config.Code;
            device["port"] = App.Port;
            device["version"] = LanDropApp.Version;
            device["addresses"] = App.GetLocalUrls();
            device["localUrl"] = App.LocalUrl;
            device["autostart"] = Autostart.IsEnabled();
            device["home"] = FsUtil.GetHomePath();
            device["dataDir"] = App.DataDir;
            data["device"] = device;
            string last = App.Config.LastLocalPath;
            if (string.IsNullOrEmpty(last) || (!Directory.Exists(last) && !File.Exists(last))) last = FsUtil.GetHomePath();
            data["localPath"] = last;
            data["drives"] = GetDrives();
            data["peers"] = BuildPeerViews(true);
            res.Json(data);
        }

        private static void HandleList(HttpRequest req, HttpResponse res)
        {
            string path = req.QueryValue("path");
            bool hidden = req.QueryValue("hidden") == "1";
            Dictionary<string, object> data = ListDirectory(path, hidden);
            data["ok"] = true;
            res.Json(data);
        }

        private static Dictionary<string, object> ListDirectory(string path, bool includeHidden)
        {
            Dictionary<string, object> data = new Dictionary<string, object>();
            List<Dictionary<string, object>> entries = new List<Dictionary<string, object>>();
            if (string.IsNullOrWhiteSpace(path))
            {
                foreach (Dictionary<string, object> drive in GetDrives())
                {
                    drive["dir"] = true;
                    drive["drive"] = true;
                    drive["path"] = drive["name"];
                    drive["ext"] = "";
                    drive["hidden"] = false;
                    entries.Add(drive);
                }
                data["path"] = "";
                data["name"] = "此电脑";
                data["parent"] = null;
                data["entries"] = entries;
                data["drives"] = GetDrives();
                return data;
            }

            string full = FsUtil.NormalizePath(path);
            if (File.Exists(full))
            {
                Dictionary<string, object> single = EntryToDict(new FileInfo(full), full, false);
                entries.Add(single);
                data["path"] = full;
                data["name"] = Path.GetFileName(full);
                data["parent"] = Path.GetDirectoryName(full);
                data["entries"] = entries;
                data["drives"] = GetDrives();
                return data;
            }

            DirectoryInfo dir = new DirectoryInfo(full);
            if (!dir.Exists) throw new DirectoryNotFoundException(full);
            FileSystemInfo[] items;
            try { items = dir.GetFileSystemInfos(); }
            catch (UnauthorizedAccessException) { throw; }

            foreach (FileSystemInfo item in items)
            {
                try
                {
                    bool isDir = (item.Attributes & FileAttributes.Directory) != 0;
                    bool isHidden = FsUtil.IsHidden(item.FullName, item.Attributes);
                    if (!includeHidden && isHidden) continue;
                    entries.Add(EntryToDict(item, item.FullName, isDir));
                }
                catch { }
            }

            entries.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b)
            {
                bool da = Convert.ToBoolean(a["dir"]);
                bool db = Convert.ToBoolean(b["dir"]);
                if (da != db) return da ? -1 : 1;
                return string.Compare(Convert.ToString(a["name"]), Convert.ToString(b["name"]),
                    StringComparison.CurrentCultureIgnoreCase);
            });

            data["path"] = full;
            data["name"] = string.IsNullOrEmpty(Path.GetFileName(full.TrimEnd('\\'))) ? full : Path.GetFileName(full.TrimEnd('\\'));
            data["parent"] = dir.Parent == null ? "" : dir.Parent.FullName;
            data["entries"] = entries;
            data["drives"] = GetDrives();
            return data;
        }

        private static Dictionary<string, object> EntryToDict(FileSystemInfo item, string full, bool isDir)
        {
            Dictionary<string, object> entry = new Dictionary<string, object>();
            entry["name"] = item.Name;
            entry["path"] = full;
            entry["dir"] = isDir;
            entry["drive"] = false;
            entry["ext"] = isDir ? "" : Path.GetExtension(item.Name).TrimStart('.').ToLowerInvariant();
            entry["hidden"] = FsUtil.IsHidden(full, item.Attributes);
            entry["modified"] = FsUtil.Iso(item.LastWriteTime);
            long size = 0;
            if (!isDir)
            {
                FileInfo fi = item as FileInfo;
                if (fi != null) size = fi.Length;
            }
            entry["size"] = size;
            return entry;
        }

        private static List<Dictionary<string, object>> GetDrives()
        {
            List<Dictionary<string, object>> drives = new List<Dictionary<string, object>>();
            try
            {
                foreach (DriveInfo drive in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (!drive.IsReady) continue;
                        Dictionary<string, object> item = new Dictionary<string, object>();
                        item["name"] = drive.Name;
                        item["label"] = string.IsNullOrEmpty(drive.VolumeLabel)
                            ? (drive.DriveType == DriveType.Fixed ? "本地磁盘" : drive.DriveType.ToString())
                            : drive.VolumeLabel;
                        item["size"] = drive.TotalSize;
                        item["free"] = drive.TotalFreeSpace;
                        item["driveType"] = drive.DriveType.ToString();
                        drives.Add(item);
                    }
                    catch { }
                }
            }
            catch { }
            return drives;
        }

        private static void HandleTree(HttpRequest req, HttpResponse res)
        {
            string path = req.QueryValue("path");
            string full = FsUtil.NormalizePath(path);
            Dictionary<string, object> data = new Dictionary<string, object>();
            if (File.Exists(full))
            {
                FileInfo fi = new FileInfo(full);
                data["ok"] = true;
                data["path"] = full;
                data["name"] = fi.Name;
                data["dir"] = false;
                data["size"] = fi.Length;
                data["modified"] = FsUtil.Iso(fi.LastWriteTime);
                data["entries"] = new List<object>();
                res.Json(data);
                return;
            }
            DirectoryInfo dir = new DirectoryInfo(full);
            if (!dir.Exists) throw new DirectoryNotFoundException(full);
            List<FsEntry> entries = FsUtil.Walk(full);
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>(entries.Count);
            long total = 0;
            foreach (FsEntry entry in entries)
            {
                Dictionary<string, object> item = new Dictionary<string, object>();
                item["rel"] = entry.Rel;
                item["dir"] = entry.IsDir;
                item["size"] = entry.Size;
                item["hidden"] = entry.Hidden;
                item["modified"] = FsUtil.Iso(entry.Modified);
                list.Add(item);
                if (!entry.IsDir) total += entry.Size;
            }
            data["ok"] = true;
            data["path"] = full;
            data["name"] = string.IsNullOrEmpty(dir.Name) ? full : dir.Name;
            data["dir"] = true;
            data["size"] = total;
            data["modified"] = FsUtil.Iso(dir.LastWriteTime);
            data["entries"] = list;
            res.Json(data);
        }

        private static void HandleDownload(HttpRequest req, HttpResponse res)
        {
            string path = req.QueryValue("path");
            string full = FsUtil.NormalizePath(path);
            if (Directory.Exists(full)) throw new InvalidOperationException("不能下载文件夹");
            FileInfo fi = new FileInfo(full);
            if (!fi.Exists) throw new FileNotFoundException(full);
            string name = fi.Name;
            string ascii = ToAsciiFileName(name);
            res.FilePath = full;
            res.ContentType = TextUtil.ContentType(full);
            res.Headers["Content-Disposition"] = "attachment; filename=\"" + ascii + "\"; filename*=UTF-8''" + Uri.EscapeDataString(name);
        }

        private static string ToAsciiFileName(string name)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in name)
            {
                if (c >= 32 && c < 127 && c != '"' && c != '\\') sb.Append(c);
                else sb.Append('_');
            }
            if (sb.Length == 0) sb.Append("download");
            return sb.ToString();
        }

        private static void HandleUpload(HttpRequest req, HttpResponse res)
        {
            if (req.Method != "PUT") { res.Error(405, "不支持的请求方法"); return; }
            if (req.ContentLength <= 0 && req.Header("Content-Length") == null)
            {
                res.Error(411, "缺少 Content-Length");
                return;
            }
            string dir = req.QueryValue("dir");
            string name = req.QueryValue("name");
            if (string.IsNullOrWhiteSpace(dir)) throw new ArgumentException("目标文件夹不能为空");
            name = FsUtil.SafeName(name);
            dir = FsUtil.NormalizePath(dir);
            Directory.CreateDirectory(dir);
            string target = Path.Combine(dir, name);
            if (Directory.Exists(target)) throw new IOException("同名文件夹已存在：" + name);
            bool overwrite = req.QueryValue("overwrite") == "1";
            if (File.Exists(target))
            {
                if (!overwrite) target = FsUtil.UniqueFile(dir, name);
                else File.SetAttributes(target, FileAttributes.Normal);
            }

            string temp = Path.Combine(dir, ".landrop-" + Guid.NewGuid().ToString("N") + ".part");
            try
            {
                using (FileStream output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 256 * 1024, FileOptions.SequentialScan))
                {
                    req.CopyBodyTo(output, null);
                    if (output.Length != req.ContentLength)
                        throw new IOException("上传内容不完整");
                }
                if (File.Exists(target))
                {
                    try { File.Replace(temp, target, null); }
                    catch { File.Delete(target); File.Move(temp, target); }
                }
                else
                {
                    File.Move(temp, target);
                }
                Dictionary<string, object> data = new Dictionary<string, object>();
                data["ok"] = true;
                data["path"] = target;
                data["name"] = Path.GetFileName(target);
                data["size"] = new FileInfo(target).Length;
                res.Json(data);
            }
            catch
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                throw;
            }
        }

        private static void HandleMkdir(HttpRequest req, HttpResponse res)
        {
            if (req.Method != "POST") { res.Error(405, "不支持的请求方法"); return; }
            Dictionary<string, object> body = req.ReadJson(1024 * 1024);
            string path = Json.GetString(body, "path", "");
            bool unique = Json.GetBool(body, "unique", false);
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("路径不能为空");
            string full = FsUtil.NormalizePath(path);
            string parent = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(parent)) throw new ArgumentException("不能创建磁盘根目录");
            string name = Path.GetFileName(full.TrimEnd('\\'));
            if (unique && (Directory.Exists(full) || File.Exists(full)))
            {
                full = FsUtil.UniqueDir(parent, name);
            }
            Directory.CreateDirectory(full);
            Dictionary<string, object> data = new Dictionary<string, object>();
            data["ok"] = true;
            data["path"] = full;
            data["name"] = Path.GetFileName(full);
            res.Json(data);
        }

        private static void HandleRename(HttpRequest req, HttpResponse res)
        {
            if (req.Method != "POST") { res.Error(405, "不支持的请求方法"); return; }
            Dictionary<string, object> body = req.ReadJson(1024 * 1024);
            string path = Json.GetString(body, "path", "");
            string newName = FsUtil.SafeName(Json.GetString(body, "newName", ""));
            string full = FsUtil.NormalizePath(path);
            string parent = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(parent)) throw new ArgumentException("不能重命名磁盘根目录");
            string target = Path.Combine(parent, newName);
            if (string.Equals(full, target, StringComparison.OrdinalIgnoreCase))
            {
                res.Json(Ok());
                return;
            }
            if (File.Exists(target) || Directory.Exists(target)) throw new IOException("同名项目已存在");
            if (Directory.Exists(full)) Directory.Move(full, target);
            else if (File.Exists(full)) File.Move(full, target);
            else throw new FileNotFoundException(full);
            Dictionary<string, object> data = Ok();
            data["path"] = target;
            data["name"] = newName;
            res.Json(data);
        }

        private static void HandleDelete(HttpRequest req, HttpResponse res)
        {
            if (req.Method != "POST") { res.Error(405, "不支持的请求方法"); return; }
            Dictionary<string, object> body = req.ReadJson(2 * 1024 * 1024);
            List<string> paths = ToStringList(Json.GetList(body, "paths"));
            if (paths.Count == 0) throw new ArgumentException("没有选择要删除的项目");
            if (paths.Count > 2000) throw new ArgumentException("一次删除的项目过多");
            int deleted = 0;
            List<string> errors = new List<string>();
            foreach (string path in paths)
            {
                try
                {
                    if (FsUtil.DeletePath(path)) deleted++;
                }
                catch (Exception ex)
                {
                    errors.Add(Path.GetFileName(path) + "：" + ex.Message);
                }
            }
            Dictionary<string, object> data = Ok();
            data["deleted"] = deleted;
            data["errors"] = errors;
            res.Json(data);
        }

        private static void HandlePeers(HttpRequest req, HttpResponse res)
        {
            if (req.QueryValue("scan") == "1" && App.Discovery != null) App.Discovery.ScanNow(true);
            Dictionary<string, object> data = Ok();
            data["peers"] = BuildPeerViews(true);
            res.Json(data);
        }

        private static void HandleDiscover(HttpRequest req, HttpResponse res)
        {
            if (App.Discovery != null) App.Discovery.ScanNow(true);
            Dictionary<string, object> data = Ok();
            data["peers"] = BuildPeerViews(true);
            res.Json(data);
        }

        private static List<Dictionary<string, object>> BuildPeerViews(bool includeDiscovered)
        {
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (PeerInfo peer in App.Store.GetPeers())
            {
                DiscoveredPeer found = App.Discovery == null ? null : App.Discovery.Find(peer.Id);
                if (found != null)
                {
                    if (!string.Equals(peer.Host, found.Host, StringComparison.Ordinal) || peer.Port != found.Port)
                    {
                        peer.Host = found.Host;
                        peer.Port = found.Port;
                        App.Store.TouchPeer(peer.Id, peer.Host, peer.Port);
                    }
                }
                Dictionary<string, object> item = new Dictionary<string, object>();
                item["id"] = peer.Id;
                item["name"] = peer.Name;
                item["host"] = peer.Host;
                item["port"] = peer.Port;
                item["online"] = found != null || App.IsPeerOnline(peer);
                item["paired"] = true;
                item["home"] = peer.Home;
                item["version"] = peer.Version;
                item["lastSeen"] = peer.LastSeen;
                result.Add(item);
                ids.Add(peer.Id);
            }

            if (includeDiscovered && App.Discovery != null)
            {
                foreach (DiscoveredPeer found in App.Discovery.GetPeers())
                {
                    if (ids.Contains(found.Id) || found.Id == App.Config.Id) continue;
                    Dictionary<string, object> item = new Dictionary<string, object>();
                    item["id"] = found.Id;
                    item["name"] = found.Name;
                    item["host"] = found.Host;
                    item["port"] = found.Port;
                    item["online"] = true;
                    item["paired"] = false;
                    item["home"] = "";
                    item["version"] = found.Version;
                    item["lastSeen"] = found.LastSeenUtc.ToString("o", CultureInfo.InvariantCulture);
                    result.Add(item);
                    ids.Add(found.Id);
                }
            }

            result.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b)
            {
                bool pa = Convert.ToBoolean(a["paired"]);
                bool pb = Convert.ToBoolean(b["paired"]);
                if (pa != pb) return pa ? -1 : 1;
                bool oa = Convert.ToBoolean(a["online"]);
                bool ob = Convert.ToBoolean(b["online"]);
                if (oa != ob) return oa ? -1 : 1;
                return string.Compare(Convert.ToString(a["name"]), Convert.ToString(b["name"]),
                    StringComparison.CurrentCultureIgnoreCase);
            });
            return result;
        }

        private static void HandlePeerAdd(HttpRequest req, HttpResponse res)
        {
            if (req.Method != "POST") { res.Error(405, "不支持的请求方法"); return; }
            Dictionary<string, object> body = req.ReadJson(65536);
            string host = NetUtil.NormalizeHost(Json.GetString(body, "host", ""));
            int port = (int)Json.GetLong(body, "port", 52180);
            string code = Json.GetString(body, "code", "");
            if (port <= 0 || port > 65535) throw new ArgumentException("端口不正确");
            if (string.IsNullOrWhiteSpace(code)) throw new ArgumentException("请输入连接码");

            Dictionary<string, object> paired = PeerClient.Pair(host, port, code);
            string id = Json.GetString(paired, "id", "");
            if (string.IsNullOrEmpty(id) || id == App.Config.Id) throw new InvalidOperationException("不能添加本机");
            string key = Json.GetString(paired, "key", "");
            if (string.IsNullOrEmpty(key)) throw new InvalidOperationException("对方没有返回连接凭据");

            PeerInfo peer = new PeerInfo();
            peer.Id = id;
            peer.Name = Json.GetString(paired, "name", "Windows 电脑");
            peer.Host = host;
            peer.Port = (int)Json.GetLong(paired, "port", port);
            peer.Key = key;
            peer.Home = Json.GetString(paired, "home", "");
            peer.Version = Json.GetString(paired, "version", "");
            App.Store.AddOrUpdatePeer(peer);

            Dictionary<string, object> data = Ok();
            data["peer"] = PeerToDict(peer, true);
            res.Json(data);
        }

        private static void HandlePeerRemove(HttpRequest req, HttpResponse res)
        {
            if (req.Method != "POST") { res.Error(405, "不支持的请求方法"); return; }
            Dictionary<string, object> body = req.ReadJson(65536);
            string id = Json.GetString(body, "id", "");
            if (string.IsNullOrEmpty(id)) throw new ArgumentException("缺少设备 ID");
            App.Store.RemovePeer(id);
            res.Json(Ok());
        }

        private static void HandleConfigName(HttpRequest req, HttpResponse res)
        {
            if (req.Method != "POST") { res.Error(405, "不支持的请求方法"); return; }
            Dictionary<string, object> body = req.ReadJson(65536);
            string name = Json.GetString(body, "name", "").Trim();
            if (name.Length == 0) throw new ArgumentException("设备名称不能为空");
            App.Store.SetName(name);
            Dictionary<string, object> data = Ok();
            data["name"] = App.Config.Name;
            res.Json(data);
        }

        private static void HandleAutostart(HttpRequest req, HttpResponse res)
        {
            if (req.Method != "POST") { res.Error(405, "不支持的请求方法"); return; }
            Dictionary<string, object> body = req.ReadJson(65536);
            bool enabled = Json.GetBool(body, "enabled", false);
            Autostart.Set(enabled, App.ExePath);
            Dictionary<string, object> data = Ok();
            data["autostart"] = Autostart.IsEnabled();
            res.Json(data);
        }

        private static void HandleOpenFolder(HttpRequest req, HttpResponse res)
        {
            App.OpenDataFolder();
            res.Json(Ok());
        }

        private static void HandleTransfers(HttpRequest req, HttpResponse res)
        {
            Dictionary<string, object> data = Ok();
            data["jobs"] = App.Transfers.Snapshot();
            res.Json(data);
        }

        private static void HandleTransferAdd(HttpRequest req, HttpResponse res, bool push)
        {
            if (req.Method != "POST") { res.Error(405, "不支持的请求方法"); return; }
            Dictionary<string, object> body = req.ReadJson(2 * 1024 * 1024);
            string peerId = Json.GetString(body, "peerId", "");
            string target = Json.GetString(body, "target", "");
            bool overwrite = Json.GetBool(body, "overwrite", false);
            List<string> paths = ToStringList(Json.GetList(body, "paths"));
            if (string.IsNullOrEmpty(peerId)) throw new ArgumentException("请先选择对方电脑");
            if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("目标文件夹不能为空");
            if (paths.Count == 0) throw new ArgumentException("请选择要传输的文件或文件夹");
            if (paths.Count > 500) throw new ArgumentException("一次最多传输 500 项");

            List<int> ids;
            if (push)
            {
                ids = App.Transfers.AddPush(peerId, paths, target, overwrite);
            }
            else
            {
                string localTarget = FsUtil.NormalizePath(target);
                if (!Directory.Exists(localTarget)) throw new DirectoryNotFoundException(localTarget);
                ids = App.Transfers.AddPull(peerId, paths, localTarget, overwrite);
            }
            Dictionary<string, object> data = Ok();
            data["ids"] = ids;
            data["count"] = ids.Count;
            res.Json(data);
        }

        private static void HandleTransferCancel(HttpRequest req, HttpResponse res)
        {
            if (req.Method != "POST") { res.Error(405, "不支持的请求方法"); return; }
            Dictionary<string, object> body = req.ReadJson(65536);
            int id = (int)Json.GetLong(body, "id", 0);
            if (id > 0) App.Transfers.Cancel(id);
            res.Json(Ok());
        }

        private static void HandleTransferClear(HttpRequest req, HttpResponse res)
        {
            App.Transfers.ClearFinished();
            res.Json(Ok());
        }

        private static void HandleRemoteList(HttpRequest req, HttpResponse res)
        {
            if (req.Method != "POST") { res.Error(405, "不支持的请求方法"); return; }
            Dictionary<string, object> body = req.ReadJson(1024 * 1024);
            PeerInfo peer = RequirePeer(Json.GetString(body, "peerId", ""));
            string path = Json.GetString(body, "path", "");
            bool hidden = Json.GetBool(body, "hidden", false);
            Dictionary<string, object> data = PeerClient.List(peer.Host, peer.Port, peer.Key, path, hidden);
            res.Json(data);
        }

        private static void HandleRemoteMkdir(HttpRequest req, HttpResponse res)
        {
            if (req.Method != "POST") { res.Error(405, "不支持的请求方法"); return; }
            Dictionary<string, object> body = req.ReadJson(1024 * 1024);
            PeerInfo peer = RequirePeer(Json.GetString(body, "peerId", ""));
            string path = Json.GetString(body, "path", "");
            res.Json(PeerClient.Mkdir(peer.Host, peer.Port, peer.Key, path, false));
        }

        private static void HandleRemoteRename(HttpRequest req, HttpResponse res)
        {
            if (req.Method != "POST") { res.Error(405, "不支持的请求方法"); return; }
            Dictionary<string, object> body = req.ReadJson(1024 * 1024);
            PeerInfo peer = RequirePeer(Json.GetString(body, "peerId", ""));
            string path = Json.GetString(body, "path", "");
            string newName = Json.GetString(body, "newName", "");
            res.Json(PeerClient.Rename(peer.Host, peer.Port, peer.Key, path, newName));
        }

        private static void HandleRemoteDelete(HttpRequest req, HttpResponse res)
        {
            if (req.Method != "POST") { res.Error(405, "不支持的请求方法"); return; }
            Dictionary<string, object> body = req.ReadJson(2 * 1024 * 1024);
            PeerInfo peer = RequirePeer(Json.GetString(body, "peerId", ""));
            List<string> paths = ToStringList(Json.GetList(body, "paths"));
            res.Json(PeerClient.Delete(peer.Host, peer.Port, peer.Key, paths));
        }

        private static void HandleReveal(HttpRequest req, HttpResponse res)
        {
            if (req.Method != "POST") { res.Error(405, "不支持的请求方法"); return; }
            Dictionary<string, object> body = req.ReadJson(65536);
            string path = FsUtil.NormalizePath(Json.GetString(body, "path", ""));
            if (Directory.Exists(path))
            {
                Process.Start("explorer.exe", "\"" + path + "\"");
            }
            else if (File.Exists(path))
            {
                Process.Start("explorer.exe", "/select,\"" + path + "\"");
            }
            else
            {
                throw new FileNotFoundException(path);
            }
            res.Json(Ok());
        }

        private static void HandleOpen(HttpRequest req, HttpResponse res)
        {
            if (req.Method != "POST") { res.Error(405, "不支持的请求方法"); return; }
            Dictionary<string, object> body = req.ReadJson(65536);
            string path = FsUtil.NormalizePath(Json.GetString(body, "path", ""));
            if (!File.Exists(path) && !Directory.Exists(path)) throw new FileNotFoundException(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            res.Json(Ok());
        }

        private static PeerInfo RequirePeer(string id)
        {
            PeerInfo peer = App.Store.FindPeer(id);
            if (peer == null) throw new ArgumentException("设备不存在，请重新添加");
            return peer;
        }

        private static Dictionary<string, object> PeerToDict(PeerInfo peer, bool online)
        {
            Dictionary<string, object> item = new Dictionary<string, object>();
            item["id"] = peer.Id;
            item["name"] = peer.Name;
            item["host"] = peer.Host;
            item["port"] = peer.Port;
            item["online"] = online;
            item["paired"] = true;
            item["home"] = peer.Home;
            item["version"] = peer.Version;
            item["lastSeen"] = peer.LastSeen;
            return item;
        }

        private static List<string> ToStringList(List<object> raw)
        {
            List<string> result = new List<string>();
            foreach (object item in raw)
            {
                string text = Convert.ToString(item, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(text)) result.Add(text);
            }
            return result;
        }

        private static Dictionary<string, object> Ok()
        {
            Dictionary<string, object> data = new Dictionary<string, object>();
            data["ok"] = true;
            return data;
        }
    }
}
