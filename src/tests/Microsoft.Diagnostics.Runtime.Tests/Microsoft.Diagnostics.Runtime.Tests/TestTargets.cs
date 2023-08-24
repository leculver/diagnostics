// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Xml.Linq;
using Microsoft.Diagnostics.Runtime.Implementation;
using Microsoft.Diagnostics.Runtime.Utilities.DbgEng;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Microsoft.Diagnostics.Runtime.Tests
{
    public enum GCMode
    {
        Workstation,
        Server
    }

    public class ExceptionTestData
    {
        public readonly string OuterExceptionMessage = "IOE Message";
        public readonly string OuterExceptionType = "System.InvalidOperationException";
    }

    public static class TestTargets
    {
        private static TestTarget _arrays;
        private static TestTarget _clrObjects;
        private static TestTarget _gcroot;
        private static TestTarget _gcroot2;
        private static TestTarget _nestedException;
        private static TestTarget _nestedTypes;
        private static TestTarget _gcHandles;
        private static TestTarget _types;
        private static TestTarget _appDomains;
        private static TestTarget _finalizationQueue;
        private static TestTarget _byReference;

        public static TestTarget GCRoot => _gcroot ??= new("GCRoot");
        public static TestTarget GCRoot2 => _gcroot2 ??= new("GCRoot2");
        public static TestTarget NestedException => _nestedException ??= new("NestedException");
        public static TestTarget NestedTypes => _nestedTypes ??= new("NestedTypes");
        public static ExceptionTestData NestedExceptionData => new();
        public static TestTarget GCHandles => _gcHandles ??= new("GCHandles");
        public static TestTarget Types => _types ??= new("Types");
        public static TestTarget AppDomains => _appDomains ??= new("AppDomains");
        public static TestTarget FinalizationQueue => _finalizationQueue ??= new("FinalizationQueue");
        public static TestTarget ClrObjects => _clrObjects ??= new("ClrObjects");
        public static TestTarget Arrays => _arrays ??= new("Arrays");
        public static TestTarget ByReference => _byReference ??= new("ByReference");

        public static string GetTestArtifactFolder()
        {
            string curr = Environment.CurrentDirectory;
            while (curr != null)
            {
                string artifacts = Path.Combine(curr, "test_artifacts");
                if (Directory.Exists(artifacts))
                    return artifacts;

                curr = Path.GetDirectoryName(curr);
            }

            return null;
        }
    }

    public class TestTarget
    {
        public string Name { get; }
        public string Executable { get; }

        public TestTarget(string name)
        {
            Name = name;
            Executable = name;

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Executable += ".exe";

            DirectoryInfo info = new(Environment.CurrentDirectory);
            while (info != null)
            {
                if (info.Parent is null)
                {
                    throw new DirectoryNotFoundException("Could not find 'artifacts/bin' directory!");
                }

                if (info.Parent.Name.Equals("artifacts", StringComparison.OrdinalIgnoreCase) && info.Name.Equals("bin"))
                    break;

                info = info.Parent;
            }

            DirectoryInfo[] matches = info.GetDirectories(Name, SearchOption.TopDirectoryOnly);
            if (matches.Length == 0)
                throw new DirectoryNotFoundException($"Could not find artifacts/bin/{Name} directory!");

            if (matches.Length > 1)
                throw new DirectoryNotFoundException($"Found multiple artifacts/bin/{Name} directories!");

            info = matches[0];

            FileInfo[] files = info.GetFiles(Executable, SearchOption.AllDirectories);

            if (files.Length == 0)
                throw new FileNotFoundException($"Could not find '{Executable}' under '{info.FullName}'!");

            if (files.Length > 1)
                throw new FileNotFoundException($"Found multiple '{Executable}' under '{info.FullName}'!");

            Executable = files[0].FullName;
        }

        private DataTarget LoadDump(GCMode gc, bool full)
        {
            string path = BuildDumpName(gc, full);
            if (!File.Exists(path))
            {
                CreateDumpFile(gc);
                Assert.True(File.Exists(path));
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                DataTarget dataTarget = DataTarget.LoadDump(path);
                dataTarget.FileLocator = SymbolGroup.CreateFromSymbolPath(string.Empty);
                return dataTarget;
            }
            else
            {
                return DataTarget.LoadDump(path);
            }
        }

        private void CreateDumpFile(GCMode gc)
        {
            DebuggerStartInfo info = new();
            if (gc == GCMode.Server)
            {
                info.SetEnvironmentVariable("COMPlus_BuildFlavor", "SVR");
                info.SetEnvironmentVariable("DOTNET_gcServer", "1");
            }

            using Debugger debugger = info.LaunchProcess(Executable, Path.GetDirectoryName(Executable));
            debugger.OnException += (debugger, exception, firstChance) => {
                if (!firstChance && exception.ExceptionCode == (uint)ExceptionTypes.Clr)
                {
                    string fullDumpPath = BuildDumpName(gc, full: true);
                    _ = debugger.WriteDumpFile(fullDumpPath, DEBUG_DUMP.DEFAULT);

                    string miniDumpPath = BuildDumpName(gc, full: false);
                    _ = debugger.WriteDumpFile(miniDumpPath, DEBUG_DUMP.SMALL);
                }
            };

            debugger.RunUntilExit();
        }

        public string BuildDumpName(GCMode gcmode, bool full)
        {
            string fileName = Path.Combine(Path.GetDirectoryName(Executable), Path.GetFileNameWithoutExtension(Executable));

            string gc = gcmode == GCMode.Server ? "svr" : "wks";
            string dumpType = full ? string.Empty : "_mini";
            fileName = $"{fileName}_{gc}{dumpType}.dmp";
            return fileName;
        }

        public DataTarget LoadMinidump(GCMode gc = GCMode.Workstation) => LoadDump(gc, false);

        public DataTarget LoadFullDump(GCMode gc = GCMode.Workstation) => LoadDump(gc, true);

        [SupportedOSPlatform("windows")]
        public DataTarget LoadFullDumpWithDbgEng(GCMode gc = GCMode.Workstation)
        {
            string dumpPath = BuildDumpName(gc, true);
            if (!File.Exists(dumpPath))
                CreateDumpFile(gc);

            Utilities.DbgEng.DbgEngIDataReader dbgengReader = new(dumpPath);
            return new DataTarget(new CustomDataTarget(dbgengReader));
        }
    }
}
