using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace PcTogetherRus
{
    public static class LabelPatch
    {
        public static string Build(string originalPath, string outputPath)
        {
            if (LocalePatch.FileHash(originalPath) != LocalePatch.AssemblyHash)
            {
                throw new InvalidDataException("Не совпадает исходная сборка игрового кода.");
            }
            return BuildCompatible(originalPath, outputPath);
        }

        internal static string BuildCompatible(string originalPath, string outputPath)
        {
            using (var resolver = new DefaultAssemblyResolver())
            {
                resolver.AddSearchDirectory(Path.GetDirectoryName(originalPath));
                var parameters = new ReaderParameters { AssemblyResolver = resolver, InMemory = true };
                using (var module = ModuleDefinition.ReadModule(originalPath, parameters))
                {
                    var loc = module.GetType("PcStore.Localization.Loc");
                    CheckLocaleSchema(module);
                    PatchSearch(module.GetType("PcStore.Interaction.PartBrowserUI"), FindTranslator(loc, "Get"));
                    PatchPlaylist(module.GetType("Michsky.DreamOS.MusicPlayerManager"), FindTranslator(loc, "TranslateSource"));

                    using (var stream = new FileStream(outputPath, FileMode.CreateNew))
                    {
                        module.Write(stream, new WriterParameters { Timestamp = 0 });
                        stream.Flush(true);
                    }
                }
            }
            using (var patched = ModuleDefinition.ReadModule(outputPath))
            {
                var browser = patched.GetType("PcStore.Interaction.PartBrowserUI");
                var player = patched.GetType("Michsky.DreamOS.MusicPlayerManager");
                Require(HasTranslation(FindMethod(browser, "Open", 4), "copy.search", "Get", "UnityEngine.UI.Text") &&
                    HasTranslation(FindMethod(player, "PlayCustomClip", 4), "Custom", "TranslateSource", "TMPro.TMP_Text"),
                    "Проверка подписей поиска и медиаплеера не пройдена.");
            }
            return LocalePatch.FileHash(outputPath);
        }

        private static void PatchSearch(TypeDefinition browser, MethodDefinition get)
        {
            Require(browser != null, "В игровом коде отсутствует окно поиска деталей.");
            var searchField = browser.Fields.SingleOrDefault(field => field.Name == "searchField");
            var initializers = browser.Methods.Where(method => method.HasBody).Where(method =>
                method.Body.Instructions.Any(i => IsLiteral(i, "search..."))).ToArray();
            Require(initializers.Length == 1 && initializers[0].Name == "BuildSearchField",
                "Не найден код создания подсказки поиска деталей.");
            var initialization = initializers[0];
            var literals = initialization.Body.Instructions.Where(i => IsLiteral(i, "search...")).ToArray();
            Require(searchField != null && literals.Length == 1, "Изменился код подсказки поиска деталей.");
            var literal = literals[0];
            Require(!searchField.IsStatic && searchField.FieldType.FullName == "UnityEngine.UI.InputField" &&
                IsSetter(literal.Next, "UnityEngine.UI.Text", "text", "System.String"),
                "Изменился код подсказки поиска деталей.");
            var setText = (MethodReference)literal.Next.Operand;
            var setPlaceholder = initialization.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
                .Single(method => method.Name == "set_placeholder" && method.DeclaringType.FullName == searchField.FieldType.FullName);
            Require(setPlaceholder.HasThis && setPlaceholder.ReturnType.FullName == "System.Void" &&
                setPlaceholder.Parameters.Count == 1 && setPlaceholder.Parameters[0].ParameterType.FullName == "UnityEngine.UI.Graphic",
                "Изменилась сигнатура поля подсказки поиска.");
            var getPlaceholder = new MethodReference("get_placeholder", setPlaceholder.Parameters[0].ParameterType,
                searchField.FieldType) { HasThis = true };
            var fieldType = searchField.FieldType.Resolve();
            Require(fieldType != null && fieldType.Methods.Any(method => method.Name == "get_placeholder" &&
                method.IsPublic && method.HasThis && method.Parameters.Count == 0 &&
                method.ReturnType.FullName == getPlaceholder.ReturnType.FullName),
                "Изменилась сигнатура чтения подсказки поиска.");

            var open = FindMethod(browser, "Open", 4);
            Require(!open.IsStatic && open.ReturnType.FullName == "System.Void" &&
                !open.Body.Instructions.Any(i => HasCall(i, "Get") && IsLiteral(i.Previous, "copy.search")),
                "Изменился или уже пропатчен код открытия поиска деталей.");
            var il = open.Body.GetILProcessor();
            var first = open.Body.Instructions[0];
            var key = il.Create(OpCodes.Ldstr, "copy.search");
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Ldfld, searchField));
            il.InsertBefore(first, il.Create(OpCodes.Brfalse, first));
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Ldfld, searchField));
            il.InsertBefore(first, il.Create(OpCodes.Callvirt, getPlaceholder));
            il.InsertBefore(first, il.Create(OpCodes.Isinst, setText.DeclaringType));
            il.InsertBefore(first, il.Create(OpCodes.Dup));
            il.InsertBefore(first, il.Create(OpCodes.Brtrue, key));
            il.InsertBefore(first, il.Create(OpCodes.Pop));
            il.InsertBefore(first, il.Create(OpCodes.Br, first));
            il.InsertBefore(first, key);
            il.InsertBefore(first, il.Create(OpCodes.Call, get));
            il.InsertBefore(first, il.Create(OpCodes.Callvirt, setText));
        }

        private static void PatchPlaylist(TypeDefinition player, MethodDefinition translate)
        {
            var method = FindMethod(player, "PlayCustomClip", 4);
            var il = method.Body.GetILProcessor();
            var literals = method.Body.Instructions.Where(i => IsLiteral(i, "Custom")).ToArray();
            Require(literals.Length == 1, "Не найдена однозначная точка перевода названия списка воспроизведения.");
            var literal = literals[0];
            var field = literal.Previous == null ? null : literal.Previous.Operand as FieldReference;
            Require(!method.IsStatic && method.ReturnType.FullName == "System.Void" &&
                field != null && literal.Previous.OpCode == OpCodes.Ldfld &&
                field.Name == "nowPlayingListTitle" && field.DeclaringType.FullName == player.FullName &&
                IsSetter(literal.Next, "TMPro.TMP_Text", "text", "System.String"),
                "Изменился или уже пропатчен код названия списка воспроизведения.");
            il.InsertAfter(literal, il.Create(OpCodes.Call, translate));
            CheckShortBranches(method);
        }

        private static void CheckShortBranches(MethodDefinition method)
        {
            var offsets = new Dictionary<Instruction, int>();
            int offset = 0;
            foreach (var instruction in method.Body.Instructions)
            {
                offsets.Add(instruction, offset);
                offset += instruction.GetSize();
            }
            foreach (var branch in method.Body.Instructions.Where(i => i.OpCode.OperandType == OperandType.ShortInlineBrTarget))
            {
                int distance = offsets[(Instruction)branch.Operand] - offsets[branch] - branch.GetSize();
                Require(distance >= SByte.MinValue && distance <= SByte.MaxValue,
                    "Переход в коде списка воспроизведения выходит за допустимый диапазон.");
            }
        }

        private static MethodDefinition FindTranslator(TypeDefinition type, string name)
        {
            var method = FindMethod(type, name, 1);
            Require(method.IsStatic && method.ReturnType.FullName == "System.String" &&
                method.Parameters[0].ParameterType.FullName == "System.String",
                "Изменилась сигнатура метода локализации " + name + ".");
            return method;
        }

        private static MethodDefinition FindMethod(TypeDefinition type, string name, int parameterCount)
        {
            Require(type != null, "Отсутствует требуемый тип игрового кода для метода " + name + ".");
            var methods = type.Methods.Where(method => method.Name == name && method.Parameters.Count == parameterCount &&
                method.HasBody && method.Body.Instructions.Count > 0).ToArray();
            Require(methods.Length == 1, "Не найден однозначный игровой метод " + type.FullName + "." + name + ".");
            return methods[0];
        }

        private static void CheckLocaleSchema(ModuleDefinition module)
        {
            const string scope = "PcStore.Localization.";
            CheckFields(module.GetType(scope + "LocaleTable"), "System.String code", "System.String nativeName",
                "System.Int32 order", scope + "LocaleTable/Entry[] entries");
            CheckFields(module.GetType(scope + "LocaleTable/Entry"), "System.String key", "System.String value");
            CheckFields(module.GetType(scope + "LocaleCatalog"),
                "System.Collections.Generic.List`1<" + scope + "LocaleTable> locales", scope + "LocaleTable fallback");
        }

        private static void CheckFields(TypeDefinition type, params string[] expected)
        {
            const string reference = "UnityEngine.SerializeReference";
            Require(type != null, "В игровом коде отсутствует тип таблицы или каталога локалей.");
            var fields = type.Fields.Where(field => !field.IsStatic && !field.IsNotSerialized &&
                (field.IsPublic || field.CustomAttributes.Any(attribute =>
                    attribute.AttributeType.FullName == "UnityEngine.SerializeField" ||
                    attribute.AttributeType.FullName == reference))).ToArray();
            Require(fields.All(field => !field.CustomAttributes.Any(attribute => attribute.AttributeType.FullName == reference)) &&
                fields.Select(field => field.FieldType.FullName + " " + field.Name).SequenceEqual(expected),
                "Изменилась структура таблицы или каталога локалей в игровом коде.");
        }

        private static bool IsLiteral(Instruction instruction, string value)
        {
            return instruction != null && instruction.OpCode == OpCodes.Ldstr && (string)instruction.Operand == value;
        }

        private static bool IsSetter(Instruction instruction, string type, string property, string parameter)
        {
            var method = instruction == null ? null : instruction.Operand as MethodReference;
            return method != null && instruction.OpCode == OpCodes.Callvirt && method.HasThis &&
                method.Name == "set_" + property && method.DeclaringType.FullName == type &&
                method.ReturnType.FullName == "System.Void" && method.Parameters.Count == 1 &&
                method.Parameters[0].ParameterType.FullName == parameter;
        }

        private static bool HasTranslation(MethodDefinition method, string literal, string translator, string setterType)
        {
            return method.Body.Instructions.Any(i => IsLiteral(i, literal) && HasCall(i.Next, translator) &&
                IsSetter(i.Next.Next, setterType, "text", "System.String"));
        }

        private static bool HasCall(Instruction instruction, string name)
        {
            var method = instruction == null ? null : instruction.Operand as MethodReference;
            return method != null && instruction.OpCode == OpCodes.Call &&
                method.Name == name && method.DeclaringType.FullName == "PcStore.Localization.Loc";
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
