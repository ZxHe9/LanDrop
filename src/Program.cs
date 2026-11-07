using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace LanDrop
{
    public static class Program
    {
        private static Mutex _mutex;
        private static readonly bool DebugMode = Environment.GetEnvironmentVariable("LANDROP_DEBUG") == "1";

        private static void D(string message)
        {
            if (DebugMode) Console.Error.WriteLine("[lanDrop] " + message);
        }

        private static void T(string message)
        {
            if (Environment.GetEnvironmentVariable("LANDROP_TRACE") != "1") return;
            try
            {
                File.AppendAllText(Path.Combine(Path.GetTempPath(), "landrop-startup.trace"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + message + Environment.NewLine);
            }
            catch { }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            D("main start");
            T("main start");
            bool noBrowser = HasArg(args, "--no-browser") || HasArg(args, "--autostart");
            bool noDiscovery = HasArg(args, "--no-discovery");
            string customDataDir = GetArg(args, "--data-dir");
            string customPort = GetArg(args, "--port");

            string dataDir = string.IsNullOrEmpty(customDataDir)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LanDrop")
                : Path.GetFullPath(customDataDir);
            Directory.CreateDirectory(dataDir);
            Log.Init(dataDir);
            D("dataDir=" + dataDir);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            T("winforms initialized");
            Application.ThreadException += delegate(object sender, ThreadExceptionEventArgs e)
            {
                Log.Error("界面线程异常", e.Exception);
                MessageBox.Show("LanDrop 遇到问题：" + e.Exception.Message, "LanDrop", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Log.Error("未处理异常", e.ExceptionObject as Exception);
            };

            bool createdNew;
            _mutex = new Mutex(true, MutexName(dataDir), out createdNew);
            T("mutex createdNew=" + createdNew.ToString());
            if (!createdNew)
            {
                if (!noBrowser) OpenExistingInstance(dataDir);
                return;
            }

            try
            {
                ServicePointManager.Expect100Continue = false;
                ServicePointManager.DefaultConnectionLimit = 64;
                int minWorkers;
                int minIo;
                ThreadPool.GetMinThreads(out minWorkers, out minIo);
                ThreadPool.SetMinThreads(Math.Max(minWorkers, 16), Math.Max(minIo, 16));

                ConfigStore store = new ConfigStore(dataDir);
                T("config loaded");
                D("config loaded, device=" + store.Config.Name);
                int preferred = store.Config.Port;
                if (!string.IsNullOrEmpty(customPort))
                {
                    int parsed;
                    if (int.TryParse(customPort, out parsed) && parsed > 0 && parsed <= 65535) preferred = parsed;
                }
                int port = FindFreePort(preferred);
                if (port <= 0)
                {
                    MessageBox.Show("没有找到可用端口，LanDrop 无法启动。", "LanDrop", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                string exePath = Application.ExecutablePath;
                LanDropApp app = new LanDropApp(store, port, exePath);
                app.EnableDiscovery = !noDiscovery;
                D("starting server on port " + port);
                app.Start();
                T("server started");
                D("server started: " + app.LocalUrl);

                try
                {
                    File.WriteAllText(Path.Combine(dataDir, "endpoint.txt"), app.LocalUrl, Encoding.UTF8);
                }
                catch { }

                if (!noBrowser) OpenUrl(app.LocalUrl);
                TrayApp tray = new TrayApp(app);
                T("tray created");
                D("tray created, entering message loop");
                Application.Run(tray);
                D("message loop exited");
                app.Stop();
            }
            catch (Exception ex)
            {
                Log.Error("启动失败", ex);
                T("STARTUP ERROR: " + ex.ToString());
                D("STARTUP ERROR: " + ex.ToString());
                if (!DebugMode)
                    MessageBox.Show("LanDrop 启动失败：\n" + ex.Message, "LanDrop", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                try { _mutex.ReleaseMutex(); } catch { }
                try { _mutex.Dispose(); } catch { }
            }
        }

        private static string MutexName(string dataDir)
        {
            try
            {
                using (SHA1 sha = SHA1.Create())
                {
                    byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(dataDir.TrimEnd('\\').ToLowerInvariant()));
                    StringBuilder sb = new StringBuilder();
                    for (int i = 0; i < 8; i++) sb.Append(hash[i].ToString("x2"));
                    return "Local\\LanDrop_" + sb.ToString();
                }
            }
            catch
            {
                return "Local\\LanDrop_Default";
            }
        }

        private static int FindFreePort(int start)
        {
            for (int i = 0; i < 30; i++)
            {
                int candidate = start + i;
                if (candidate > 65535) break;
                TcpListener listener = null;
                try
                {
                    listener = new TcpListener(IPAddress.Any, candidate);
                    listener.Start();
                    return candidate;
                }
                catch (SocketException)
                {
                }
                finally
                {
                    try { if (listener != null) listener.Stop(); } catch { }
                }
            }
            return 0;
        }

        private static void OpenExistingInstance(string dataDir)
        {
            string url = null;
            string endpoint = Path.Combine(dataDir, "endpoint.txt");
            for (int i = 0; i < 15; i++)
            {
                try
                {
                    if (File.Exists(endpoint))
                    {
                        url = File.ReadAllText(endpoint, Encoding.UTF8).Trim();
                        if (url.Length > 0) break;
                    }
                }
                catch { }
                Thread.Sleep(200);
            }
            if (!string.IsNullOrEmpty(url))
            {
                OpenUrl(url);
            }
            else
            {
                MessageBox.Show("LanDrop 已经在运行，请从任务栏右下角的托盘图标打开。", "LanDrop",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }

        private static void OpenUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Error("打开浏览器失败", ex);
            }
        }

        private static bool HasArg(string[] args, string name)
        {
            foreach (string arg in args)
            {
                if (string.Equals(arg, name, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        private static string GetArg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            }
            return null;
        }
    }
}
