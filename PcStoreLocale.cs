using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace PcTogetherRus
{
    public static class LocalePatch
    {
        public const string AssetName = "sharedassets1.assets";
        public const string OriginalHash = "1933E381018CCD2C68D19315A8583A3212B473C0837DEC72AF460818DEAD9629";
        public const string AssemblyHash = "D6FCCC38DC44A6EC90892714A1BF15217DA947F8818321EC135AE5414A159586";
        public const int EntryCount = 4060;

        private const long EnglishId = 5886;
        private const long CatalogId = 5966;
        private const long RussianId = 8313;
        private const int HeaderSize = 48;
        private const int RowSize = 24;
        private const int BehaviourHeaderSize = 28;
        private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
        private static readonly Regex FormatTokens = new Regex(@"\{\{|\}\}|\{[^{}\r\n]*\}|%[sdif]");
        private static readonly Regex Tags = new Regex(@"</?[A-Za-z][^<>\r\n]*>");
        private const string CustomPlaylistKey = "copy.rus.customPlaylist";

        private sealed class ObjectRow
        {
            public long Id;
            public long Offset;
            public int Size;
            public int Type;
            public int Position;
        }

        private sealed class Asset
        {
            public readonly Dictionary<long, ObjectRow> Objects = new Dictionary<long, ObjectRow>();
            public readonly int[] ClassIds;
            public readonly int DataOffset;
            public readonly int MetadataSize;
            public readonly int CountPosition;
            public readonly int TableEnd;

            public Asset(byte[] bytes)
            {
                Require(bytes.Length >= HeaderSize && ReadBigEndian(bytes, 8, 4) == 23 && bytes[16] == 0,
                    "Неподдерживаемый формат ресурса Unity.");
                MetadataSize = checked((int)ReadBigEndian(bytes, 20, 4));
                DataOffset = checked((int)ReadBigEndian(bytes, 32, 8));
                Require(ReadBigEndian(bytes, 24, 8) == bytes.Length &&
                        MetadataSize >= 0 && MetadataSize <= DataOffset - HeaderSize && DataOffset < bytes.Length,
                    "Размеры ресурса Unity не совпадают.");

                using (var reader = Reader(bytes))
                {
                    reader.BaseStream.Position = HeaderSize;
                    var version = new StringBuilder();
                    for (byte next = reader.ReadByte(); next != 0; next = reader.ReadByte())
                    {
                        Require(version.Length < 32, "Некорректная версия Unity.");
                        version.Append((char)next);
                    }
                    Require(version.ToString() == "6000.5.4f1" && reader.ReadInt32() == 19 &&
                            !reader.ReadBoolean(), "Не совпадают версия, платформа или тип ресурса Unity.");
                    int typeCount = reader.ReadInt32();
                    int metadataEnd = HeaderSize + MetadataSize;
                    Require(typeCount > 0 && typeCount <= (metadataEnd - reader.BaseStream.Position) / 23,
                        "Некорректная таблица типов Unity.");
                    ClassIds = new int[typeCount];
                    for (int i = 0; i < typeCount; i++)
                    {
                        int classId = reader.ReadInt32();
                        ClassIds[i] = classId;
                        reader.BaseStream.Position += classId == 114 ? 35 : 19;
                    }
                    Require(reader.BaseStream.Position <= metadataEnd - sizeof(int),
                        "Таблица типов выходит за пределы ресурса.");
                    CountPosition = checked((int)reader.BaseStream.Position);
                    int count = reader.ReadInt32();
                    reader.BaseStream.Position = Align(checked((int)reader.BaseStream.Position), 4);
                    Require(count > 0 && count <= (metadataEnd - reader.BaseStream.Position) / RowSize,
                        "Некорректное число объектов Unity.");
                    for (int i = 0; i < count; i++)
                    {
                        var row = new ObjectRow
                        {
                            Position = checked((int)reader.BaseStream.Position),
                            Id = reader.ReadInt64(),
                            Offset = reader.ReadInt64(),
                            Size = reader.ReadInt32(),
                            Type = reader.ReadInt32()
                        };
                        Require(row.Offset >= 0 && row.Size >= 0 &&
                                row.Offset <= bytes.Length - DataOffset - row.Size &&
                                row.Type >= 0 && row.Type < typeCount, "Некорректная запись ресурса Unity.");
                        Require(!Objects.ContainsKey(row.Id), "Повторяющийся идентификатор объекта Unity.");
                        Objects.Add(row.Id, row);
                    }
                    TableEnd = checked((int)reader.BaseStream.Position);
                    Require(TableEnd <= HeaderSize + MetadataSize, "Таблица объектов выходит за пределы ресурса.");
                }
            }

            public byte[] ReadObject(byte[] bytes, long id)
            {
                var row = Objects[id];
                var result = new byte[row.Size];
                Buffer.BlockCopy(bytes, checked(DataOffset + (int)row.Offset), result, 0, row.Size);
                return result;
            }

            public long FindBehaviour(byte[] bytes, string name)
            {
                byte[] expected = Utf8.GetBytes(name);
                long found = 0;
                foreach (var row in Objects.Values)
                {
                    if (ClassIds[row.Type] != 114 || row.Size < BehaviourHeaderSize + sizeof(int) + expected.Length)
                    {
                        continue;
                    }
                    int position = checked(DataOffset + (int)row.Offset + BehaviourHeaderSize);
                    if (BitConverter.ToInt32(bytes, position) != expected.Length)
                    {
                        continue;
                    }
                    if (new ArraySegment<byte>(bytes, position + sizeof(int), expected.Length).SequenceEqual(expected))
                    {
                        Require(found == 0 && row.Id != 0, "Повторяющийся или недопустимый объект " + name + ".");
                        found = row.Id;
                    }
                }
                return found;
            }
        }

        public static string FileHash(string path)
        {
            using (var file = File.OpenRead(path))
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(file)).Replace("-", "");
            }
        }

        public static string Build(string originalPath, string outputPath, Dictionary<string, string> russian)
        {
            Require(FileHash(originalPath) == OriginalHash, "Не совпадает контрольная сумма исходного ресурса игры.");
            int fallbackCount;
            return BuildResource(originalPath, outputPath, russian, false, out fallbackCount);
        }

        internal static string BuildCompatible(string originalPath, string outputPath,
            Dictionary<string, string> russian, out int fallbackCount)
        {
            return BuildResource(originalPath, outputPath, russian, true, out fallbackCount);
        }

        private static string BuildResource(string originalPath, string outputPath, Dictionary<string, string> russian,
            bool compatible, out int fallbackCount)
        {
            byte[] original = File.ReadAllBytes(originalPath);
            var asset = new Asset(original);
            long englishId = EnglishId;
            long catalogId = CatalogId;
            long russianId = RussianId;
            int order = 17;
            if (compatible)
            {
                englishId = asset.FindBehaviour(original, "Locale_en");
                catalogId = asset.FindBehaviour(original, "LocaleCatalog");
                Require(englishId != 0 && catalogId != 0, "Не найдены английская таблица и каталог языков.");
                Require(asset.FindBehaviour(original, "Locale_ru") == 0, "В ресурсе уже есть русский язык.");
                russianId = checked(asset.Objects.Keys.Max() + 1);
                Require(russianId > 0, "Не удалось выделить идентификатор русской локали.");
            }
            else
            {
                Require(asset.Objects.Count == 8312 && asset.Objects[8312].Position + RowSize == asset.TableEnd &&
                    !asset.Objects.ContainsKey(RussianId), "Не совпадает список объектов исходного ресурса.");
            }

            byte[] english = asset.ReadObject(original, englishId);
            byte[] catalog = asset.ReadObject(original, catalogId);
            byte[] newCatalog = BuildCatalog(catalog, englishId, russianId, compatible ? asset : null, original, ref order);
            var tables = BuildLocales(english, russian, compatible, order, out fallbackCount);
            byte[] locale = tables[0];
            byte[] newEnglish = tables[1];
            int dataSize = original.Length - asset.DataOffset;
            int catalogOffset, englishOffset, localeOffset, metadataSize, dataOffset, length;
            checked
            {
                catalogOffset = Align(dataSize, 16);
                englishOffset = Align(catalogOffset + newCatalog.Length, 16);
                localeOffset = Align(englishOffset + newEnglish.Length, 16);
                metadataSize = asset.MetadataSize + RowSize;
                dataOffset = Align(HeaderSize + metadataSize, 16);
                length = dataOffset + localeOffset + locale.Length;
            }

            using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(output, Utf8))
            {
                writer.Write(original, 0, asset.TableEnd);
                writer.Write(russianId);
                writer.Write((long)localeOffset);
                writer.Write(locale.Length);
                writer.Write(asset.Objects[englishId].Type);
                writer.Write(original, asset.TableEnd, HeaderSize + asset.MetadataSize - asset.TableEnd);
                output.Position = dataOffset;
                writer.Write(original, asset.DataOffset, dataSize);
                output.Position = dataOffset + catalogOffset;
                writer.Write(newCatalog);
                output.Position = dataOffset + englishOffset;
                writer.Write(newEnglish);
                output.Position = dataOffset + localeOffset;
                writer.Write(locale);

                output.Position = 20;
                WriteBigEndian(writer, metadataSize, 4);
                WriteBigEndian(writer, length, 8);
                WriteBigEndian(writer, dataOffset, 8);
                output.Position = asset.CountPosition;
                writer.Write(asset.Objects.Count + 1);
                output.Position = asset.Objects[catalogId].Position + sizeof(long);
                writer.Write((long)catalogOffset);
                writer.Write(newCatalog.Length);
                output.Position = asset.Objects[englishId].Position + sizeof(long);
                writer.Write((long)englishOffset);
                writer.Write(newEnglish.Length);
                writer.Flush();
                output.Flush(true);
            }

            byte[] patched = File.ReadAllBytes(outputPath);
            var check = new Asset(patched);
            Require(check.Objects.Count == asset.Objects.Count + 1 &&
                    check.Objects[russianId].Type == asset.Objects[englishId].Type &&
                    check.ReadObject(patched, russianId).SequenceEqual(locale) &&
                    check.ReadObject(patched, catalogId).SequenceEqual(newCatalog) &&
                    check.ReadObject(patched, englishId).SequenceEqual(newEnglish),
                "Проверка подготовленного ресурса не пройдена.");
            return FileHash(outputPath);
        }

        private static byte[][] BuildLocales(byte[] english, Dictionary<string, string> russian,
            bool compatible, int order, out int fallbackCount)
        {
            fallbackCount = 0;
            using (var source = Reader(english))
            using (var englishResult = new MemoryStream())
            using (var russianResult = new MemoryStream())
            using (var englishWriter = new BinaryWriter(englishResult, Utf8))
            using (var russianWriter = new BinaryWriter(russianResult, Utf8))
            {
                byte[] header = source.ReadBytes(BehaviourHeaderSize);
                Require(ReadString(source) == "Locale_en" && ReadString(source) == "en" &&
                        ReadString(source) == "English" && source.ReadInt32() == 0,
                    "Английская таблица имеет неизвестный формат.");
                int count = source.ReadInt32();
                Require(count > 0 && count <= (source.BaseStream.Length - source.BaseStream.Position) / 8,
                    "Некорректное число строк английской таблицы.");
                Require(compatible || (count == EntryCount && russian.Count == count + 1),
                    "Неполный русский словарь.");
                string playlist;
                Require(russian.TryGetValue(CustomPlaylistKey, out playlist) && IsValidTranslation("Custom", playlist),
                    "Отсутствует или повреждён перевод названия списка.");
                WriteLocaleHeader(englishWriter, header, "Locale_en", "en", "English", 0, count + 1);
                WriteLocaleHeader(russianWriter, header, "Locale_ru", "ru", "Русский", order, count + 1);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < count; i++)
                {
                    string key = ReadString(source);
                    string original = ReadString(source);
                    string translation;
                    Require(key.Length != 0 && key != CustomPlaylistKey && seen.Add(key),
                        "Недопустимый или повторяющийся ключ в игре: " + key);
                    if (!russian.TryGetValue(key, out translation) || !IsValidTranslation(original, translation))
                    {
                        Require(compatible, "Отсутствует или несовместим перевод строки: " + key);
                        translation = original;
                        fallbackCount++;
                    }
                    WriteString(englishWriter, key);
                    WriteString(englishWriter, original);
                    WriteString(russianWriter, key);
                    WriteString(russianWriter, translation);
                }
                Require(source.BaseStream.Position == source.BaseStream.Length, "Остались непрочитанные данные таблицы.");
                Require(seen.Contains("copy.search"), "В таблице отсутствует строка подсказки поиска деталей.");
                WriteString(englishWriter, CustomPlaylistKey);
                WriteString(englishWriter, "Custom");
                WriteString(russianWriter, CustomPlaylistKey);
                WriteString(russianWriter, playlist);
                return new[] { russianResult.ToArray(), englishResult.ToArray() };
            }
        }

        private static bool IsValidTranslation(string original, string translation)
        {
            return translation != null &&
                (original.Length == 0 ? translation.Length == 0 : !string.IsNullOrWhiteSpace(translation)) &&
                !translation.Contains("\uFFFD") && !translation.Contains("\0") &&
                Matches(FormatTokens, original).OrderBy(x => x, StringComparer.Ordinal)
                    .SequenceEqual(Matches(FormatTokens, translation).OrderBy(x => x, StringComparer.Ordinal)) &&
                Matches(Tags, original).SequenceEqual(Matches(Tags, translation));
        }

        private static void WriteLocaleHeader(BinaryWriter writer, byte[] header, string name, string code,
            string displayName, int order, int count)
        {
            writer.Write(header);
            WriteString(writer, name);
            WriteString(writer, code);
            WriteString(writer, displayName);
            writer.Write(order);
            writer.Write(count);
        }

        private static byte[] BuildCatalog(byte[] catalog, long englishId, long russianId, Asset asset,
            byte[] original, ref int order)
        {
            using (var reader = Reader(catalog))
            using (var result = new MemoryStream())
            using (var writer = new BinaryWriter(result, Utf8))
            {
                reader.BaseStream.Position = BehaviourHeaderSize;
                Require(ReadString(reader) == "LocaleCatalog", "Не найден штатный каталог локалей.");
                int countPosition = checked((int)reader.BaseStream.Position);
                int count = reader.ReadInt32();
                const int referenceSize = sizeof(int) + sizeof(long);
                Require(count > 0 && count < (catalog.Length - countPosition - sizeof(int)) / referenceSize &&
                    (asset != null || count == 17), "Не совпадает размер штатного каталога локалей.");
                int fallbackPosition = countPosition + sizeof(int) + count * referenceSize;
                Require(catalog.Length == fallbackPosition + referenceSize, "Не совпадает структура каталога.");
                if (asset != null)
                {
                    var seen = new HashSet<long>();
                    var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < count; i++)
                    {
                        int fileId = reader.ReadInt32();
                        long id = reader.ReadInt64();
                        ObjectRow row;
                        Require(fileId == 0 && seen.Add(id), "Некорректная ссылка в каталоге локалей.");
                        Require(asset.Objects.TryGetValue(id, out row) && row.Type == asset.Objects[englishId].Type,
                            "Каталог ссылается на объект неизвестного типа.");
                        // The leading MonoBehaviour fields identify the script in the external script file.
                        int offset = checked(asset.DataOffset + (int)row.Offset);
                        int englishOffset = checked(asset.DataOffset + (int)asset.Objects[englishId].Offset);
                        Require(row.Size >= BehaviourHeaderSize &&
                            new ArraySegment<byte>(original, offset + 16, 12).SequenceEqual(
                                new ArraySegment<byte>(original, englishOffset + 16, 12)),
                            "Не совпадают ссылки на скрипты таблиц локалей.");
                        using (var locale = new BinaryReader(new MemoryStream(original, offset, row.Size, false), Utf8))
                        {
                            locale.BaseStream.Position = BehaviourHeaderSize;
                            string name = ReadString(locale);
                            string code = ReadString(locale);
                            Require(name.StartsWith("Locale_", StringComparison.Ordinal) &&
                                code.Length != 0 && codes.Add(code) && !code.Equals("ru", StringComparison.OrdinalIgnoreCase),
                                "Некорректный язык или русский язык уже есть в каталоге.");
                            ReadString(locale);
                            order = Math.Max(order, checked(locale.ReadInt32() + 1));
                        }
                    }
                    Require(seen.Contains(englishId), "В каталоге нет ссылки на английскую таблицу.");
                }
                reader.BaseStream.Position = fallbackPosition;
                Require(reader.ReadInt32() == 0 && reader.ReadInt64() == englishId,
                    "Не совпадает резервная локаль каталога.");

                writer.Write(catalog, 0, countPosition);
                writer.Write(count + 1);
                writer.Write(catalog, countPosition + sizeof(int), count * referenceSize);
                writer.Write(0);
                writer.Write(russianId);
                writer.Write(catalog, fallbackPosition, referenceSize);
                return result.ToArray();
            }
        }

        private static IEnumerable<string> Matches(Regex regex, string value)
        {
            return regex.Matches(value).Cast<Match>().Select(match => match.Value);
        }

        private static BinaryReader Reader(byte[] bytes)
        {
            return new BinaryReader(new MemoryStream(bytes, false), Utf8);
        }

        private static string ReadString(BinaryReader reader)
        {
            int length = reader.ReadInt32();
            Require(length >= 0 && length <= reader.BaseStream.Length - reader.BaseStream.Position,
                "Неверная длина строки Unity.");
            string value = Utf8.GetString(reader.ReadBytes(length));
            int aligned = Align(checked((int)reader.BaseStream.Position), 4);
            Require(aligned <= reader.BaseStream.Length, "Строка выходит за пределы объекта Unity.");
            reader.BaseStream.Position = aligned;
            return value;
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            byte[] bytes = Utf8.GetBytes(value);
            writer.Write(bytes.Length);
            writer.Write(bytes);
            int padding = Align(bytes.Length, 4) - bytes.Length;
            for (int i = 0; i < padding; i++)
            {
                writer.Write((byte)0);
            }
        }

        private static int Align(int offset, int alignment)
        {
            return checked(offset + alignment - 1) & -alignment;
        }

        private static long ReadBigEndian(byte[] bytes, int position, int size)
        {
            long value = 0;
            for (int i = 0; i < size; i++)
            {
                value = (value << 8) | bytes[position + i];
            }
            return value;
        }

        private static void WriteBigEndian(BinaryWriter writer, long value, int size)
        {
            for (int i = size - 1; i >= 0; i--)
            {
                writer.Write((byte)(value >> (8 * i)));
            }
        }

        private static void Require(bool condition, string message)
        {
            if (!condition)
            {
                throw new InvalidDataException(message);
            }
        }
    }
}
