using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace WindowsS0PowerOverride;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new WindowsS0PowerOverrideContext());
    }
}

internal sealed class WindowsS0PowerOverrideContext : ApplicationContext
{
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;
    private const string StartupValueName = "WindowsS0PowerOverride";
    private const string StartupKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly Control dispatcher;
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem statusItem;
    private readonly ToolStripMenuItem startupItem;
    private readonly ToolStripMenuItem preventSleepItem;
    private readonly System.Windows.Forms.Timer timer;
    private readonly string settingsDirectory;
    private readonly string settingsPath;

    private AppSettings settings;
    private uint resetBaselineTick;
    private bool hibernateIssued;
    private bool exiting;

    public WindowsS0PowerOverrideContext()
    {
        // A hidden WinForms control marshals power/session notifications onto
        // the UI thread before touching the WinForms timer or tray controls.
        dispatcher = new Control();
        _ = dispatcher.Handle;

        settingsDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WindowsS0PowerOverride");
        settingsPath = Path.Combine(settingsDirectory, "settings.json");
        Directory.CreateDirectory(settingsDirectory);
        settings = LoadSettings();

        statusItem = new ToolStripMenuItem("Checking idle time…") { Enabled = false };
        startupItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = IsStartupEnabled()
        };
        startupItem.CheckedChanged += StartupItemChanged;

        preventSleepItem = new ToolStripMenuItem("Prevent system sleep while monitoring")
        {
            CheckOnClick = true,
            Checked = settings.PreventSystemSleep
        };
        preventSleepItem.CheckedChanged += PreventSleepItemChanged;

        var menu = new ContextMenuStrip();
        menu.Items.Add(statusItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Configure idle time…", null, (_, _) => OpenSettings());
        menu.Items.Add(preventSleepItem);
        menu.Items.Add(startupItem);
        menu.Items.Add("Hibernate now", null, (_, _) => Hibernate());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        tray = new NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Windows S0 Power Override",
            ContextMenuStrip = menu,
            Visible = true
        };
        tray.DoubleClick += (_, _) => ShowStatus();

        timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += (_, _) => CheckIdle();
        timer.Start();

