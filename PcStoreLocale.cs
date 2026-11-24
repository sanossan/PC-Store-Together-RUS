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
        public const string OriginalHash = "A01012AB9A74082A224586393CCF3956D02F621C2E3D5ED69066F737B4EAA3BD";
        public const string AssemblyHash = "3F59A45CF7508ADFAFBF03FBED9B2B7FF640E48963CE1093CE3EFED1F09C6E30";
        public const int EntryCount = 4044;

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
                        HeaderSize + MetadataSize <= DataOffset && DataOffset < bytes.Length,
                    "Размеры ресурса Unity не совпадают.");

                using (var reader = Reader(bytes))
                {
                    reader.BaseStream.Position = HeaderSize;
                    var version = new StringBuilder();
                    for (byte next = reader.ReadByte(); next != 0; next = reader.ReadByte())
                    {
                        version.Append((char)next);
                    }
                    Require(version.ToString() == "6000.5.4f1" && reader.ReadInt32() == 19 &&
                            !reader.ReadBoolean(), "Не совпадают версия, платформа или тип ресурса Unity.");
                    int typeCount = reader.ReadInt32();
                    for (int i = 0; i < typeCount; i++)
                    {
                        int classId = reader.ReadInt32();
                        reader.BaseStream.Position += classId == 114 ? 35 : 19;
                    }
                    CountPosition = checked((int)reader.BaseStream.Position);
                    int count = reader.ReadInt32();
                    reader.BaseStream.Position = Align(checked((int)reader.BaseStream.Position), 4);
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
            byte[] original = File.ReadAllBytes(originalPath);
            var asset = new Asset(original);
            Require(asset.Objects.Count == 8312 && asset.Objects[8312].Position + RowSize == asset.TableEnd &&
                    !asset.Objects.ContainsKey(RussianId), "Не совпадает список объектов исходного ресурса.");

            byte[] english = asset.ReadObject(original, EnglishId);
            byte[] catalog = asset.ReadObject(original, CatalogId);
            byte[] locale = BuildLocale(english, russian, true);
            byte[] newEnglish = BuildLocale(english, russian, false);
            byte[] newCatalog = BuildCatalog(catalog);
            int dataSize = original.Length - asset.DataOffset;
            int catalogOffset = Align(dataSize, 16);
            int englishOffset = Align(catalogOffset + newCatalog.Length, 16);
            int localeOffset = Align(englishOffset + newEnglish.Length, 16);
            int metadataSize = asset.MetadataSize + RowSize;
            int dataOffset = Align(HeaderSize + metadataSize, 16);
            int length = dataOffset + localeOffset + locale.Length;

            using (var output = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(output, Utf8))
            {
                writer.Write(original, 0, asset.TableEnd);
                writer.Write(RussianId);
                writer.Write((long)localeOffset);
                writer.Write(locale.Length);
                writer.Write(asset.Objects[EnglishId].Type);
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
                output.Position = asset.Objects[CatalogId].Position + sizeof(long);
                writer.Write((long)catalogOffset);
                writer.Write(newCatalog.Length);
                output.Position = asset.Objects[EnglishId].Position + sizeof(long);
                writer.Write((long)englishOffset);
                writer.Write(newEnglish.Length);
                writer.Flush();
                output.Flush(true);
            }

            byte[] patched = File.ReadAllBytes(outputPath);
            var check = new Asset(patched);
            Require(check.Objects.Count == asset.Objects.Count + 1 &&
                    check.Objects[RussianId].Type == asset.Objects[EnglishId].Type &&
                    check.ReadObject(patched, RussianId).SequenceEqual(locale) &&
                    check.ReadObject(patched, CatalogId).SequenceEqual(newCatalog) &&
                    check.ReadObject(patched, EnglishId).SequenceEqual(newEnglish),
                "Проверка подготовленного ресурса не пройдена.");
            return FileHash(outputPath);
        }

        private static byte[] BuildLocale(byte[] english, Dictionary<string, string> russian, bool translate)
        {
            using (var source = Reader(english))
            using (var result = new MemoryStream())
            using (var writer = new BinaryWriter(result, Utf8))
            {
                byte[] header = source.ReadBytes(BehaviourHeaderSize);
                Require(ReadString(source) == "Locale_en" && ReadString(source) == "en" &&
                        ReadString(source) == "English" && source.ReadInt32() == 0,
                    "Английская таблица имеет неизвестный формат.");
                int count = source.ReadInt32();
                Require(count == EntryCount && russian.Count == count + 1 &&
                        russian.ContainsKey(CustomPlaylistKey), "Неполный русский словарь.");
                writer.Write(header);
                WriteString(writer, translate ? "Locale_ru" : "Locale_en");
                WriteString(writer, translate ? "ru" : "en");
                WriteString(writer, translate ? "Русский" : "English");
                writer.Write(translate ? 17 : 0);
                writer.Write(count + 1);
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < count; i++)
                {
                    string key = ReadString(source);
                    string original = ReadString(source);
                    string translation;
                    Require(seen.Add(key), "Повторяющийся ключ в игре: " + key);
                    Require(russian.TryGetValue(key, out translation), "Отсутствует перевод: " + key);
                    Require(translation != null && (original.Length == 0
                        ? translation.Length == 0
                        : !String.IsNullOrWhiteSpace(translation)), "Некорректная пустая строка: " + key);
                    Require(!translation.Contains("\uFFFD") && !translation.Contains("\0"),
                        "Недопустимый символ в переводе: " + key);
                    Require(Matches(FormatTokens, original).OrderBy(x => x, StringComparer.Ordinal)
                            .SequenceEqual(Matches(FormatTokens, translation).OrderBy(x => x, StringComparer.Ordinal)),
                        "Не совпадают параметры подстановки: " + key);
                    Require(Matches(Tags, original).SequenceEqual(Matches(Tags, translation)),
                        "Не совпадают теги форматирования: " + key);
                    WriteString(writer, key);
                    WriteString(writer, translate ? translation : original);
                }
                Require(source.BaseStream.Position == source.BaseStream.Length, "Остались непрочитанные данные таблицы.");
                Require(!string.IsNullOrWhiteSpace(russian[CustomPlaylistKey]), "Отсутствует перевод названия списка.");
                WriteString(writer, CustomPlaylistKey);
                WriteString(writer, translate ? russian[CustomPlaylistKey] : "Custom");
                return result.ToArray();
            }
        }

        private static byte[] BuildCatalog(byte[] catalog)
        {
            using (var reader = Reader(catalog))
            using (var result = new MemoryStream())
            using (var writer = new BinaryWriter(result, Utf8))
            {
                reader.BaseStream.Position = BehaviourHeaderSize;
                Require(ReadString(reader) == "LocaleCatalog", "Не найден штатный каталог локалей.");
                int countPosition = checked((int)reader.BaseStream.Position);
                Require(reader.ReadInt32() == 17, "Не совпадает размер штатного каталога локалей.");
                const int referenceSize = sizeof(int) + sizeof(long);
                int fallbackPosition = countPosition + sizeof(int) + 17 * referenceSize;
                Require(catalog.Length == fallbackPosition + referenceSize, "Не совпадает структура каталога.");
                reader.BaseStream.Position = fallbackPosition;
                Require(reader.ReadInt32() == 0 && reader.ReadInt64() == EnglishId,
                    "Не совпадает резервная локаль каталога.");

                writer.Write(catalog, 0, countPosition);
                writer.Write(18);
                writer.Write(catalog, countPosition + sizeof(int), 17 * referenceSize);
                writer.Write(0);
                writer.Write(RussianId);
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
            reader.BaseStream.Position = Align(checked((int)reader.BaseStream.Position), 4);
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
