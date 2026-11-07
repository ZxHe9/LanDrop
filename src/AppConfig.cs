using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LanDrop
{
    public class PeerInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Host { get; set; }
        public int Port { get; set; }
        public string Key { get; set; }
        public string LastSeen { get; set; }
        public string Home { get; set; }
        public string Version { get; set; }
    }

    public class AppConfig
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Code { get; set; }
        public string Key { get; set; }
        public int Port { get; set; }
        public string LastLocalPath { get; set; }
        public List<PeerInfo> Peers { get; set; }

        public AppConfig()
        {
            Peers = new List<PeerInfo>();
        }
    }

    public class ConfigStore
    {
        private readonly object _sync = new object();
        private readonly string _path;
        private AppConfig _config;

        public ConfigStore(string dataDir)
        {
            DataDir = dataDir;
            Directory.CreateDirectory(dataDir);
            _path = Path.Combine(dataDir, "config.json");
            Load();
        }

        public string DataDir { get; private set; }
        public string ConfigPath { get { return _path; } }

        public AppConfig Config
        {
            get { return _config; }
        }

        private void Load()
        {
            AppConfig cfg = null;
            try
            {
                if (File.Exists(_path))
                {
                    string json = File.ReadAllText(_path, Encoding.UTF8);
                    // JavaScriptSerializer gives strongly typed objects for known classes.
                    System.Web.Script.Serialization.JavaScriptSerializer ser =
                        new System.Web.Script.Serialization.JavaScriptSerializer();
                    ser.MaxJsonLength = int.MaxValue;
                    cfg = ser.Deserialize<AppConfig>(json);
                }
            }
            catch (Exception ex)
            {
                Log.Error("读取配置失败", ex);
                try
                {
                    if (File.Exists(_path)) File.Copy(_path, _path + ".bad", true);
                }
                catch { }
            }

            if (cfg == null) cfg = new AppConfig();
            bool changed = false;
            if (string.IsNullOrEmpty(cfg.Id)) { cfg.Id = Guid.NewGuid().ToString("N"); changed = true; }
            if (string.IsNullOrEmpty(cfg.Name)) { cfg.Name = Environment.MachineName; changed = true; }
            if (string.IsNullOrEmpty(cfg.Code)) { cfg.Code = TextUtil.RandomCode(); changed = true; }
            if (string.IsNullOrEmpty(cfg.Key) || cfg.Key.Length < 32) { cfg.Key = TextUtil.RandomKey(); changed = true; }
            if (cfg.Port <= 0 || cfg.Port > 65535) { cfg.Port = 52180; changed = true; }
            if (cfg.Peers == null) { cfg.Peers = new List<PeerInfo>(); changed = true; }
            if (string.IsNullOrEmpty(cfg.LastLocalPath)) { cfg.LastLocalPath = FsUtil.GetHomePath(); changed = true; }
            _config = cfg;
            if (changed) Save();
        }

        public void Save()
        {
            lock (_sync)
            {
                try
                {
                    string json = Json.Serialize(_config);
                    string tmp = _path + ".tmp";
                    File.WriteAllText(tmp, json, new UTF8Encoding(false));
                    if (File.Exists(_path))
                    {
                        try { File.Replace(tmp, _path, null); }
                        catch { File.Copy(tmp, _path, true); File.Delete(tmp); }
                    }
                    else
                    {
                        File.Move(tmp, _path);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("保存配置失败", ex);
                }
            }
        }

        public void SetName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("设备名称不能为空");
            name = name.Trim();
            if (name.Length > 32) name = name.Substring(0, 32);
            lock (_sync) { _config.Name = name; }
            Save();
        }

        public void SetLastPath(string path)
        {
            lock (_sync) { _config.LastLocalPath = path; }
            Save();
        }

        public List<PeerInfo> GetPeers()
        {
            lock (_sync)
            {
                List<PeerInfo> copy = new List<PeerInfo>();
                foreach (PeerInfo peer in _config.Peers)
                {
                    copy.Add(new PeerInfo
                    {
                        Id = peer.Id,
                        Name = peer.Name,
                        Host = peer.Host,
                        Port = peer.Port,
                        Key = peer.Key,
                        LastSeen = peer.LastSeen,
                        Home = peer.Home,
                        Version = peer.Version
                    });
                }
                return copy;
            }
        }

        public PeerInfo FindPeer(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (_sync)
            {
                foreach (PeerInfo peer in _config.Peers)
                {
                    if (string.Equals(peer.Id, id, StringComparison.Ordinal))
                    {
                        return new PeerInfo
                        {
                            Id = peer.Id,
                            Name = peer.Name,
                            Host = peer.Host,
                            Port = peer.Port,
                            Key = peer.Key,
                            LastSeen = peer.LastSeen,
                            Home = peer.Home,
                            Version = peer.Version
                        };
                    }
                }
            }
            return null;
        }

        public void AddOrUpdatePeer(PeerInfo peer)
        {
            if (peer == null || string.IsNullOrEmpty(peer.Id)) return;
            lock (_sync)
            {
                PeerInfo existing = null;
                foreach (PeerInfo item in _config.Peers)
                {
                    if (string.Equals(item.Id, peer.Id, StringComparison.Ordinal)) { existing = item; break; }
                }
                if (existing == null)
                {
                    existing = new PeerInfo();
                    existing.Id = peer.Id;
                    _config.Peers.Add(existing);
                }
                existing.Name = peer.Name;
                existing.Host = peer.Host;
                existing.Port = peer.Port;
                existing.Key = peer.Key;
                existing.Home = peer.Home;
                existing.Version = peer.Version;
                existing.LastSeen = DateTime.UtcNow.ToString("o");
            }
            Save();
        }

        public void TouchPeer(string id, string host, int port)
        {
            bool changed = false;
            lock (_sync)
            {
                foreach (PeerInfo peer in _config.Peers)
                {
                    if (string.Equals(peer.Id, id, StringComparison.Ordinal))
                    {
                        if (!string.Equals(peer.Host, host, StringComparison.Ordinal) || peer.Port != port)
                        {
                            peer.Host = host;
                            peer.Port = port;
                            changed = true;
                        }
                        peer.LastSeen = DateTime.UtcNow.ToString("o");
                        break;
                    }
                }
            }
            if (changed) Save();
        }

        public void RemovePeer(string id)
        {
            lock (_sync)
            {
                for (int i = _config.Peers.Count - 1; i >= 0; i--)
                {
                    if (string.Equals(_config.Peers[i].Id, id, StringComparison.Ordinal)) _config.Peers.RemoveAt(i);
                }
            }
            Save();
        }
    }
}
