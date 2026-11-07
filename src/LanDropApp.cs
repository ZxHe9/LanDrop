using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LanDrop
{
    public class LanDropApp
    {
        public const string Version = "1.0.0";
        private readonly object _healthSync = new object();
        private readonly Dictionary<string, bool> _healthResult = new Dictionary<string, bool>(StringComparer.Ordinal);
        private readonly Dictionary<string, DateTime> _healthChecked = new Dictionary<string, DateTime>(StringComparer.Ordinal);

        public LanDropApp(ConfigStore store, int port, string exePath)
        {
            Store = store;
            Port = port;
            ExePath = exePath;
            DataDir = store.DataDir;
            LocalUrl = "http://127.0.0.1:" + port.ToString() + "/";
            EnableDiscovery = true;
        }

        public ConfigStore Store { get; private set; }
        public AppConfig Config { get { return Store.Config; } }
        public DiscoveryService Discovery { get; set; }
        public TransferManager Transfers { get; set; }
        public HttpServer Server { get; set; }
        public int Port { get; private set; }
        public string DataDir { get; private set; }
        public string LocalUrl { get; private set; }
        public string ExePath { get; private set; }
        public bool EnableDiscovery { get; set; }

        public event Action<string, string> Notify;

        public void RaiseNotify(string title, string text)
        {
            Action<string, string> handler = Notify;
            if (handler != null)
            {
                try { handler(title, text); } catch { }
            }
        }

        public bool IsPeerOnline(PeerInfo peer)
        {
            if (peer == null) return false;
            lock (_healthSync)
            {
                DateTime checkedAt;
                bool value;
                if (_healthChecked.TryGetValue(peer.Id, out checkedAt)
                    && (DateTime.UtcNow - checkedAt).TotalSeconds < 8
                    && _healthResult.TryGetValue(peer.Id, out value))
                {
                    return value;
                }
            }
            bool online = NetUtil.IsPortOpen(peer.Host, peer.Port, 450);
            lock (_healthSync)
            {
                _healthResult[peer.Id] = online;
                _healthChecked[peer.Id] = DateTime.UtcNow;
            }
            return online;
        }

        public void MarkPeerOffline(string peerId)
        {
            if (string.IsNullOrEmpty(peerId)) return;
            lock (_healthSync)
            {
                _healthResult[peerId] = false;
                _healthChecked[peerId] = DateTime.UtcNow;
            }
        }

        public List<string> GetLocalUrls()
        {
            List<string> urls = new List<string>();
            foreach (string ip in NetUtil.GetLocalAddresses())
                urls.Add("http://" + ip + ":" + Port.ToString() + "/");
            return urls;
        }

        public void Start()
        {
            ApiRouter.App = this;
            Server = new HttpServer(Port);
            Server.Start(ApiRouter.Handle);
            Discovery = new DiscoveryService(Config, Port, EnableDiscovery);
            Discovery.Start();
            Transfers = new TransferManager(this);
            Transfers.Start();
            Transfers.Finished += delegate(TransferJob job, bool idle)
            {
                if (job.Status == "done" && idle)
                    RaiseNotify("传输完成", job.Title + " 已传输到 " + (job.Kind == "push" ? "对方电脑" : "本机"));
                else if (job.Status == "error")
                    RaiseNotify("传输失败", job.Title + "：" + job.Message);
            };
            Log.Info("LanDrop 已启动，端口 " + Port.ToString() + "，设备名 " + Config.Name);
        }

        public void Stop()
        {
            try { if (Transfers != null) Transfers.Stop(); } catch { }
            try { if (Discovery != null) Discovery.Stop(); } catch { }
            try { if (Server != null) Server.Stop(); } catch { }
            try
            {
                string endpoint = Path.Combine(DataDir, "endpoint.txt");
                if (File.Exists(endpoint)) File.Delete(endpoint);
            }
            catch { }
        }

        public void OpenDataFolder()
        {
            try { Process.Start("explorer.exe", "\"" + DataDir + "\""); } catch { }
        }
    }

    public static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "LanDrop";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null) return false;
                    return key.GetValue(ValueName) != null;
                }
            }
            catch { return false; }
        }

        public static void Set(bool enabled, string exePath)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (key == null) throw new InvalidOperationException("无法写入开机启动设置");
                if (enabled)
                {
                    string command = "\"" + exePath + "\" --autostart";
                    key.SetValue(ValueName, command, RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                }
            }
        }
    }
}
