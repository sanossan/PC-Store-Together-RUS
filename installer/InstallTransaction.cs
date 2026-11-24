using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace PcTogetherRus
{
    public sealed class ReleaseManifest
    {
        public string version { get; set; }
        public int steamAppId { get; set; }
        public long steamBuildId { get; set; }
        public string patchedAssetSha256 { get; set; }
        public string patchedAssemblySha256 { get; set; }
        public PreviousPatch[] previousPatches { get; set; }

        public static ReleaseManifest Parse(string json)
        {
            var release = new JavaScriptSerializer().Deserialize<ReleaseManifest>(json);
            if (release == null || release.steamAppId <= 0 || release.steamBuildId <= 0)
            {
                throw new InvalidDataException("Некорректные сведения о сборке Steam в манифесте.");
            }
            Version current = ParseVersion(release.version);
            release.patchedAssetSha256 = ParseHash(release.patchedAssetSha256);
            release.patchedAssemblySha256 = ParseHash(release.patchedAssemblySha256);
            release.previousPatches = release.previousPatches ?? new PreviousPatch[0];
            var seen = new HashSet<Version>();
            foreach (var patch in release.previousPatches)
            {
                Version previous = ParseVersion(patch == null ? null : patch.version);
                if (previous.CompareTo(current) >= 0 || !seen.Add(previous))
                {
                    throw new InvalidDataException("Некорректная история версий в манифесте.");
                }
                patch.patchedAssetSha256 = ParseHash(patch.patchedAssetSha256);
                patch.patchedAssemblySha256 = ParseHash(patch.patchedAssemblySha256);
            }
            return release;
        }

        private static Version ParseVersion(string value)
        {
            Version version;
            if (!Version.TryParse(value, out version) || version.Build < 0 || version.Revision >= 0 ||
                version.ToString(3) != value)
            {
                throw new InvalidDataException("Некорректный номер версии в манифесте.");
            }
            return version;
        }

        private static string ParseHash(string hash)
        {
            if (hash == null || !Regex.IsMatch(hash, @"\A[0-9a-fA-F]{64}\z"))
            {
                throw new InvalidDataException("Некорректная контрольная сумма в манифесте.");
            }
            return hash.ToUpperInvariant();
        }

        public sealed class PreviousPatch
        {
            public string version { get; set; }
            public string patchedAssetSha256 { get; set; }
            public string patchedAssemblySha256 { get; set; }
        }
    }

    public static class InstallTransaction
    {
        internal const string GameExe = "PC Store Simulator Together.exe";
        private const string GameData = "PC Store Simulator Together_Data";

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

        public static string Run(string gameFolder, bool restore, string releaseJson, string russianJson)
        {
            return Run(gameFolder, restore, ReleaseManifest.Parse(releaseJson), russianJson);
        }

        internal static string Run(string gameFolder, bool restore, ReleaseManifest release, string russianJson)
        {
            string root = Path.GetFullPath(gameFolder);
            if (!IsGameFolder(root))
            {
                throw new DirectoryNotFoundException("Укажите папку игры с файлом " + GameExe + ".");
            }
            EnsureGameClosed();
            string data = Path.Combine(root, GameData);
            using (var transactionLock = new FileStream(Path.Combine(data, ".pc-together-rus.lock"),
                FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose))
            {
                return Apply(data, release, russianJson, restore);
            }
        }

        private static string Apply(string data, ReleaseManifest release, string russianJson, bool restore)
        {
            string suffix = ".pc-together-rus-" + Guid.NewGuid().ToString("N");
            var locale = Describe(Path.Combine(data, LocalePatch.AssetName), LocalePatch.OriginalHash,
                release.patchedAssetSha256, release.previousPatches.Select(patch => patch.patchedAssetSha256),
                release.steamBuildId, restore, suffix);
            var labels = Describe(Path.Combine(data, "Managed", "Assembly-CSharp.dll"), LocalePatch.AssemblyHash,
                release.patchedAssemblySha256, release.previousPatches.Select(patch => patch.patchedAssemblySha256),
                release.steamBuildId, restore, suffix);
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
                    string source = file.Current == file.Original ? file.Path : file.Backup;
                    string actual;
                    if (restore)
                    {
                        File.Copy(file.Backup, file.Stage);
                        actual = LocalePatch.FileHash(file.Stage);
                    }
                    else if (file == locale)
                    {
                        var dictionary = new JavaScriptSerializer().Deserialize<Translation[]>(russianJson).ToDictionary(
                            row => row.key, row => row.value, StringComparer.Ordinal);
                        actual = LocalePatch.Build(source, file.Stage, dictionary);
                    }
                    else
                    {
                        actual = LabelPatch.Build(source, file.Stage);
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

        private static GameFile Describe(string path, string original, string patched, IEnumerable<string> previous,
            long steamBuildId, bool restore, string suffix)
        {
            string legacyBackup = path + ".pc-together-rus-original";
            string backup = legacyBackup + "." + steamBuildId;
            string current = LocalePatch.FileHash(path);
            bool hasBackup = File.Exists(backup);
            if (current != original && current != patched && !previous.Contains(current))
            {
                throw new InvalidDataException("Неизвестная контрольная сумма игрового файла. " +
                    "Проверьте версию игры и целостность файлов в Steam. Требуется Steam-сборка " +
                    steamBuildId + "." + Environment.NewLine + path);
            }
            bool validBackup = hasBackup && LocalePatch.FileHash(backup) == original;
            if (!validBackup && File.Exists(legacyBackup) && LocalePatch.FileHash(legacyBackup) == original)
            {
                backup = legacyBackup;
                validBackup = true;
            }
            if (!validBackup && (hasBackup || current != original))
            {
                throw new InvalidDataException("Резервная копия оригинала отсутствует или повреждена. " +
                    "Проверьте целостность файлов в Steam и резервную копию для сборки " +
                    steamBuildId + "." + Environment.NewLine + backup);
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
    }
}