        // Establish a fresh inactivity window at app startup.
        ResetIdleBaseline();
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
        ApplyExecutionState();
        CheckIdle();
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            DispatchResetIdleBaseline();
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        // A fresh full timeout begins on logon/unlock. Locking does not reset the timer.
        if (e.Reason is SessionSwitchReason.SessionLogon or SessionSwitchReason.SessionUnlock)
            DispatchResetIdleBaseline();
    }

    private void DispatchResetIdleBaseline()
    {
        try
        {
            if (!dispatcher.IsDisposed && dispatcher.IsHandleCreated)
                dispatcher.BeginInvoke((Action)ResetIdleBaseline);
        }
        catch (InvalidOperationException)
        {
            // The UI is shutting down; ignore late OS notifications.
        }
    }

    private void ResetIdleBaseline()
    {
        resetBaselineTick = unchecked((uint)Environment.TickCount);
        hibernateIssued = false;
        if (!exiting)
        {
            timer?.Start();
            ApplyExecutionState();
        }
    }

    private void ApplyExecutionState()
    {
        if (settings.PreventSystemSleep && !exiting && !hibernateIssued)
            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
        else
            SetThreadExecutionState(ES_CONTINUOUS);
    }

    private void CheckIdle()
    {
        if (hibernateIssued || exiting) return;

        TimeSpan idle = GetEffectiveIdleTime();
        TimeSpan threshold = TimeSpan.FromMinutes(settings.IdleMinutes);
        statusItem.Text = $"Idle: {FormatDuration(idle)} / {FormatDuration(threshold)}";
        tray.Text = $"Windows S0 Power Override — {FormatDuration(idle)} / {FormatDuration(threshold)}";

        if (idle >= threshold)
            Hibernate();
    }

    private TimeSpan GetEffectiveIdleTime()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info))
            return TimeSpan.Zero;

        uint now = unchecked((uint)Environment.TickCount);
        uint inputAgeMs = unchecked(now - info.dwTime);
        uint baselineAgeMs = unchecked(now - resetBaselineTick);

        // Use whichever timestamp is more recent: last real input or the
        // post-resume/post-unlock baseline. This prevents stale pre-hibernate
        // input timestamps from triggering an immediate second hibernation.
        uint effectiveAgeMs = Math.Min(inputAgeMs, baselineAgeMs);
        return TimeSpan.FromMilliseconds(effectiveAgeMs);
    }

    private void Hibernate()
    {
        if (hibernateIssued || exiting) return;
        hibernateIssued = true;
        timer.Stop();
        ApplyExecutionState();

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "shutdown.exe"),
                Arguments = "/h",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            System.Diagnostics.Process.Start(psi);
        }
        catch (Exception ex)
        {
            hibernateIssued = false;
            timer.Start();
            ApplyExecutionState();
            MessageBox.Show($"Could not hibernate: {ex.Message}", "Windows S0 Power Override",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void OpenSettings()
    {
        using var form = new SettingsForm(settings.IdleMinutes, settings.PreventSystemSleep);
        if (form.ShowDialog() != DialogResult.OK) return;

        settings.IdleMinutes = form.SelectedIdleMinutes;
        settings.PreventSystemSleep = form.PreventSystemSleep;
        SaveSettings();

        // Applying a changed threshold starts a fresh timer, avoiding an
        // immediate hibernation just because the previous threshold was shorter.
        ResetIdleBaseline();
        preventSleepItem.CheckedChanged -= PreventSleepItemChanged;
        preventSleepItem.Checked = settings.PreventSystemSleep;
        preventSleepItem.CheckedChanged += PreventSleepItemChanged;
        ApplyExecutionState();
    }

    private void PreventSleepItemChanged(object? sender, EventArgs e)
    {
        settings.PreventSystemSleep = preventSleepItem.Checked;
        SaveSettings();
        ApplyExecutionState();
    }

    private void ShowStatus()
    {
        MessageBox.Show(
            $"Configured idle timeout: {settings.IdleMinutes} minute(s)\n" +
            $"Current idle time: {FormatDuration(GetEffectiveIdleTime())}\n" +
            $"Prevent system sleep while monitoring: {(settings.PreventSystemSleep ? "On" : "Off")}\n" +
            $"Start with Windows: {(IsStartupEnabled() ? "On" : "Off")}\n\n" +
            "The inactivity counter resets when Windows resumes or the user logs on/unlocks.",
            "Windows S0 Power Override", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private AppSettings LoadSettings()
    {
        try
        {
            if (File.Exists(settingsPath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(settingsPath));
                if (loaded is not null)
                {
                    loaded.IdleMinutes = Math.Clamp(loaded.IdleMinutes, 1, 1440);
                    return loaded;
                }
            }
        }
        catch { /* Use defaults if the settings file is damaged or unreadable. */ }

        return new AppSettings();
    }

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(settingsDirectory);
            File.WriteAllText(settingsPath,
                JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not save settings: {ex.Message}", "Windows S0 Power Override",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(StartupKeyPath, false);
        var value = key?.GetValue(StartupValueName) as string;
        if (string.IsNullOrWhiteSpace(value)) return false;

        string expected = Environment.ProcessPath ?? "";
        return value.Contains(expected, StringComparison.OrdinalIgnoreCase);
    }

    private void SetStartup(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(StartupKeyPath, true)
                ?? Registry.CurrentUser.CreateSubKey(StartupKeyPath, true);

            if (enabled)
            {
                string exe = Environment.ProcessPath
                    ?? throw new InvalidOperationException("Cannot determine application path.");
                key.SetValue(StartupValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(StartupValueName, false);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not update startup setting: {ex.Message}", "Windows S0 Power Override",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            startupItem.CheckedChanged -= StartupItemChanged;
            startupItem.Checked = IsStartupEnabled();
            startupItem.CheckedChanged += StartupItemChanged;
        }
    }

    private void StartupItemChanged(object? sender, EventArgs e) => SetStartup(startupItem.Checked);

    private static string FormatDuration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        int hours = (int)t.TotalHours;
        return $"{hours:00}:{t.Minutes:00}:{t.Seconds:00}";
    }

    protected override void ExitThreadCore()
    {
        exiting = true;
        timer.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SetThreadExecutionState(ES_CONTINUOUS);
        tray.Visible = false;
        tray.Dispose();
        timer.Dispose();
        dispatcher.Dispose();
        base.ExitThreadCore();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);

    private sealed class AppSettings
    {
        public int IdleMinutes { get; set; } = 60;
        public bool PreventSystemSleep { get; set; } = true;
    }

    private sealed class SettingsForm : Form
    {
        private readonly ComboBox idleCombo;
        private readonly CheckBox preventSleepCheck;
        public int SelectedIdleMinutes { get; private set; }
        public bool PreventSystemSleep => preventSleepCheck.Checked;

        private static readonly int[] Options = { 1, 2, 5, 10, 15, 20, 30, 45, 60, 90, 120, 180, 240, 360, 480, 720, 1440 };

        public SettingsForm(int currentMinutes, bool preventSystemSleep)
        {
            Text = "Idle Hibernate Settings";
            FormBorderStyle = FormBorderStyle.Sizable;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = true;
            MinimizeBox = false;
            MinimumSize = new System.Drawing.Size(560, 330);
            ShowInTaskbar = false;
            ClientSize = new System.Drawing.Size(600, 360);
            AutoScaleMode = AutoScaleMode.Dpi;
            Padding = new Padding(20);

            var label = new Label
            {
                Text = "Hibernate after user inactivity:",
                AutoSize = true,
                Left = 24,
                Top = 28
            };
            idleCombo = new ComboBox
            {
                Left = 24,
                Top = 62,
                Width = 540,
                DropDownStyle = ComboBoxStyle.DropDownList,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            foreach (int minutes in Options)
                idleCombo.Items.Add(new IdleOption(minutes));
            int selectedIndex = Array.FindIndex(Options, x => x == currentMinutes);
            if (selectedIndex < 0)
            {
                idleCombo.Items.Add(new IdleOption(currentMinutes));
                selectedIndex = idleCombo.Items.Count - 1;
            }
            idleCombo.SelectedIndex = selectedIndex;

            preventSleepCheck = new CheckBox
            {
                Text = "Prevent automatic system sleep while monitoring (uses more battery)",
                Left = 24,
                Top = 112,
                Width = 540,
                Height = 60,
                Checked = preventSystemSleep,
                AutoSize = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            var ok = new Button
            {
                Text = "Save",
                Left = 400,
                Top = 292,
                Width = 85,
                Height = 36,
                DialogResult = DialogResult.OK,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            var cancel = new Button
            {
                Text = "Cancel",
                Left = 495,
                Top = 292,
                Width = 85,
                Height = 36,
                DialogResult = DialogResult.Cancel,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            ok.Click += (_, _) =>
            {
                if (idleCombo.SelectedItem is IdleOption option)
                    SelectedIdleMinutes = option.Minutes;
            };

            Controls.AddRange(new Control[] { label, idleCombo, preventSleepCheck, ok, cancel });
            AcceptButton = ok;
            CancelButton = cancel;
        }

        private sealed class IdleOption
        {
            public int Minutes { get; }
            public IdleOption(int minutes) => Minutes = minutes;
            public override string ToString()
            {
                if (Minutes < 60) return $"{Minutes} minute(s)";
                if (Minutes == 60) return "1 hour";
                if (Minutes < 1440 && Minutes % 60 == 0) return $"{Minutes / 60} hours";
                if (Minutes < 1440) return $"{Minutes / 60} hour(s), {Minutes % 60} minute(s)";
                return "24 hours";
            }
        }
    }
}
