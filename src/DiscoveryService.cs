using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace LanDrop
{
    public class DiscoveredPeer
    {
        public string Id;
        public string Name;
        public string Host;
        public int Port;
        public string Version;
        public DateTime LastSeenUtc;
    }

    public class DiscoveryService
    {
        public const int DiscoveryPort = 52181;
        private const string DiscoverMagic = "LANDROP_DISCOVER_V1";
        private const string PeerMagic = "LANDROP_PEER_V1";

        private readonly object _sync = new object();
        private readonly Dictionary<string, DiscoveredPeer> _peers = new Dictionary<string, DiscoveredPeer>(StringComparer.Ordinal);
        private readonly AppConfig _config;
        private readonly int _httpPort;
        private readonly bool _enabled;
        private UdpClient _client;
        private Thread _receiveThread;
        private Thread _scanThread;
        private volatile bool _running;

        public DiscoveryService(AppConfig config, int httpPort, bool enabled)
        {
            _config = config;
            _httpPort = httpPort;
            _enabled = enabled;
        }

        public void Start()
        {
            if (!_enabled) return;
            try
            {
                _client = new UdpClient();
                _client.ExclusiveAddressUse = false;
                _client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _client.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
                _client.EnableBroadcast = true;
                _running = true;

                _receiveThread = new Thread(ReceiveLoop);
                _receiveThread.IsBackground = true;
                _receiveThread.Name = "LanDrop Discovery RX";
                _receiveThread.Start();

                _scanThread = new Thread(ScanLoop);
                _scanThread.IsBackground = true;
                _scanThread.Name = "LanDrop Discovery Scan";
                _scanThread.Start();
                Log.Info("设备发现服务已启动，UDP " + DiscoveryPort.ToString());
            }
            catch (Exception ex)
            {
                Log.Error("启动设备发现失败（不影响手动输入 IP）", ex);
                _running = false;
            }
        }

        public void Stop()
        {
            _running = false;
            try { if (_client != null) _client.Close(); } catch { }
            try { if (_receiveThread != null) _receiveThread.Join(1000); } catch { }
            try { if (_scanThread != null) _scanThread.Join(1000); } catch { }
        }

        private void ReceiveLoop()
        {
            IPEndPoint remote = new IPEndPoint(IPAddress.Any, 0);
            while (_running)
            {
                try
                {
                    byte[] data = _client.Receive(ref remote);
                    if (data == null || data.Length == 0 || data.Length > 8192) continue;
                    string text = Encoding.UTF8.GetString(data).Trim();
                    if (text == DiscoverMagic)
                    {
                        Respond(remote);
                        continue;
                    }
                    if (text.StartsWith(PeerMagic, StringComparison.Ordinal))
                    {
                        int newline = text.IndexOf('\n');
                        string json = newline >= 0 ? text.Substring(newline + 1) : "";
                        HandleAnnounce(json, remote);
                    }
                }
                catch (SocketException)
                {
                    if (!_running) break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (!_running) break;
                    Log.Warn("发现服务接收异常: " + ex.Message);
                    Thread.Sleep(200);
                }
            }
        }

        private void Respond(IPEndPoint remote)
        {
            try
            {
                Dictionary<string, object> data = new Dictionary<string, object>();
                data["magic"] = PeerMagic;
                data["id"] = _config.Id;
                data["name"] = _config.Name;
                data["port"] = _httpPort;
                data["version"] = "1.0.0";
                string json = Json.Serialize(data);
                byte[] payload = Encoding.UTF8.GetBytes(PeerMagic + "\n" + json);
                _client.Send(payload, payload.Length, remote);
            }
            catch { }
        }

        private void HandleAnnounce(string json, IPEndPoint remote)
        {
            try
            {
                Dictionary<string, object> data = Json.ParseObject(json);
                string id = Json.GetString(data, "id", "");
                if (string.IsNullOrEmpty(id) || id == _config.Id) return;
                int port = (int)Json.GetLong(data, "port", 0);
                if (port <= 0 || port > 65535) return;
                DiscoveredPeer peer = new DiscoveredPeer();
                peer.Id = id;
                peer.Name = Json.GetString(data, "name", "Windows 电脑");
                peer.Port = port;
                peer.Host = remote.Address.ToString();
                peer.Version = Json.GetString(data, "version", "");
                peer.LastSeenUtc = DateTime.UtcNow;
                lock (_sync) { _peers[id] = peer; }
            }
            catch { }
        }

        private void ScanLoop()
        {
            Thread.Sleep(800);
            while (_running)
            {
                try
                {
                    SendDiscover();
                    Thread.Sleep(6000);
                }
                catch
                {
                    Thread.Sleep(3000);
                }
            }
        }

        public void ScanNow(bool wait)
        {
            if (!_enabled) return;
            try { SendDiscover(); } catch { }
            if (wait) Thread.Sleep(900);
        }

        private void SendDiscover()
        {
            if (_client == null) return;
            byte[] payload = Encoding.UTF8.GetBytes(DiscoverMagic);
            foreach (IPAddress address in NetUtil.GetBroadcastAddresses())
            {
                try { _client.Send(payload, payload.Length, new IPEndPoint(address, DiscoveryPort)); }
                catch { }
            }
        }

        public List<DiscoveredPeer> GetPeers()
        {
            List<DiscoveredPeer> result = new List<DiscoveredPeer>();
            lock (_sync)
            {
                DateTime cutoff = DateTime.UtcNow.AddSeconds(-25);
                List<string> stale = new List<string>();
                foreach (KeyValuePair<string, DiscoveredPeer> pair in _peers)
                {
                    if (pair.Value.LastSeenUtc < cutoff) { stale.Add(pair.Key); continue; }
                    DiscoveredPeer copy = new DiscoveredPeer();
                    copy.Id = pair.Value.Id;
                    copy.Name = pair.Value.Name;
                    copy.Host = pair.Value.Host;
                    copy.Port = pair.Value.Port;
                    copy.Version = pair.Value.Version;
                    copy.LastSeenUtc = pair.Value.LastSeenUtc;
                    result.Add(copy);
                }
                foreach (string id in stale) _peers.Remove(id);
            }
            result.Sort(delegate(DiscoveredPeer a, DiscoveredPeer b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            return result;
        }

        public DiscoveredPeer Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            lock (_sync)
            {
                DiscoveredPeer peer;
                if (!_peers.TryGetValue(id, out peer)) return null;
                if (peer.LastSeenUtc < DateTime.UtcNow.AddSeconds(-25)) return null;
                return peer;
            }
        }
    }
}
