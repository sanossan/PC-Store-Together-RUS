using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Web.Script.Serialization;

namespace PcTogetherRus
{
    internal static class InstallTransaction
    {
        internal const string GameExe = "PC Store Simulator Together.exe";
        private const string GameData = "PC Store Simulator Together_Data";

        private sealed class Manifest
        {
            public string patchedAssetSha256 { get; set; }
            public string patchedAssemblySha256 { get; set; }
        }

        private sealed class Translation
        {
            public string key { get; set; }
            public string value { get; set; }
        }

        private sealed class GameFile
        {
            public string Path;
            public string Original;
            public string Current;
            public string Target;
            public string Backup;
            public string Stage;
            public string BackupStage;
            public string Previous;
        }

        internal static bool IsGameFolder(string folder)
        {
            return File.Exists(Path.Combine(folder, GameExe)) &&
                Directory.Exists(Path.Combine(folder, GameData));
        }

        internal static string Run(string gameFolder, bool restore)
        {
            string root = Path.GetFullPath(gameFolder);
            if (!IsGameFolder(root))
            {
                throw new DirectoryNotFoundException("Укажите папку игры с файлом " + GameExe + ".");
            }
            EnsureGameClosed();
            string data = Path.Combine(root, GameData);
            var manifest = ReadResource<Manifest>("release.json");
            using (var transactionLock = new FileStream(Path.Combine(data, ".pc-together-rus.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose))
            {
                return Apply(data, manifest, restore);
            }
        }

        private static string Apply(string data, Manifest manifest, bool restore)
        {
            string suffix = ".pc-together-rus-" + Guid.NewGuid().ToString("N");
            var locale = Describe(Path.Combine(data, LocalePatch.AssetName), LocalePatch.OriginalHash,
                manifest.patchedAssetSha256, restore, suffix);
            var labels = Describe(Path.Combine(data, "Managed", "Assembly-CSharp.dll"), LocalePatch.AssemblyHash,
                manifest.patchedAssemblySha256, restore, suffix);
            var files = new[] { locale, labels };
            var pending = files.Where(file => file.Current != file.Target).ToArray();
            if (pending.Length == 0)
            {
                return restore ? "Игра уже использует оригинальные файлы." : "Русификатор уже установлен.";
            }

            var replaced = new List<GameFile>();
            try
            {
                foreach (var file in pending)
                {
                    string actual;
                    if (restore)
                    {
                        File.Copy(file.Backup, file.Stage);
                        actual = LocalePatch.FileHash(file.Stage);
                    }
                    else if (file == locale)
                    {
                        var dictionary = ReadResource<Translation[]>("ru.json").ToDictionary(
                            row => row.key, row => row.value, StringComparer.Ordinal);
                        actual = LocalePatch.Build(file.Path, file.Stage, dictionary);
                    }
                    else
                    {
                        actual = LabelPatch.Build(file.Path, file.Stage);
                    }
                    RequireHash(actual, file.Target, file.Stage);
                    if (!File.Exists(file.Backup))
                    {
                        File.Copy(file.Path, file.BackupStage);
                        RequireHash(LocalePatch.FileHash(file.BackupStage), file.Original, file.BackupStage);
                        File.Move(file.BackupStage, file.Backup);
                    }
                }

                EnsureGameClosed();
                foreach (var file in files)
                {
                    RequireHash(LocalePatch.FileHash(file.Path), file.Current, file.Path);
                }
                foreach (var file in pending)
                {
                    File.Replace(file.Stage, file.Path, file.Previous);
                    replaced.Add(file);
                    RequireHash(LocalePatch.FileHash(file.Path), file.Target, file.Path);
                }
            }
            catch (Exception cause)
            {
                var failedRollbacks = new List<Exception>();
                for (int index = replaced.Count - 1; index >= 0; index--)
                {
                    GameFile file = replaced[index];
                    try
                    {
                        File.Replace(file.Previous, file.Path, null);
                        RequireHash(LocalePatch.FileHash(file.Path), file.Current, file.Path);
                    }
                    catch (Exception rollback)
                    {
                        failedRollbacks.Add(rollback);
                    }
                }
                if (failedRollbacks.Count != 0)
                {
                    failedRollbacks.Insert(0, cause);
                    throw new AggregateException("Откат прервался. Резервные копии сохранены. " +
                        "Закройте игру и повторите восстановление.", failedRollbacks);
                }
                throw;
            }
            finally
            {
                foreach (var file in pending)
                {
                    DeleteTemporaryFile(file.Stage);
                    DeleteTemporaryFile(file.BackupStage);
                }
            }
            foreach (var file in replaced)
            {
                DeleteTemporaryFile(file.Previous);
            }
            return restore ? "Оригинальные файлы восстановлены." :
                "Русификатор установлен. Запустите игру и выберите «Русский» в настройках языка.";
        }

        private static GameFile Describe(string path, string original, string patched, bool restore, string suffix)
        {
            string backup = path + ".pc-together-rus-original";
            string current = LocalePatch.FileHash(path);
            bool hasBackup = File.Exists(backup);
            if (hasBackup)
            {
                RequireHash(LocalePatch.FileHash(backup), original, backup);
            }
            if (current != original && (current != patched || !hasBackup))
            {
                throw new InvalidDataException("Требуется Steam-сборка 26897952. Файл отличается от оригинала " +
                    "или установлен без резервной копии." + Environment.NewLine + path);
            }
            return new GameFile
            {
                Path = path,
                Original = original,
                Current = current,
                Target = restore ? original : patched,
                Backup = backup,
                Stage = path + suffix + ".tmp",
                BackupStage = path + suffix + ".backup",
                Previous = path + suffix + ".previous"
            };
        }

        private static void RequireHash(string actual, string expected, string path)
        {
            if (!string.Equals(actual, expected, StringComparison.Ordinal))
            {
                throw new InvalidDataException("Контрольная сумма файла не совпадает." + Environment.NewLine + path);
            }
        }

        private static void EnsureGameClosed()
        {
            Process[] matches = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GameExe));
            foreach (Process process in matches)
            {
                process.Dispose();
            }
            if (matches.Length != 0)
            {
                throw new InvalidOperationException("Закройте PC Store Together и повторите действие.");
            }
        }

        private static void DeleteTemporaryFile(string path)
        {
            try { File.Delete(path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        private static T ReadResource<T>(string name)
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            using (var reader = new StreamReader(stream))
            {
                return new JavaScriptSerializer().Deserialize<T>(reader.ReadToEnd());
            }
        }
    }
}
