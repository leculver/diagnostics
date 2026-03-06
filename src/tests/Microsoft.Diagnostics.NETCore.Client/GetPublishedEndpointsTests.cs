// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Diagnostics.TestHelpers;
using Xunit;
using Xunit.Abstractions;
using Xunit.Extensions;
using TestRunner = Microsoft.Diagnostics.CommonTestRunner.TestRunner;

// Newer SDKs flag MemberData(nameof(Configurations)) with this error
// Avoid unnecessary zero-length array allocations.  Use Array.Empty<object>() instead.
#pragma warning disable CA1825

namespace Microsoft.Diagnostics.NETCore.Client
{
    public class GetPublishedEndpointsTest
    {
        private readonly ITestOutputHelper _output;

        public static IEnumerable<object[]> Configurations => TestRunner.Configurations;

        public GetPublishedEndpointsTest(ITestOutputHelper outputHelper)
        {
            _output = outputHelper;
        }

        [SkippableTheory, MemberData(nameof(Configurations))]
        public async Task PublishedEndpointsContainsRunningProcess(TestConfiguration config)
        {
            await using TestRunner runner = await TestRunner.Create(config, _output, "Tracee");
            await runner.Start();

            List<ProcessEndpointInfo> endpoints = new(DiagnosticsClient.GetPublishedEndpoints());
            foreach (ProcessEndpointInfo ep in endpoints)
            {
                runner.WriteLine($"Saw endpoint PID={ep.ProcessId}, Address={ep.EndpointAddress}");
            }

            Assert.Contains(endpoints, ep => ep.ProcessId == runner.Pid);
            runner.WakeupTracee();
        }

        [SkippableTheory, MemberData(nameof(Configurations))]
        public async Task PublishedEndpointsIncludesAddress(TestConfiguration config)
        {
            await using TestRunner runner = await TestRunner.Create(config, _output, "Tracee");
            await runner.Start();

            List<ProcessEndpointInfo> endpoints = new(DiagnosticsClient.GetPublishedEndpoints());
            ProcessEndpointInfo match = endpoints.FirstOrDefault(ep => ep.ProcessId == runner.Pid);

            Assert.NotEqual(default, match);
            Assert.False(string.IsNullOrEmpty(match.EndpointAddress),
                "EndpointAddress should not be null or empty for a running process.");

            runner.WakeupTracee();
        }

        [SkippableTheory, MemberData(nameof(Configurations))]
        public async Task MultipleProcessesAllHaveEndpoints(TestConfiguration config)
        {
            TestRunner[] runners = new TestRunner[3];
            int[] pids = new int[3];

            try
            {
                for (int i = 0; i < 3; i++)
                {
                    runners[i] = await TestRunner.Create(config, _output, "Tracee");
                    await runners[i].Start();
                    pids[i] = runners[i].Pid;
                }

                List<ProcessEndpointInfo> endpoints = new(DiagnosticsClient.GetPublishedEndpoints());
                foreach (ProcessEndpointInfo ep in endpoints)
                {
                    _output.WriteLine($"[{DateTime.Now}] Saw endpoint PID={ep.ProcessId}, Address={ep.EndpointAddress}");
                }

                for (int i = 0; i < 3; i++)
                {
                    Assert.Contains(endpoints, ep => ep.ProcessId == pids[i]);
                }

                // Each process should have a distinct endpoint address
                var matchingEndpoints = endpoints.Where(ep => pids.Contains(ep.ProcessId)).ToList();
                var distinctAddresses = matchingEndpoints.Select(ep => ep.EndpointAddress).Distinct().ToList();
                Assert.Equal(matchingEndpoints.Count, distinctAddresses.Count);

                for (int i = 0; i < 3; i++)
                {
                    runners[i].WakeupTracee();
                }
            }
            finally
            {
                for (int i = 0; i < 3; i++)
                {
                    if (runners[i] != null)
                    {
                        await runners[i].DisposeAsync();
                    }
                }
            }
        }

