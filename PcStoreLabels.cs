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
        private const string HoldHudType = "PcStore.Player.InteractionHud";
        private const string LocType = "PcStore.Localization.Loc";
        private const string LowerHoldAction = "LowerRussianHoldAction";

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
                    var loc = module.GetType(LocType);
                    CheckLocaleSchema(module);
                    PatchSearch(module.GetType("PcStore.Interaction.PartBrowserUI"), FindTranslator(loc, "Get"));
                    PatchPlaylist(module.GetType("Michsky.DreamOS.MusicPlayerManager"), FindTranslator(loc, "TranslateSource"));
                    PatchHoldHint(module, FindCurrentCode(loc));

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
                var hud = patched.GetType(HoldHudType);
                FindHoldActionLoad(FindMethod(hud, "Show", 2), FindCurrentCode(patched.GetType(LocType)),
                    FindMethod(hud, LowerHoldAction, 2));
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

        private static void PatchHoldHint(ModuleDefinition module, MethodDefinition currentCode)
        {
            var hud = module.GetType(HoldHudType);
            var show = FindMethod(hud, "Show", 2);
            var label = FindHoldActionLoad(show, currentCode, null);
            Require(!hud.Methods.Any(method => method.Name == LowerHoldAction),
                "В игровом коде уже есть метод форматирования русской подсказки удержания.");

            var lower = CreateHoldActionNormalizer(module.TypeSystem);
            hud.Methods.Add(lower);
            var il = show.Body.GetILProcessor();
            var storeArgument = label.Next;
            il.InsertBefore(storeArgument, il.Create(OpCodes.Call, currentCode));
            il.InsertBefore(storeArgument, il.Create(OpCodes.Call, lower));
            CheckShortBranches(show);
        }

        private static Instruction FindHoldActionLoad(MethodDefinition show, MethodDefinition currentCode, MethodDefinition lower)
        {
            const string error = "Изменился код подсказки удержания кнопки.";
            var instructions = show.Body.Instructions;
            var literals = instructions.Where(instruction => IsLiteral(instruction, "hint.hold")).ToArray();
            Require(literals.Length == 1, error);
            var key = literals[0];
            int index = instructions.IndexOf(key);
            int storeIndex = index + (lower == null ? 6 : 8);
            Require(index >= 4 && storeIndex + 2 < instructions.Count, error);

            var hold = instructions[index - 4];
            var holdField = hold.Operand as FieldReference;
            var whenHeld = instructions[index - 3];
            var plainLabel = instructions[index - 2];
            var skipFormat = instructions[index - 1];
            var label = instructions[index + 5];
            var arrayType = instructions[index + 2].Operand as TypeReference;
            var format = instructions[storeIndex + 1];
            var formatMethod = format.Operand as MethodReference;
            var setter = instructions[storeIndex + 2];
            var local = label.Operand as VariableDefinition;
            Require(hold.OpCode == OpCodes.Ldfld && holdField != null && holdField.Name == "hold" &&
                holdField.DeclaringType.FullName == "PcStore.Player.InteractionHint" &&
                holdField.FieldType.FullName == "System.Boolean" &&
                (whenHeld.OpCode == OpCodes.Brtrue || whenHeld.OpCode == OpCodes.Brtrue_S) && whenHeld.Operand == key &&
                skipFormat.OpCode.FlowControl == FlowControl.Branch && skipFormat.Operand == setter &&
                local != null && local.VariableType.FullName == "System.String" &&
                (label.OpCode == OpCodes.Ldloc || label.OpCode == OpCodes.Ldloc_S) &&
                (plainLabel.OpCode == OpCodes.Ldloc || plainLabel.OpCode == OpCodes.Ldloc_S) && plainLabel.Operand == local &&
                instructions[index + 1].OpCode == OpCodes.Ldc_I4_1 && instructions[index + 2].OpCode == OpCodes.Newarr &&
                arrayType != null && arrayType.FullName == "System.Object" &&
                instructions[index + 3].OpCode == OpCodes.Dup && instructions[index + 4].OpCode == OpCodes.Ldc_I4_0 &&
                instructions[storeIndex].OpCode == OpCodes.Stelem_Ref &&
                format.OpCode == OpCodes.Call && formatMethod != null &&
                formatMethod.FullName == "System.String PcStore.Shop.StoreText::Format(System.String,System.Object[])" &&
                IsSetter(setter, "TMPro.TMP_Text", "text", "System.String"), error);
            if (lower != null)
            {
                Require(label.Next.OpCode == OpCodes.Call && label.Next.Operand == currentCode &&
                    label.Next.Next.OpCode == OpCodes.Call && label.Next.Next.Operand == lower,
                    "Проверка русской подсказки удержания кнопки не пройдена.");
            }
            return label;
        }

        private static MethodDefinition CreateHoldActionNormalizer(TypeSystem types)
        {
            var method = new MethodDefinition(LowerHoldAction, MethodAttributes.Private | MethodAttributes.Static, types.String);
            method.Parameters.Add(new ParameterDefinition("label", ParameterAttributes.None, types.String));
            method.Parameters.Add(new ParameterDefinition("localeCode", ParameterAttributes.None, types.String));
            var il = method.Body.GetILProcessor();
            var unchanged = il.Create(OpCodes.Ldarg_0);
            var lowerInitial = il.Create(OpCodes.Ldloc_0);
            var initial = new VariableDefinition(types.Char);
            method.Body.Variables.Add(initial);
            method.Body.InitLocals = true;

            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Ldstr, "ru");
            il.Emit(OpCodes.Call, CoreMethod(types.String, "op_Equality", types.Boolean, false, types.String, types.String));
            il.Emit(OpCodes.Brfalse_S, unchanged);
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Call, CoreMethod(types.String, "IsNullOrEmpty", types.Boolean, false, types.String));
            il.Emit(OpCodes.Brtrue_S, unchanged);

            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4_0);
            il.Emit(OpCodes.Callvirt, CoreMethod(types.String, "get_Chars", types.Char, true, types.Int32));
            il.Emit(OpCodes.Stloc_0);
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Ldc_I4, (int)'Ё');
            il.Emit(OpCodes.Beq_S, lowerInitial);
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Ldc_I4, (int)'А');
            il.Emit(OpCodes.Blt_S, unchanged);
            il.Emit(OpCodes.Ldloc_0);
            il.Emit(OpCodes.Ldc_I4, (int)'Я');
            il.Emit(OpCodes.Bgt_S, unchanged);

            il.Append(lowerInitial);
            il.Emit(OpCodes.Call, CoreMethod(types.Char, "ToLowerInvariant", types.Char, false, types.Char));
            il.Emit(OpCodes.Call, CoreMethod(types.Char, "ToString", types.String, false, types.Char));
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldc_I4_1);
            il.Emit(OpCodes.Callvirt, CoreMethod(types.String, "Substring", types.String, true, types.Int32));
            il.Emit(OpCodes.Call, CoreMethod(types.String, "Concat", types.String, false, types.String, types.String));
            il.Emit(OpCodes.Ret);
            il.Append(unchanged);
            il.Emit(OpCodes.Ret);
            return method;
        }

        private static MethodReference CoreMethod(TypeReference type, string name, TypeReference result, bool hasThis,
            params TypeReference[] arguments)
        {
            var method = new MethodReference(name, result, type) { HasThis = hasThis };
            foreach (var argument in arguments)
            {
                method.Parameters.Add(new ParameterDefinition(argument));
            }
            return method;
        }

        private static MethodDefinition FindCurrentCode(TypeDefinition loc)
        {
            var method = FindMethod(loc, "get_CurrentCode", 0);
            Require(method.IsStatic && method.IsPublic && method.ReturnType.FullName == "System.String",
                "Изменилась сигнатура чтения текущей локали.");
            return method;
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
                    "Переход в игровом методе " + method.FullName + " выходит за допустимый диапазон.");
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
                method.Name == name && method.DeclaringType.FullName == LocType;
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
