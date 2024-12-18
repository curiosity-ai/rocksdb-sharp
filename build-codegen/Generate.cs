﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace RocksDbPrepareCApiHeader
{
    class Generate
    {
        static async Task<int> Main(string[] args)
        {
            try
            {
                await ProcessAsync();
                return 0;
            }
            catch (Exception E)
            {
                Console.WriteLine(E.ToString());
                return -1;
            }
        }

        /* Some enum names can't be guessed because there are no clues,
         * Damn you, C enums!
         * So if that happens, we can just manually enter them here
         */
        private static string ManualEnumName(NativeEnum nativeEnum)
            =>
            nativeEnum.Values.Any(v => v.Name == "rocksdb_enable_time_except_for_mutex") ? "PerfLevel" :
            nativeEnum.Values.Any(v => v.Name == "rocksdb_user_key_comparison_count") ? "PerfMetric" :
            "";

        /* Sometimes we can't determine the appropriate type automatically and we just have to override it
         * These are the function/argname:type -> managed type mappings
         */
        private static IEnumerable<(string Type, string Strategy)> OverrideType(NativeArg arg, int argCount, string funcName)
        {
            // The Strategy returned allows multiple overloads for a function.
            // In general, just pass "default" unless you need to ensure multiple overloads

            // "keys" and "values" in "options" functions are strings
            if (funcName.IsMatchedBy(@".*options.*") && arg.Name.IsMatchedBy("keys|values") && arg.NativeType.IsMatchedBy(@".*\*\*|.*\* const\*"))
                yield return ("string[]", "default");

            // Some options take enumerations as values
            if (funcName == "rocksdb_options_set_compression_per_level" && arg.Name == "level_values")
            {
                yield return ("Compression[]", "enum");
                yield return ("int[]", "default");
            }
            if (funcName == "rocksdb_options_set_compression" && arg.NativeType == "int")
            {
                yield return ("Compression", "enum");
                yield return ("int", "default");
            }
            if (funcName == "rocksdb_options_set_compaction_style" && arg.NativeType == "int")
            {
                yield return ("Compaction", "enum");
                yield return ("int", "default");
            }
            if (funcName == "rocksdb_options_set_wal_recovery_mode" && arg.NativeType == "int")
            {
                yield return ("Recovery", "enum");
                yield return ("int", "default");
            }
            if (funcName == "rocksdb_block_based_options_set_index_type" && arg.NativeType == "int")
            {
                yield return ("BlockBasedTableIndexType", "enum");
                yield return ("int", "default");
            }
            if (funcName == "rocksdb_set_perf_level" && arg.NativeType == "int")
            {
                yield return ("PerfLevel", "enum");
                yield return ("int", "default");
            }
            if (funcName == "rocksdb_perfcontext_metric" && arg.NativeType == "int")
            {
                yield return ("PerfMetric", "enum");
                yield return ("int", "default");
            }
            if (funcName == "rocksdb_mergeoperator_create_full_merge" && arg.Name == "success")
            {
                yield return ("out unsigned_char_ptr", "default");
            }
            if (funcName == "rocksdb_mergeoperator_create_full_merge" && arg.Name == "new_value_length")
            {
                yield return ("out size_t_ptr", "default");
            }
            if (funcName == "rocksdb_mergeoperator_create_partial_merge" && arg.Name == "success")
            {
                yield return ("out unsigned_char_ptr", "default");
            }
            if (funcName == "rocksdb_mergeoperator_create_partial_merge" && arg.Name == "new_value_length")
            {
                yield return ("out size_t_ptr", "default");
            }

            yield break;
        }
        
        static async Task ProcessAsync()
        {
            var version = await File.ReadAllTextAsync(@"../rocksdbversion");
            version = version.Trim(new char[] { ' ', '\r', '\n' });

            Console.WriteLine($"Building version  {version}");
            // Download the original by commit id
            var urlOfCHeader = $"https://raw.githubusercontent.com/facebook/rocksdb/v{version}/include/rocksdb/c.h";
            var original = await DownloadAsync(urlOfCHeader);

            Console.Error.WriteLine($"Using: {urlOfCHeader}");

            // Transform from CRLF to LF
            var modified = original.Replace("\r\n", "\n");

            //Fix missing callback name 
            modified = modified.Replace("void (*)(void* priv, unsigned lev,", "void (*logger_callback)(void* priv, unsigned int lev,");

            if(modified.Contains("void (*)("))
            {
                Console.WriteLine("Warning: There might be a missing callback type name: void (*)(...)");
            }
            
            var regions = ParseRocksHeaderFileRegion(modified).ToList();

            var managedFunctions = regions.ToDictionary(r => r.Title, r => GetManagedFunctions(r).ToArray());

            var nativeRawCs = new IndentedCodeBuilder();
            nativeRawCs.AppendLine($"/* WARNING: This file was autogenerated from version {version} of rocksdb/c.h at");
            nativeRawCs.AppendLine($"   {urlOfCHeader}");
            nativeRawCs.AppendLine($" */");

            nativeRawCs.AppendLine(GetCsharpHeader());

            nativeRawCs.AppendLine("namespace RocksDbSharp");
            nativeRawCs.StartBlock("{");

            nativeRawCs.AppendLine("#region Type aliases");
            // Get every type in use as an argument type or a return type
            var typeVariations = managedFunctions
                .SelectMany(rmf => rmf.Value)
                .SelectMany(mf =>
                    mf.ReturnType.Once()
                    .Concat(mf.Args
                        .SelectMany(mfa => mfa.Variations.Where(mfav => mfav.TypeStrategy != "delegate"))
                        .Select(mfav => mfav.Type)
                    )
                )
                .Select(v => Regex.Replace(v, @"\[\]$", ""))
                .ToHashSet();
            foreach (var typeAlias in GetTypeAliases(regions))
            {
                // Find all the variations that are actually in use and include those
                foreach (var typeVariation in typeVariations.Where(tv => tv.Contains(typeAlias.Name)))
                    nativeRawCs.AppendLine(typeAlias.Variation(typeVariation));
            }
            nativeRawCs.AppendLine("#endregion");

            nativeRawCs.AppendLine("#region Delegates");
            var delegateSignatures = managedFunctions
                .SelectMany(rmf => rmf.Value)
                .SelectMany(mf => mf.Args.Select(mfa => (FuncName: mf.Name.RegexReplace(@"^rocksdb_|_create$", ""), Arg: mfa)))
                .SelectMany(mfa => mfa.Arg.Variations
                    .Where(mfav => mfav.TypeStrategy == "delegate")
                    .Select(mfav => (mfa.FuncName, mfav.Type, ArgName: mfav.Name))
                )
                .ToList();
            var delegatesByName = delegateSignatures.GroupBy(ds => ds.ArgName);
            foreach (var nameGroup in delegatesByName)
            {
                var unique = nameGroup.Select(ng => ng.Type).Distinct();
                if (unique.Count() == 1)
                {
                    var definition = nameGroup.First();
                    var parsed = Regex.Match(definition.Type, @"^\((.*?)\) -> (.+)$");
                    var delegateReturnType = parsed.Groups[2].Value;
                    var delegateSignature = parsed.Groups[1].Value;
                    var delegateName = nameGroup.Key;
                    var name = $"{SnakeCaseToPascalCase(delegateName)}Delegate";
                    nativeRawCs.AppendLine($"public delegate {delegateReturnType} {name}({delegateSignature});");
                }
                else
                {
                    nativeRawCs.AppendLine("// If you get the below error, then that means the c.h file in the rocksdb project");
                    nativeRawCs.AppendLine("// now contains delegate arguments which share a name, but have different signatures.");
                    nativeRawCs.AppendLine("// The code generator will need logic to try to generate different names for the");
                    nativeRawCs.AppendLine("// different arguments");
                    nativeRawCs.AppendLine(string.Join("", nameGroup.Select((ng, i) => $"  Arg {i + 1}: {ng.ArgName} in {ng.FuncName} with signature {ng.Type}\n")));
                    nativeRawCs.AppendLineWithoutIndent($"#error Unable to create single delegate because arguments named {nameGroup.Key} have different delegate signatures");
                }
            }
            nativeRawCs.AppendLine("#endregion");

            foreach (var region in regions.Where(r => (r.NativeEnums.Length + r.NativeFunctions.Length) > 0))
            {
                nativeRawCs.AppendLine($"#region {region.Title}");
                foreach (var managedEnum in GetManagedEnums(region))
                {
                    nativeRawCs.AppendLine(managedEnum);
                }

                nativeRawCs.AppendLine($"public partial class Native");
                nativeRawCs.StartBlock("{");
                foreach (var managedFunction in managedFunctions[region.Title])
                {
                    nativeRawCs.AppendLine(managedFunction.WithTypeLookup((name, type, strategy) => strategy == "delegate" ? $"{SnakeCaseToPascalCase(name)}Delegate" : type));
                }
                nativeRawCs.EndBlock($"}} // class Native");
                nativeRawCs.AppendLine($"#endregion // {region.Title}");
            }

            nativeRawCs.EndBlock("} // namespace RocksDbSharp");

            var output = nativeRawCs.ToString();

            await using (var outputStream = File.Create(@"../csharp/src/Native.cs"))
            await using (var writer = new StreamWriter(outputStream, Encoding.UTF8))
            {
                await writer.WriteLineAsync(output.Replace("void p0", ""));
            }

            await File.WriteAllLinesAsync(@"../csharp/src/Native.Load.cs", new[]
            {
                "using System;",
                "using System.Collections.Generic;",
                "using System.Linq;",
                "using System.Runtime.InteropServices;",
                "using System.Text;",
                "using System.Threading.Tasks;",
                "",
                "namespace RocksDbSharp",
                "{",
                "    public abstract partial class Native",
                "    {",
                "        public static Native Instance;",
                "",
                "        static Native()",
                "        {",
                "            if (RuntimeInformation.ProcessArchitecture == Architecture.X86 && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))",
                "                throw new RocksDbSharpException(\"Rocksdb on windows is not supported for 32 bit applications\");",
                "            Instance = NativeImport.Auto.Import<Native>(\"rocksdb\", \"$VERSION\", true);".Replace("$VERSION",version),
                "        }",
                "",
                "        public Native()",
                "        {",
                "        }",
                "    }",
                "}",
            });

            Console.Error.WriteLine($"Done");

        }

        private static IEnumerable<TypeAlias> GetTypeAliases(IEnumerable<RocksDbHeaderFileRegion> regions)
            => regions.SelectMany(r => r.NativeTypeDefs).Select(GetManagedTypeDef);

        private static TypeAlias GetManagedTypeDef(NativeTypeDef typedef)
            => new TypeAlias(comment: typedef.Comment, name: $"{typedef.Name}_ptr");

        class TypeAlias
        {
            public string Comment { get; }
            public string Name { get; }

            public TypeAlias(string comment, string name)
            {
                Comment = comment;
                Name = name;
            }

            public override string ToString()
                => Variation(Name);

            public string Variation(string variantName)
                => $"{Comment.Trim().OrElse(null).Then(c => $"{c}\n")}using {variantName} = System.IntPtr;";
        }

        private static IEnumerable<string> GetManagedEnums(RocksDbHeaderFileRegion region)
            => region.NativeEnums.Select(GetManagedEnum);

        private static IEnumerable<ManagedFunction> GetManagedFunctions(RocksDbHeaderFileRegion region)
            => region.NativeFunctions.Select(GetManagedFunction);

        private static string GetManagedEnum(NativeEnum nativeEnum)
        {
            var commonPrefix = GetCommonPrefix(nativeEnum.Values.Select(e => e.Name).ToArray()).If(p => p.EndsWith("_"));
            var commonSuffix = GetCommonSuffix(nativeEnum.Values.Select(e => e.Name).ToArray()).If(p => p.StartsWith("_"));
            var prefixLength = commonPrefix?.Length ?? 0;
            var suffixLength = commonSuffix?.Length ?? 0;
            var guessedEnumName =
                (prefixLength >= suffixLength) ? Regex.Replace(SnakeCaseToPascalCase(commonPrefix.TrimEnd('_')), "^Rocksdb", "", RegexOptions.IgnoreCase) :
                (suffixLength > 0) ? SnakeCaseToPascalCase(commonSuffix.TrimStart('_')) :
                "(Unable To Guess Name)";
            var enumName = nativeEnum.Name.OrElse(guessedEnumName).OrElse(ManualEnumName(nativeEnum));
            var values = nativeEnum.Values
                .Select(v => $"    {SnakeCaseToPascalCase(v.Name.Substring(prefixLength, v.Name.Length - prefixLength - suffixLength))} = {v.Value},\n");
            return $"\npublic enum {enumName}\n{{\n{string.Join("", values)}}}\n";
            /*
            if (enumName == "")
                enumName = EnumNamesForValues[entries.First().Name];
            var entriesOut = entries.Select(e => $"    {SnakeCaseToPascalCase(e.Name.Substring(commonPrefix.Length))} = {e.Value},");
            return $"}}\npublic enum {enumName} {{\n{string.Join("\n", entriesOut)}\n}}\npublic abstract partial class Native {{";
            */
            throw new NotImplementedException();
        }

        private static string AsArrayType(string managedType)
            => Regex.Replace(managedType, "_ptr$|_const_ptr$", "[]");

        private static string GetManagedType(NativeArg nativeArg)
        {
            if (nativeArg.Name == "errptr" && nativeArg.NativeType == "char**")
                return "out char_ptr_ptr";
            if (nativeArg.Name == "errs" && nativeArg.NativeType == "char**")
                return "char_ptr[]";
            if (nativeArg.Name.IsMatchedBy(@"len.*|size|.*len") && nativeArg.NativeType == "size_t*")
                return "out size_t";
            if (nativeArg.Name == "file_list" && nativeArg.NativeType == "const char* const*")
                return "string[]";
            return GetManagedType(nativeArg.NativeType);
        }

        private static string GetManagedType(string nativeType)
        {
            var managedType = nativeType
                .RegexReplace(@"\s+", "_")
                .RegexReplace(@"\*", "_ptr_")
                .RegexReplace(@"_+", "_")
                .RegexReplace(@"_$", "")
                .RegexReplace(@"^_", "")
                .Trim('_');

            switch (managedType)
            {
                case "const_bool":
                case "unsigned_char": return "bool";
                case "uint8_t": return "byte";
                default:return managedType;
            }
        }

        private static IEnumerable<(string Type, string Strategy)> GetManagedTypeVariations(NativeArg nativeArg, string managedArgName)
        {
            // Note: the managedArgName can be convenient since a native arg name is not always supplied
            yield return (Type: GetManagedType(nativeArg), Strategy: "default");
            
            if (managedArgName.IsMatchedBy(".*name|name.*|.*dir|.*path") && nativeArg.NativeType == "const char*")
                yield return (Type: "string", Strategy: "string name");
            
            if (nativeArg.Name.IsMatchedBy(".*names|names.*") && nativeArg.NativeType == "const char**")
                yield return (Type: "string[]", Strategy: "array");
            
            if (nativeArg.Name.IsMatchedBy(".*names|names.*") && nativeArg.NativeType == "const char* const*")
                yield return (Type: "string[]", Strategy: "array");
            
            if (nativeArg.Name.IsMatchedBy(".*column_famil.*|.*colummn_famil.*|iterators") && nativeArg.NativeType.EndsWith("**"))
                yield return (Type: $"{AsArrayType(GetManagedType(nativeArg.NativeType))}", Strategy: "array");
            
            if (nativeArg.Name.IsMatchedBy(".*column_famil.*|.*colummn_famil.*|iterators") && nativeArg.NativeType.EndsWith("* const*"))
                yield return (Type: $"{AsArrayType(GetManagedType(nativeArg.NativeType))}", Strategy: "array");

            if (nativeArg.Name.IsMatchedBy(@"key|k|val|v|.*_key|.*_val") && nativeArg.NativeType.In("const char*", "char*"))
            {
                yield return (Type: "byte*", Strategy: "kv byte*");
                yield return (Type: "byte[]", Strategy: "kv byte[]");
            }
            if (nativeArg.Name.IsMatchedBy(@"rep") && nativeArg.NativeType.In("const char*", "char*"))
            {
                yield return (Type: "byte*", Strategy: "rep byte*");
                yield return (Type: "byte[]", Strategy: "rep byte[]");
            }
            if (nativeArg.Name.IsMatchedBy(@"blob") && nativeArg.NativeType.In("const char*", "char*"))
            {
                yield return (Type: "byte*", Strategy: "blob byte*");
                yield return (Type: "byte[]", Strategy: "blob byte[]");
            }
            if (nativeArg.Name.ToLower().EndsWith("list") && nativeArg.NativeType.In("const char* const*", "char**"))
                yield return (Type: "IntPtr[]", Strategy: "array");

            if (nativeArg.Name.ToLower().EndsWith("sizes") && nativeArg.NativeType.In("const size_t*", "size_t*"))
                yield return (Type: "size_t[]", Strategy: "array");

            if (nativeArg.Name == "column_families" && nativeArg.NativeType == "const rocksdb_column_family_handle_t* const*")
                yield return (Type: "rocksdb_column_family_handle_t_ptr[]", Strategy: "array");

            if (nativeArg.Name == "level_values" && nativeArg.NativeType == "int*")
                yield return (Type: "int[]", Strategy: "array");

        }

        private static IEnumerable<(string Type, string Strategy)> GetManagedTypeOverride(NativeArg nativeArg, int argCount, string funcName)
        {
            var overrides = OverrideType(nativeArg, argCount, funcName).ToList();
            return (overrides.Count > 0) ? overrides : null;
        }

        private static Regex RocksDbStructPtrPattern { get; } = new Regex(@"(?:const )?rocksdb_(.*)_t\*", RegexOptions.Compiled);
        private static string GuessManagedArgNameForArg(NativeArg nativeArg)
        {
            return RocksDbStructPtrPattern.Match(nativeArg.NativeType).If(m => m.Success)?.Groups[1].Value
                ?? nativeArg.NativeType.If(t => t == "size_t").Then(t => "size");
        }

        private static Regex RocksDbOptionSetPattern { get; } = new Regex(@"_set_([a-zA-Z0-9_]+)", RegexOptions.Compiled);
        private static string GuessOptionSetArgName(NativeArg nativeArg, int argCount, string funcName)
        {
            return
                // second arg in a set operation that has 2 args
                // or first arg in a set operation that has one arg which is not a pointer
                (nativeArg.Index == 1 && argCount == 2 && funcName != null) || (nativeArg.Index == 0 && argCount == 1 && !nativeArg.NativeType.EndsWith("*")) ? RocksDbOptionSetPattern.Match(funcName).If(m => m.Success)?.Groups[1].Value :
                null;
        }

        private static ManagedArg GetManagedArg(NativeArg nativeArg, int argCount, string funcName)
        {
            var name = nativeArg.Name.OrElse(null)
                ?? GuessOptionSetArgName(nativeArg, argCount, funcName)
                ?? GuessManagedArgNameForArg(nativeArg)
                ?? $"p{nativeArg.Index}";
            if (nativeArg.IsDelegate)
            {
                var argsLength = FindClosingParenthesis(nativeArg.NativeType, 1) - 1;
                var funcArgsUnparsed = nativeArg.NativeType.Substring(1, argsLength);
                var nativeReturnType = nativeArg.NativeType.Substring(1 + argsLength + ") -> ".Length);
                var delegateNativeArgs = ParseNativeArgs(funcArgsUnparsed).ToList();
                var delegateManagedArgs = delegateNativeArgs.Select(dna => GetManagedArg(dna, delegateNativeArgs.Count, $"{funcName}_{name}"));
                var delegateManagedArgsString = string.Join(", ", delegateManagedArgs.Select(dma => dma.Variations.First()));
                var delegateManagedReturnType = GetManagedType(nativeReturnType);
                var managedType = $"({delegateManagedArgsString}) -> {delegateManagedReturnType}";
                return new ManagedArg(
                    new[]
                    {
                        new ManagedArgVariation("delegate_ptr", name, "default"),
                        new ManagedArgVariation(managedType, name, "delegate"),
                    }
                );
            }
            else
            {
                var managedTypes = GetManagedTypeOverride(nativeArg, argCount, funcName) ?? GetManagedTypeVariations(nativeArg, name);
                return new ManagedArg(managedTypes.Select(mt => new ManagedArgVariation(mt.Type, name, mt.Strategy)));
            }
        }

        class ManagedFunction
        {
            public string ReturnType { get; }
            public string Name { get; }
            public ManagedArg[] Args { get; }
            public string Comment { get; }

            public ManagedFunction(string returnType, string name, string comment, IEnumerable<ManagedArg> args)
            {
                ReturnType = returnType;
                Name = name;
                Comment = comment;
                Args = args.ToArray();
            }

            public override string ToString()
                => WithTypeLookup((name, type, strategy) => type);

            public string WithTypeLookup(Func<string, string, string, string> typeLookup)
            {
                var additionalArgStrategies = Args
                    .SelectMany(ma => ma.Variations)
                    .Select(mav => mav.TypeStrategy)
                    .Where(s => s != "default")
                    .Distinct();
                var variations = GetVariations(additionalArgStrategies, typeLookup).Distinct();
                return string.Join("\n", variations);
            }

            private IEnumerable<string> GetVariations(IEnumerable<string> additionalArgStrategies, Func<string, string, string, string> typeLookup)
            {
                var strategies = additionalArgStrategies.ToArray();
                var combinationCount = Math.Pow(2, strategies.Length);
                for (int combinationIndex = 0; combinationIndex < combinationCount; combinationIndex++)
                {
                    var useStrategies = strategies.Where((s, i) => 0 != ((1 << i) & combinationIndex)).ToHashSet();
                    var managedArgs = Args.Select(arg => arg.Variations.FirstOrDefault(v => useStrategies.Contains(v.TypeStrategy)) ?? arg.Variations.FirstOrDefault(v => v.TypeStrategy == "default") ?? arg.Variations.First()).ToArray();
                    var argStrings = managedArgs.Select(ma => ma.WithTypeLookup(typeLookup));
                    
                    // Return the default abstract native import declaration for this variation
                    yield return ToCsharpCode(
                        comment: Comment,
                        returnType: ReturnType,
                        name: Name,
                        args: argStrings
                    );

                    // should we generate an exception-throwing wrapper?
                    if (argStrings.LastOrDefault() == "out char_ptr_ptr errptr")
                    {
                        // Generate an errptr -> exception-throwing wrapper for this one
                        // i.e. remove errptr arg from signature, and generate a body to call the errptr version
                        var withoutErrPtr = managedArgs.Take(managedArgs.Length - 1).ToArray();
                        yield return ToCsharpCode(
                            comment: Comment,
                            returnType: ReturnType,
                            name: Name,
                            args: withoutErrPtr.Select(ma => ma.WithTypeLookup(typeLookup)),
                            body: WrapWithThrow(
                                isVoid: ReturnType == "void",
                                name: Name,
                                args: withoutErrPtr.Select(ma => ma.Type.StartsWith("out ") ? $"out {ma.Name}" : ma.Name)
                            )
                        );
                    }
                }
            }

            public static string WrapWithThrow(bool isVoid, string name, IEnumerable<string> args)
            {
                var argList = string.Join("", args.Select(a => $"{a}, "));
                var cs = new IndentedCodeBuilder();
                cs.StartBlock("{");
                cs.AppendLine($"{(isVoid ? "" : "var result = ")}{name}({argList}out char_ptr_ptr errptr);");
                cs.AppendLine($"if (errptr != IntPtr.Zero)");
                cs.AppendIndentedLine("throw new RocksDbException(errptr);");
                if (!isVoid)
                    cs.AppendLine($"return result;");
                cs.EndBlock("}");
                return cs.ToString();
            }

            public static string ToCsharpCode(
                string comment, 
                string returnType, 
                string name, 
                IEnumerable<string> args,
                string body = null)
            {
                var managedArgLines = string.Join(",", args.Select(arg => $"\n    {arg}"));
                var isUnsafe = managedArgLines.Contains("*");
                string bodyText = body == null ? ";" : $"\n{body}";
                return $"{comment.If(c => !string.IsNullOrEmpty(c)).Then(c => $"{c}\n").OrElse("")}public {(isUnsafe ? "unsafe " : "")}{(body == null ? "abstract " : "")}{returnType} {name}({managedArgLines}){bodyText}\n";
            }
        }

        class ManagedArg
        {
            public ManagedArgVariation[] Variations { get; }

            public ManagedArg(IEnumerable<ManagedArgVariation> variations)
            {
                Variations = variations.ToArray();
            }

            public override string ToString()
                => Variations.First().ToString() + (Variations.Length > 1 ? $" (+{Variations.Length - 1} variations)" : "");
        }

        class ManagedArgVariation
        {
            public string Type { get; }
            public string Name { get; set;  }
            public string TypeStrategy { get; }

            public ManagedArgVariation(string type, string name, string typeStrategy)
            {
                Type = type;
                Name = name;
                TypeStrategy = typeStrategy;
            }

            public override string ToString()
                => WithTypeLookup((name, type, strategy) => strategy == "delegate" ? $"{SnakeCaseToPascalCase(name)}Delegate" : Type);

            public string WithTypeLookup(Func<string, string, string, string> typeLookup)
                => $"{typeLookup(Name, Type, TypeStrategy)} {Name}";
        }

        private static ManagedFunction GetManagedFunction(NativeFunction nativeFunc)
        {
            return new ManagedFunction(
                returnType: GetManagedType(nativeFunc.ReturnType),
                name: nativeFunc.Name,
                comment: nativeFunc.Comments.Trim(),
                args: MakeUniqueNames(nativeFunc.Args.Select(a => GetManagedArg(a, nativeFunc.Args.Length, nativeFunc.Name)))
            );
        }

        private static IEnumerable<ManagedArg> MakeUniqueNames(IEnumerable<ManagedArg> managedArgs)
        {
            var uniqueSet = new Dictionary<string, int>();
            foreach(var arg in managedArgs)
            {
                var name = arg.Variations[0].Name;

                if (!uniqueSet.TryGetValue(name, out var count))
                {
                    count = 0;
                }

                if(count == 0)
                {
                    yield return arg;
                }
                else
                {
                    foreach(var v in arg.Variations)
                    {
                        v.Name = $"{v.Name}_{count}";
                    }

                    yield return arg;
                }

                count++;
                uniqueSet[name] = count;
            }
        }

        private static string SnakeCaseToPascalCase(string commonPrefix)
        {
            var camel = Regex.Replace(commonPrefix.ToLower(), "_([a-z])", m => m.Groups[1].Value.ToUpper());
            var pascal = Regex.Replace(camel, "^[a-z]", m => m.Value.ToUpper());
            return pascal;
        }

        private static string GetCommonPrefix(string[] strings)
        {
            //Fix for one case where the first value is entirely contained in the second, which breaks the algorithm below
            if(strings.Any(s=> s == "rocksdb_block_based_table_data_block_index_type_binary_search") && strings.All(s => s.StartsWith("rocksdb_block_based_table_data_block_index_type_binary_search")))
            {
                return "rocksdb_block_based_table_data_block_index_type_";
            }

            var minLength = strings.Select(s => s.Length).Min();
            for (int i = minLength - 1; i > 0; i--)
            {
                var first = strings.First();
                (var prefix, var rest) = first.SplitAt(i);
                if (strings.All(s => s.Left(i) == prefix))
                    return prefix;
            }
            return "";
        }

        private static string GetCommonSuffix(string[] strings)
        {
            var minLength = strings.Select(s => s.Length).Min();
            for (int i = minLength - 1; i > 0; i--)
            {
                var first = strings.First();
                (var rest, var suffix) = first.SplitAt(first.Length - i);
                if (strings.All(s => s.Right(i) == suffix))
                    return suffix;
            }
            return "";
        }

        private static string GetCsharpHeader()
        {
            return string.Join("\n", new string[]
            {
                @"using System;",
                @"using System.Runtime.InteropServices;",
                @"using byte_ptr = System.IntPtr;",
                @"using int_ptr = System.IntPtr;",
                @"using const_int_ptr = System.IntPtr;",
                @"using size_t = System.UIntPtr;",
                @"using const_size_t = System.UIntPtr;",
                @"using uint32_t = System.UInt32;",
                @"using const_uint32_t = System.UInt32;",
                @"using unsigned_int = System.UInt32;",
                @"using int32_t = System.Int32;",
                @"using int64_t = System.Int64;",
                @"using uint64_t = System.UInt64;",
                @"using uint64_t_ptr = System.IntPtr;",
                @"using unsigned_char = System.Boolean;",
                @"using unsigned_char_ptr = System.IntPtr;",
                @"using char_ptr = System.IntPtr;",
                @"using const_char_ptr = System.IntPtr;",
                @"using char_ptr_ptr = System.IntPtr;",
                @"using const_char_ptr_const_ptr = System.IntPtr;",
                @"using const_char_ptr_ptr = System.IntPtr;",
                @"using char_ptr_ptr_ptr = System.IntPtr;",
                @"using const_size_t_ptr = System.IntPtr;",
                @"using void_ptr = System.IntPtr;",
                @"using size_t_ptr = System.IntPtr;",
                @"using delegate_ptr = System.IntPtr;",

                @"",
                @"#pragma warning disable IDE1006 // Intentionally violating naming conventions because this is meant to match the C API",
            });
        }

        // This is a bit tricky because it tries to accommodate some inconsistencies in rocksdb's c.h region commenting
        // We'll assume it's a region only if the text is on one line and capitalized and either has a blank line underneath it, or is only one word long
        static Regex RegionSeparatorPattern { get; } = new Regex(@"(?:\n\n\/\* ([A-Z][\S]+?) \*\/\n|\n\n\/\* ([A-Z].+?) \*\/\n\n)|#ifdef __cplusplus\n\}", RegexOptions.Compiled | RegexOptions.Multiline);
        static IEnumerable<RocksDbHeaderFileRegion> ParseRocksHeaderFileRegion(string source)
        {
            var m = RegionSeparatorPattern.Match(source, 0);
            var start = m.Index + m.Length;
            for (; ; )
            {
                var title =
                    m.Groups[1].Success ? m.Groups[1].Value :
                    m.Groups[2].Success ? m.Groups[2].Value :
                    null;
                m = RegionSeparatorPattern.Match(source, start);
                var isLast = !m.Groups[1].Success && !m.Groups[2].Success;
                var regionText = source.Substring(start, m.Index - start);

                var nativeFunctions = ParseNativeFunctions(regionText).ToList();

                var nativeEnums = ParseNativeEnumerations(regionText).ToList();

                var typeDefs = ParseNativeTypeDefs(regionText).ToList();

                yield return new RocksDbHeaderFileRegion(
                    title: title,
                    nativeFunctions: nativeFunctions,
                    nativeEnums: nativeEnums,
                    nativeTypeDefs: typeDefs
                );

                if (isLast)
                    yield break;
                start = m.Index + m.Length;
            }
        }

        class NativeEnum
        {
            public string Name { get; }
            public NativeEnumValue[] Values { get; }
            public string Comment { get; }

            public NativeEnum(string name, IEnumerable<NativeEnumValue> values, string comment)
            {
                Name = name;
                Values = values.ToArray();
                Comment = comment;
            }

            public override string ToString()
                => $"enum {Name ?? "(no name)"}({string.Join(", ", Values.Select(a => a.ToString()))})";
        }

        class NativeEnumValue
        {
            public string Name { get; }
            public int Value { get; }

            public NativeEnumValue(string name, int value)
            {
                Name = name;
                Value = value;
            }

            public override string ToString()
                => $"{Name} = {Value}";
        }

        class NativeTypeDef
        {
            public string Name { get; }
            public string Comment { get; }
            public NativeTypeDef(string name, string comment)
            {
                Name = name;
                Comment = comment;
            }
        }

        static string CommentPrologPattern { get; } = @"((?:\/\*+[^*]*\*+(?:[^/*][^*]*\*+)*\/|\s|//[^\n]*)*)";

        static Regex RocksDbNativeTypeDefPattern { get; } = new Regex(CommentPrologPattern + @"typedef\sstruct\srocksdb.*?_t\s+(rocksdb.*?_t);", RegexOptions.Compiled);
        static IEnumerable<NativeTypeDef> ParseNativeTypeDefs(string source)
        {
            var matches = RocksDbNativeTypeDefPattern.Matches(source);
            foreach (var match in matches.AsEnumerable())
            {
                var comment = match.Groups[1].Value.Trim();
                var name = match.Groups[2].Value;
                yield return new NativeTypeDef(name: name, comment: comment);
            }
        }

        static Regex NativeEnumPattern { get; } = new Regex(CommentPrologPattern + @"enum ([a-zA-Z0-9_]+)?{(.*?)};", RegexOptions.Compiled | RegexOptions.Singleline);
        static IEnumerable<NativeEnum> ParseNativeEnumerations(string source)
        {
            var matches = NativeEnumPattern.Matches(source);
            foreach (var match in matches.AsEnumerable())
            {
                var comment = match.Groups[1].Value.Trim();
                var name = match.Groups[2].Success ? match.Groups[2].Value : null;
                var body = match.Groups[3].Value;
                var v = 1;
                
                body = Regex.Replace(body, @" =\n +", " = ");
                body = body.Replace("= rocksdb_statistics_level_disable_all", "= 0"); //Fix bug in https://github.com/facebook/rocksdb/blob/9202db1867e412e51e72fc04062ca3664deb097b/include/rocksdb/c.h#L1268

                if(body.Contains("<<"))
                {
                    for(int i = 255; i >=0; i-- )
                    {
                        body = body.Replace($"1 << {i}", (1 << i).ToString());
                        body = body.Replace($"2 << {i}", (2 << i).ToString());
                    }
                }

                var values = Regex.Matches(body, @"([0-9a-zA-Z_]+)\s*(?:=\s*([0-9]+))?,?")
                    .AsEnumerable()
                    .Select(e => 
                    {
                        var n = e.Groups[1].Value;
                        var val = e.Groups[2].Success ? (v = int.Parse(e.Groups[2].Value)) : ++v;
                        return new NativeEnumValue(name: n, value: val);
                    })
                    .ToList();
                yield return new NativeEnum(name: name, values: values, comment: comment);
            }
        }

        //static Regex NativeFunctionStartPattern { get; } = new Regex(CommentPrologPattern + @"extern(\s+)ROCKSDB_LIBRARY_API(\s+)(const\s+|unsigned\s+)?([a-z0-9_]+(?:\*\*|\*)?)(\s+)([a-z0-9_]+)(\s*)\(", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.Singleline);
        static Regex NativeFunctionStartPattern { get; } = new Regex(CommentPrologPattern + @"extern\s+ROCKSDB_LIBRARY_API\s+([^\(]+)\(", RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.Singleline);
        static Regex FunctionClosePattern { get; } = new Regex(@"^\)\s*;[ \t]*", RegexOptions.Compiled);
        static IEnumerable<NativeFunction> ParseNativeFunctions(string source)
        {
            for (; ; )
            {
                var m = NativeFunctionStartPattern.Match(source);
                if (!m.Success)
                    yield break;
                var prolog = m.Groups[1].Value;
                var typeAndName = m.Groups[2].Value;
                (var funcName, var rawType) = typeAndName.PopRight(@"\s+([a-zA-Z0-9][a-zA-Z0-9_]*)\s*");

                Console.WriteLine($"Found function {funcName} with raw type {rawType}");

                var type = rawType.RegexReplace(@"\s+", " ");
                if (type == null || funcName == null)
                    throw new Exception($"Error parsing function type and name from:\n{typeAndName}");
                //var modifier = m.Groups[4].Value;
                //var type = AddModifier(m.Groups[5].Value, modifier);
                //var funcName = m.Groups[7].Value;

                var argsStart = m.Index + m.Length;
                var argsLength = FindClosingParenthesis(source, argsStart) - argsStart;
                var funcArgsUnparsed = source.Substring(argsStart, argsLength);

                (var argsProlog, var r0) = ParseWhitespace(funcArgsUnparsed);
                funcArgsUnparsed = r0;
                var funcArgs = ParseNativeArgs(funcArgsUnparsed).ToList();

                yield return new NativeFunction(funcName, type, funcArgs, prolog);

                source = source.Substring(argsStart + argsLength);
                var closing = FunctionClosePattern.Match(source);
                source = source.Substring(closing.Index + closing.Length);
            }
        }

        static string AddModifier(string type, string mod)
            => string.IsNullOrEmpty(mod) ? type : $"{mod} {type}";

        static int FindClosingParenthesis(string source, int start)
        {
            // starting with 1 open parenthesis
            var open = 1;
            for (; ; )
            {
                var c = source[start];
                if (c == '(')
                    open++;
                else if (c == ')')
                    open--;
                if (open == 0)
                    break;
                start++;
            }
            return start;
        }

        static (string Whitespace, string Remain) ParseWhitespace(string source)
        {
            var mod = Regex.Match(source, @"^\s*");
            return (Whitespace: mod.Value, Remain: source.Substring(mod.Length));
        }

        static (string Modifier, string Remain) ParseArgModifier(string source)
        {
            var mod = Regex.Match(source, @"^(const|unsigned)\s+");
            return (mod.Groups[1].Value, source.Substring(mod.Length));
        }

        static (string Type, string Remain) ParseType(string source)
        {
            var type = Regex.Match(source, @"^[a-zA-Z_0-9]+(\s+const\*|\s+const|\s*\*)*\s*");
            return (type.Value?.Trim(), source.Substring(type.Length));
        }

        static (string DelegateName, string DelegateArgs, string EndWs, string Remain) ParseDelegateSignature(string source)
        {
            var funcArgNameMatch = Regex.Match(source, @"^\(\*([a-zA-Z_0-9]+)\)\s*\(\s*");
            if (!funcArgNameMatch.Success)
                return (null, null, null, source);
            var delegateName = funcArgNameMatch.Groups[1].Value;
            source = source.Substring(funcArgNameMatch.Length);
            var cursor = 0;
            var open = 1;
            for (; ; )
            {
                var c = source[cursor];
                if (c == '(')
                    open++;
                else if (c == ')')
                    open--;
                if (open == 0)
                    break;
                cursor++;
            }
            var delegateArgs = string.Join(",", ParseNativeArgs(source.Substring(0, cursor)).ToList());
            source = source.Substring(cursor);
            var close = Regex.Match(source, @"^\)(\s*),?");
            var endWs = close.Groups[1].Value;
            source = source.Substring(close.Length);
            return (delegateName, delegateArgs, endWs, source);
        }

        static (string Name, bool IsArray, string Ending, string Remain) ParseSimpleArgName(string source)
        {
            var nameAndEnding = Regex.Match(source, @"^(?:([a-zA-Z_0-9]+)(\[\])?)?(\s*,?)");
            source = source.Substring(nameAndEnding.Length);
            return (nameAndEnding.Groups[1].Value, nameAndEnding.Groups[2].Success, nameAndEnding.Groups[3].Value, source);
        }

        static (NativeArg NativeArg, string Remain) ParseNextNativeArg(string args, int index)
        {
            Debug.Assert(!Regex.Match(args, @"^\s+").Success, "Must remove trailing white space first");

            (var mod, var r0) = ParseArgModifier(args);
            args = r0;

            (var type, var r1) = ParseType(args);
            args = r1;

            (var delegateName, var delegateArgs, var delegateEnding, var r2) = ParseDelegateSignature(args);
            args = r2;

            (var argName, var isArray, var ending, var r3) =
                delegateName == null ? ParseSimpleArgName(args) :
                (Name: delegateName, IsArray: false, Ending: delegateEnding, Remain: args);

            args = r3;

            (var endingWs, var r4) = ParseWhitespace(args);
            args = r4;

            return (
                NativeArg: new NativeArg(
                    index: index,
                    nativeType: delegateArgs.Then(a => $"({a}) -> {type}") ?? $"{AddModifier(type, mod)}{(isArray ? "*" : "")}".TrimStart(),
                    isDelegate: delegateArgs != null,
                    name: argName,
                    ending: $"{ending}{endingWs}"
                ),
                Remain: args
            );
        }

        static IEnumerable<NativeArg> ParseNativeArgs(string args)
        {
            Debug.Assert(!Regex.Match(args, @"^\s+").Success, "Must remove trailing white space first");
            var index = 0;
            while (args != "")
            {
                (var nativeArg, var remain) = ParseNextNativeArg(args, index++);
                args = remain;
                Debug.Assert(!string.IsNullOrEmpty(nativeArg.NativeType) || remain != "", "the args string is still non-empty, but we didn't parse an argument");
                yield return nativeArg;
            }
        }

        class RocksDbHeaderFileRegion
        {
            public string Title { get; }
            public NativeFunction[] NativeFunctions { get; }
            public NativeEnum[] NativeEnums { get; }
            public NativeTypeDef[] NativeTypeDefs { get; }

            public RocksDbHeaderFileRegion(string title, IEnumerable<NativeFunction> nativeFunctions, IEnumerable<NativeEnum> nativeEnums, IEnumerable<NativeTypeDef> nativeTypeDefs)
            {
                Title = title;
                NativeFunctions = nativeFunctions.ToArray();
                NativeEnums = nativeEnums.ToArray();
                NativeTypeDefs = nativeTypeDefs.ToArray();
            }

            public override string ToString()
                => $"{Title}: funcs[{NativeFunctions.Length}] enums[{NativeEnums.Length}] typedefs[{NativeTypeDefs.Length}]";
        }

        class NativeFunction
        {
            public string Name { get; }
            public string ReturnType { get; }
            public NativeArg[] Args { get; }
            public string Comments { get; }

            public NativeFunction(string name, string returnType, IEnumerable<NativeArg> args, string comments)
            {
                Name = name;
                ReturnType = returnType;
                Args = args.ToArray();
                Comments = comments;
            }

            public override string ToString()
                => $"{ReturnType} {Name}({string.Join(", ", Args.Select(a => a.ToString()))})";
        }

        class NativeArg
        {
            public int Index { get; }
            public string NativeType { get; }
            public bool IsDelegate { get; }
            public string Name { get; }
            public string Ending { get; }

            public NativeArg(int index, string nativeType, bool isDelegate, string name, string ending)
            {
                Index = index;
                NativeType = nativeType;
                IsDelegate = isDelegate;
                Name = name;
                Ending = ending;
            }

            public override string ToString()
                => IsDelegate ? "TODO: delegate" : $"{NativeType} {Name}";
        }

        static async Task<string> DownloadAsync(string url)
        {
            using (var client = new HttpClient())
            {
                var request  = await client.GetAsync(url);
                var response = await request.Content.ReadAsStringAsync();
                return response;
            }
        }
    }

    class IndentedCodeBuilder
    {
        private int Indent { set; get; } = 0;
        private List<string> Lines { get; } = new List<string>();

        public void StartBlock(string line)
        {
            AppendLine(line);
            Indent++;
        }

        public void EndBlock(string line)
        {
            Indent--;
            AppendLine(line);
        }

        public IndentedCodeBuilder AppendLine(string text)
            => AppendLines(SplitLines(text));

        public IndentedCodeBuilder AppendIndentedLine(string text)
            => AppendLineWithIndent((Indent: Indent + 1, Line: text));

        public IndentedCodeBuilder AppendLineWithoutIndent(string text)
            => AppendLineWithIndent((0, text));

        public IndentedCodeBuilder AppendLines(IEnumerable<string> lines)
            => AppendLinesWithIndent(lines.Select(line => (Indent, line)));

        public IndentedCodeBuilder AppendLinesWithIndent(int indent, IEnumerable<string> lines)
            => AppendLinesWithIndent(lines.Select(line => (indent, line)));

        public IndentedCodeBuilder AppendLineWithIndent(int indent, string text)
            => AppendLineWithIndent(line: (indent, text));

        private IndentedCodeBuilder AppendLinesWithIndent(IEnumerable<(int Indent, string Line)> lines)
        {
            foreach (var line in lines)
                AppendLineWithIndent(line);
            return this;
        }

        private IndentedCodeBuilder AppendLineWithIndent((int Indent, string Line) line)
        {
            var whitespace = string.Join("", Enumerable.Repeat("    ", line.Indent));
            if (string.IsNullOrWhiteSpace(line.Line))
                Lines.Add("");
            else
                Lines.Add($"{whitespace}{line.Line}");
            return this;
        }

        public static IEnumerable<string> SplitLines(string lines)
            => lines.Split('\n');

        public override string ToString()
            => string.Join("\n", Lines);
    }


    static class Extensions
    {
        public static IEnumerable<string> Lines(this string input)
            => input.Split('\n');

        public static string AsLines(this IEnumerable<string> str)
            => string.Join("\n", str);

        public static string RegexReplace(this string input, string pattern, string replacement)
            => pattern == null ? input : Regex.Replace(input, pattern, replacement);

        public static (string, string) SplitAt(this string input, int location)
            => (input.Substring(0, location), input.Substring(location));

        public static string Left(this string input, int chars)
            => input.Substring(0, chars);

        public static string Right(this string input, int chars)
            => input.Substring(input.Length - chars, chars);

        public static T If<T>(this T input, Func<T, bool> predicate)
            => input == null ? default(T) : predicate(input) ? input : default(T);

        public static string OrElse(this string input, string ifDefault)
            => string.IsNullOrEmpty(input) ? ifDefault : input;

        public static T OrElse<T>(this T input, T ifDefault)
            => input == null || input.Equals(default(T)) ? ifDefault : input;

        public static R Then<T, R>(this T input, Func<T, R> transformWhenNonNull)
            => input == null || input.Equals(default(T)) ? default(R) : transformWhenNonNull(input);

        public static IEnumerable<T> Once<T>(this T single)
            => Enumerable.Repeat(single, 1);

        public static bool In<T>(this T value, params T[] list)
            => list.Contains(value);

        public static bool IsMatchedBy(this string input, string pattern)
            => Regex.Match(input, pattern).If(m => m.Success && m.Index == 0 && m.Length == input.Length).Then(m => true);

        public static (string Matched, string Remain) PopRight(this string input, string pattern, RegexOptions options = RegexOptions.None)
        {
            var match = Regex.Match(input, $"{pattern}$");
            if (!match.Success)
                return (null, input);
            var remain = input.Substring(0, match.Index);
            var value = match.Groups.Count > 1 ? match.Groups[1].Value : match.Value;
            return (value, remain);
        }
    }
}
