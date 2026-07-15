// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.DebugServices;
using Microsoft.Diagnostics.Runtime;

namespace SOS.Hosting
{
    /// <summary>
    /// Prototype demonstrating the "SOS supplies the runtime" model that ClrMD's
    /// <see cref="DataTarget.AddLoadedRuntime(ClrInfo, Func{IntPtr}, object)"/> API enables.
    ///
    /// The production goal is:
    ///   - SOS performs its own runtime detection.
    ///   - SOS asks dbgshim for the data-access interface (dbgshim decides cDAC vs in-box DAC).
    ///   - SOS hands that IXCLRDataProcess to ClrMD, which builds a ClrRuntime over it.
    ///
    /// This prototype exercises that end-to-end round trip against a single runtime, without the full
    /// dbgshim selection logic: it detects coreclr itself from the module list, loads the bundled cDAC,
    /// creates an IXCLRDataProcess, registers it through AddLoadedRuntime, then finds the registered
    /// ClrInfo back through DataTarget.ClrVersions and creates the runtime from it. If the runtime walks,
    /// the hand-off contract is correct.
    /// </summary>
    internal static class ClrInfoExample
    {
        // IID_IXCLRDataProcess
        private static readonly Guid IID_IXCLRDataProcess = new("5c552ab6-fc09-4cb3-8e36-22fa03c798b7");

        // coreclr's exported symbol whose address is the cDAC contract descriptor.
        private const string ContractDescriptorExport = "DotNetRuntimeContractDescriptor";

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate int ClrDataCreateInstanceDelegate(in Guid riid, IntPtr dacDataInterface, out IntPtr ppObj);

        /// <summary>
        /// Runs the detect -> load cDAC -> AddLoadedRuntime -> find -> CreateRuntime round trip.
        /// </summary>
        /// <param name="services">The host service provider (memory, modules, target, cDAC path, etc.).</param>
        /// <returns>The ClrRuntime created through the host-supplied IXCLRDataProcess, or null on failure.</returns>
        public static ClrRuntime CreateRuntimeFromHostDetection(IServiceProvider services)
        {
            if (services is null)
            {
                throw new ArgumentNullException(nameof(services));
            }

            IDataReader dataReader = services.GetService(typeof(IDataReader)) as IDataReader
                ?? throw new DiagnosticsException("An IDataReader is required.");

            // Build the DataTarget but do NOT let ClrMD enumerate runtimes: we register our own below and
            // then read it back, proving the host-driven path rather than ClrMD's built-in detection.
            DataTarget dataTarget = new(dataReader, new DataTargetOptions
            {
                SymbolProvider = services.GetService(typeof(IClrSymbolProvider)) as IClrSymbolProvider,
            });

            // Step 1: SOS-side detection. Find a coreclr module of version 11.0 or greater. On Windows the
            // module version is available directly; on other platforms module versions are commonly 0.0.0.0,
            // so this prototype keys on the Windows "coreclr.dll" as the SOS team requested.
            ModuleInfo coreclr = FindCoreClr(dataTarget, minMajorVersion: 11);
            if (coreclr is null)
            {
                Trace.TraceInformation("ClrInfoExample: no coreclr.dll v11.0+ module found; nothing to do.");
                return null;
            }

            // The cDAC needs the runtime's contract descriptor address, which SOS reads from the coreclr
            // module's export table.
            ulong contractDescriptor = coreclr.GetExportSymbolAddress(ContractDescriptorExport);
            if (contractDescriptor == 0)
            {
                Trace.TraceError($"ClrInfoExample: '{ContractDescriptorExport}' export not found in {coreclr.FileName}; runtime is not cDAC-capable.");
                return null;
            }

            // Step 2: SOS authors the ClrInfo describing the runtime it detected. This is a runtime that ClrMD
            // did not (and need not) discover on its own.
            ClrInfo clrInfo = new(dataTarget, coreclr, coreclr.Version)
            {
                Flavor = ClrFlavor.Core,
                ContractDescriptorAddress = contractDescriptor,
            };

            // Step 3: Register the runtime with a factory that lazily produces the host-owned IXCLRDataProcess.
            // The lock would, in real SOS, be shared with SOS's own DAC usage; here ClrMD's own lock suffices.
            object dacLock = new();
            dataTarget.AddLoadedRuntime(clrInfo, () => CreateCDacProcess(services, clrInfo, coreclr), dacLock);

            // Step 4: Read the runtime back out of ClrMD to prove registration took effect. ClrMD did not
            // detect this runtime; it is present only because AddLoadedRuntime added it.
            ClrInfo found = dataTarget.ClrVersions.FirstOrDefault(c => ReferenceEquals(c, clrInfo));
            if (found is null)
            {
                Trace.TraceError("ClrInfoExample: registered ClrInfo was not found in DataTarget.ClrVersions.");
                return null;
            }

            // Step 5: Create the runtime from the registered ClrInfo. This invokes the factory, which loads the
            // cDAC and returns the IXCLRDataProcess ClrMD then wraps.
            ClrRuntime runtime = found.CreateRuntime();

            // Step 6: Prove the runtime actually works over the host-supplied data-access interface.
            int moduleCount = runtime.EnumerateModules().Count();
            Trace.TraceInformation($"ClrInfoExample: round trip succeeded. Runtime {clrInfo.Version} walked {moduleCount} modules over the host-supplied cDAC IXCLRDataProcess.");

            return runtime;
        }

