using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;

namespace LanDrop
{
    public class TransferJob
    {
        public int Id;
        public string Kind;
        public string Title;
        public string PeerId;
        public string PeerName;
        public string SourcePath;
        public string TargetPath;
        public bool Overwrite;
        public string Status;
        public string Message;
        public long TotalBytes;
        public long DoneBytes;
        public string CurrentFile;
        public DateTime CreatedUtc;
        public DateTime StartedUtc;
        public DateTime FinishedUtc;
        public bool CancelRequested;

        public TransferJob()
        {
            Status = "queued";
            Message = "";
            CurrentFile = "";
            CreatedUtc = DateTime.UtcNow;
        }
    }

    public class TransferManager : IDisposable
    {
        private readonly LanDropApp _app;
        private readonly object _sync = new object();
        private readonly List<TransferJob> _jobs = new List<TransferJob>();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private Thread _worker;
        private volatile bool _running;
        private int _nextId = 1;

        public TransferManager(LanDropApp app)
        {
            _app = app;
        }

        public event Action<TransferJob, bool> Finished;

        public void Start()
        {
            _running = true;
            _worker = new Thread(WorkerLoop);
            _worker.IsBackground = true;
            _worker.Name = "LanDrop Transfer";
            _worker.Start();
        }

        public void Stop()
        {
            _running = false;
            _signal.Set();
            try { if (_worker != null) _worker.Join(1500); } catch { }
        }

        public void Dispose()
        {
            Stop();
        }

        public List<int> AddPush(string peerId, List<string> paths, string remoteDir, bool overwrite)
        {
            PeerInfo peer = _app.Store.FindPeer(peerId);
            if (peer == null) throw new ArgumentException("设备不存在，请重新添加");
            List<int> ids = new List<int>();
            foreach (string source in paths)
            {
                if (!File.Exists(source) && !Directory.Exists(source))
                    throw new FileNotFoundException("本机文件不存在：" + source);
                TransferJob job = new TransferJob();
                job.Kind = "push";
                job.Title = Path.GetFileName(source.TrimEnd('\\'));
                job.PeerId = peer.Id;
                job.PeerName = peer.Name;
                job.SourcePath = source;
                job.TargetPath = remoteDir;
                job.Overwrite = overwrite;
                AddJob(job);
                ids.Add(job.Id);
            }
            return ids;
        }

        public List<int> AddPull(string peerId, List<string> remotePaths, string localDir, bool overwrite)
        {
            PeerInfo peer = _app.Store.FindPeer(peerId);
            if (peer == null) throw new ArgumentException("设备不存在，请重新添加");
            List<int> ids = new List<int>();
            foreach (string source in remotePaths)
            {
                TransferJob job = new TransferJob();
                job.Kind = "pull";
                job.Title = Path.GetFileName(source.TrimEnd('\\'));
                job.PeerId = peer.Id;
                job.PeerName = peer.Name;
                job.SourcePath = source;
                job.TargetPath = localDir;
                job.Overwrite = overwrite;
                AddJob(job);
                ids.Add(job.Id);
            }
            return ids;
        }

        private void AddJob(TransferJob job)
        {
            lock (_sync)
            {
                job.Id = _nextId++;
                _jobs.Add(job);
                if (_jobs.Count > 120)
                {
                    for (int i = _jobs.Count - 1; i >= 0 && _jobs.Count > 80; i--)
                    {
                        string status = _jobs[i].Status;
                        if (status == "done" || status == "error" || status == "canceled") _jobs.RemoveAt(i);
                    }
                }
            }
            _signal.Set();
        }

        public void Cancel(int id)
        {
            lock (_sync)
            {
                foreach (TransferJob job in _jobs)
                {
                    if (job.Id == id)
                    {
                        job.CancelRequested = true;
                        if (job.Status == "queued") job.Status = "canceled";
                        break;
                    }
                }
            }
            _signal.Set();
        }

