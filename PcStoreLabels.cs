using System;
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

            using (var resolver = new DefaultAssemblyResolver())
            {
                resolver.AddSearchDirectory(Path.GetDirectoryName(originalPath));
                var parameters = new ReaderParameters { AssemblyResolver = resolver, InMemory = true };
                using (var module = ModuleDefinition.ReadModule(originalPath, parameters))
                {
                    var loc = module.GetType("PcStore.Localization.Loc");
                    PatchSearch(module.GetType("PcStore.Interaction.PartBrowserUI"), FindMethod(loc, "Get", 1));
                    PatchPlaylist(module.GetType("Michsky.DreamOS.MusicPlayerManager"), FindMethod(loc, "TranslateSource", 1));

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
                if (!FindMethod(browser, "Open", 4).Body.Instructions.Any(i => HasCall(i, "Get")) ||
                    !FindMethod(player, "PlayCustomClip", 4).Body.Instructions.Any(i => HasCall(i, "TranslateSource")))
                {
                    throw new InvalidDataException("Проверка подписей поиска и медиаплеера не пройдена.");
                }
            }
            return LocalePatch.FileHash(outputPath);
        }

        private static void PatchSearch(TypeDefinition browser, MethodDefinition get)
        {
            var searchField = browser.Fields.Single(field => field.Name == "searchField");
            var initialization = browser.Methods.Where(method => method.HasBody).Single(method =>
                method.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "search..."));
            var literal = initialization.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldstr &&
                (string)i.Operand == "search...");
            var setText = (MethodReference)literal.Next.Operand;
            var setPlaceholder = initialization.Body.Instructions.Select(i => i.Operand).OfType<MethodReference>()
                .Single(method => method.Name == "set_placeholder" && method.DeclaringType.FullName == searchField.FieldType.FullName);
            var getPlaceholder = new MethodReference("get_placeholder", setPlaceholder.Parameters[0].ParameterType,
                searchField.FieldType) { HasThis = true };

            var open = FindMethod(browser, "Open", 4);
            var il = open.Body.GetILProcessor();
            var first = open.Body.Instructions[0];
            // Build creates a Text placeholder before Open can be called.
            il.InsertBefore(first, il.Create(OpCodes.Ldarg_0));
            il.InsertBefore(first, il.Create(OpCodes.Ldfld, searchField));
            il.InsertBefore(first, il.Create(OpCodes.Callvirt, getPlaceholder));
            il.InsertBefore(first, il.Create(OpCodes.Castclass, setText.DeclaringType));
            il.InsertBefore(first, il.Create(OpCodes.Ldstr, "copy.search"));
            il.InsertBefore(first, il.Create(OpCodes.Call, get));
            il.InsertBefore(first, il.Create(OpCodes.Callvirt, setText));
        }

        private static void PatchPlaylist(TypeDefinition player, MethodDefinition translate)
        {
            var method = FindMethod(player, "PlayCustomClip", 4);
            var il = method.Body.GetILProcessor();
            var literal = method.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "Custom");
            il.InsertAfter(literal, il.Create(OpCodes.Call, translate));
        }

        private static MethodDefinition FindMethod(TypeDefinition type, string name, int parameterCount)
        {
            return type.Methods.Single(method => method.Name == name && method.Parameters.Count == parameterCount);
        }

        private static bool HasCall(Instruction instruction, string name)
        {
            var method = instruction.Operand as MethodReference;
            return instruction.OpCode == OpCodes.Call && method != null &&
                method.Name == name && method.DeclaringType.FullName == "PcStore.Localization.Loc";
        }
    }
}
