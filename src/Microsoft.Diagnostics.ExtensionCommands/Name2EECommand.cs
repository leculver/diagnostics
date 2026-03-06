// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Diagnostics.DebugServices;
using Microsoft.Diagnostics.Runtime;

namespace Microsoft.Diagnostics.ExtensionCommands
{
    [Command(Name = "name2ee", Aliases = new[] { "Name2EE" }, Help = "Displays the MethodTable structure and EEClass structure for the specified type or method in the specified module.")]
    public class Name2EECommand : ClrRuntimeCommandBase
    {
        [Argument(Name = "arguments", Help = "module_name type_or_method_name (or module_name!type_or_method_name)")]
        public string[] Arguments { get; set; }

        private enum MatchKind
        {
            None,
            Type,
            Method,
            Field,
        }

        public override void Invoke()
        {
            if (Arguments == null || Arguments.Length == 0)
            {
                PrintUsage();
                return;
            }

            string moduleName;
            string itemName;

            if (Arguments.Length == 1)
            {
                // Try parsing "module!type" format
                string combined = Arguments[0];
                int bangIndex = combined.IndexOf('!');
                if (bangIndex > 0 && bangIndex != combined.Length - 1 && combined.IndexOf('!', bangIndex + 1) == -1)
                {
                    moduleName = combined.Substring(0, bangIndex);
                    itemName = combined.Substring(bangIndex + 1);
                }
                else
                {
                    PrintUsage();
                    return;
                }
            }
            else if (Arguments.Length == 2)
            {
                moduleName = Arguments[0];
                itemName = Arguments[1];
            }
            else
            {
                PrintUsage();
                return;
            }

            if (string.IsNullOrEmpty(moduleName) || string.IsNullOrEmpty(itemName))
            {
                PrintUsage();
                return;
            }

            bool isWildcard = moduleName == "*";

            List<ClrModule> modules;
            if (isWildcard)
            {
                modules = Runtime.EnumerateModules().ToList();
            }
            else
            {
                modules = Runtime.EnumerateModules().Where(m => MatchesModuleName(m, moduleName)).ToList();
            }

            int matchCount = 0;
            int nonMatchCount = 0;
            bool anyTypeFound = false;

            foreach (ClrModule module in modules)
            {
                Console.CancellationToken.ThrowIfCancellationRequested();

                string fileName = GetModuleFileName(module);
                bool foundInModule = SearchModule(module, itemName, isWildcard, fileName, ref matchCount);

                if (foundInModule)
                {
                    anyTypeFound = true;
                }
                else if (isWildcard)
                {
                    nonMatchCount++;
                }
            }

            // Heap-based fallback: constructed generic types (e.g., List<string>) are not
            // in the TypeDef map and won't be found by module-based search. Walk the GC heap
            // to discover them from live object method tables.
            if (!anyTypeFound)
            {
                HashSet<ulong> targetModuleAddresses = isWildcard
                    ? null
                    : new HashSet<ulong>(modules.Select(m => m.Address));

                anyTypeFound = SearchHeapForConstructedTypes(targetModuleAddresses, itemName, ref matchCount);
            }

            if (isWildcard && nonMatchCount > 0)
            {
                if (matchCount > 0)
                {
                    WriteLine("--------------------------------------");
                }

                WriteLine($"\nScanned {nonMatchCount} module{(nonMatchCount == 1 ? "" : "s")} which had no matches.");
            }

            if (matchCount == 0 && nonMatchCount == 0)
            {
                WriteLine($"Failed to find module matching '{moduleName}'.");
            }
        }

        /// <summary>
        /// Searches a module for the given item name. Returns true if any match was found.
        /// </summary>
        private bool SearchModule(ClrModule module, string itemName, bool isWildcard, string fileName, ref int matchCount)
        {
            // Normalize nested type separators (from the original C++ version)
            string normalizedName = itemName.Replace('/', '+');

            // Try to find as a type first, then as method/field
            // Walk all types via EnumerateTypeDefToMethodTableMap
            bool found = false;

            foreach ((ulong mt, int token) in module.EnumerateTypeDefToMethodTableMap())
            {
                Console.CancellationToken.ThrowIfCancellationRequested();

                if (mt == 0)
                {
                    continue;
                }

                ClrType type = Runtime.GetTypeByMethodTable(mt);
                if (type == null)
                {
                    continue;
                }

                MatchKind matchKind = GetMatchKind(type, normalizedName, out ClrMethod matchedMethod, out ClrField matchedField);
                if (matchKind == MatchKind.None)
                {
                    continue;
                }

                // Found a match
                if (!found)
                {
                    // First match for this module: print header
                    if (matchCount > 0)
                    {
                        WriteLine("--------------------------------------");
                    }
                    PrintModuleHeader(module, fileName);
                }
                else
                {
                    // Multiple matches within the same module
                    WriteLine("-----------------------");
                }

                found = true;

                switch (matchKind)
                {
                    case MatchKind.Type:
                        PrintTypeInfo(type);
                        break;
                    case MatchKind.Method:
                        PrintMethodInfo(matchedMethod);
                        break;
                    case MatchKind.Field:
                        PrintFieldInfo(type, matchedField);
                        break;
                }

                matchCount++;
            }

            if (!found && !isWildcard)
            {
                // Non-wildcard with no match: still print module header
                if (matchCount > 0)
                {
                    WriteLine("--------------------------------------");
                }
                PrintModuleHeader(module, fileName);
                matchCount++;
            }

            return found;
        }

