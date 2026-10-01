using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace DarnessTool
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            try
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(
                        Path.Combine(Path.GetTempPath(), "DarnessTool_error.log"),
                        DateTime.Now + "  " + ex + Environment.NewLine);
                }
                catch { }
                MessageBox.Show("Критическая ошибка: " + ex.Message,
                    "DarnessTool", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    internal static class Native
    {
        [DllImport("ntdll.dll")]
        public static extern int NtSuspendProcess(IntPtr hProcess);

        [DllImport("ntdll.dll")]
        public static extern int NtResumeProcess(IntPtr hProcess);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool CloseHandle(IntPtr hObject);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool TerminateProcess(IntPtr hProcess, uint uExitCode);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int RegLoadKey(IntPtr hKey, string lpSubKey, string lpFile);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern int RegUnLoadKey(IntPtr hKey, string lpSubKey);

        public static readonly IntPtr HKEY_LOCAL_MACHINE = new IntPtr(unchecked((int)0x80000002));

        public const uint PROCESS_SUSPEND_RESUME = 0x0800;
        public const uint PROCESS_TERMINATE      = 0x0001;
        public const uint PROCESS_QUERY_LIMITED  = 0x1000;
    }

    internal sealed class InputDialog : Form
    {
        private readonly TextBox _txt;
        public string Value => _txt.Text;

        public InputDialog(string prompt, string caption, string initial)
        {
            Text = caption;
            Width = 500;
            Height = 170;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;

            var lbl = new Label { Text = prompt, Left = 12, Top = 12, Width = 460, Height = 36 };
            _txt = new TextBox { Left = 12, Top = 54, Width = 460, Text = initial ?? "" };
            var ok = new Button { Text = "OK", Left = 310, Top = 90, Width = 78, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = "Отмена", Left = 394, Top = 90, Width = 78, DialogResult = DialogResult.Cancel };

            Controls.Add(lbl);
            Controls.Add(_txt);
            Controls.Add(ok);
            Controls.Add(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly TextBox _txtWinPath;
        private readonly TabControl _tabs;
        private readonly StatusStrip _status;
        private readonly ToolStripStatusLabel _statusLabel;

        private ListView _lvProc;
        private readonly Dictionary<int, bool> _frozen = new Dictionary<int, bool>();
        private System.Windows.Forms.Timer _procTimer;
        private CheckBox _chkAutoRefresh;

        private ListView _lvAutoruns;
        private CheckedListBox _clRestrictions;
        private RichTextBox _rtDisk;
        private TreeView _regTree;
        private ListView _regValues;

        private bool _hivesLoaded;
        private readonly List<string> _loadedHives = new List<string>();

        public MainForm()
        {
            Text = "DarnessTool — Recovery Utility";
            Width = 1180;
            Height = 760;
            MinimumSize = new Size(960, 620);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.FromArgb(40, 40, 40);
            ForeColor = Color.White;

            var menu = new MenuStrip { BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.White };
            var fileMenu = new ToolStripMenuItem("Файл");
            fileMenu.DropDownItems.Add("Выход", null, (s, e) => Close());
            var toolsMenu = new ToolStripMenuItem("Инструменты");
            toolsMenu.DropDownItems.Add("Открыть cmd.exe", null, (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo("cmd.exe") { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show(ex.Message); }
            });
            toolsMenu.DropDownItems.Add("Открыть regedit.exe", null, (s, e) =>
            {
                try { Process.Start(new ProcessStartInfo("regedit.exe") { UseShellExecute = true }); }
                catch (Exception ex) { MessageBox.Show("regedit недоступен: " + ex.Message); }
            });
            toolsMenu.DropDownItems.Add(new ToolStripSeparator());
            toolsMenu.DropDownItems.Add("Очистить IFEO (Image Hijack)", null, (s, e) => CleanIFEO());
            toolsMenu.DropDownItems.Add("Очистить DisallowRun", null, (s, e) => CleanDisallowRun());
            var helpMenu = new ToolStripMenuItem("Справка");
            helpMenu.DropDownItems.Add("О программе", null, (s, e) => MessageBox.Show(
                "DarnessTool  v1.0\nУтилита восстановления Windows.\n",
                "О программе", MessageBoxButtons.OK, MessageBoxIcon.Information));

            menu.Items.Add(fileMenu);
            menu.Items.Add(toolsMenu);
            menu.Items.Add(helpMenu);
            MainMenuStrip = menu;
            Controls.Add(menu);

            _status = new StatusStrip { BackColor = Color.FromArgb(30, 30, 30), ForeColor = Color.White };
            _statusLabel = new ToolStripStatusLabel("Готово") { ForeColor = Color.LightGreen };
            _status.Items.Add(_statusLabel);
            Controls.Add(_status);

            var top = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = Color.FromArgb(30, 30, 30) };
            var lbl = new Label { Text = "Windows:", Left = 10, Top = 16, Width = 70, ForeColor = Color.White };

            _txtWinPath = new TextBox { Left = 80, Top = 12, Width = 480, Text = DetectWindowsPath() };

            var btnBrowse = new Button
            {
                Text = "…", Left = 566, Top = 11, Width = 34, Height = 24,
                BackColor = Color.FromArgb(60, 60, 60), ForeColor = Color.White, FlatStyle = FlatStyle.Flat
            };
            btnBrowse.Click += (s, e) => BrowseWindowsPath();

            var btnLoad = new Button
            {
                Text = "Загрузить кусты реестра", Left = 610, Top = 10, Width = 190, Height = 26,
                BackColor = Color.FromArgb(50, 90, 50), ForeColor = Color.White, FlatStyle = FlatStyle.Flat
            };
            btnLoad.Click += (s, e) => LoadOfflineHives();

            var btnUnload = new Button
            {
                Text = "Выгрузить", Left = 806, Top = 10, Width = 100, Height = 26,
                BackColor = Color.FromArgb(90, 50, 50), ForeColor = Color.White, FlatStyle = FlatStyle.Flat
            };
            btnUnload.Click += (s, e) => UnloadOfflineHives();

            top.Controls.Add(lbl);
            top.Controls.Add(_txtWinPath);
            top.Controls.Add(btnBrowse);
            top.Controls.Add(btnLoad);
            top.Controls.Add(btnUnload);
            Controls.Add(top);

            _tabs = new TabControl { Dock = DockStyle.Fill };
            _tabs.TabPages.Add(BuildProcessTab());
            _tabs.TabPages.Add(BuildAutorunsTab());
            _tabs.TabPages.Add(BuildRestrictionsTab());
            _tabs.TabPages.Add(BuildDiskTab());
            _tabs.TabPages.Add(BuildRegistryTab());
            Controls.Add(_tabs);

            _tabs.BringToFront();
            top.BringToFront();
            menu.BringToFront();
            _status.BringToFront();
        }

        private void SetStatus(string s)
        {
            if (_statusLabel != null) _statusLabel.Text = s;
        }

        private TabPage BuildProcessTab()
        {
            var page = new TabPage("Диспетчер задач") { BackColor = Color.FromArgb(40, 40, 40), ForeColor = Color.White };

            _lvProc = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                MultiSelect = true,
                BackColor = Color.FromArgb(20, 20, 20),
                ForeColor = Color.White
            };
            _lvProc.Columns.Add("PID", 70);
            _lvProc.Columns.Add("Имя", 220);
            _lvProc.Columns.Add("Состояние", 110);
            _lvProc.Columns.Add("Путь к файлу", 700);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = Color.FromArgb(30, 30, 30) };

            var btnRefresh  = MakeButton("Обновить",      8, 10, 100);
            var btnKill     = MakeButton("Завершить",   116, 10, 110, Color.FromArgb(150, 40, 40));
            var btnFreeze   = MakeButton("Заморозить",  234, 10, 110, Color.FromArgb(40, 80, 140));
            var btnUnfreeze = MakeButton("Разморозить", 352, 10, 110, Color.FromArgb(60, 100, 60));
            var btnKillTree = MakeButton("Убить дерево",470, 10, 130, Color.FromArgb(150, 40, 40));

            _chkAutoRefresh = new CheckBox
            {
                Text = "Автообновление (3 сек)", Left = 620, Top = 13, Width = 180, ForeColor = Color.White
            };

            btnRefresh.Click  += (s, e) => RefreshProcesses();
            btnKill.Click     += (s, e) => KillSelected();
            btnFreeze.Click   += (s, e) => FreezeSelected(true);
            btnUnfreeze.Click += (s, e) => FreezeSelected(false);
            btnKillTree.Click += (s, e) => KillTreeSelected();

            bottom.Controls.Add(btnRefresh);
            bottom.Controls.Add(btnKill);
            bottom.Controls.Add(btnFreeze);
            bottom.Controls.Add(btnUnfreeze);
            bottom.Controls.Add(btnKillTree);
            bottom.Controls.Add(_chkAutoRefresh);

            page.Controls.Add(_lvProc);
            page.Controls.Add(bottom);

            _procTimer = new System.Windows.Forms.Timer { Interval = 3000 };
            _procTimer.Tick += (s, e) => RefreshProcesses();
            _chkAutoRefresh.CheckedChanged += (s, e) => _procTimer.Enabled = _chkAutoRefresh.Checked;

            RefreshProcesses();
            return page;
        }

        private Button MakeButton(string text, int left, int top, int width, Color? bg = null)
        {
            return new Button
            {
                Text = text, Left = left, Top = top, Width = width, Height = 26,
                BackColor = bg ?? Color.FromArgb(60, 60, 60),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat
            };
        }

        private void RefreshProcesses()
        {
            if (_lvProc == null) return;
            var selectedPids = new HashSet<int>();
            foreach (ListViewItem it in _lvProc.SelectedItems)
                selectedPids.Add(int.Parse(it.Text));

            _lvProc.BeginUpdate();
            _lvProc.Items.Clear();

            Process[] procs;
            try { procs = Process.GetProcesses(); }
            catch { procs = Array.Empty<Process>(); }

            foreach (var p in procs.OrderBy(x => x.ProcessName, StringComparer.OrdinalIgnoreCase))
            {
                string path = "";
                try { path = p.MainModule?.FileName ?? ""; } catch { path = "(доступ запрещён)"; }

                bool isFrozen = _frozen.TryGetValue(p.Id, out var f) && f;

                var item = new ListViewItem(p.Id.ToString());
                item.SubItems.Add(p.ProcessName);
                item.SubItems.Add(isFrozen ? "❄ Заморожен" : "Работает");
                item.SubItems.Add(path);
                item.Tag = p.Id;
                if (isFrozen) { item.ForeColor = Color.SkyBlue; item.BackColor = Color.FromArgb(30, 40, 60); }
                _lvProc.Items.Add(item);
            }
            _lvProc.EndUpdate();

            foreach (ListViewItem it in _lvProc.Items)
                if (selectedPids.Contains(int.Parse(it.Text))) it.Selected = true;

            SetStatus($"Процессов: {_lvProc.Items.Count}   |   Заморожено: {_frozen.Count}");
        }

        private void FreezeSelected(bool freeze)
        {
            if (_lvProc.SelectedItems.Count == 0) { MessageBox.Show("Выберите процессы."); return; }

            int done = 0;
            foreach (ListViewItem it in _lvProc.SelectedItems)
            {
                int pid = int.Parse(it.Text);
                IntPtr h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, pid);
                if (h == IntPtr.Zero) continue;
                try
                {
                    if (freeze)
                    {
                        Native.NtSuspendProcess(h);
                        _frozen[pid] = true;
                        it.SubItems[2].Text = "❄ Заморожен";
                        it.ForeColor = Color.SkyBlue;
                        it.BackColor = Color.FromArgb(30, 40, 60);
                    }
                    else
                    {
                        Native.NtResumeProcess(h);
                        _frozen.Remove(pid);
                        it.SubItems[2].Text = "Работает";
                        it.ForeColor = Color.White;
                        it.BackColor = Color.FromArgb(20, 20, 20);
                    }
                    done++;
                }
                finally { Native.CloseHandle(h); }
            }
            SetStatus((freeze ? "Заморожено: " : "Разморожено: ") + done);
        }

        private void KillSelected()
        {
            if (_lvProc.SelectedItems.Count == 0) return;
            if (MessageBox.Show($"Завершить {_lvProc.SelectedItems.Count} процесс(ов)?",
                    "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            int done = 0;
            foreach (ListViewItem it in _lvProc.SelectedItems)
            {
                int pid = int.Parse(it.Text);
                try { Process.GetProcessById(pid).Kill(); done++; }
                catch
                {
                    IntPtr h = Native.OpenProcess(Native.PROCESS_TERMINATE, false, pid);
                    if (h != IntPtr.Zero)
                    {
                        try { if (Native.TerminateProcess(h, 1)) done++; }
                        finally { Native.CloseHandle(h); }
                    }
                }
            }
            SetStatus("Завершено: " + done);
            RefreshProcesses();
        }

        private void KillTreeSelected()
        {
            if (_lvProc.SelectedItems.Count == 0) return;
            if (MessageBox.Show("Завершить выбранные процессы вместе с дочерними?",
                    "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            foreach (ListViewItem it in _lvProc.SelectedItems)
            {
                int pid = int.Parse(it.Text);
                try
                {
                    var psi = new ProcessStartInfo("taskkill", $"/F /T /PID {pid}")
                    {
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    Process.Start(psi)?.WaitForExit(5000);
                }
                catch { }
            }
            RefreshProcesses();
        }

        private TabPage BuildAutorunsTab()
        {
            var page = new TabPage("Автозагрузка / Winlogon / Службы / Tasks")
            { BackColor = Color.FromArgb(40, 40, 40), ForeColor = Color.White };

            _lvAutoruns = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                BackColor = Color.FromArgb(20, 20, 20),
                ForeColor = Color.White
            };
            _lvAutoruns.Columns.Add("Категория", 200);
            _lvAutoruns.Columns.Add("Имя", 240);
            _lvAutoruns.Columns.Add("Значение", 660);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = Color.FromArgb(30, 30, 30) };
            var btnScan = MakeButton("Обновить", 8, 10, 110);
            var btnDel  = MakeButton("Удалить запись", 126, 10, 150, Color.FromArgb(150, 40, 40));
            var btnOpen = MakeButton("Открыть папку", 284, 10, 140);
            var btnCopy = MakeButton("Копировать путь", 432, 10, 150);

            btnScan.Click += (s, e) => ScanAutoruns();
            btnDel.Click  += (s, e) => DeleteAutorunEntry();
            btnOpen.Click += (s, e) => OpenAutorunPath();
            btnCopy.Click += (s, e) =>
            {
                if (_lvAutoruns.SelectedItems.Count == 0) return;
                try { Clipboard.SetText(_lvAutoruns.SelectedItems[0].SubItems[2].Text); }
                catch { }
            };

            bottom.Controls.Add(btnScan);
            bottom.Controls.Add(btnDel);
            bottom.Controls.Add(btnOpen);
            bottom.Controls.Add(btnCopy);

            page.Controls.Add(_lvAutoruns);
            page.Controls.Add(bottom);
            page.Enter += (s, e) => ScanAutoruns();
            return page;
        }

        private RegistryKey GetOfflineOrLiveSoftware()
        {
            if (_hivesLoaded)
            {
                var k = Registry.LocalMachine.OpenSubKey("OfflineSOFTWARE");
                if (k != null) return k;
            }
            return Registry.LocalMachine.OpenSubKey("SOFTWARE");
        }

        private RegistryKey GetOfflineOrLiveSystem()
        {
            if (_hivesLoaded)
            {
                var k = Registry.LocalMachine.OpenSubKey("OfflineSYSTEM");
                if (k != null) return k;
            }
            return Registry.LocalMachine.OpenSubKey("SYSTEM");
        }

        private void ScanAutoruns()
        {
            if (_lvAutoruns == null) return;
            _lvAutoruns.BeginUpdate();
            _lvAutoruns.Items.Clear();

            using (var sw = GetOfflineOrLiveSoftware())
            {
                if (sw != null)
                {
                    AddRunEntries(sw, @"Microsoft\Windows\CurrentVersion\Run",           "HKLM\\Run");
                    AddRunEntries(sw, @"Microsoft\Windows\CurrentVersion\RunOnce",       "HKLM\\RunOnce");
                    AddRunEntries(sw, @"Wow6432Node\Microsoft\Windows\CurrentVersion\Run",     "HKLM\\Run (x86)");
                    AddRunEntries(sw, @"Wow6432Node\Microsoft\Windows\CurrentVersion\RunOnce", "HKLM\\RunOnce (x86)");

                    using (var wl = sw.OpenSubKey(@"Microsoft\Windows NT\CurrentVersion\Winlogon"))
                    {
                        if (wl != null)
                        {
                            foreach (var v in new[] { "Shell", "Userinit", "Taskman", "AppSetup",
                                                      "VmApplet", "GinaDLL", "System" })
                            {
                                var val = wl.GetValue(v)?.ToString();
                                if (!string.IsNullOrEmpty(val))
                                    AddAutorunRow("Winlogon", v, val, wl.Name, v);
                            }
                        }
                    }

                    using (var rp = sw.OpenSubKey(@"Microsoft\Windows\CurrentVersion\Policies\Explorer\Run"))
                    {
                        if (rp != null)
                            foreach (var n in rp.GetValueNames())
                                AddAutorunRow("HKLM\\Policies\\Explorer\\Run",
                                    n, rp.GetValue(n)?.ToString() ?? "", rp.Name, n);
                    }
                }
            }

            try
            {
                foreach (var sub in Registry.LocalMachine.GetSubKeyNames())
                {
                    if (!sub.StartsWith("OfflineNTUSER", StringComparison.OrdinalIgnoreCase)) continue;
                    using var nk = Registry.LocalMachine.OpenSubKey(sub);
                    if (nk == null) continue;
                    AddRunEntries(nk, @"Software\Microsoft\Windows\CurrentVersion\Run",     $"({sub})\\Run");
                    AddRunEntries(nk, @"Software\Microsoft\Windows\CurrentVersion\RunOnce", $"({sub})\\RunOnce");
                    AddRunEntries(nk, @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer\Run",
                                      $"({sub})\\Policies\\Run");
                }
            }
            catch { }

            try
            {
                string win = _txtWinPath.Text.TrimEnd('\\');
                string tasksDir = Path.Combine(win, "System32", "Tasks");
                if (Directory.Exists(tasksDir))
                    WalkTaskFiles(tasksDir, "Планировщик задач");
            }
            catch { }

            using (var sys = GetOfflineOrLiveSystem())
            {
                if (sys != null)
                {
                    using var svc = sys.OpenSubKey(@"CurrentControlSet\Services");
                    if (svc != null)
                    {
                        foreach (var s in svc.GetSubKeyNames())
                        {
                            using var sk = svc.OpenSubKey(s);
                            var img = sk?.GetValue("ImagePath")?.ToString();
                            var start = sk?.GetValue("Start");
                            if (img == null) continue;
                            int st = 0;
                            if (start != null) try { st = Convert.ToInt32(start); } catch { }
                            if (st == 2 || st == 0 || st == 1)
                            {
                                var it = new ListViewItem("Служба (Start=" + st + ")");
                                it.SubItems.Add(s);
                                it.SubItems.Add(img);
                                it.Tag = new RowTag { Kind = RowKind.Service, ServiceName = s };
                                _lvAutoruns.Items.Add(it);
                            }
                        }
                    }
                }
            }

            _lvAutoruns.EndUpdate();
            SetStatus("Автозагрузок найдено: " + _lvAutoruns.Items.Count);
        }

        private void WalkTaskFiles(string dir, string category)
        {
            try
            {
                foreach (var f in Directory.GetFiles(dir))
                {
                    var it = new ListViewItem(category);
                    it.SubItems.Add(Path.GetFileName(f));
                    it.SubItems.Add(f);
                    it.Tag = new RowTag { Kind = RowKind.TaskFile, FilePath = f };
                    _lvAutoruns.Items.Add(it);
                }
                foreach (var d in Directory.GetDirectories(dir))
                    WalkTaskFiles(d, category);
            }
            catch { }
        }

        private void AddRunEntries(RegistryKey root, string sub, string cat)
        {
            try
            {
                using var k = root.OpenSubKey(sub);
                if (k == null) return;
                foreach (var name in k.GetValueNames())
                {
                    var val = k.GetValue(name)?.ToString() ?? "";
                    AddAutorunRow(cat, name, val, k.Name, name);
                }
            }
            catch { }
        }

        private void AddAutorunRow(string cat, string name, string val, string keyPath, string valueName)
        {
            var it = new ListViewItem(cat);
            it.SubItems.Add(name);
            it.SubItems.Add(val);
            it.Tag = new RowTag { Kind = RowKind.RegValue, KeyPath = keyPath, ValueName = valueName };
            _lvAutoruns.Items.Add(it);
        }

        private enum RowKind { RegValue, Service, TaskFile }

        private sealed class RowTag
        {
            public RowKind Kind;
            public string KeyPath;
            public string ValueName;
            public string ServiceName;
            public string FilePath;
        }

        private void DeleteAutorunEntry()
        {
            if (_lvAutoruns.SelectedItems.Count == 0) return;
            var it = _lvAutoruns.SelectedItems[0];
            if (!(it.Tag is RowTag tag)) return;

            if (tag.Kind == RowKind.RegValue)
            {
                if (MessageBox.Show($"Удалить значение?\n\n{tag.KeyPath}\n  → {tag.ValueName}",
                        "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                try
                {
                    var hive = ResolveHivePath(tag.KeyPath);
                    if (hive == null) { MessageBox.Show("Не удалось открыть ключ."); return; }
                    using var k = hive.OpenSubKey(StripHivePrefix(tag.KeyPath), true);
                    k?.DeleteValue(tag.ValueName, false);
                    _lvAutoruns.Items.Remove(it);
                    SetStatus("Удалено: " + tag.ValueName);
                }
                catch (Exception ex) { MessageBox.Show(ex.Message); }
            }
            else if (tag.Kind == RowKind.TaskFile)
            {
                if (MessageBox.Show($"Удалить файл задачи?\n\n{tag.FilePath}",
                        "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                try { File.Delete(tag.FilePath); _lvAutoruns.Items.Remove(it); }
                catch (Exception ex) { MessageBox.Show(ex.Message); }
            }
            else if (tag.Kind == RowKind.Service)
            {
                if (MessageBox.Show($"Отключить службу \"{tag.ServiceName}\" (Start=4)?",
                        "Подтверждение", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                try
                {
                    using var sys = GetOfflineOrLiveSystem();
                    using var sk = sys?.OpenSubKey($@"CurrentControlSet\Services\{tag.ServiceName}", true);
                    sk?.SetValue("Start", 4, RegistryValueKind.DWord);
                    MessageBox.Show("Служба отключена. Перезагрузка требуется.");
                }
                catch (Exception ex) { MessageBox.Show(ex.Message); }
            }
        }

        private RegistryKey ResolveHivePath(string fullKeyName)
        {
            if (fullKeyName.StartsWith("HKEY_LOCAL_MACHINE", StringComparison.OrdinalIgnoreCase))
                return Registry.LocalMachine;
            if (fullKeyName.StartsWith("HKEY_CURRENT_USER", StringComparison.OrdinalIgnoreCase))
                return Registry.CurrentUser;
            return null;
        }

        private string StripHivePrefix(string fullKeyName)
        {
            if (fullKeyName.StartsWith("HKEY_LOCAL_MACHINE\\", StringComparison.OrdinalIgnoreCase))
                return fullKeyName.Substring("HKEY_LOCAL_MACHINE\\".Length);
            if (fullKeyName.StartsWith("HKEY_CURRENT_USER\\", StringComparison.OrdinalIgnoreCase))
                return fullKeyName.Substring("HKEY_CURRENT_USER\\".Length);
            return fullKeyName;
        }

        private void OpenAutorunPath()
        {
            if (_lvAutoruns.SelectedItems.Count == 0) return;
            var tag = _lvAutoruns.SelectedItems[0].Tag as RowTag;

            string path = "";
            if (tag != null && tag.Kind == RowKind.TaskFile)
                path = tag.FilePath;
            else
                path = _lvAutoruns.SelectedItems[0].SubItems[2].Text;

            if (string.IsNullOrWhiteSpace(path)) return;

            path = path.Trim().Trim('"');
            int q = path.IndexOf("\" ");
            if (q > 0) path = path.Substring(0, q);
            else
            {
                int sp = path.IndexOf(" -");
                if (sp > 0) path = path.Substring(0, sp);
            }
            path = path.Trim('"').Trim();
            path = Environment.ExpandEnvironmentVariables(path);

            try
            {
                string dir = null;
                try { dir = Path.GetDirectoryName(path); } catch { }
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                    Process.Start("explorer.exe", "\"" + dir + "\"");
                else if (File.Exists(path))
                    Process.Start("explorer.exe", "/select,\"" + path + "\"");
                else
                    MessageBox.Show("Путь не найден: " + path);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private TabPage BuildRestrictionsTab()
        {
            var page = new TabPage("Снятие ограничений")
            { BackColor = Color.FromArgb(40, 40, 40), ForeColor = Color.White };

            var lbl = new Label
            {
                Text = "Отметьте галочками ограничения, которые нужно снять, и нажмите кнопку.\n" +
                       "Для работы с оффлайн-системой сначала загрузите кусты реестра.",
                Dock = DockStyle.Top,
                Height = 44,
                Padding = new Padding(10, 8, 0, 0),
                ForeColor = Color.LightGray
            };

            _clRestrictions = new CheckedListBox
            {
                Dock = DockStyle.Fill,
                CheckOnClick = true,
                Font = new Font("Consolas", 9),
                BackColor = Color.FromArgb(20, 20, 20),
                ForeColor = Color.White
            };
            foreach (var r in RestrictionCatalog.All)
                _clRestrictions.Items.Add(r.Description, true);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Color.FromArgb(30, 30, 30) };
            var btnRemove   = MakeButton("Снять отмеченные",   8, 14, 180);
            var btnAll      = MakeButton("Снять ВСЁ",        196, 14, 140, Color.FromArgb(170, 50, 50));
            var btnFixAssoc = MakeButton("Ассоциации .exe/.reg", 344, 14, 190, Color.FromArgb(60, 90, 60));
            var btnSelAll   = MakeButton("Выбрать всё",      542, 14, 120);
            var btnDeselAll = MakeButton("Снять всё",        670, 14, 120);

            btnRemove.Click  += (s, e) => RemoveRestrictions(true);
            btnAll.Click     += (s, e) => RemoveRestrictions(false);
            btnFixAssoc.Click += (s, e) => FixFileAssociations();
            btnSelAll.Click  += (s, e) => { for (int i = 0; i < _clRestrictions.Items.Count; i++) _clRestrictions.SetItemChecked(i, true); };
            btnDeselAll.Click += (s, e) => { for (int i = 0; i < _clRestrictions.Items.Count; i++) _clRestrictions.SetItemChecked(i, false); };

            bottom.Controls.Add(btnRemove);
            bottom.Controls.Add(btnAll);
            bottom.Controls.Add(btnFixAssoc);
            bottom.Controls.Add(btnSelAll);
            bottom.Controls.Add(btnDeselAll);

            page.Controls.Add(_clRestrictions);
            page.Controls.Add(lbl);
            page.Controls.Add(bottom);
            return page;
        }

        private void RemoveRestrictions(bool checkedOnly)
        {
            if (!_hivesLoaded)
            {
                if (MessageBox.Show("Оффлайн-кусты не загружены.\n" +
                    "Снимать ограничения в ТЕКУЩЕЙ системе?",
                    "Внимание", MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            }

            int done = 0, failed = 0;
            for (int i = 0; i < RestrictionCatalog.All.Count; i++)
            {
                if (checkedOnly && !_clRestrictions.GetItemChecked(i)) continue;
                var r = RestrictionCatalog.All[i];
                if (ApplyRestriction(r)) done++; else failed++;
            }
            SetStatus($"Снято: {done}, не удалось: {failed}");
            MessageBox.Show($"Готово.\nСнято ограничений: {done}\nНе удалось: {failed}");
        }

        private bool ApplyRestriction(Restriction r)
        {
            try
            {
                using var hk = OpenRestrictionHive(r.Hive);
                if (hk == null) return false;
                using var sk = hk.OpenSubKey(r.SubKey, writable: true);
                if (sk == null) return false;
                if (r.ResetTo.HasValue)
                    sk.SetValue(r.ValueName, r.ResetTo.Value, RegistryValueKind.DWord);
                else
                    sk.DeleteValue(r.ValueName, false);
                return true;
            }
            catch { return false; }
        }

        private RegistryKey OpenRestrictionHive(RestrictionHive hive)
        {
            switch (hive)
            {
                case RestrictionHive.HKCU_Offline:
                    if (_hivesLoaded)
                    {
                        var first = _loadedHives.FirstOrDefault(x => x.StartsWith("OfflineNTUSER"));
                        if (first == null) return null;
                        return Registry.LocalMachine.OpenSubKey(first, true);
                    }
                    else return Registry.CurrentUser;

                case RestrictionHive.HKLM_Software:
                    if (_hivesLoaded)
                    {
                        var k = Registry.LocalMachine.OpenSubKey("OfflineSOFTWARE", true);
                        if (k != null) return k;
                    }
                    return Registry.LocalMachine.OpenSubKey("SOFTWARE", true);

                case RestrictionHive.HKLM_System:
                    if (_hivesLoaded)
                    {
                        var k = Registry.LocalMachine.OpenSubKey("OfflineSYSTEM", true);
                        if (k != null) return k;
                    }
                    return Registry.LocalMachine.OpenSubKey("SYSTEM", true);
            }
            return null;
        }

        private void FixFileAssociations()
        {
            try
            {
                RegistryKey hkcr;
                RegistryKey opened = null;
                if (_hivesLoaded)
                {
                    opened = Registry.LocalMachine.OpenSubKey("OfflineSOFTWARE\\Classes", true);
                    hkcr = opened;
                }
                else hkcr = Registry.ClassesRoot;

                if (hkcr == null) { MessageBox.Show("Classes не найден."); return; }

                var map = new Dictionary<string, string>
                {
                    { ".exe", "exefile" }, { ".com", "comfile" }, { ".bat", "batfile" },
                    { ".cmd", "cmdfile" }, { ".reg", "regfile" }, { ".vbs", "VBSFile" },
                    { ".scr", "scrfile" }, { ".pif", "piffile" }, { ".msc", "mscfile" },
                    { ".lnk", "lnkfile" }, { ".inf", "inffile" }, { ".hta", "htafile" }
                };

                int fixedCount = 0;
                foreach (var kv in map)
                {
                    try
                    {
                        using var k = hkcr.CreateSubKey(kv.Key);
                        k?.SetValue("", kv.Value);
                        fixedCount++;
                    }
                    catch { }
                }
                opened?.Dispose();
                MessageBox.Show("Восстановлено ассоциаций: " + fixedCount);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private TabPage BuildDiskTab()
        {
            var page = new TabPage("Проверка диска")
            { BackColor = Color.FromArgb(40, 40, 40), ForeColor = Color.White };

            _rtDisk = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Consolas", 9),
                BackColor = Color.Black,
                ForeColor = Color.LightGreen,
                ReadOnly = true,
                WordWrap = false,
                ScrollBars = RichTextBoxScrollBars.Both
            };

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 56, BackColor = Color.FromArgb(30, 30, 30) };
            var btnChk     = MakeButton("CHKDSK /f",            8, 14, 120, Color.FromArgb(60, 90, 60));
            var btnChkR    = MakeButton("CHKDSK /r",          136, 14, 120, Color.FromArgb(60, 90, 60));
            var btnSfc     = MakeButton("SFC /scannow offline", 264, 14, 190, Color.FromArgb(60, 90, 60));
            var btnDism    = MakeButton("DISM CheckHealth",   462, 14, 170, Color.FromArgb(60, 90, 60));
            var btnDismAll = MakeButton("DISM RestoreHealth", 640, 14, 180, Color.FromArgb(60, 90, 60));
            var btnClear   = MakeButton("Очистить",           828, 14, 100);

            btnChk.Click     += (s, e) => RunExternal("chkdsk", $"{GetDriveLetter()} /f");
            btnChkR.Click    += (s, e) => RunExternal("chkdsk", $"{GetDriveLetter()} /r");
            btnSfc.Click     += (s, e) => RunExternal("sfc",
                $"/scannow /offbootdir={GetDriveLetter()}\\ /offwindir={_txtWinPath.Text.TrimEnd('\\')}");
            btnDism.Click    += (s, e) => RunExternal("dism",
                $"/Image:\"{_txtWinPath.Text.TrimEnd('\\')}\" /Cleanup-Image /CheckHealth");
            btnDismAll.Click += (s, e) => RunExternal("dism",
                $"/Image:\"{_txtWinPath.Text.TrimEnd('\\')}\" /Cleanup-Image /RestoreHealth");
            btnClear.Click   += (s, e) => _rtDisk.Clear();

            bottom.Controls.Add(btnChk);
            bottom.Controls.Add(btnChkR);
            bottom.Controls.Add(btnSfc);
            bottom.Controls.Add(btnDism);
            bottom.Controls.Add(btnDismAll);
            bottom.Controls.Add(btnClear);

            page.Controls.Add(_rtDisk);
            page.Controls.Add(bottom);
            return page;
        }

        private string GetDriveLetter()
        {
            var root = Path.GetPathRoot(_txtWinPath.Text.TrimEnd('\\'));
            return string.IsNullOrEmpty(root) ? "C:" : root.TrimEnd('\\');
        }

        private void RunExternal(string exe, string args)
        {
            _rtDisk.AppendText($"\n===== > {exe} {args} =====\n");
            SetStatus("Выполняется: " + exe + " " + args);

            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            try
            {
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += (s, e) => { if (e.Data != null) AppendDisk(e.Data + "\n"); };
                p.ErrorDataReceived  += (s, e) => { if (e.Data != null) AppendDisk("[err] " + e.Data + "\n"); };
                p.Exited += (s, e) => { SetStatus("Готово: " + exe); };
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                _rtDisk.AppendText("Ошибка запуска: " + ex.Message + "\n");
                SetStatus("Ошибка: " + ex.Message);
            }
        }

        private void AppendDisk(string s)
        {
            if (_rtDisk.InvokeRequired)
                _rtDisk.BeginInvoke(new Action<string>(AppendDisk), s);
            else
            {
                _rtDisk.AppendText(s);
                _rtDisk.SelectionStart = _rtDisk.TextLength;
                _rtDisk.ScrollToCaret();
            }
        }

        private TabPage BuildRegistryTab()
        {
            var page = new TabPage("Редактор реестра")
            { BackColor = Color.FromArgb(40, 40, 40), ForeColor = Color.White };

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                SplitterDistance = 380,
                BackColor = Color.FromArgb(40, 40, 40)
            };

            _regTree = new TreeView
            {
                Dock = DockStyle.Fill,
                BackColor = Color.FromArgb(20, 20, 20),
                ForeColor = Color.White
            };
            _regTree.BeforeExpand += RegTree_BeforeExpand;
            _regTree.AfterSelect += RegTree_AfterSelect;

            _regValues = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                BackColor = Color.FromArgb(20, 20, 20),
                ForeColor = Color.White
            };
            _regValues.Columns.Add("Имя", 220);
            _regValues.Columns.Add("Тип", 110);
            _regValues.Columns.Add("Значение", 520);
            _regValues.DoubleClick += (s, e) => EditRegValue();

            split.Panel1.Controls.Add(_regTree);
            split.Panel2.Controls.Add(_regValues);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 46, BackColor = Color.FromArgb(30, 30, 30) };
            var btnReload = MakeButton("Обновить корни",  8, 10, 150);
            var btnEdit   = MakeButton("Изменить",      166, 10, 130);
            var btnDelete = MakeButton("Удалить",       304, 10, 130, Color.FromArgb(150, 40, 40));
            var btnNewKey = MakeButton("Создать раздел",442, 10, 150);
            var btnNewVal = MakeButton("Создать значение",600, 10, 160);

            btnReload.Click += (s, e) => PopulateRegRoots();
            btnEdit.Click   += (s, e) => EditRegValue();
            btnDelete.Click += (s, e) => DeleteRegValue();
            btnNewKey.Click += (s, e) => CreateRegKey();
            btnNewVal.Click += (s, e) => CreateRegValue();

            bottom.Controls.Add(btnReload);
            bottom.Controls.Add(btnEdit);
            bottom.Controls.Add(btnDelete);
            bottom.Controls.Add(btnNewKey);
            bottom.Controls.Add(btnNewVal);

            page.Controls.Add(split);
            page.Controls.Add(bottom);
            page.Enter += (s, e) => { if (_regTree.Nodes.Count == 0) PopulateRegRoots(); };
            return page;
        }

        private void PopulateRegRoots()
        {
            _regTree.BeginUpdate();
            _regTree.Nodes.Clear();

            AddRootNode("HKEY_LOCAL_MACHINE", Registry.LocalMachine);
            AddRootNode("HKEY_CURRENT_USER", Registry.CurrentUser);
            AddRootNode("HKEY_USERS", Registry.Users);
            AddRootNode("HKEY_CLASSES_ROOT", Registry.ClassesRoot);
            AddRootNode("HKEY_CURRENT_CONFIG", Registry.CurrentConfig);

            try
            {
                foreach (var sub in Registry.LocalMachine.GetSubKeyNames())
                {
                    if (sub.StartsWith("Offline", StringComparison.OrdinalIgnoreCase))
                    {
                        var node = new TreeNode("HKLM\\" + sub);
                        node.Tag = new RegRef { Root = Registry.LocalMachine, Path = sub };
                        node.Nodes.Add(new TreeNode("…"));
                        _regTree.Nodes.Add(node);
                    }
                }
            }
            catch { }

            _regTree.EndUpdate();
        }

        private void AddRootNode(string name, RegistryKey key)
        {
            var node = new TreeNode(name);
            node.Tag = new RegRef { Root = key, Path = "" };
            try
            {
                foreach (var s in key.GetSubKeyNames())
                    node.Nodes.Add(new TreeNode(s));
            }
            catch { }
            _regTree.Nodes.Add(node);
        }

        private sealed class RegRef
        {
            public RegistryKey Root;
            public string Path;
        }

        private void RegTree_BeforeExpand(object sender, TreeViewCancelEventArgs e)
        {
            var n = e.Node;
            if (n.Nodes.Count == 1 && n.Nodes[0].Text == "…")
            {
                n.Nodes.Clear();
                try
                {
                    var r = (RegRef)n.Tag;
                    using var k = r.Root.OpenSubKey(r.Path);
                    if (k != null)
                    {
                        foreach (var s in k.GetSubKeyNames())
                        {
                            var child = new TreeNode(s);
                            child.Tag = new RegRef { Root = r.Root, Path = CombinePath(r.Path, s) };
                            child.Nodes.Add(new TreeNode("…"));
                            n.Nodes.Add(child);
                        }
                    }
                }
                catch { }
            }
        }

        private static string CombinePath(string a, string b)
            => string.IsNullOrEmpty(a) ? b : a + "\\" + b;

        private void RegTree_AfterSelect(object sender, TreeViewEventArgs e)
        {
            _regValues.BeginUpdate();
            _regValues.Items.Clear();
            try
            {
                var r = (RegRef)e.Node.Tag;
                using var k = r.Root.OpenSubKey(r.Path);
                if (k == null) { _regValues.EndUpdate(); return; }

                try
                {
                    var def = k.GetValue("");
                    if (def != null)
                    {
                        var kind = k.GetValueKind("");
                        var it = new ListViewItem("(По умолчанию)");
                        it.SubItems.Add(kind.ToString());
                        it.SubItems.Add(FormatValue(def, kind));
                        it.Tag = "";
                        _regValues.Items.Add(it);
                    }
                }
                catch { }

                foreach (var name in k.GetValueNames())
                {
                    if (name == "") continue;
                    RegistryValueKind kind;
                    object val;
                    try { kind = k.GetValueKind(name); val = k.GetValue(name); }
                    catch { continue; }

                    var it = new ListViewItem(name);
                    it.SubItems.Add(kind.ToString());
                    it.SubItems.Add(FormatValue(val, kind));
                    it.Tag = name;
                    _regValues.Items.Add(it);
                }
            }
            catch { }
            _regValues.EndUpdate();
            SetStatus("Значений: " + _regValues.Items.Count);
        }

        private string FormatValue(object val, RegistryValueKind kind)
        {
            if (val == null) return "";
            if (kind == RegistryValueKind.Binary && val is byte[] b)
            {
                if (b.Length > 256) return BitConverter.ToString(b, 0, 256) + $" … (+{b.Length - 256} байт)";
                return BitConverter.ToString(b).Replace("-", " ");
            }
            if (kind == RegistryValueKind.MultiString && val is string[] arr)
                return string.Join(" | ", arr);
            return val.ToString();
        }

        private void EditRegValue()
        {
            if (_regValues.SelectedItems.Count == 0) return;
            var sel = _regValues.SelectedItems[0];
            var name = sel.Tag?.ToString() ?? "";
            var node = _regTree.SelectedNode;
            if (node == null) return;
            var r = (RegRef)node.Tag;

            string curVal = sel.SubItems[2].Text;
            using var dlg = new InputDialog("Новое значение для \"" +
                (string.IsNullOrEmpty(name) ? "(По умолчанию)" : name) + "\":",
                "Изменить значение", curVal);
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            string input = dlg.Value;
            if (input == curVal) return;

            try
            {
                using var k = r.Root.OpenSubKey(r.Path, true);
                if (k == null) { MessageBox.Show("Нет доступа на запись."); return; }
                var kind = k.GetValueKind(name);
                object value;
                switch (kind)
                {
                    case RegistryValueKind.DWord:
                        value = unchecked((int)uint.Parse(input.Trim())); break;
                    case RegistryValueKind.QWord:
                        value = long.Parse(input.Trim()); break;
                    case RegistryValueKind.Binary:
                        value = ParseHex(input); break;
                    case RegistryValueKind.MultiString:
                        value = input.Split('|').Select(x => x.Trim()).ToArray(); break;
                    default:
                        value = input; break;
                }
                k.SetValue(name, value, kind);
                RegTree_AfterSelect(this, new TreeViewEventArgs(node));
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private static byte[] ParseHex(string s)
        {
            s = s.Replace("-", "").Replace(" ", "");
            if (s.Length % 2 != 0) s = "0" + s;
            var bytes = new byte[s.Length / 2];
            for (int i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(s.Substring(i * 2, 2), 16);
            return bytes;
        }

        private void DeleteRegValue()
        {
            if (_regValues.SelectedItems.Count == 0) return;
            var name = _regValues.SelectedItems[0].Tag?.ToString() ?? "";
            var node = _regTree.SelectedNode;
            if (node == null) return;
            var r = (RegRef)node.Tag;
            if (MessageBox.Show($"Удалить значение \"{name}\"?", "Подтверждение",
                    MessageBoxButtons.YesNo) != DialogResult.Yes) return;
            try
            {
                using var k = r.Root.OpenSubKey(r.Path, true);
                k?.DeleteValue(name, false);
                RegTree_AfterSelect(this, new TreeViewEventArgs(node));
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private void CreateRegKey()
        {
            var node = _regTree.SelectedNode;
            if (node == null) return;
            var r = (RegRef)node.Tag;
            using var dlg = new InputDialog("Имя нового раздела:", "Создать раздел", "");
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            string name = dlg.Value.Trim();
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                using var k = r.Root.OpenSubKey(r.Path, true);
                k?.CreateSubKey(name);
                node.Nodes.Clear();
                node.Nodes.Add(new TreeNode("…"));
                node.Collapse();
                node.Expand();
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private void CreateRegValue()
        {
            var node = _regTree.SelectedNode;
            if (node == null) return;
            var r = (RegRef)node.Tag;
            using var dlg = new InputDialog("Имя значения (или пусто для (По умолчанию)):",
                "Создать значение", "");
            if (dlg.ShowDialog(this) != DialogResult.OK) return;
            string name = dlg.Value;

            using var dlg2 = new InputDialog("Значение (строка):", "Значение", "");
            if (dlg2.ShowDialog(this) != DialogResult.OK) return;

            try
            {
                using var k = r.Root.OpenSubKey(r.Path, true);
                k?.SetValue(name, dlg2.Value, RegistryValueKind.String);
                RegTree_AfterSelect(this, new TreeViewEventArgs(node));
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        }

        private static string DetectWindowsPath()
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    var p = Path.Combine(drive.RootDirectory.FullName, "Windows", "System32", "config", "SOFTWARE");
                    if (File.Exists(p))
                        return Path.Combine(drive.RootDirectory.FullName, "Windows");
                }
                catch { }
            }
            return @"C:\Windows";
        }

        private void BrowseWindowsPath()
        {
            using var fbd = new FolderBrowserDialog
            {
                Description = "Выберите папку Windows целевой системы",
                SelectedPath = _txtWinPath.Text
            };
            if (fbd.ShowDialog(this) == DialogResult.OK)
                _txtWinPath.Text = fbd.SelectedPath;
        }

        private void LoadOfflineHives()
        {
            string win = _txtWinPath.Text.TrimEnd('\\');
            string cfg = Path.Combine(win, "System32", "config");
            if (!Directory.Exists(cfg))
            {
                MessageBox.Show("Не найдено: " + cfg);
                return;
            }

            UnloadOfflineHives();

            TryLoadHive("OfflineSYSTEM",   Path.Combine(cfg, "SYSTEM"));
            TryLoadHive("OfflineSOFTWARE", Path.Combine(cfg, "SOFTWARE"));
            TryLoadHive("OfflineSAM",      Path.Combine(cfg, "SAM"));
            TryLoadHive("OfflineSECURITY", Path.Combine(cfg, "SECURITY"));
            TryLoadHive("OfflineDEFAULT",  Path.Combine(cfg, "DEFAULT"));
            TryLoadHive("OfflineBCD00000000", Path.Combine(cfg, "BCD00000000"));

            try
            {
                string parent = Directory.GetParent(win)?.FullName;
                if (parent != null)
                {
                    string usersDir = Path.Combine(parent, "Users");
                    if (Directory.Exists(usersDir))
                    {
                        foreach (var dir in Directory.GetDirectories(usersDir))
                        {
                            var nt = Path.Combine(dir, "NTUSER.DAT");
                            if (File.Exists(nt))
                            {
                                string profileName = Path.GetFileName(dir);
                                string key = "OfflineNTUSER_" + Sanitize(profileName);
                                TryLoadHive(key, nt);
                            }
                        }
                    }
                }
            }
            catch { }

            _hivesLoaded = _loadedHives.Count > 0;
            SetStatus("Загружено кустов: " + _loadedHives.Count);

            MessageBox.Show(_hivesLoaded
                ? "Успешно загружено:\n" + string.Join("\n", _loadedHives)
                : "Не удалось загрузить ни одного куста.\nПроверьте путь и права администратора.");
        }

        private static string Sanitize(string s)
        {
            var sb = new StringBuilder();
            foreach (var c in s)
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            return sb.ToString();
        }

        private void TryLoadHive(string name, string file)
        {
            try
            {
                if (!File.Exists(file)) return;
                int r = Native.RegLoadKey(Native.HKEY_LOCAL_MACHINE, name, file);
                if (r == 0) _loadedHives.Add(name);
            }
            catch { }
        }

        private void UnloadOfflineHives()
        {
            foreach (var name in _loadedHives.ToList())
            {
                try { Native.RegUnLoadKey(Native.HKEY_LOCAL_MACHINE, name); } catch { }
            }
            _loadedHives.Clear();
            _hivesLoaded = false;
            SetStatus("Кусты выгружены.");
        }

        private void CleanIFEO()
        {
            if (MessageBox.Show("Удалить все ключи Image File Execution Options с параметром Debugger?\n" +
                "(Этим вирусы часто блокируют taskmgr.exe, regedit.exe и др.)",
                "Очистка IFEO", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;

            int removed = 0;
            try
            {
                RegistryKey ifeo = _hivesLoaded
                    ? Registry.LocalMachine.OpenSubKey(@"OfflineSOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options", true)
                    : Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options", true);

                if (ifeo == null) { MessageBox.Show("Ключ IFEO не найден."); return; }

                foreach (var sub in ifeo.GetSubKeyNames())
                {
                    using var sk = ifeo.OpenSubKey(sub, true);
                    if (sk?.GetValue("Debugger") != null)
                    {
                        try { sk.DeleteValue("Debugger", false); removed++; } catch { }
                    }
                }
                ifeo.Dispose();
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); return; }

            MessageBox.Show("Удалено вредных Debugger-значений: " + removed);
        }

        private void CleanDisallowRun()
        {
            if (MessageBox.Show("Удалить разделы DisallowRun и RestrictRun из политик Explorer?",
                "Подтверждение", MessageBoxButtons.YesNo) != DialogResult.Yes) return;

            int removed = 0;
            try
            {
                RegistryKey hk;
                string path;
                if (_hivesLoaded)
                {
                    var firstUser = _loadedHives.FirstOrDefault(x => x.StartsWith("OfflineNTUSER"));
                    if (firstUser == null) { MessageBox.Show("Нет загруженного NTUSER."); return; }
                    hk = Registry.LocalMachine;
                    path = firstUser + @"\Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
                }
                else
                {
                    hk = Registry.CurrentUser;
                    path = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
                }

                using var ex = hk.OpenSubKey(path, true);
                if (ex != null)
                {
                    foreach (var sub in new[] { "DisallowRun", "RestrictRun" })
                    {
                        try { ex.DeleteSubKeyTree(sub, false); removed++; } catch { }
                    }
                    try { ex.DeleteValue("DisallowRun", false); removed++; } catch { }
                    try { ex.DeleteValue("RestrictRun", false); removed++; } catch { }
                }
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); return; }
            MessageBox.Show("Удалено: " + removed);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            foreach (var pid in _frozen.Keys.ToList())
            {
                try
                {
                    IntPtr h = Native.OpenProcess(Native.PROCESS_SUSPEND_RESUME, false, pid);
                    if (h != IntPtr.Zero)
                    {
                        Native.NtResumeProcess(h);
                        Native.CloseHandle(h);
                    }
                }
                catch { }
            }
            _frozen.Clear();
            UnloadOfflineHives();
            base.OnFormClosing(e);
        }
    }

    internal enum RestrictionHive
    {
        HKCU_Offline,
        HKLM_Software,
        HKLM_System
    }

    internal sealed class Restriction
    {
        public RestrictionHive Hive { get; set; }
        public string SubKey { get; set; }
        public string ValueName { get; set; }
        public string Description { get; set; }
        public int? ResetTo { get; set; }
    }

    internal static class RestrictionCatalog
    {
        public static readonly List<Restriction> All = new List<Restriction>
        {
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\System", ValueName = "DisableTaskMgr", Description = "[HKCU] Заблокирован диспетчер задач" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\System", ValueName = "DisableRegistryTools", Description = "[HKCU] Заблокирован редактор реестра" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\System", ValueName = "DisableCMD", Description = "[HKCU] Заблокирована командная строка" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\System", ValueName = "DisableLockWorkstation", Description = "[HKCU] Заблокирована блокировка рабочей станции" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\System", ValueName = "DisableChangePassword", Description = "[HKCU] Запрет смены пароля" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\System", ValueName = "HideFastUserSwitching", Description = "[HKCU] Скрыто быстрое переключение пользователей", ResetTo = 0 },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Policies\Microsoft\Windows\System", ValueName = "DisableCMD", Description = "[HKCU] Заблокирована CMD (политика)" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Policies\Microsoft\Windows\System", ValueName = "DisableTaskMgr", Description = "[HKCU] Диспетчер задач (политика)" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", ValueName = "NoControlPanel", Description = "[HKCU] Заблокирована панель управления" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", ValueName = "NoRun", Description = "[HKCU] Убран пункт «Выполнить»" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", ValueName = "NoFind", Description = "[HKCU] Убран поиск" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", ValueName = "NoFolderOptions", Description = "[HKCU] Скрыты «Параметры папок»" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", ValueName = "NoViewContextMenu", Description = "[HKCU] Заблокировано контекстное меню" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", ValueName = "NoDesktop", Description = "[HKCU] Скрыт рабочий стол" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", ValueName = "NoTrayContextMenu", Description = "[HKCU] Заблокировано меню трея" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", ValueName = "NoSetTaskbar", Description = "[HKCU] Заблокированы настройки панели задач" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", ValueName = "NoClose", Description = "[HKCU] Заблокировано завершение работы" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", ValueName = "NoLogoff", Description = "[HKCU] Заблокирован выход из системы" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", ValueName = "NoWindowsUpdate", Description = "[HKCU] Заблокирован Windows Update" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", ValueName = "NoNetworkConnections", Description = "[HKCU] Заблокированы сетевые подключения" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", ValueName = "NoInternetIcon", Description = "[HKCU] Скрыт значок Internet Explorer" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", ValueName = "NoStartMenuSubFolders", Description = "[HKCU] Убраны вложенные папки меню Пуск" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", ValueName = "NoTrayItemsDisplay", Description = "[HKCU] Скрыт трей" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", ValueName = "NoFileMenu", Description = "[HKCU] Убрано меню «Файл»" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", ValueName = "NoSMMyPictures", Description = "[HKCU] Скрыт раздел «Мои рисунки»" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer", ValueName = "ForceClassicControlPanel", Description = "[HKCU] Классический вид панели управления", ResetTo = 0 },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\ActiveDesktop", ValueName = "NoChangingWallPaper", Description = "[HKCU] Заблокирована смена обоев" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\ActiveDesktop", ValueName = "NoHTMLWallPaper", Description = "[HKCU] Заблокированы HTML-обои" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\WinOldApp", ValueName = "Disabled", Description = "[HKCU] Заблокированы старые приложения" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\WinOldApp", ValueName = "NoRealMode", Description = "[HKCU] Заблокирован реальный режим" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", ValueName = "Hidden", Description = "[HKCU] Скрытые файлы отключены (сброс в 1)", ResetTo = 1 },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", ValueName = "ShowSuperHidden", Description = "[HKCU] Системные файлы скрыты (сброс в 1)", ResetTo = 1 },
            new Restriction { Hive = RestrictionHive.HKLM_Software, SubKey = @"Microsoft\Windows\CurrentVersion\Policies\System", ValueName = "DisableTaskMgr", Description = "[HKLM] Заблокирован диспетчер задач" },
            new Restriction { Hive = RestrictionHive.HKLM_Software, SubKey = @"Microsoft\Windows\CurrentVersion\Policies\System", ValueName = "DisableRegistryTools", Description = "[HKLM] Заблокирован regedit" },
            new Restriction { Hive = RestrictionHive.HKLM_Software, SubKey = @"Microsoft\Windows\CurrentVersion\Policies\System", ValueName = "DisableCMD", Description = "[HKLM] Заблокирована CMD" },
            new Restriction { Hive = RestrictionHive.HKLM_Software, SubKey = @"Microsoft\Windows\CurrentVersion\Policies\Explorer", ValueName = "NoControlPanel", Description = "[HKLM] Заблокирована панель управления" },
            new Restriction { Hive = RestrictionHive.HKLM_Software, SubKey = @"Microsoft\Windows\CurrentVersion\Policies\Explorer", ValueName = "NoFolderOptions", Description = "[HKLM] Параметры папок" },
            new Restriction { Hive = RestrictionHive.HKLM_Software, SubKey = @"Microsoft\Windows\CurrentVersion\Policies\Explorer", ValueName = "NoRun", Description = "[HKLM] Пункт «Выполнить»" },
            new Restriction { Hive = RestrictionHive.HKLM_System, SubKey = @"CurrentControlSet\Control\StorageDevicePolicies", ValueName = "WriteProtect", Description = "[SYSTEM] Запрещена запись на USB (сброс в 0)", ResetTo = 0 },
            new Restriction { Hive = RestrictionHive.HKLM_System, SubKey = @"CurrentControlSet\Services\USBSTOR", ValueName = "Start", Description = "[SYSTEM] USB-накопители отключены (сброс в 3)", ResetTo = 3 },
            new Restriction { Hive = RestrictionHive.HKLM_System, SubKey = @"CurrentControlSet\Services\Cdrom", ValueName = "Start", Description = "[SYSTEM] CD/DVD отключены (сброс в 1)", ResetTo = 1 },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Network", ValueName = "NoNetSetup", Description = "[HKCU] Заблокирована настройка сети" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Network", ValueName = "NoNetSetupIDPage", Description = "[HKCU] Скрыта вкладка «Идентификация»" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Network", ValueName = "NoNetSetupSecurityPage", Description = "[HKCU] Скрыта вкладка «Управление доступом»" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Network", ValueName = "NoFileSharingControl", Description = "[HKCU] Скрыты настройки общего доступа" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Internet Explorer\Restrictions", ValueName = "NoBrowserOptions", Description = "[HKCU] Заблокированы настройки IE" },
            new Restriction { Hive = RestrictionHive.HKCU_Offline, SubKey = @"Software\Policies\Microsoft\Internet Explorer\Restrictions", ValueName = "NoBrowserOptions", Description = "[HKCU] IE (политика)" },
        };
    }
}
