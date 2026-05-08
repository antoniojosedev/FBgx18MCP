using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Artech.Architecture.Common.Objects;
using GxMcp.Worker.Helpers;

namespace GxMcp.Worker.Services
{
    public class KbWatcherService
    {
        // Shared gate: WriteService increments while a save transaction is in flight,
        // so the watcher can skip its tick instead of racing GetKeys against a live tx.
        // Both sides go through the SDK's KB.DesignModel.Objects collection; concurrent
        // access during writes was producing intermittent generic "Erro" messages.
        private static int _activeWriteCount = 0;

        public static IDisposable AcquireWriteGate()
        {
            Interlocked.Increment(ref _activeWriteCount);
            return new WriteGate();
        }

        private sealed class WriteGate : IDisposable
        {
            private int _disposed;
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                    Interlocked.Decrement(ref _activeWriteCount);
            }
        }

        public static bool IsWriteInProgress => Volatile.Read(ref _activeWriteCount) > 0;

        private readonly KbService _kbService;
        private DateTime _lastCheckTime;
        private bool _isRunning = false;
        private Thread _watcherThread;
        private readonly Action<string, string, DateTime> _onObjectChanged;
        private readonly HashSet<Guid> _notifiedInLastTick = new HashSet<Guid>();

        public KbWatcherService(KbService kbService, Action<string, string, DateTime> onObjectChanged)
        {
            _kbService = kbService;
            _onObjectChanged = onObjectChanged;
            _lastCheckTime = DateTime.UtcNow; // Changed to UtcNow because KBObject.LastUpdate uses UTC. This prevents an initial flood of notifications.
        }

        public void Start()
        {
            if (_isRunning) return;
            _isRunning = true;

            _watcherThread = new Thread(WatcherLoop)
            {
                IsBackground = true,
                Name = "KbWatcherThread",
                Priority = ThreadPriority.BelowNormal
            };
            _watcherThread.SetApartmentState(ApartmentState.STA);
            _watcherThread.Start();
            
            Logger.Info("KbWatcherService started.");
        }

        public void Stop()
        {
            _isRunning = false;
        }

        private void WatcherLoop()
        {
            // Initial delay to let the system settle
            Thread.Sleep(5000);

            while (_isRunning)
            {
                try
                {
                    if (IsWriteInProgress)
                    {
                        // Skip this tick: a save transaction is open; polling DesignModel.Objects
                        // mid-transaction races the SDK and surfaces as random "Erro" later.
                        Logger.Debug("KbWatcher: skipping tick (write in progress).");
                    }
                    else
                    {
                        var kb = _kbService.GetKB();
                        if (kb != null)
                        {
                            CheckForChanges(kb);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Logger.Debug($"KbWatcher Loop Error: {ex.Message}");
                }

                // Poll interval: 5 seconds (standard for metadata checks)
                Thread.Sleep(5000);
            }
        }

        private void CheckForChanges(dynamic kb)
        {
            try
            {
                // FAST PATH: Use GetKeys with DateTime to find modified objects since last check.
                var modifiedKeys = kb.DesignModel.Objects.GetKeys(_lastCheckTime);
                
                DateTime nextCheckTime = _lastCheckTime;
                bool foundNewer = false;
                var batch = new List<dynamic>();

                foreach (var key in (System.Collections.IEnumerable)modifiedKeys)
                {
                    try
                    {
                        var obj = kb.DesignModel.Objects.Get((Artech.Udm.Framework.EntityKey)key);
                        if (obj == null) continue;

                        if (obj.LastUpdate > _lastCheckTime)
                        {
                            if (obj.LastUpdate > nextCheckTime) 
                            {
                                nextCheckTime = obj.LastUpdate;
                                foundNewer = true;
                            }
                            batch.Add(obj);
                        }
                        else if (obj.LastUpdate == _lastCheckTime && !_notifiedInLastTick.Contains(obj.Guid))
                        {
                            batch.Add(obj);
                        }
                    }
                    catch { }
                }

                if (batch.Count > 0)
                {
                    if (foundNewer)
                    {
                        _notifiedInLastTick.Clear();
                    }

                    foreach (var obj in batch)
                    {
                        if (obj.LastUpdate == nextCheckTime)
                        {
                            _notifiedInLastTick.Add(obj.Guid);
                        }

                        Logger.Info($"External change detected: {obj.Name} ({obj.TypeDescriptor.Name}) at {obj.LastUpdate}");
                        _onObjectChanged?.Invoke(obj.Name, obj.TypeDescriptor.Name, obj.LastUpdate);
                    }

                    _lastCheckTime = nextCheckTime;
                }
            }
            catch (Exception ex)
            {
                if (!ex.Message.Contains("KB is busy"))
                {
                    Logger.Debug($"CheckForChanges error: {ex.Message}");
                }
            }
        }
    }
}