        public void ClearFinished()
        {
            lock (_sync)
            {
                for (int i = _jobs.Count - 1; i >= 0; i--)
                {
                    if (_jobs[i].Status != "queued" && _jobs[i].Status != "preparing" && _jobs[i].Status != "running")
                        _jobs.RemoveAt(i);
                }
            }
        }

        public List<Dictionary<string, object>> Snapshot()
        {
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();
            lock (_sync)
            {
                foreach (TransferJob job in _jobs)
                {
                    result.Add(ToDictionary(job));
                }
            }
            return result;
        }

        private Dictionary<string, object> ToDictionary(TransferJob job)
        {
            Dictionary<string, object> item = new Dictionary<string, object>();
            item["id"] = job.Id;
            item["kind"] = job.Kind;
            item["title"] = job.Title;
            item["peerName"] = job.PeerName;
            item["target"] = job.TargetPath;
            item["source"] = job.SourcePath;
            item["status"] = job.Status;
            item["total"] = job.TotalBytes;
            item["done"] = job.DoneBytes;
            item["current"] = job.CurrentFile;
            item["message"] = job.Message;
            item["created"] = job.CreatedUtc.ToString("o", CultureInfo.InvariantCulture);
            item["started"] = job.StartedUtc == DateTime.MinValue ? "" : job.StartedUtc.ToString("o", CultureInfo.InvariantCulture);
            item["finished"] = job.FinishedUtc == DateTime.MinValue ? "" : job.FinishedUtc.ToString("o", CultureInfo.InvariantCulture);
            double progress = 0;
            if (job.TotalBytes > 0) progress = Math.Min(1.0, (double)job.DoneBytes / (double)job.TotalBytes);
            else if (job.Status == "done") progress = 1;
            item["progress"] = Math.Round(progress, 4);
            long speed = 0;
            if (job.StartedUtc != DateTime.MinValue && job.Status == "running")
            {
                double seconds = Math.Max(0.3, (DateTime.UtcNow - job.StartedUtc).TotalSeconds);
                speed = (long)(job.DoneBytes / seconds);
            }
            item["speed"] = speed;
            return item;
        }

