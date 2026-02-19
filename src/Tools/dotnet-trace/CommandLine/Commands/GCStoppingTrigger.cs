// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Microsoft.Diagnostics.Tools.Trace
{
    /// <summary>
    /// Monitors GC events during trace collection and triggers a stop when configured GC conditions are met.
    /// Supports stopping on Gen2 GC, GC duration exceeding a threshold, GC suspension duration,
    /// and background GC final pause duration.
    /// </summary>
    internal sealed class GCStoppingTrigger : IAsyncDisposable
    {
        private readonly Stream _eventStream;
        private readonly Action _onTriggered;
        private readonly bool _stopOnGen2GC;
        private readonly int _stopOnGCOverMsec;
        private readonly int _stopOnGCSuspendOverMSec;
        private readonly int _stopOnBGCFinalPauseOverMsec;
        private EventPipeEventSource _eventSource;

        // Track GC start times for duration computation, keyed by GC Count
        private readonly Dictionary<int, double> _gcStartTimes = new();

        // Track most recent EE suspension start time and reason
        private double _lastSuspendEEStartTime = -1;
        private GCSuspendEEReason _lastSuspendEEReason;

        private bool _triggered;

        public GCStoppingTrigger(
            Stream eventStream,
            Action onTriggered,
            bool stopOnGen2GC,
            int stopOnGCOverMsec,
            int stopOnGCSuspendOverMSec,
            int stopOnBGCFinalPauseOverMsec)
        {
            _eventStream = eventStream;
            _onTriggered = onTriggered;
            _stopOnGen2GC = stopOnGen2GC;
            _stopOnGCOverMsec = stopOnGCOverMsec;
            _stopOnGCSuspendOverMSec = stopOnGCSuspendOverMSec;
            _stopOnBGCFinalPauseOverMsec = stopOnBGCFinalPauseOverMsec;
        }

        public Task ProcessAsync(CancellationToken token)
        {
            return Task.Run(() =>
            {
                _eventSource = new EventPipeEventSource(_eventStream);
                token.ThrowIfCancellationRequested();
                using IDisposable registration = token.Register(() => _eventSource.Dispose());

                ClrTraceEventParser clr = _eventSource.Clr;

                if (_stopOnGen2GC)
                {
                    clr.GCStart += OnGCStartForGen2;
                }

                if (_stopOnGCOverMsec > 0)
                {
                    clr.GCStart += OnGCStartForDuration;
                    clr.GCStop += OnGCStopForDuration;
                }

                if (_stopOnGCSuspendOverMSec > 0 || _stopOnBGCFinalPauseOverMsec > 0)
                {
                    clr.GCSuspendEEStart += OnSuspendEEStart;
                }

                if (_stopOnGCSuspendOverMSec > 0)
                {
                    clr.GCSuspendEEStop += OnSuspendEEStop;
                }

                if (_stopOnBGCFinalPauseOverMsec > 0)
                {
                    clr.GCRestartEEStop += OnRestartEEStopForBGC;
                }

                _eventSource.Process();
                token.ThrowIfCancellationRequested();
            }, token);
        }

        private void TriggerStop()
        {
            if (!_triggered)
            {
                _triggered = true;
                _onTriggered();
            }
        }

        private void OnGCStartForGen2(GCStartTraceData data)
        {
            if (data.Depth >= 2 && data.Type != GCType.BackgroundGC)
            {
                TriggerStop();
            }
        }

        private void OnGCStartForDuration(GCStartTraceData data)
        {
            if (data.Type != GCType.BackgroundGC)
            {
                _gcStartTimes[data.Count] = data.TimeStampRelativeMSec;
            }
        }

        private void OnGCStopForDuration(GCEndTraceData data)
        {
            if (_gcStartTimes.TryGetValue(data.Count, out double startTime))
            {
                _gcStartTimes.Remove(data.Count);
                double durationMsec = data.TimeStampRelativeMSec - startTime;
                if (durationMsec > _stopOnGCOverMsec)
                {
                    TriggerStop();
                }
            }
        }

        private void OnSuspendEEStart(GCSuspendEETraceData data)
        {
            _lastSuspendEEStartTime = data.TimeStampRelativeMSec;
            _lastSuspendEEReason = data.Reason;
        }

        private void OnSuspendEEStop(TraceEvent data)
        {
            if (_lastSuspendEEStartTime >= 0)
            {
                double durationMsec = data.TimeStampRelativeMSec - _lastSuspendEEStartTime;
                _lastSuspendEEStartTime = -1;
                if (durationMsec > _stopOnGCSuspendOverMSec)
                {
                    TriggerStop();
                }
            }
        }

        private void OnRestartEEStopForBGC(TraceEvent data)
        {
            if (_lastSuspendEEStartTime >= 0 && _lastSuspendEEReason == GCSuspendEEReason.SuspendForGCPrep)
            {
                double durationMsec = data.TimeStampRelativeMSec - _lastSuspendEEStartTime;
                _lastSuspendEEStartTime = -1;
                if (durationMsec > _stopOnBGCFinalPauseOverMsec)
                {
                    TriggerStop();
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            _eventSource?.Dispose();
            return default;
        }
    }
}