        /// <summary>
        /// Determines what kind of match the given name is for this type.
        /// Checks: exact type name, method name, field name.
        /// </summary>
        private static MatchKind GetMatchKind(ClrType type, string name, out ClrMethod matchedMethod, out ClrField matchedField)
        {
            matchedMethod = null;
            matchedField = null;

            string typeName = type.Name;
            if (typeName == null)
            {
                return MatchKind.None;
            }

            // Search for partial type names
            if (typeName.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return MatchKind.Type;
            }

            // Check if the name could be TypeName.MethodOrField by splitting on the last '.'
            // Handle the ".." case (explicit interface implementation) same as C++ version
            int dotIndex = name.LastIndexOf('.');
            if (dotIndex <= 0)
            {
                return MatchKind.None;
            }

            // Check for ".." (back up one more)
            if (dotIndex > 0 && name[dotIndex - 1] == '.')
            {
                dotIndex--;
            }

            string typePartOfName = name.Substring(0, dotIndex);
            string memberName = name.Substring(dotIndex + 1);

            // If the ".." case: memberName will start with the interface method
            // e.g., "MyType..InterfaceMethod" -> typePart="MyType", memberName=".InterfaceMethod"

            if (!typeName.Contains(typePartOfName, StringComparison.OrdinalIgnoreCase))
            {
                return MatchKind.None;
            }

            // Check methods
            foreach (ClrMethod method in type.Methods)
            {
                if (method.Name != null && string.Equals(method.Name, memberName, StringComparison.Ordinal))
                {
                    matchedMethod = method;
                    return MatchKind.Method;
                }
            }

            // Check instance fields
            foreach (ClrInstanceField field in type.Fields)
            {
                if (field.Name != null && string.Equals(field.Name, memberName, StringComparison.Ordinal))
                {
                    matchedField = field;
                    return MatchKind.Field;
                }
            }

            // Check static fields
            foreach (ClrStaticField field in type.StaticFields)
            {
                if (field.Name != null && string.Equals(field.Name, memberName, StringComparison.Ordinal))
                {
                    matchedField = field;
                    return MatchKind.Field;
                }
            }

            return MatchKind.None;
        }

        /// <summary>
        /// Searches the GC heap for constructed generic types matching the given name.
        /// Constructed generic types (e.g., List&lt;string&gt;) have their own method tables
        /// but are not present in the module TypeDef map. This method discovers them by
        /// walking live heap objects and collecting unique types by method table.
        /// </summary>
        /// <param name="targetModuleAddresses">
        /// If non-null, only types from these modules are matched. Null means search all modules.
        /// </param>
        /// <param name="itemName">The type or member name to search for.</param>
        /// <param name="matchCount">Running count of total matches (updated in place).</param>
        /// <returns>True if any match was found on the heap.</returns>
        private bool SearchHeapForConstructedTypes(HashSet<ulong> targetModuleAddresses, string itemName, ref int matchCount)
        {
            string normalizedName = itemName.Replace('/', '+');
            HashSet<ulong> seenMethodTables = new();
            bool found = false;

            foreach (ClrObject obj in Runtime.Heap.EnumerateObjects())
            {
                Console.CancellationToken.ThrowIfCancellationRequested();

                if (!obj.IsValid)
                {
                    continue;
                }

                ClrType type = obj.Type;
                if (type == null || type.MethodTable == 0)
                {
                    continue;
                }

                // Only examine each unique method table once
                if (!seenMethodTables.Add(type.MethodTable))
                {
                    continue;
                }

                // Filter by target module when a specific module was requested
                if (targetModuleAddresses != null && (type.Module == null || !targetModuleAddresses.Contains(type.Module.Address)))
                {
                    continue;
                }

                MatchKind matchKind = GetMatchKind(type, normalizedName, out ClrMethod matchedMethod, out ClrField matchedField);
                if (matchKind == MatchKind.None)
                {
                    continue;
                }

                if (!found)
                {
                    if (matchCount > 0)
                    {
                        WriteLine("--------------------------------------");
                    }

                    WriteLine("Searching heap for constructed generic types...");
                }
                else
                {
                    WriteLine("-----------------------");
                }

                found = true;

                ClrModule module = type.Module;
                if (module != null)
                {
                    string fileName = GetModuleFileName(module);
                    PrintModuleHeader(module, fileName);
                }

                switch (matchKind)
                {
                    case MatchKind.Type:
                        PrintTypeInfo(type);
                        break;
                    case MatchKind.Method:
                        PrintMethodInfo(matchedMethod);
                        break;
                    case MatchKind.Field:
                        PrintFieldInfo(type, matchedField);
                        break;
                }

                matchCount++;
            }

            return found;
        }

