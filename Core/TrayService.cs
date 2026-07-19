using System;
using System.Drawing;
using System.Windows.Forms;
using Color = System.Drawing.Color;

namespace PalAssist.Core
{
    /// <summary>
    /// System tray icon (WinForms NotifyIcon) for an always-on WPF overlay.
    /// </summary>
    public sealed class TrayService : IDisposable
    {
        private NotifyIcon? _icon;
        private bool _disposed;

        public event Action? ShowMenuRequested;
        public event Action? HideMenuRequested;
        public event Action? ExitRequested;

        public bool IsVisible => _icon?.Visible == true;

        public void Initialize(string tooltip = "PalAssist")
        {
            if (_icon != null) return;

            _icon = new NotifyIcon
            {
                Text = tooltip,
                Visible = true,
                Icon = CreateDefaultIcon()
            };

            var menu = new ContextMenuStrip();
            menu.Items.Add("Show Menu", null, (_, _) => ShowMenuRequested?.Invoke());
            menu.Items.Add("Hide Menu", null, (_, _) => HideMenuRequested?.Invoke());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());
            _icon.ContextMenuStrip = menu;
            _icon.DoubleClick += (_, _) => ShowMenuRequested?.Invoke();
        }

        public void SetVisible(bool visible)
        {
            if (_icon != null)
                _icon.Visible = visible;
        }

        public void SetTooltip(string text)
        {
            if (_icon != null)
                _icon.Text = text.Length > 63 ? text[..63] : text;
        }

        public void ShowBalloon(string title, string text, int timeoutMs = 3000)
        {
            if (_icon == null || !_icon.Visible) return;
            try
            {
                _icon.BalloonTipTitle = title;
                _icon.BalloonTipText = text;
                _icon.BalloonTipIcon = ToolTipIcon.Info;
                _icon.ShowBalloonTip(timeoutMs);
            }
            catch
            {
                // Balloon tips can fail on restricted shells
            }
        }

        private static Icon CreateDefaultIcon()
        {
            // Prefer application icon if Windows can extract it from the exe
            try
            {
                string? path = Environment.ProcessPath;
                if (!string.IsNullOrEmpty(path))
                {
                    var extracted = Icon.ExtractAssociatedIcon(path);
                    if (extracted != null)
                        return extracted;
                }
            }
            catch { /* fall through */ }

            // Simple generated cyan-ish square icon
            var bmp = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(22, 33, 62));
                using var brush = new SolidBrush(Color.FromArgb(0, 210, 255));
                g.FillEllipse(brush, 2, 2, 12, 12);
            }
            IntPtr hIcon = bmp.GetHicon();
            try
            {
                return (Icon)Icon.FromHandle(hIcon).Clone();
            }
            finally
            {
                DestroyIcon(hIcon);
                bmp.Dispose();
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_icon != null)
            {
                _icon.Visible = false;
                _icon.Dispose();
                _icon = null;
            }
        }
    }
}