        private static ModuleInfo FindCoreClr(DataTarget dataTarget, int minMajorVersion)
        {
            foreach (ModuleInfo module in dataTarget.EnumerateModules())
            {
                string name = Path.GetFileName(module.FileName ?? string.Empty);
                if (!name.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (module.Version.Major >= minMajorVersion)
                {
                    return module;
                }
            }

            return null;
        }

        /// <summary>
        /// Loads the bundled cDAC and creates an IXCLRDataProcess over it using SOS's own ICLRDataTarget
        /// (<see cref="DataTargetWrapper"/>). This mirrors what SOS does in production, minus the dbgshim
        /// cDAC-vs-DAC selection.
        /// </summary>
        private static IntPtr CreateCDacProcess(IServiceProvider services, ClrInfo clrInfo, ModuleInfo coreclr)
        {
            IHostAssetResolver assetResolver = services.GetService(typeof(IHostAssetResolver)) as IHostAssetResolver
                ?? throw new DiagnosticsException("An IHostAssetResolver is required to locate the bundled cDAC.");

            string cdacPath = assetResolver.GetCDacPath();
            if (string.IsNullOrEmpty(cdacPath) || !File.Exists(cdacPath))
            {
                throw new FileNotFoundException($"The bundled cDAC was not found at '{cdacPath}'.");
            }

            // The cDAC ships in the signed tool directory and carries no individual signature, so it is
            // loaded without verification (the same policy the production cDAC load path uses).
            IntPtr cdacHandle = DataTarget.PlatformFunctions.LoadLibrary(cdacPath);
            if (cdacHandle == IntPtr.Zero)
            {
                throw new FileLoadException($"Failed to load the cDAC at '{cdacPath}'.");
            }

            IntPtr createInstanceAddr = DataTarget.PlatformFunctions.GetLibraryExport(cdacHandle, "CLRDataCreateInstance");
            if (createInstanceAddr == IntPtr.Zero)
            {
                throw new EntryPointNotFoundException("The cDAC does not export CLRDataCreateInstance.");
            }

            ClrDataCreateInstanceDelegate createInstance =
                Marshal.GetDelegateForFunctionPointer<ClrDataCreateInstanceDelegate>(createInstanceAddr);

            // SOS's real ICLRDataTarget. It reads target memory, metadata, and the contract descriptor. We
            // feed it a lightweight IRuntime carrying only what it needs (target, coreclr base, our ClrInfo).
            IRuntime exampleRuntime = new ExampleRuntime(services, clrInfo, coreclr.ImageBase);
            DataTargetWrapper dataTargetWrapper = new(services, exampleRuntime);
            try
            {
                int hr = createInstance(IID_IXCLRDataProcess, dataTargetWrapper.IDataTarget, out IntPtr clrDataProcess);
                if (hr != 0)
                {
                    throw new ClrDiagnosticsException($"CLRDataCreateInstance failed 0x{hr:x8}.");
                }

                return clrDataProcess;
            }
            finally
            {
                // The cDAC holds its own reference to the data target; release ours (same as production).
                dataTargetWrapper.ReleaseWithCheck();
            }
        }

        /// <summary>
        /// Minimal <see cref="IRuntime"/> exposing only what <see cref="DataTargetWrapper"/> reads:
        /// the target (architecture), the runtime module base, and the ClrInfo (contract descriptor).
        /// </summary>
        private sealed class ExampleRuntime : IRuntime
        {
            private readonly IServiceProvider _hostServices;
            private readonly ClrInfo _clrInfo;
            private readonly ulong _runtimeBaseAddress;

            public ExampleRuntime(IServiceProvider hostServices, ClrInfo clrInfo, ulong runtimeBaseAddress)
            {
                _hostServices = hostServices;
                _clrInfo = clrInfo;
                _runtimeBaseAddress = runtimeBaseAddress;
                Services = new ClrInfoServiceProvider(clrInfo);
                RuntimeModule = (_hostServices.GetService(typeof(IModuleService)) as IModuleService)
                    ?.GetModuleFromBaseAddress(runtimeBaseAddress);
            }

            public int Id => 0;

            public ITarget Target => _hostServices.GetService(typeof(ITarget)) as ITarget;

            public IServiceProvider Services { get; }

            public RuntimeType RuntimeType => RuntimeType.NetCore;

            public Version RuntimeVersion => _clrInfo.Version;

            public IModule RuntimeModule { get; }

            public string RuntimeModuleDirectory { get; set; }

            public string GetDacFilePath(out bool verifySignature)
            {
                // Not used by this prototype: the data-access path is the cDAC created above.
                verifySignature = false;
                return null;
            }

            public string GetCDacFilePath() => null;

            public string GetDbiFilePath() => null;

            /// <summary>Tiny provider so DataTargetWrapper's Services.GetService&lt;ClrInfo&gt;() resolves.</summary>
            private sealed class ClrInfoServiceProvider : IServiceProvider
            {
                private readonly ClrInfo _clrInfo;

                public ClrInfoServiceProvider(ClrInfo clrInfo) => _clrInfo = clrInfo;

                public object GetService(Type serviceType) => serviceType == typeof(ClrInfo) ? _clrInfo : null;
            }
        }
    }
}