        private void PrintUsage()
        {
            WriteLine("Usage: !name2ee module_name item_name");
            WriteLine("  or   !name2ee module_name!item_name");
            WriteLine("       use * for module_name to search all loaded modules");
            WriteLine("Examples: !name2ee  mscorlib.dll System.String.ToString");
            WriteLine("          !name2ee *!System.String");
        }

        private void PrintModuleHeader(ClrModule module, string fileName)
        {
            if (Console.SupportsDml)
            {
                Console.WriteDml($"Module:      <exec cmd=\"!dumpmodule /d {module.Address:x}\">{module.Address:x16}</exec>\n");
            }
            else
            {
                WriteLine($"Module:      {module.Address:x16}");
            }

            WriteLine($"Assembly:    {fileName}");
        }

        private void PrintTypeInfo(ClrType type)
        {
            WriteLine($"Token:       {type.MetadataToken:x16}");

            if (type.MethodTable != 0)
            {
                if (Console.SupportsDml)
                {
                    Console.WriteDml($"MethodTable: <exec cmd=\"!dumpmt /d {type.MethodTable:x}\">{type.MethodTable:x16}</exec>\n");
                }
                else
                {
                    WriteLine($"MethodTable: {type.MethodTable:x16}");
                }
            }
            else
            {
                WriteLine("MethodTable: <not loaded yet>");
            }

            WriteLine($"Name:        {type.Name}");
        }

        private void PrintMethodInfo(ClrMethod method)
        {
            WriteLine($"Token:       {(uint)method.MetadataToken:x16}");

            if (method.MethodDesc != 0)
            {
                if (Console.SupportsDml)
                {
                    Console.WriteDml($"MethodDesc:  <exec cmd=\"!dumpmd /d {method.MethodDesc:x}\">{method.MethodDesc:x16}</exec>\n");
                }
                else
                {
                    WriteLine($"MethodDesc:  {method.MethodDesc:x16}");
                }
            }
            else
            {
                WriteLine("MethodDesc:  <not loaded yet>");
            }

            WriteLine($"Name:        {method.Signature ?? method.Name ?? "<unknown>"}");

            if (method.NativeCode != 0)
            {
                if (Console.SupportsDml)
                {
                    Console.WriteDml($"JITTED Code Address: <exec cmd=\"!u {method.NativeCode:x}\">{method.NativeCode:x16}</exec>\n");
                }
                else
                {
                    WriteLine($"JITTED Code Address: {method.NativeCode:x16}");
                }
            }
            else
            {
                if (method.MethodDesc != 0)
                {
                    if (Console.SupportsDml)
                    {
                        Console.WriteDml($"Not JITTED yet. Use <exec cmd=\"!bpmd -md {method.MethodDesc:x}\">!bpmd -md {method.MethodDesc:x16}</exec> to break on run.\n");
                    }
                    else
                    {
                        WriteLine($"Not JITTED yet. Use !bpmd -md {method.MethodDesc:x16} to break on run.");
                    }
                }
                else
                {
                    WriteLine("Not JITTED yet.");
                }
            }
        }

        private void PrintFieldInfo(ClrType type, ClrField field = null)
        {
            WriteLine($"Field {field?.Name ?? "<unknown>"} (mdToken token) of");
            PrintTypeInfo(type);
        }

        private static string GetModuleFileName(ClrModule module)
        {
            if (string.IsNullOrEmpty(module.Name))
            {
                return module.IsDynamic ? "<dynamic>" : "<unknown>";
            }

            return Path.GetFileName(module.Name);
        }

        private static bool MatchesModuleName(ClrModule module, string name)
        {
            if (string.IsNullOrEmpty(module.Name))
            {
                return false;
            }

            string fileName = Path.GetFileName(module.Name);

            // Match by filename (with or without extension)
            if (string.Equals(fileName, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Try without .dll extension
            string fileNameWithoutExt = Path.GetFileNameWithoutExtension(module.Name);
            if (string.Equals(fileNameWithoutExt, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Match by full path
            if (string.Equals(module.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        [HelpInvoke]
        public static string GetDetailedHelp() =>
@"Name2EE displays the MethodTable and EEClass for the specified type or method
in the specified module. The specified module must be loaded in the process.

To get the proper type name, browse the module with the IL disassembler 
(ildasm.exe). You can also pass * as the module name parameter to search all
loaded managed modules. When using the wildcard, only matching modules are
displayed and non-matching modules are summarized at the end.

If the type is not found in the module metadata (e.g., a constructed generic
type like List<string>), the command will search the GC heap for matching
types that have live object instances.

    {prompt}name2ee mscorlib.dll System.String.ToString
    Module:      00007ffe65744000
    Assembly:    System.Private.CoreLib.dll
    MethodDesc:  00007ffe65ff0bf0
    Name:        System.String.ToString(System.IFormatProvider)
    JITTED Code Address: 00007ffe660a1234

    {prompt}name2ee *!System.String
    Module:      00007ffe65744000
    Assembly:    System.Private.CoreLib.dll
    Token:       0000000002000XXX
    MethodTable: 00007ffe65ff0bf0
    Name:        System.String
    --------------------------------------

    Scanned 45 modules which had no matches.
";
    }
}
