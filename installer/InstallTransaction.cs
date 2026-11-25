using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
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

        internal static string ParseHash(string hash)
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
        private const string BackupSuffix = ".pc-together-rus-original";
        private const string VerifySteam = "Проверьте целостность файлов игры в Steam и повторите действие.";

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

        private sealed class InstalledBuild
        {
            public InstalledFile Asset { get; set; }
            public InstalledFile Assembly { get; set; }

            public static InstalledBuild Read(string path)
            {
                var build = new JavaScriptSerializer().Deserialize<InstalledBuild>(File.ReadAllText(path));
                if (build == null || build.Asset == null || build.Assembly == null)
                {
                    throw new InvalidDataException("Повреждены сведения об установленном переводе.");
                }
                build.Asset.Validate();
                build.Assembly.Validate();
                return build;
            }
        }

        private sealed class InstalledFile
        {
            public string Original { get; set; }
            public List<string> Patched { get; set; }

            public void Validate()
            {
                Original = ReleaseManifest.ParseHash(Original);
                if (Patched == null || Patched.Count == 0)
                {
                    throw new InvalidDataException("Отсутствуют контрольные суммы установленного перевода.");
                }
                Patched = Patched.Select(ReleaseManifest.ParseHash).ToList();
            }
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
                long? detectedBuild = ReadSteamBuild(root, release.steamAppId);
                if (detectedBuild.HasValue && detectedBuild.Value < release.steamBuildId)
                {
                    throw new InvalidDataException("Установлена Steam-сборка " + detectedBuild.Value +
                        ". Требуется сборка " + release.steamBuildId + " или новее. Обновите игру в Steam.");
                }
                return Apply(root, data, release, detectedBuild, russianJson, restore);
            }
        }

        private static string Apply(string root, string data, ReleaseManifest release, long? detectedBuild,
            string russianJson, bool restore)
        {
            string suffix = ".pc-together-rus-" + Guid.NewGuid().ToString("N");
            long buildId = detectedBuild ?? release.steamBuildId;
            bool compatible = buildId > release.steamBuildId;
            string document = Path.Combine(data, ".pc-together-rus." + buildId + ".json");
            string documentStage = document + suffix + ".tmp";
            string assetPath = Path.Combine(data, LocalePatch.AssetName);
            string assemblyPath = Path.Combine(data, "Managed", "Assembly-CSharp.dll");
            InstalledBuild installed = null;
            GameFile locale, labels;
            if (compatible)
            {
                if (File.Exists(document))
                {
                    try
                    {
                        installed = InstalledBuild.Read(document);
                    }
                    catch (Exception error)
                    {
                        if (!IsCompatibilityError(error))
                        {
                            throw;
                        }
                        throw new InvalidDataException("Повреждены сведения об установке русификатора. " +
                            VerifySteam + Environment.NewLine + document, error);
                    }
                }
                else if (restore)
                {
                    throw new InvalidDataException("Для Steam-сборки " + buildId +
                        " нет записи об установке русификатора. " + VerifySteam);
                }
                locale = DescribeCompatible(assetPath, installed == null ? null : installed.Asset,
                    buildId, restore, suffix);
                labels = DescribeCompatible(assemblyPath, installed == null ? null : installed.Assembly,
                    buildId, restore, suffix);
            }
            else
            {
                locale = Describe(assetPath, LocalePatch.OriginalHash, release.patchedAssetSha256,
                    release.previousPatches.Select(patch => patch.patchedAssetSha256), buildId, restore, suffix);
                labels = Describe(assemblyPath, LocalePatch.AssemblyHash, release.patchedAssemblySha256,
                    release.previousPatches.Select(patch => patch.patchedAssemblySha256), buildId, restore, suffix);
            }
            var files = new[] { locale, labels };
            var replaced = new List<GameFile>();
            try
            {
                foreach (var file in files)
                {
                    if (file.Current == file.Target)
                    {
                        continue;
                    }
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
                        actual = compatible ? LocalePatch.BuildCompatible(source, file.Stage, dictionary)
                            : LocalePatch.Build(source, file.Stage, dictionary);
                    }
                    else
                    {
                        actual = compatible ? LabelPatch.BuildCompatible(source, file.Stage)
                            : LabelPatch.Build(source, file.Stage);
                    }
                    file.Target = file.Target ?? actual;
                    RequireHash(actual, file.Target, file.Stage);
                }

                var pending = files.Where(file => file.Current != file.Target).ToArray();
                if (pending.Length == 0)
                {
                    return restore ? "Игра уже использует оригинальные файлы." : "Русификатор уже установлен.";
                }
                foreach (var file in compatible && !restore ? files : pending)
                {
                    if (!File.Exists(file.Backup))
                    {
                        File.Copy(file.Path, file.BackupStage);
                        RequireHash(LocalePatch.FileHash(file.BackupStage), file.Original, file.BackupStage);
                        File.Move(file.BackupStage, file.Backup);
                    }
                    RequireHash(LocalePatch.FileHash(file.Backup), file.Original, file.Backup);
                }
                if (compatible && !restore)
                {
                    SaveInstalledBuild(installed, document, documentStage, locale, labels);
                }

                EnsureGameClosed();
                if (ReadSteamBuild(root, release.steamAppId) != detectedBuild)
                {
                    throw new InvalidDataException("Сборка Steam изменилась во время работы. " + VerifySteam);
                }
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
                if (compatible && !restore && replaced.Count == 0 && IsCompatibilityError(cause))
                {
                    throw new InvalidDataException("Не удалось подготовить файлы для Steam-сборки " + buildId +
                        ". " + cause.Message + Environment.NewLine + VerifySteam, cause);
                }
                throw;
            }
            finally
            {
                foreach (var file in files)
                {
                    DeleteTemporaryFile(file.Stage);
                    DeleteTemporaryFile(file.BackupStage);
                }
                DeleteTemporaryFile(documentStage);
            }
            foreach (var file in replaced)
            {
                DeleteTemporaryFile(file.Previous);
            }
            return restore ? "Оригинальные файлы восстановлены." :
                "Русификатор установлен. Запустите игру и выберите «Русский» в настройках языка.";
        }

        private static bool IsCompatibilityError(Exception error)
        {
            return error is InvalidDataException || error is EndOfStreamException || error is BadImageFormatException ||
                error is OverflowException || error is DecoderFallbackException || error is ArgumentException ||
                error is InvalidOperationException || error is IndexOutOfRangeException || error is KeyNotFoundException;
        }

        private static GameFile Describe(string path, string original, string patched, IEnumerable<string> previous,
            long steamBuildId, bool restore, string suffix)
        {
            string legacyBackup = path + BackupSuffix;
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
            return CreateGameFile(path, original, current, restore ? original : patched, backup, suffix);
        }

        private static GameFile DescribeCompatible(string path, InstalledFile installed, long buildId, bool restore,
            string suffix)
        {
            string backup = path + BackupSuffix + "." + buildId;
            string current = LocalePatch.FileHash(path);
            string original = installed == null ? current : installed.Original;
            if (installed != null && current != original && !installed.Patched.Contains(current))
            {
                throw new InvalidDataException("Файл игры не совпадает с оригиналом или установленным переводом " +
                    "для сборки " + buildId + ". " + VerifySteam + Environment.NewLine + path);
            }
            bool hasBackup = File.Exists(backup);
            if ((hasBackup && LocalePatch.FileHash(backup) != original) || (installed != null && !hasBackup))
            {
                throw new InvalidDataException("Резервная копия оригинала отсутствует или повреждена. " +
                    VerifySteam + Environment.NewLine + backup);
            }
            return CreateGameFile(path, original, current, restore ? original : null, backup, suffix);
        }

        private static GameFile CreateGameFile(string path, string original, string current, string target, string backup,
            string suffix)
        {
            return new GameFile
            {
                Path = path,
                Original = original,
                Current = current,
                Target = target,
                Backup = backup,
                Stage = path + suffix + ".tmp",
                BackupStage = path + suffix + ".backup",
                Previous = path + suffix + ".previous"
            };
        }

        private static void SaveInstalledBuild(InstalledBuild installed, string document, string stage, GameFile locale,
            GameFile labels)
        {
            if (installed == null)
            {
                installed = new InstalledBuild
                {
                    Asset = new InstalledFile { Original = locale.Original, Patched = new List<string>() },
                    Assembly = new InstalledFile { Original = labels.Original, Patched = new List<string>() }
                };
            }
            // Publish the hashes first so a retry recognizes either file if replacement is interrupted.
            if (!installed.Asset.Patched.Contains(locale.Target))
            {
                installed.Asset.Patched.Add(locale.Target);
            }
            if (!installed.Assembly.Patched.Contains(labels.Target))
            {
                installed.Assembly.Patched.Add(labels.Target);
            }
            byte[] json = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(installed));
            using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(json, 0, json.Length);
                stream.Flush(true);
            }
            if (File.Exists(document))
            {
                File.Replace(stage, document, null);
            }
            else
            {
                File.Move(stage, document);
            }
        }

        private static long? ReadSteamBuild(string root, int appId)
        {
            var game = new DirectoryInfo(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            DirectoryInfo common = game.Parent;
            if (common == null || common.Parent == null ||
                !common.Name.Equals("common", StringComparison.OrdinalIgnoreCase) ||
                !common.Parent.Name.Equals("steamapps", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            string path = Path.Combine(common.Parent.FullName, "appmanifest_" + appId + ".acf");
            if (!File.Exists(path))
            {
                return null;
            }
            var fields = ReadAppState(File.ReadAllText(path));
            string value;
            int declaredApp;
            if (!fields.TryGetValue("appid", out value) ||
                !Int32.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out declaredApp) ||
                declaredApp != appId || !fields.TryGetValue("installdir", out value) ||
                !value.Equals(game.Name, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            long buildId;
            if (!fields.TryGetValue("buildid", out value) ||
                !Int64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out buildId) || buildId <= 0)
            {
                throw new InvalidDataException("Не удалось определить Steam Build ID из " + path + ".");
            }
            return buildId;
        }

        private static Dictionary<string, string> ReadAppState(string manifest)
        {
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var tokens = Regex.Matches(manifest, @"(?m)//[^\r\n]*|""(?:\\.|[^""\\])*""|[{}]|[^\s""{}]+")
                .Cast<Match>().Select(match => match.Value)
                .Where(token => !token.StartsWith("//", StringComparison.Ordinal)).ToArray();
            if (tokens.Length < 3 || !tokens[0].Trim('"').Equals("AppState", StringComparison.OrdinalIgnoreCase) ||
                tokens[1] != "{" || tokens[tokens.Length - 1] != "}")
            {
                return fields;
            }
            int depth = 1;
            string key = null;
            foreach (string token in tokens.Skip(2))
            {
                if (token == "{")
                {
                    depth++;
                    key = null;
                }
                else if (token == "}")
                {
                    depth--;
                }
                else if (depth == 1)
                {
                    string value = token.Length >= 2 && token[0] == '"'
                        ? token.Substring(1, token.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\")
                        : token;
                    if (key == null)
                    {
                        key = value;
                    }
                    else
                    {
                        if (fields.ContainsKey(key))
                        {
                            throw new InvalidDataException("Повторяющееся поле в манифесте Steam: " + key + ".");
                        }
                        fields.Add(key, value);
                        key = null;
                    }
                }
            }
            return depth == 0 && key == null ? fields : new Dictionary<string, string>();
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