        private void WorkerLoop()
        {
            while (_running)
            {
                TransferJob job = null;
                lock (_sync)
                {
                    foreach (TransferJob item in _jobs)
                    {
                        if (item.Status == "queued" && !item.CancelRequested) { job = item; break; }
                    }
                }
                if (job == null)
                {
                    _signal.WaitOne(1000);
                    continue;
                }

                if (job.CancelRequested)
                {
                    lock (_sync) { job.Status = "canceled"; job.FinishedUtc = DateTime.UtcNow; }
                    RaiseFinished(job);
                    continue;
                }

                lock (_sync)
                {
                    job.Status = "preparing";
                    job.StartedUtc = DateTime.UtcNow;
                    job.Message = "";
                }

                try
                {
                    if (job.Kind == "push") RunPush(job);
                    else RunPull(job);
                    lock (_sync)
                    {
                        job.Status = job.CancelRequested ? "canceled" : "done";
                        job.FinishedUtc = DateTime.UtcNow;
                        if (job.TotalBytes > 0) job.DoneBytes = job.TotalBytes;
                        job.CurrentFile = "";
                    }
                }
                catch (OperationCanceledException)
                {
                    lock (_sync)
                    {
                        job.Status = "canceled";
                        job.FinishedUtc = DateTime.UtcNow;
                        job.Message = "已取消";
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("传输失败 " + job.Title, ex);
                    lock (_sync)
                    {
                        job.Status = "error";
                        job.FinishedUtc = DateTime.UtcNow;
                        job.Message = ex is PeerException ? ex.Message : "传输失败：" + ex.Message;
                    }
                }
                RaiseFinished(job);
            }
        }

        private void RunPush(TransferJob job)
        {
            PeerInfo peer = _app.Store.FindPeer(job.PeerId);
            if (peer == null) throw new InvalidOperationException("设备已被移除");
            if (job.CancelRequested) throw new OperationCanceledException();

            if (File.Exists(job.SourcePath))
            {
                long size = new FileInfo(job.SourcePath).Length;
                lock (_sync) { job.TotalBytes = size; job.Status = "running"; job.CurrentFile = job.Title; }
                PeerClient.Upload(peer.Host, peer.Port, peer.Key, job.TargetPath, job.Title, job.SourcePath, job.Overwrite,
                    delegate(long done) { UpdateProgress(job, done); },
                    delegate() { return job.CancelRequested; });
                UpdateProgress(job, size);
                return;
            }

            if (!Directory.Exists(job.SourcePath)) throw new FileNotFoundException("源文件不存在", job.SourcePath);

            List<FsEntry> entries = FsUtil.Walk(job.SourcePath);
            long total = 0;
            foreach (FsEntry entry in entries) if (!entry.IsDir) total += entry.Size;
            lock (_sync) { job.TotalBytes = total; job.Status = "running"; }

            string rootName = Path.GetFileName(job.SourcePath.TrimEnd('\\'));
            if (string.IsNullOrEmpty(rootName)) rootName = "新建文件夹";
            string remoteRootTarget = Path.Combine(job.TargetPath, rootName);
            Dictionary<string, object> rootResult = PeerClient.Mkdir(peer.Host, peer.Port, peer.Key, remoteRootTarget, true);
            string remoteRoot = Json.GetString(rootResult, "path", null);
            if (string.IsNullOrEmpty(remoteRoot)) throw new IOException("对方未能创建目标文件夹");

            entries.Sort(CompareEntries);
            long baseDone = 0;
            foreach (FsEntry entry in entries)
            {
                if (job.CancelRequested) throw new OperationCanceledException();
                string remotePath = Path.Combine(remoteRoot, entry.Rel);
                if (entry.IsDir)
                {
                    PeerClient.Mkdir(peer.Host, peer.Port, peer.Key, remotePath, false);
                    continue;
                }

                string remoteDir = Path.GetDirectoryName(remotePath);
                if (!string.IsNullOrEmpty(remoteDir)) PeerClient.Mkdir(peer.Host, peer.Port, peer.Key, remoteDir, false);
                lock (_sync) { job.CurrentFile = entry.Rel; }
                long baseForFile = baseDone;
                PeerClient.Upload(peer.Host, peer.Port, peer.Key, remoteDir, entry.Name, entry.FullPath, false,
                    delegate(long done) { UpdateProgress(job, baseForFile + done); },
                    delegate() { return job.CancelRequested; });
                baseDone += entry.Size;
                UpdateProgress(job, baseDone);
            }
        }

        private void RunPull(TransferJob job)
        {
            PeerInfo peer = _app.Store.FindPeer(job.PeerId);
            if (peer == null) throw new InvalidOperationException("设备已被移除");
            if (job.CancelRequested) throw new OperationCanceledException();

            Dictionary<string, object> tree = PeerClient.Tree(peer.Host, peer.Port, peer.Key, job.SourcePath);
            bool isDir = Json.GetBool(tree, "dir", true);
            string name = Json.GetString(tree, "name", job.Title);
            if (string.IsNullOrEmpty(name)) name = "未命名";

            if (!isDir)
            {
                long size = Json.GetLong(tree, "size", 0);
                string localFile = FsUtil.UniqueFile(job.TargetPath, name);
                lock (_sync) { job.Title = name; job.TotalBytes = size; job.Status = "running"; job.CurrentFile = name; }
                PeerClient.Download(peer.Host, peer.Port, peer.Key, job.SourcePath, localFile,
                    delegate(long done) { UpdateProgress(job, done); },
                    delegate() { return job.CancelRequested; });
                try
                {
                    string modified = Json.GetString(tree, "modified", null);
                    if (!string.IsNullOrEmpty(modified))
                        File.SetLastWriteTime(localFile, DateTime.Parse(modified, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToLocalTime());
                }
                catch { }
                UpdateProgress(job, size);
                return;
            }

            List<object> rawEntries = Json.GetList(tree, "entries");
            long total = 0;
            foreach (object raw in rawEntries)
            {
                Dictionary<string, object> item = raw as Dictionary<string, object>;
                if (item != null && !Json.GetBool(item, "dir", false)) total += Json.GetLong(item, "size", 0);
            }
            string localRoot = FsUtil.UniqueDir(job.TargetPath, name);
            Directory.CreateDirectory(localRoot);
            lock (_sync) { job.TotalBytes = total; job.Status = "running"; }

            List<Dictionary<string, object>> items = new List<Dictionary<string, object>>();
            foreach (object raw in rawEntries)
            {
                Dictionary<string, object> item = raw as Dictionary<string, object>;
                if (item != null) items.Add(item);
            }
            items.Sort(delegate(Dictionary<string, object> a, Dictionary<string, object> b)
            {
                string ra = Json.GetString(a, "rel", "");
                string rb = Json.GetString(b, "rel", "");
                int da = Depth(ra);
                int db = Depth(rb);
                if (da != db) return da.CompareTo(db);
                bool dirA = Json.GetBool(a, "dir", false);
                bool dirB = Json.GetBool(b, "dir", false);
                if (dirA != dirB) return dirA ? -1 : 1;
                return string.Compare(ra, rb, StringComparison.CurrentCultureIgnoreCase);
            });

            long baseDone = 0;
            foreach (Dictionary<string, object> item in items)
            {
                if (job.CancelRequested) throw new OperationCanceledException();
                string rel = Json.GetString(item, "rel", "");
                if (string.IsNullOrEmpty(rel)) continue;
                string localPath = Path.Combine(localRoot, rel);
                bool isItemDir = Json.GetBool(item, "dir", false);
                if (isItemDir)
                {
                    Directory.CreateDirectory(localPath);
                    continue;
                }
                string parent = Path.GetDirectoryName(localPath);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                string remotePath = Path.Combine(job.SourcePath, rel);
                long size = Json.GetLong(item, "size", 0);
                lock (_sync) { job.CurrentFile = rel; }
                long baseForFile = baseDone;
                PeerClient.Download(peer.Host, peer.Port, peer.Key, remotePath, localPath,
                    delegate(long done) { UpdateProgress(job, baseForFile + done); },
                    delegate() { return job.CancelRequested; });
                try
                {
                    string modified = Json.GetString(item, "modified", null);
                    if (!string.IsNullOrEmpty(modified))
                        File.SetLastWriteTime(localPath, DateTime.Parse(modified, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToLocalTime());
                }
                catch { }
                baseDone += size;
                UpdateProgress(job, baseDone);
            }
        }

        private static int CompareEntries(FsEntry a, FsEntry b)
        {
            int da = Depth(a.Rel);
            int db = Depth(b.Rel);
            if (da != db) return da.CompareTo(db);
            if (a.IsDir != b.IsDir) return a.IsDir ? -1 : 1;
            return string.Compare(a.Rel, b.Rel, StringComparison.CurrentCultureIgnoreCase);
        }

        private static int Depth(string rel)
        {
            if (string.IsNullOrEmpty(rel)) return 0;
            int count = 0;
            foreach (char c in rel) if (c == '\\' || c == '/') count++;
            return count;
        }

        private void UpdateProgress(TransferJob job, long done)
        {
            lock (_sync)
            {
                if (done > job.DoneBytes) job.DoneBytes = done;
                if (job.TotalBytes > 0 && job.DoneBytes > job.TotalBytes) job.DoneBytes = job.TotalBytes;
                if (job.Status == "preparing") job.Status = "running";
            }
        }

        private void RaiseFinished(TransferJob job)
        {
            bool idle = true;
            lock (_sync)
            {
                foreach (TransferJob item in _jobs)
                {
                    if (item.Status == "queued" || item.Status == "preparing" || item.Status == "running") { idle = false; break; }
                }
            }
            Action<TransferJob, bool> handler = Finished;
            if (handler != null)
            {
                try { handler(job, idle); } catch { }
            }
        }
    }
}
