// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

namespace Microsoft.Diagnostics.NETCore.Client
{
    /// <summary>
    /// Represents a discovered diagnostic endpoint, including the process ID extracted
    /// from its name and the full endpoint address (socket path or pipe name).
    /// Unlike <see cref="DiagnosticsClient.GetPublishedProcesses"/>, which returns only
    /// distinct PIDs, this type preserves per-endpoint information needed for
    /// cross-container scenarios where multiple processes share the same PID.
    /// </summary>
    public readonly struct ProcessEndpointInfo : IEquatable<ProcessEndpointInfo>
    {
        /// <summary>
        /// The process ID extracted from the diagnostic endpoint name.
        /// In cross-container scenarios this may be a namespace-local PID
        /// rather than a host PID.
        /// </summary>
        public int ProcessId { get; }

        /// <summary>
        /// The full endpoint address (e.g., Unix domain socket path or named pipe name)
        /// that can be used to connect to this diagnostic endpoint.
        /// </summary>
        public string EndpointAddress { get; }

        internal ProcessEndpointInfo(int processId, string endpointAddress)
        {
            ProcessId = processId;
            EndpointAddress = endpointAddress;
        }

        public override bool Equals(object obj) => obj is ProcessEndpointInfo other && Equals(other);

        public bool Equals(ProcessEndpointInfo other) =>
            ProcessId == other.ProcessId &&
            string.Equals(EndpointAddress, other.EndpointAddress, StringComparison.Ordinal);

        public override int GetHashCode()
        {
            unchecked
            {
                return (ProcessId * 397) ^ (EndpointAddress?.GetHashCode() ?? 0);
            }
        }

        public static bool operator ==(ProcessEndpointInfo left, ProcessEndpointInfo right) => left.Equals(right);
        public static bool operator !=(ProcessEndpointInfo left, ProcessEndpointInfo right) => !left.Equals(right);

        public override string ToString() => $"PID={ProcessId}, Address={EndpointAddress}";
    }
}
