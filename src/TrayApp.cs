using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;

namespace LanDrop
{
    public class TrayApp : ApplicationContext
    {
        private readonly LanDropApp _app;
        private readonly NotifyIcon _icon;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _autoItem;
        private readonly SynchronizationContext _ui;

        public TrayApp(LanDropApp app)
        {
            _app = app;
            _app.Notify += OnNotify;
            _ui = SynchronizationContext.Current;
            if (_ui == null) _ui = new WindowsFormsSynchronizationContext();

            _menu = new ContextMenuStrip();
            _menu.ShowImageMargin = false;
            ToolStripMenuItem openItem = new ToolStripMenuItem("打开传输界面");
            openItem.Font = new Font(openItem.Font, FontStyle.Bold);
            openItem.Click += delegate { OpenUi(); };
            _menu.Items.Add(openItem);

            ToolStripMenuItem codeItem = new ToolStripMenuItem("复制连接码");
            codeItem.Click += delegate { CopyCode(); };
            _menu.Items.Add(codeItem);

            ToolStripMenuItem addressItem = new ToolStripMenuItem("复制访问地址");
            addressItem.Click += delegate { CopyAddress(); };
            _menu.Items.Add(addressItem);
            _menu.Items.Add(new ToolStripSeparator());

            _autoItem = new ToolStripMenuItem("开机自动启动");
            _autoItem.Click += delegate { ToggleAutostart(); };
            _menu.Items.Add(_autoItem);

            ToolStripMenuItem folderItem = new ToolStripMenuItem("打开数据文件夹");
            folderItem.Click += delegate { _app.OpenDataFolder(); };
            _menu.Items.Add(folderItem);
            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem exitItem = new ToolStripMenuItem("退出 LanDrop");
            exitItem.Click += delegate { ExitApp(); };
            _menu.Items.Add(exitItem);

            _menu.Opening += delegate
            {
                _autoItem.Checked = Autostart.IsEnabled();
            };

            _icon = new NotifyIcon();
            _icon.Icon = LoadIcon();
            _icon.Text = "LanDrop 局域网快传";
            _icon.Visible = true;
            _icon.ContextMenuStrip = _menu;
            _icon.DoubleClick += delegate { OpenUi(); };
            _icon.BalloonTipClicked += delegate { OpenUi(); };

            ShowBalloon("LanDrop 已启动", "连接码 " + _app.Config.Code + " · 双击托盘图标打开");
        }

        private static Icon LoadIcon()
        {
            try
            {
                Assembly asm = Assembly.GetExecutingAssembly();
                using (Stream stream = asm.GetManifestResourceStream("App.icon"))
                {
                    if (stream != null)
                    {
                        using (Icon icon = new Icon(stream)) return (Icon)icon.Clone();
                    }
                }
            }
            catch { }
            return SystemIcons.Application;
        }

        private void OnNotify(string title, string text)
        {
            if (_ui == null) { ShowBalloon(title, text); return; }
            _ui.Post(delegate(object state) { ShowBalloon(title, text); }, null);
        }

        private void ShowBalloon(string title, string text)
        {
            try
            {
                _icon.BalloonTipTitle = title;
                _icon.BalloonTipText = text;
                _icon.BalloonTipIcon = ToolTipIcon.Info;
                _icon.ShowBalloonTip(3500);
            }
            catch { }
        }

        private void OpenUi()
        {
            try { Process.Start(new ProcessStartInfo(_app.LocalUrl) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Error("打开浏览器失败", ex); }
        }

        private void CopyCode()
        {
            try { Clipboard.SetText(_app.Config.Code); } catch { }
        }

        private void CopyAddress()
        {
            try
            {
                string url = _app.LocalUrl;
                System.Collections.Generic.List<string> urls = _app.GetLocalUrls();
                if (urls.Count > 0) url = urls[0];
                Clipboard.SetText(url);
            }
            catch { }
        }

        private void ToggleAutostart()
        {
            try
            {
                bool next = !Autostart.IsEnabled();
                Autostart.Set(next, _app.ExePath);
                _autoItem.Checked = Autostart.IsEnabled();
                ShowBalloon("LanDrop", next ? "已开启开机自动启动" : "已关闭开机自动启动");
            }
            catch (Exception ex)
            {
                MessageBox.Show("设置开机启动失败：" + ex.Message, "LanDrop", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ExitApp()
        {
            try { _icon.Visible = false; } catch { }
            try { _app.Stop(); } catch { }
            try { _menu.Dispose(); } catch { }
            try { _icon.Dispose(); } catch { }
            ExitThread();
        }
    }
}