        [Fact]
        public void ProcessEndpointInfo_Equality()
        {
            ProcessEndpointInfo a = new(42, "/tmp/dotnet-diagnostic-42-12345-socket");
            ProcessEndpointInfo b = new(42, "/tmp/dotnet-diagnostic-42-12345-socket");
            ProcessEndpointInfo c = new(42, "/tmp/dotnet-diagnostic-42-99999-socket");
            ProcessEndpointInfo d = new(99, "/tmp/dotnet-diagnostic-42-12345-socket");

            Assert.Equal(a, b);
            Assert.NotEqual(a, c);
            Assert.NotEqual(a, d);
            Assert.True(a == b);
            Assert.True(a != c);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
        }

        [Fact]
        public void ProcessEndpointInfo_ToString_ContainsPidAndAddress()
        {
            ProcessEndpointInfo info = new(42, "/tmp/dotnet-diagnostic-42-12345-socket");
            string str = info.ToString();
            Assert.Contains("42", str);
            Assert.Contains("/tmp/dotnet-diagnostic-42-12345-socket", str);
        }

        #region ResolveAllAddresses tests

        [Fact]
        public void ResolveAllAddresses_EmptyDirectory_ReturnsEmpty()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"diag-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                List<string> addresses = PidIpcEndpoint.ResolveAllAddresses(tempDir, 42);
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    // On Windows, the default pipe name is always returned
                    Assert.Single(addresses);
                    Assert.Contains("dotnet-diagnostic-42", addresses[0]);
                }
                else
                {
                    Assert.Empty(addresses);
                }
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [ConditionalFact(typeof(PidIpcEndpointTests), nameof(PidIpcEndpointTests.IsNotLinux))]
        public void ResolveAllAddresses_NonexistentDirectory_ReturnsEmpty()
        {
            // On non-Windows, a missing directory should return empty (not throw)
            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                List<string> addresses = PidIpcEndpoint.ResolveAllAddresses("/nonexistent/path", 42);
                Assert.Empty(addresses);
            }
        }

        [ConditionalFact(typeof(PidIpcEndpointTests), nameof(PidIpcEndpointTests.IsLinux))]
        public void ResolveAllAddresses_MultipleSocketsForSamePid_ReturnsAll()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"diag-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                // Simulate multiple containers with PID 1 and different timestamps
                string socket1 = Path.Combine(tempDir, "dotnet-diagnostic-1-100-socket");
                string socket2 = Path.Combine(tempDir, "dotnet-diagnostic-1-200-socket");
                string socket3 = Path.Combine(tempDir, "dotnet-diagnostic-1-300-socket");
                // Also a socket for a different PID (should not be returned)
                string socket4 = Path.Combine(tempDir, "dotnet-diagnostic-99-400-socket");

                File.WriteAllText(socket1, "");
                File.WriteAllText(socket2, "");
                File.WriteAllText(socket3, "");
                File.WriteAllText(socket4, "");

                List<string> addresses = PidIpcEndpoint.ResolveAllAddresses(tempDir, 1);
                Assert.Equal(3, addresses.Count);
                Assert.All(addresses, a => Assert.Contains("dotnet-diagnostic-1-", a));
                Assert.DoesNotContain(addresses, a => a.Contains("dotnet-diagnostic-99-"));
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        [ConditionalFact(typeof(PidIpcEndpointTests), nameof(PidIpcEndpointTests.IsLinux))]
        public void ResolveAllAddresses_IncludesDsrouterSockets()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), $"diag-test-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            try
            {
                string normalSocket = Path.Combine(tempDir, "dotnet-diagnostic-42-100-socket");
                string dsrouterSocket = Path.Combine(tempDir, "dotnet-diagnostic-dsrouter-42-200-socket");

                File.WriteAllText(normalSocket, "");
                File.WriteAllText(dsrouterSocket, "");

                List<string> addresses = PidIpcEndpoint.ResolveAllAddresses(tempDir, 42);
                Assert.Equal(2, addresses.Count);
            }
            finally
            {
                Directory.Delete(tempDir, true);
            }
        }

        #endregion
    }
}
