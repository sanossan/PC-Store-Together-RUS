using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("Русификатор PC Store Together")]
[assembly: AssemblyDescription("Установщик русской локализации для Steam-сборки 26897952")]
[assembly: AssemblyProduct("PC Store Together RUS")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]

namespace PcTogetherRus
{
    internal static class Setup
    {
        internal const string Title = "Русификатор PC Store Together";

        [STAThread]
        private static int Main(string[] args)
        {
            AppDomain.CurrentDomain.AssemblyResolve += ResolveDependency;
            if (args.Length == 0)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new SetupForm());
                return 0;
            }

            bool elevatedUi = args.Length == 3 && args[0] == "--show-result";
            int start = elevatedUi ? 1 : 0;
            if (args.Length != start + 2 || (args[start] != "--install" && args[start] != "--restore"))
            {
                Console.Error.WriteLine("Использование: --install <папка игры> или --restore <папка игры>.");
                return 2;
            }
            try
            {
                string message = InstallTransaction.Run(args[start + 1], args[start] == "--restore");
                if (elevatedUi)
                {
                    MessageBox.Show(message, Title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else
                {
                    Console.WriteLine(message);
                }
                return 0;
            }
            catch (Exception error)
            {
                if (elevatedUi)
                {
                    MessageBox.Show(error.Message, Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                else
                {
                    Console.Error.WriteLine(error.Message);
                }
                return 1;
            }
        }

        private static Assembly ResolveDependency(object sender, ResolveEventArgs args)
        {
            if (new AssemblyName(args.Name).Name != "Mono.Cecil")
            {
                return null;
            }
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Mono.Cecil.dll"))
            using (var bytes = new MemoryStream())
            {
                stream.CopyTo(bytes);
                return Assembly.Load(bytes.ToArray());
            }
        }

        internal static bool RunElevated(string path, bool restore)
        {
            if (path.IndexOf('"') >= 0)
            {
                throw new ArgumentException("Путь содержит недопустимый символ.");
            }
            int closingSlashes = path.Length - path.TrimEnd('\\').Length;
            string arguments = "--show-result " + (restore ? "--restore" : "--install") + " \"" +
                path + new string('\\', closingSlashes) + "\"";
            using (var process = Process.Start(new ProcessStartInfo(Application.ExecutablePath, arguments)
            {
                UseShellExecute = true,
                Verb = "runas"
            }))
            {
                process.WaitForExit();
                return process.ExitCode == 0;
            }
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly TextBox folder = new TextBox { Dock = DockStyle.Fill };
        private readonly Button browse = new Button { Text = "Обзор…", AutoSize = true };
        private readonly Button install = new Button { Text = "Установить", AutoSize = true };
        private readonly Button restore = new Button { Text = "Восстановить оригинал", AutoSize = true };
        private readonly Label status = new Label { Dock = DockStyle.Fill };
        private bool busy;

        internal SetupForm()
        {
            Text = Setup.Title;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(565, 290);
            MinimumSize = new Size(520, 315);
            MaximizeBox = false;
            Font = new Font("Segoe UI", 10);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 6 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            AddRow(layout, new Label
            {
                Text = "Русификатор v1.0.0",
                AutoSize = true,
                Font = new Font(Font.FontFamily, 15, FontStyle.Bold)
            }, 0);
            AddRow(layout, new Label
            {
                Text = "Steam-сборка 26897952",
                AutoSize = true,
                Margin = new Padding(3, 0, 3, 14)
            }, 1);
            AddRow(layout, new Label { Text = "Папка игры", AutoSize = true }, 2);
            layout.Controls.Add(folder, 0, 3);
            layout.Controls.Add(browse, 1, 3);
            status.Margin = new Padding(3, 10, 3, 6);
            AddRow(layout, status, 4);

            var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
            actions.Controls.Add(install);
            actions.Controls.Add(restore);
            AddRow(layout, actions, 5);
            Controls.Add(layout);
            AcceptButton = install;

            folder.Text = SteamFolder.Detect() ?? "";
            status.Text = "Закройте игру перед установкой или восстановлением.";
            browse.Click += Browse;
            install.Click += async delegate { await Apply(false); };
            restore.Click += async delegate { await Apply(true); };
            FormClosing += delegate(object sender, FormClosingEventArgs eventArgs) { eventArgs.Cancel = busy; };
        }

        private static void AddRow(TableLayoutPanel layout, Control control, int row)
        {
            layout.Controls.Add(control, 0, row);
            layout.SetColumnSpan(control, 2);
        }

        private void Browse(object sender, EventArgs eventArgs)
        {
            using (var dialog = new FolderBrowserDialog
            {
                Description = "Выберите папку с файлом " + InstallTransaction.GameExe,
                ShowNewFolderButton = false,
                SelectedPath = folder.Text
            })
            {
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    folder.Text = dialog.SelectedPath;
                }
            }
        }

        private async Task Apply(bool restoreOriginal)
        {
            string path = folder.Text.Trim();
            if (path.Length == 0)
            {
                status.Text = "Укажите папку игры.";
                folder.Focus();
                return;
            }
            SetBusy(true);
            status.Text = restoreOriginal ? "Восстанавливаю файлы игры…" : "Проверяю файлы и устанавливаю перевод…";
            try
            {
                string fullPath = Path.GetFullPath(path);
                try
                {
                    status.Text = await Task.Run(() => InstallTransaction.Run(fullPath, restoreOriginal));
                    return;
                }
                catch (UnauthorizedAccessException)
                {
                    status.Text = "Windows запросит разрешение на запись в папку игры.";
                }
                bool success = await Task.Run(() => Setup.RunElevated(fullPath, restoreOriginal));
                status.Text = success ? "Готово." : "Действие завершилось с ошибкой.";
            }
            catch (Win32Exception error)
            {
                status.Text = error.NativeErrorCode == 1223 ? "Запрос Windows отменён." : error.Message;
            }
            catch (Exception error)
            {
                status.Text = "Действие завершилось с ошибкой.";
                MessageBox.Show(this, error.Message, Setup.Title, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void SetBusy(bool value)
        {
            busy = value;
            folder.Enabled = browse.Enabled = install.Enabled = restore.Enabled = !value;
            UseWaitCursor = value;
        }
    }

    internal static class SteamFolder
    {
        internal static string Detect()
        {
            try
            {
                var roots = new[]
                {
                    Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string,
                    Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam")
                };
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string root in roots)
                {
                    if (string.IsNullOrWhiteSpace(root) || !seen.Add(root))
                    {
                        continue;
                    }
                    foreach (string library in Libraries(root))
                    {
                        string apps = Path.Combine(library, "steamapps");
                        string manifest = Path.Combine(apps, "appmanifest_4990940.acf");
                        if (!File.Exists(manifest))
                        {
                            continue;
                        }
                        var match = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"]+)\"");
                        if (match.Success && Path.GetFileName(match.Groups[1].Value) == match.Groups[1].Value)
                        {
                            string path = Path.Combine(apps, "common", match.Groups[1].Value);
                            if (InstallTransaction.IsGameFolder(path))
                            {
                                return Path.GetFullPath(path);
                            }
                        }
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (ArgumentException) { }
            return null;
        }

        private static IEnumerable<string> Libraries(string root)
        {
            yield return root;
            string index = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            if (!File.Exists(index))
            {
                yield break;
            }
            foreach (Match match in Regex.Matches(File.ReadAllText(index), "\"path\"\\s+\"([^\"]+)\""))
            {
                yield return match.Groups[1].Value.Replace(@"\\", @"\");
            }
        }
    }
}
