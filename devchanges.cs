using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Il2CppSLZ.Bonelab;
using Il2CppSLZ.Marrow;
using Il2CppSLZ.Marrow.Data;
using Il2CppSLZ.Marrow.Pool;
using Il2CppSLZ.Marrow.Warehouse;
using MelonLoader;
using UnityEngine;

namespace VoidEngine
{
    public static class DevChanges
    {
        private const string DevManipulatorBarcode = "c1534c5a-c6a8-45d0-aaa2-2c954465764d";
        private const float IdleDespawnSeconds = 30f;
        private const float DespawnRetrySeconds = 5f;
        private const float FusionTimerProbeInterval = 0.5f;
        private const int MaxFusionTimerProbeAttempts = 20;
        private const float FusionOwnershipProbeInterval = 0.5f;
        private const int MaxFusionOwnershipProbeAttempts = 20;
        private const float DevToolSpawnScanSeconds = 10f;
        private const float DevToolSpawnScanInterval = 0.5f;

        private sealed class TrackedDevTool
        {
            public Poolee Poolee = null!;
            public InteractableHost? Host;
            public string ToolName = "Dev Tool";
            public float UnheldSince;
            public float NextDespawnAttempt;
            public float NextFusionTimerProbe;
            public float NextFusionOwnershipProbe;
            public int LastLoggedCountdown = -1;
            public int LastFusionDespawnResult = int.MinValue;
            public int FusionTimerProbeAttempts;
            public int FusionOwnershipProbeAttempts;
            public bool WasHeld;
            public bool DespawnAttemptLogged;
            public bool FusionOwnershipStatusLogged;
            public bool FusionOwnershipHandled;
            public bool FusionOwnershipRequestSent;
            public bool FusionTimerSuppressed;
            public bool FusionTimerProbeFinished;
        }

        private sealed class PendingDevTool
        {
            public Poolee Poolee = null!;
            public string ToolName = "Dev Tool";
            public float TimeoutAt;
        }

        private static readonly Dictionary<int, TrackedDevTool> TrackedTools = new();
        private static readonly Dictionary<int, PendingDevTool> PendingTools = new();
        private static readonly List<int> RemoveAfterUpdate = new();
        private static readonly List<int> RemovePendingAfterUpdate = new();
        private static readonly HashSet<string> DevToolCrateBarcodes = new(StringComparer.OrdinalIgnoreCase);
        private static bool _warningLogged;
        private static bool _spawnHookObserved;
        private static bool _diagnosticsEnabled;
        private static bool _voidworksEnabled;
        private static bool _autoDespawnEnabled = true;
        private static string? _spawnGunBarcode;
        private static string? _nimbusBarcode;
        private static float _devToolSpawnScanUntil;
        private static float _nextDevToolSpawnScan;
        private static bool _devToolSpawnScanActive;
        private static bool _devToolSpawnScanFound;
        private static CheatTool? _cheatTool;
        private static float _lastDevToolCallbackTime = -10f;

        public static void SetDiagnosticsEnabled(bool enabled)
        {
            _diagnosticsEnabled = enabled;
            if (enabled)
            {
                foreach (TrackedDevTool tracked in TrackedTools.Values)
                    tracked.LastLoggedCountdown = -1;
            }

            if (enabled && _spawnHookObserved)
                MelonLogger.Msg("[Void Engine] Dev Tools pooled-object tracking hook is active.");

            if (enabled)
            {
                MelonLogger.Msg(
                    $"[Void Engine] Dev Tools crate refs | Spawn Gun='{_spawnGunBarcode ?? "unavailable"}', " +
                    $"Nimbus='{_nimbusBarcode ?? "unavailable"}', " +
                    $"Dev Manipulator='{DevManipulatorBarcode}', " +
                    $"radial crate IDs cached={DevToolCrateBarcodes.Count}."
                );
            }
        }

        public static void SetAutoDespawnEnabled(bool enabled)
        {
            if (_autoDespawnEnabled == enabled)
                return;

            _autoDespawnEnabled = enabled;
            ResetTrackedToolTimers(ShouldAutoDespawnRun());

            MelonLogger.Msg(
                $"[Void Engine] Dev Tool auto-despawn {(enabled ? "enabled" : "disabled")}."
            );
        }

        public static void SetVoidworksEnabled(bool enabled)
        {
            if (_voidworksEnabled == enabled)
                return;

            _voidworksEnabled = enabled;
            ResetTrackedToolTimers(ShouldAutoDespawnRun());

            if (_diagnosticsEnabled)
            {
                MelonLogger.Msg(
                    $"[Void Engine] Dev Tool auto-despawn is " +
                    $"{(ShouldAutoDespawnRun() ? "active" : "inactive")} " +
                    $"(Voidworks {(enabled ? "ON" : "OFF")})."
                );
            }
        }

        private static bool ShouldAutoDespawnRun()
        {
            return _voidworksEnabled && _autoDespawnEnabled;
        }

        private static void ResetTrackedToolTimers(bool enableFusionOverride)
        {
            float now = Time.unscaledTime;
            foreach (TrackedDevTool tracked in TrackedTools.Values)
            {
                if (tracked.Poolee == null)
                    continue;

                if (tracked.Host == null)
                    tracked.Host = tracked.Poolee.GetComponentInChildren<InteractableHost>(true);

                tracked.UnheldSince = now;
                tracked.NextDespawnAttempt = 0f;
                tracked.LastLoggedCountdown = -1;
                tracked.WasHeld = tracked.Host != null && tracked.Host.IsAttached;
                if (enableFusionOverride)
                {
                    tracked.NextFusionOwnershipProbe = now;
                    tracked.FusionOwnershipProbeAttempts = 0;
                    tracked.FusionOwnershipHandled = false;
                    tracked.FusionOwnershipRequestSent = false;
                }

                if (enableFusionOverride)
                {
                    tracked.NextFusionTimerProbe = now;
                    tracked.FusionTimerProbeAttempts = 0;
                    tracked.FusionTimerSuppressed = false;
                    tracked.FusionTimerProbeFinished = false;
                }
                else
                {
                    RestoreFusionTimedDespawn(tracked);
                }
            }
        }

        internal static void TrackSpawnedPoolee(Poolee poolee, string? forcedToolName = null)
        {
            if (!_spawnHookObserved)
            {
                _spawnHookObserved = true;
                if (_diagnosticsEnabled)
                    MelonLogger.Msg("[Void Engine] Dev Tools pooled-object tracking hook is active.");
            }

            if (poolee == null)
                return;

            try
            {
                int id = poolee.GetInstanceID();
                if (!poolee.gameObject.activeInHierarchy)
                {
                    if (IsTrackableToolName(forcedToolName))
                    {
                        PendingTools[id] = new PendingDevTool
                        {
                            Poolee = poolee,
                            ToolName = forcedToolName!,
                            TimeoutAt = Time.unscaledTime + DevToolSpawnScanSeconds
                        };
                        _devToolSpawnScanFound = true;
                        if (_diagnosticsEnabled)
                        {
                            MelonLogger.Msg(
                                $"[Void Engine] {forcedToolName} spawn callback returned an inactive object; " +
                                $"waiting up to {DevToolSpawnScanSeconds:0} seconds for activation " +
                                $"(IsInPool={poolee.IsInPool})."
                            );
                        }
                    }

                    return;
                }

                PendingTools.Remove(id);
                PopUpMenuView? menu = PopUpMenuView.instance;
                string? spawnedBarcode = GetBarcode(poolee.SpawnableCrate);
                string? spawnGunBarcode = _spawnGunBarcode ?? (menu != null
                    ? GetBarcode(menu.crate_SpawnGun?.Crate as Crate)
                    : null);
                string? nimbusBarcode = _nimbusBarcode ?? (menu != null
                    ? GetBarcode(menu.crate_Nimbus?.Crate as Crate)
                    : null);

                string objectName = poolee.gameObject.name;
                bool hasBarcode = !string.IsNullOrWhiteSpace(spawnedBarcode);
                bool isSpawnGun = !string.IsNullOrEmpty(spawnGunBarcode) &&
                    string.Equals(spawnedBarcode, spawnGunBarcode, StringComparison.OrdinalIgnoreCase);
                bool isNimbus = !string.IsNullOrEmpty(nimbusBarcode) &&
                    string.Equals(spawnedBarcode, nimbusBarcode, StringComparison.OrdinalIgnoreCase);
                bool isDevManipulator = string.Equals(
                    spawnedBarcode,
                    DevManipulatorBarcode,
                    StringComparison.OrdinalIgnoreCase
                );

                if (!hasBarcode || (!isSpawnGun && !isNimbus && !isDevManipulator &&
                                    DevToolCrateBarcodes.Contains(spawnedBarcode!)))
                {
                    isSpawnGun = IsSpawnGunObjectName(objectName) &&
                        (string.IsNullOrEmpty(forcedToolName) || forcedToolName == "Spawn Gun");
                    isNimbus = IsNimbusObjectName(objectName) &&
                        (string.IsNullOrEmpty(forcedToolName) || forcedToolName == "Nimbus");
                    isDevManipulator = IsDevManipulatorObjectName(objectName) &&
                        (string.IsNullOrEmpty(forcedToolName) || forcedToolName == "Dev Manipulator");
                }

                bool isDevTool = isSpawnGun || isNimbus || isDevManipulator;
                if (isDevTool)
                    _devToolSpawnScanFound = true;

                if (!isDevTool)
                    return;

                if (TrackedTools.ContainsKey(id))
                    return;

                InteractableHost? host = poolee.GetComponentInChildren<InteractableHost>(true);
                bool isHeld = host != null && host.IsAttached;
                string toolName = GetDevToolName(isSpawnGun, isNimbus, isDevManipulator);
                TrackedTools[id] = new TrackedDevTool
                {
                    Poolee = poolee,
                    Host = host,
                    ToolName = toolName,
                    UnheldSince = Time.unscaledTime,
                    NextDespawnAttempt = 0f,
                    NextFusionTimerProbe = Time.unscaledTime,
                    NextFusionOwnershipProbe = Time.unscaledTime,
                    WasHeld = isHeld
                };
                if (_diagnosticsEnabled)
                {
                    MelonLogger.Msg(
                        $"[Void Engine] Tracking {toolName} (instance={id}) | " +
                        $"name='{objectName}', barcode='{spawnedBarcode ?? "unknown"}', " +
                        $"held={isHeld}, auto-despawn={_autoDespawnEnabled}."
                    );
                }
            }
            catch (Exception ex)
            {
                LogWarningOnce($"Could not track a Dev Tools item: {ex.Message}");
            }
        }

        internal static void RegisterDebugCheats(DebugCheats cheats)
        {
            if (cheats == null)
                return;

            try
            {
                RegisterDevToolCrates(
                    GetBarcode(cheats.crate_SpawnGun?.Crate as Crate),
                    GetBarcode(cheats.crate_Nimbus?.Crate as Crate),
                    "DebugCheats"
                );
            }
            catch (Exception ex)
            {
                LogWarningOnce($"Could not read BONELAB's DebugCheats crate references: {ex.Message}");
            }
        }

        internal static void RegisterRadialMenuCrates(PopUpMenuView menu)
        {
            if (menu == null)
                return;

            try
            {
                RegisterDevToolCrates(
                    GetBarcode(menu.crate_SpawnGun?.Crate as Crate),
                    GetBarcode(menu.crate_Nimbus?.Crate as Crate),
                    "PopUpMenuView"
                );
            }
            catch (Exception ex)
            {
                LogWarningOnce($"Could not read BONELAB's radial menu crate references: {ex.Message}");
            }
        }

        internal static void BeginDevToolSpawnScan()
        {
            float now = Time.unscaledTime;
            _devToolSpawnScanUntil = now + DevToolSpawnScanSeconds;
            _nextDevToolSpawnScan = now;
            _devToolSpawnScanActive = true;
            _devToolSpawnScanFound = false;

            if (_diagnosticsEnabled)
            {
                MelonLogger.Msg(
                    "[Void Engine] BONELAB Dev Tools radial callback fired; " +
                    $"scanning active items for {DevToolSpawnScanSeconds:0} seconds."
                );
            }
        }

        internal static void RegisterCheatToolCrates(CheatTool cheatTool)
        {
            if (cheatTool == null || cheatTool.crates == null)
                return;

            try
            {
                _cheatTool = cheatTool;
                var crates = cheatTool.crates;
                int count = Math.Min(crates.Length, 2);
                var captured = new List<string>(count);
                for (int i = 0; i < count; i++)
                {
                    string? barcode = crates[i]?.Barcode?.ID;
                    if (string.IsNullOrWhiteSpace(barcode))
                        continue;

                    DevToolCrateBarcodes.Add(barcode);
                    captured.Add(barcode);
                }

                if (_diagnosticsEnabled)
                {
                    MelonLogger.Msg(
                        $"[Void Engine] CheatTool spawn list captured | " +
                        $"total crates={crates.Length}, first Dev Tools IDs='{string.Join(", ", captured)}'."
                    );
                }
            }
            catch (Exception ex)
            {
                LogWarningOnce($"Could not read BONELAB's CheatTool spawn list: {ex.Message}");
            }
        }

        internal static bool SpawnDevTools(PopUpMenuView menu)
        {
            float now = Time.unscaledTime;
            if (now - _lastDevToolCallbackTime < 0.2f)
            {
                if (_diagnosticsEnabled)
                    MelonLogger.Msg("[Void Engine] Ignored duplicate Dev Tools callback within 0.2 seconds.");
                return true;
            }

            _lastDevToolCallbackTime = now;
            RegisterRadialMenuCrates(menu);
            Transform? spawnPoint = _cheatTool != null ? _cheatTool.spawnLocation : null;
            if (spawnPoint == null && menu.radialPageView != null)
                spawnPoint = menu.radialPageView.transform;

            if (spawnPoint == null ||
                string.IsNullOrWhiteSpace(_spawnGunBarcode) ||
                string.IsNullOrWhiteSpace(_nimbusBarcode))
            {
                LogWarningOnce(
                    "Void Engine could not resolve the Dev Tools spawn point or both native tool barcodes; " +
                    "falling back to BONELAB's original spawn callback."
                );
                return false;
            }

            BeginDevToolSpawnScan();
            SpawnDevTool(_spawnGunBarcode, "Spawn Gun", spawnPoint.position, spawnPoint.rotation);
            SpawnDevTool(_nimbusBarcode, "Nimbus", spawnPoint.position, spawnPoint.rotation);
            if (_voidworksEnabled)
            {
                SpawnDevTool(DevManipulatorBarcode, "Dev Manipulator", spawnPoint.position, spawnPoint.rotation);
            }
            else if (_diagnosticsEnabled)
            {
                MelonLogger.Msg("[Void Engine] Dev Manipulator spawn skipped because Voidworks is OFF.");
            }

            return true;
        }

        private static void SpawnDevTool(string? barcode, string toolName, Vector3 position, Quaternion rotation)
        {
            if (string.IsNullOrWhiteSpace(barcode))
            {
                LogWarningOnce($"Could not spawn {toolName}: BONELAB did not provide its crate barcode.");
                return;
            }

            if (TrySpawnThroughFusionBridge(barcode, toolName, position, rotation))
                return;

            try
            {
                Spawnable spawnable = new Spawnable
                {
                    crateRef = new SpawnableCrateReference(barcode)
                };
                AssetSpawner.Register(spawnable);

                var noVelocity = new Il2CppSystem.Nullable<Vector3>(Vector3.zero) { hasValue = false };
                var noSpawnId = new Il2CppSystem.Nullable<int>(0) { hasValue = false };
                var awaiter = AssetSpawner.SpawnAsync(
                    spawnable,
                    position,
                    rotation,
                    noVelocity,
                    null,
                    false,
                    noSpawnId,
                    null,
                    null,
                    null
                ).GetAwaiter();

                awaiter.OnCompleted((Il2CppSystem.Action)(Action)delegate
                {
                    try
                    {
                        Poolee spawned = awaiter.GetResult();
                        if (spawned == null)
                        {
                            LogWarningOnce($"BONELAB's spawner returned no Poolee for {toolName}.");
                            return;
                        }

                        bool trackForDespawn = IsTrackableToolName(toolName);
                        if (trackForDespawn)
                            TrackSpawnedPoolee(spawned, toolName);

                        if (_diagnosticsEnabled)
                        {
                            int spawnedId = spawned.GetInstanceID();
                            string trackingState = !trackForDespawn
                                ? "not tracked (despawn not implemented)"
                                : TrackedTools.ContainsKey(spawnedId)
                                ? "attached"
                                : PendingTools.ContainsKey(spawnedId)
                                    ? "waiting for activation"
                                    : "not attached";
                            MelonLogger.Msg(
                                $"[Void Engine] {toolName} spawn completed; active=" +
                                $"{spawned.gameObject.activeInHierarchy}, IsInPool={spawned.IsInPool}, " +
                                $"tracking={trackingState}."
                            );
                        }
                    }
                    catch (Exception ex)
                    {
                        LogWarningOnce($"Could not finish spawning {toolName}: {ex.GetBaseException().Message}");
                    }
                });

                if (_diagnosticsEnabled)
                    MelonLogger.Msg($"[Void Engine] Requested BONELAB spawn for {toolName} ({barcode}).");
            }
            catch (Exception ex)
            {
                LogWarningOnce($"Could not request a local spawn for {toolName}: {ex.GetBaseException().Message}");
            }
        }

        private static bool TrySpawnThroughFusionBridge(
            string barcode,
            string toolName,
            Vector3 position,
            Quaternion rotation
        )
        {
            Assembly? fusionAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, "LabFusion", StringComparison.Ordinal));
            if (fusionAssembly == null)
                return false;

            try
            {
                Type? bridge = typeof(DevChanges).Assembly.GetType("VoidEngine.FusionDevToolSpawnBridge");
                MethodInfo? spawn = bridge?.GetMethod("TrySpawn", BindingFlags.Public | BindingFlags.Static);
                if (spawn == null)
                {
                    LogWarningOnce("LabFusion is loaded, but the Dev Tools network spawn bridge is unavailable.");
                    return true;
                }

                return (bool)(spawn.Invoke(null, new object[] { barcode, toolName, position, rotation }) ?? false);
            }
            catch (Exception ex)
            {
                LogWarningOnce($"Fusion Dev Tools spawn request failed: {ex.GetBaseException().Message}");
                return true;
            }
        }

        private static int TryDespawnThroughFusionBridge(Poolee poolee)
        {
            Assembly? fusionAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, "LabFusion", StringComparison.Ordinal));
            if (fusionAssembly == null)
                return -1;

            try
            {
                Type? bridge = typeof(DevChanges).Assembly.GetType("VoidEngine.FusionDevToolSpawnBridge");
                MethodInfo? despawn = bridge?.GetMethod("TryDespawn", BindingFlags.Public | BindingFlags.Static);
                if (despawn == null)
                {
                    LogWarningOnce("Fusion is loaded, but the safe network despawn hook is unavailable; local despawn was skipped.");
                    return 0;
                }

                return (int)(despawn.Invoke(null, new object[] { poolee }) ?? 0);
            }
            catch (Exception ex)
            {
                LogWarningOnce($"Fusion Dev Tools despawn request failed: {ex.GetBaseException().Message}");
                return 0;
            }
        }

        private static void RegisterDevToolCrates(string? spawnGunBarcode, string? nimbusBarcode, string source)
        {
            if (!string.IsNullOrWhiteSpace(spawnGunBarcode))
            {
                _spawnGunBarcode = spawnGunBarcode;
                DevToolCrateBarcodes.Add(spawnGunBarcode);
            }

            if (!string.IsNullOrWhiteSpace(nimbusBarcode))
            {
                _nimbusBarcode = nimbusBarcode;
                DevToolCrateBarcodes.Add(nimbusBarcode);
            }

            if (_diagnosticsEnabled)
            {
                MelonLogger.Msg(
                    $"[Void Engine] Registered {source} Dev Tools | " +
                    $"Spawn Gun='{spawnGunBarcode ?? "unavailable"}', " +
                    $"Nimbus='{nimbusBarcode ?? "unavailable"}', " +
                    $"Dev Manipulator='{DevManipulatorBarcode}'."
                );
            }

            DevToolCrateBarcodes.Add(DevManipulatorBarcode);
        }

        internal static void TrackSpawnGun(SpawnGun spawnGun)
        {
            if (spawnGun == null)
                return;

            try
            {
                Poolee? poolee = spawnGun.GetComponentInParent<Poolee>();
                if (poolee == null)
                    poolee = spawnGun.GetComponentInChildren<Poolee>(true);

                if (poolee == null)
                {
                    if (_diagnosticsEnabled)
                    {
                        MelonLogger.Msg(
                            $"[Void Engine] Spawn Gun component enabled without a nearby Poolee " +
                            $"| object='{spawnGun.gameObject.name}'."
                        );
                    }

                    return;
                }

                TrackSpawnedPoolee(poolee);
            }
            catch (Exception ex)
            {
                LogWarningOnce($"Could not track the Spawn Gun component: {ex.Message}");
            }
        }

        public static void Update()
        {
            float now = Time.unscaledTime;
            UpdatePendingTools(now);
            ScanForNewDevTools(now);

            if (TrackedTools.Count == 0)
                return;

            RemoveAfterUpdate.Clear();

            foreach (KeyValuePair<int, TrackedDevTool> pair in TrackedTools)
            {
                TrackedDevTool tracked = pair.Value;
                try
                {
                    if (tracked.Poolee == null || !tracked.Poolee.gameObject.activeInHierarchy)
                    {
                        RemoveAfterUpdate.Add(pair.Key);
                        continue;
                    }

                    if (ShouldAutoDespawnRun())
                        TryClaimFusionOwnership(tracked, pair.Key, now);

                    if (!ShouldAutoDespawnRun())
                        continue;

                    SuppressFusionTimerIfNeeded(tracked, now);

                    if (tracked.Host == null)
                        tracked.Host = tracked.Poolee.GetComponentInChildren<InteractableHost>(true);

                    bool isHeld = tracked.Host != null && tracked.Host.IsAttached;
                    if (isHeld)
                    {
                        if (!tracked.WasHeld && _diagnosticsEnabled)
                        {
                            MelonLogger.Msg(
                                $"[Void Engine] {tracked.ToolName} (instance={pair.Key}) grabbed; " +
                                "idle despawn timer paused."
                            );
                        }

                        tracked.WasHeld = true;
                        tracked.UnheldSince = now;
                        tracked.NextDespawnAttempt = 0f;
                        tracked.LastLoggedCountdown = -1;
                        continue;
                    }

                    if (tracked.WasHeld)
                    {
                        tracked.WasHeld = false;
                        tracked.UnheldSince = now;
                        tracked.NextDespawnAttempt = 0f;
                        tracked.LastLoggedCountdown = -1;

                        if (_diagnosticsEnabled)
                        {
                            MelonLogger.Msg(
                                $"[Void Engine] {tracked.ToolName} (instance={pair.Key}) released; " +
                                "30-second idle despawn timer restarted."
                            );
                        }
                    }

                    float secondsRemaining = Math.Max(
                        0f,
                        IdleDespawnSeconds - (now - tracked.UnheldSince)
                    );
                    int countdown = (int)Math.Ceiling(secondsRemaining);
                    if (_diagnosticsEnabled && secondsRemaining > 0f &&
                        IsCountdownMilestone(countdown) && countdown != tracked.LastLoggedCountdown)
                    {
                        MelonLogger.Msg(
                            $"[Void Engine] {tracked.ToolName} (instance={pair.Key}) despawn countdown: " +
                            $"{countdown} second{(countdown == 1 ? "" : "s")} remaining."
                        );
                        tracked.LastLoggedCountdown = countdown;
                    }

                    if (secondsRemaining > 0f ||
                        now < tracked.NextDespawnAttempt)
                    {
                        continue;
                    }

                    tracked.NextDespawnAttempt = now + DespawnRetrySeconds;
                    if (_diagnosticsEnabled && !tracked.DespawnAttemptLogged)
                    {
                        tracked.DespawnAttemptLogged = true;
                        MelonLogger.Msg(
                            $"[Void Engine] {tracked.ToolName} (instance={pair.Key}) idle for 30 seconds; " +
                            "requesting cleanup."
                        );
                    }
                    int fusionDespawnResult = TryDespawnThroughFusionBridge(tracked.Poolee);
                    LogFusionDespawnResult(tracked, pair.Key, fusionDespawnResult);
                    if (fusionDespawnResult < 0)
                        tracked.Poolee.Despawn();
                }
                catch (Exception ex)
                {
                    LogWarningOnce($"Dev Tools auto-despawn failed: {ex.Message}");
                }
            }

            foreach (int id in RemoveAfterUpdate)
                TrackedTools.Remove(id);
        }

        private static void UpdatePendingTools(float now)
        {
            if (PendingTools.Count == 0)
                return;

            RemovePendingAfterUpdate.Clear();
            foreach (KeyValuePair<int, PendingDevTool> pair in PendingTools)
            {
                PendingDevTool pending = pair.Value;
                try
                {
                    if (pending.Poolee == null)
                    {
                        RemovePendingAfterUpdate.Add(pair.Key);
                        continue;
                    }

                    if (pending.Poolee.gameObject.activeInHierarchy)
                    {
                        RemovePendingAfterUpdate.Add(pair.Key);
                        TrackSpawnedPoolee(pending.Poolee, pending.ToolName);
                    }
                    else if (now >= pending.TimeoutAt)
                    {
                        RemovePendingAfterUpdate.Add(pair.Key);
                        if (_diagnosticsEnabled)
                        {
                            MelonLogger.Warning(
                                $"[Void Engine] {pending.ToolName} spawn result remained inactive; " +
                                "auto-despawn tracking could not start."
                            );
                        }
                    }
                }
                catch (Exception ex)
                {
                    RemovePendingAfterUpdate.Add(pair.Key);
                    LogWarningOnce($"Could not wait for {pending.ToolName} activation: {ex.GetBaseException().Message}");
                }
            }

            foreach (int id in RemovePendingAfterUpdate)
                PendingTools.Remove(id);
        }

        private static void ScanForNewDevTools(float now)
        {
            if (!_devToolSpawnScanActive)
                return;

            if (now >= _devToolSpawnScanUntil)
            {
                _devToolSpawnScanActive = false;
                if (_diagnosticsEnabled && !_devToolSpawnScanFound)
                {
                    MelonLogger.Warning(
                        "[Void Engine] Dev Tools callback fired, but no matching active " +
                        "Spawn Gun, Nimbus, or Dev Manipulator Poolee was found during the scan."
                    );
                }

                return;
            }

            if (now < _nextDevToolSpawnScan)
                return;

            _nextDevToolSpawnScan = now + DevToolSpawnScanInterval;
            try
            {
                Poolee[] poolees = UnityEngine.Object.FindObjectsOfType<Poolee>();
                foreach (Poolee poolee in poolees)
                {
                    if (poolee == null || !poolee.gameObject.activeInHierarchy)
                        continue;

                    string? barcode = GetBarcode(poolee.SpawnableCrate);
                    string objectName = poolee.gameObject.name;
                    bool knownCrate = !string.IsNullOrWhiteSpace(barcode) &&
                                      DevToolCrateBarcodes.Contains(barcode);
                    bool fallbackName = string.IsNullOrWhiteSpace(barcode) &&
                                        (IsSpawnGunObjectName(objectName) || IsNimbusObjectName(objectName) ||
                                         IsDevManipulatorObjectName(objectName));

                    if (!knownCrate && !fallbackName)
                        continue;

                    _devToolSpawnScanFound = true;
                    TrackSpawnedPoolee(poolee);
                }
            }
            catch (Exception ex)
            {
                LogWarningOnce($"Dev Tools post-spawn scan failed: {ex.Message}");
            }
        }

        private static void LogFusionDespawnResult(TrackedDevTool tracked, int instanceId, int result)
        {
            if (!_diagnosticsEnabled || tracked.LastFusionDespawnResult == result)
                return;

            tracked.LastFusionDespawnResult = result;
            string status = result switch
            {
                -1 => "not in a networked Fusion level; using local despawn",
                0 => "waiting for Fusion to register the tool's network entity",
                1 => "network despawn request sent",
                2 => "requesting ownership before network despawn",
                3 => "Fusion has locked this entity's ownership; despawn cannot proceed",
                _ => $"unexpected Fusion despawn result {result}"
            };

            MelonLogger.Msg(
                $"[Void Engine] Fusion despawn for {tracked.ToolName} (instance={instanceId}): {status}."
            );
        }

        internal static void LogFusionOwnershipStatus(Poolee poolee, string status)
        {
            if (!_diagnosticsEnabled || poolee == null)
                return;

            try
            {
                int id = poolee.GetInstanceID();
                if (TrackedTools.TryGetValue(id, out TrackedDevTool? tracked))
                {
                    if (tracked.FusionOwnershipStatusLogged)
                        return;

                    tracked.FusionOwnershipStatusLogged = true;
                    MelonLogger.Msg(
                        $"[Void Engine] Fusion ownership for {tracked.ToolName} (instance={id}): {status}"
                    );
                }
            }
            catch (Exception ex)
            {
                LogWarningOnce($"Could not log Fusion ownership status: {ex.GetBaseException().Message}");
            }
        }

        private static void TryClaimFusionOwnership(TrackedDevTool tracked, int instanceId, float now)
        {
            if (tracked.FusionOwnershipHandled || now < tracked.NextFusionOwnershipProbe)
                return;

            tracked.NextFusionOwnershipProbe = now + FusionOwnershipProbeInterval;
            tracked.FusionOwnershipProbeAttempts++;

            int result = tracked.FusionOwnershipRequestSent
                ? GetFusionOwnershipStatus(tracked.Poolee)
                : TryRequestFusionOwnership(tracked.Poolee);
            if (result < 0)
            {
                tracked.FusionOwnershipHandled = true;
                return;
            }

            if (result == 1)
            {
                if (!tracked.FusionOwnershipRequestSent)
                {
                    tracked.FusionOwnershipRequestSent = true;
                    LogFusionOwnershipStatus(
                        tracked.Poolee,
                        "sent an ownership-transfer request; checking until Fusion confirms local ownership"
                    );
                }
                return;
            }

            if (result == 2)
            {
                tracked.FusionOwnershipHandled = true;
                LogFusionOwnershipStatus(tracked.Poolee, "the local player already owns the registered dev tool");
                return;
            }

            if (result == 3)
            {
                tracked.FusionOwnershipHandled = true;
                LogFusionOwnershipStatus(tracked.Poolee, "Fusion has locked ownership for this entity");
                return;
            }

            if (tracked.FusionOwnershipProbeAttempts >= MaxFusionOwnershipProbeAttempts)
            {
                tracked.FusionOwnershipHandled = true;
                if (_diagnosticsEnabled)
                {
                    MelonLogger.Warning(
                        $"[Void Engine] Could not claim Fusion ownership of {tracked.ToolName} " +
                        $"(instance={instanceId}) after {MaxFusionOwnershipProbeAttempts} checks; " +
                        (tracked.FusionOwnershipRequestSent
                            ? "Fusion did not confirm local ownership."
                            : "its network entity never became registered.")
                    );
                }
            }
        }

        private static int GetFusionOwnershipStatus(Poolee poolee)
        {
            try
            {
                Type? bridge = typeof(DevChanges).Assembly.GetType("VoidEngine.FusionDevToolSpawnBridge");
                MethodInfo? status = bridge?.GetMethod(
                    "GetOwnershipStatus",
                    BindingFlags.Public | BindingFlags.Static
                );
                if (status == null)
                    return 0;

                return (int)(status.Invoke(null, new object[] { poolee }) ?? 0);
            }
            catch (Exception ex)
            {
                LogWarningOnce($"Could not check Fusion ownership: {ex.GetBaseException().Message}");
                return 0;
            }
        }

        private static int TryRequestFusionOwnership(Poolee poolee)
        {
            Assembly? fusionAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, "LabFusion", StringComparison.Ordinal));
            if (fusionAssembly == null)
                return -1;

            try
            {
                Type? bridge = typeof(DevChanges).Assembly.GetType("VoidEngine.FusionDevToolSpawnBridge");
                MethodInfo? request = bridge?.GetMethod(
                    "TryTakeOwnership",
                    BindingFlags.Public | BindingFlags.Static
                );
                if (request == null)
                {
                    LogWarningOnce("LabFusion is loaded, but the dev-tool ownership hook is unavailable.");
                    return -1;
                }

                return (int)(request.Invoke(null, new object[] { poolee }) ?? -1);
            }
            catch (Exception ex)
            {
                LogWarningOnce($"Could not request Fusion dev-tool ownership: {ex.GetBaseException().Message}");
                return -1;
            }
        }

        private static bool IsCountdownMilestone(int secondsRemaining)
        {
            return secondsRemaining == 30 || secondsRemaining == 20 || secondsRemaining == 10 ||
                   secondsRemaining == 5 || secondsRemaining == 1;
        }

        private static bool IsSpawnGunObjectName(string objectName)
        {
            return HasWholeNamePrefix(objectName, "SPAWN_GUN") ||
                   HasWholeNamePrefix(objectName, "Spawn Gun") ||
                   HasWholeNamePrefix(objectName, "SpawnGun");
        }

        private static bool IsNimbusObjectName(string objectName)
        {
            return HasWholeNamePrefix(objectName, "NIMBUS_GUN") ||
                   HasWholeNamePrefix(objectName, "Nimbus Gun") ||
                   HasWholeNamePrefix(objectName, "NimbusGun") ||
                   HasWholeNamePrefix(objectName, "Nimbus");
        }

        private static bool IsDevManipulatorObjectName(string objectName)
        {
            return HasWholeNamePrefix(objectName, "DEV_MANIPULATOR") ||
                   HasWholeNamePrefix(objectName, "Dev Manipulator") ||
                   HasWholeNamePrefix(objectName, "DevManipulator");
        }

        private static bool IsTrackableToolName(string? toolName)
        {
            return toolName == "Spawn Gun" || toolName == "Nimbus" || toolName == "Dev Manipulator";
        }

        private static string GetDevToolName(bool isSpawnGun, bool isNimbus, bool isDevManipulator)
        {
            if (isSpawnGun)
                return "Spawn Gun";
            if (isNimbus)
                return "Nimbus";
            return isDevManipulator ? "Dev Manipulator" : "Dev Tool";
        }

        private static bool HasWholeNamePrefix(string value, string prefix)
        {
            if (string.IsNullOrEmpty(value) ||
                !value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return false;

            return value.Length == prefix.Length ||
                   char.IsWhiteSpace(value[prefix.Length]) ||
                   value[prefix.Length] == '[' ||
                   value[prefix.Length] == '(' ||
                   value[prefix.Length] == '_';
        }

        private static void SuppressFusionTimerIfNeeded(TrackedDevTool tracked, float now)
        {
            if (tracked.FusionTimerSuppressed || tracked.FusionTimerProbeFinished ||
                now < tracked.NextFusionTimerProbe)
                return;

            tracked.NextFusionTimerProbe = now + FusionTimerProbeInterval;
            tracked.FusionTimerProbeAttempts++;

            int hookedCount = TryBlockFusionTimedDespawn(tracked.Poolee);
            if (hookedCount < 0)
            {
                tracked.FusionTimerProbeFinished = true;
                return;
            }

            if (hookedCount > 0)
            {
                tracked.FusionTimerSuppressed = true;
                tracked.FusionTimerProbeFinished = true;
                if (_diagnosticsEnabled)
                {
                    MelonLogger.Msg(
                        $"[Void Engine] Fusion timed cleanup blocked for {tracked.ToolName} " +
                        $"({hookedCount} component{(hookedCount == 1 ? "" : "s")}); " +
                        "Void Engine's idle timer now controls its lifetime."
                    );
                }

                return;
            }

            if (tracked.FusionTimerProbeAttempts >= MaxFusionTimerProbeAttempts)
            {
                tracked.FusionTimerProbeFinished = true;
                if (_diagnosticsEnabled)
                {
                    MelonLogger.Warning(
                        $"[Void Engine] No Fusion timed cleanup component was found on {tracked.ToolName}; " +
                        "Fusion may still apply its own multiplayer timeout."
                    );
                }
            }
        }

        private static int TryBlockFusionTimedDespawn(Poolee poolee)
        {
            Assembly? fusionAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, "LabFusion", StringComparison.Ordinal));
            if (fusionAssembly == null)
                return -1;

            try
            {
                Type? bridge = typeof(DevChanges).Assembly.GetType("VoidEngine.FusionDevToolSpawnBridge");
                MethodInfo? block = bridge?.GetMethod(
                    "BlockTimedDespawn",
                    BindingFlags.Public | BindingFlags.Static
                );
                if (block == null)
                {
                    LogWarningOnce("LabFusion is loaded, but its timed-cleanup hook is unavailable.");
                    return -1;
                }

                return (int)(block.Invoke(null, new object[] { poolee }) ?? -1);
            }
            catch (Exception ex)
            {
                LogWarningOnce($"Could not hook Fusion's timed cleanup: {ex.GetBaseException().Message}");
                return -1;
            }
        }

        private static void RestoreFusionTimedDespawn(TrackedDevTool tracked)
        {
            try
            {
                TryUnblockFusionTimedDespawn(tracked.Poolee);
            }
            finally
            {
                tracked.FusionTimerSuppressed = false;
                tracked.FusionTimerProbeFinished = false;
                tracked.FusionTimerProbeAttempts = 0;
                tracked.NextFusionTimerProbe = Time.unscaledTime;
            }
        }

        private static int TryUnblockFusionTimedDespawn(Poolee poolee)
        {
            Assembly? fusionAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(assembly => string.Equals(assembly.GetName().Name, "LabFusion", StringComparison.Ordinal));
            if (fusionAssembly == null)
                return -1;

            try
            {
                Type? bridge = typeof(DevChanges).Assembly.GetType("VoidEngine.FusionDevToolSpawnBridge");
                MethodInfo? unblock = bridge?.GetMethod(
                    "UnblockTimedDespawn",
                    BindingFlags.Public | BindingFlags.Static
                );
                if (unblock == null)
                {
                    LogWarningOnce("LabFusion is loaded, but its timed-cleanup restore hook is unavailable.");
                    return -1;
                }

                return (int)(unblock.Invoke(null, new object[] { poolee }) ?? -1);
            }
            catch (Exception ex)
            {
                LogWarningOnce($"Could not restore Fusion's timed cleanup: {ex.GetBaseException().Message}");
                return -1;
            }
        }

        private static string? GetBarcode(Crate? crate)
        {
            return crate != null && crate.Barcode != null
                ? crate.Barcode.ID
                : null;
        }

        private static void LogWarningOnce(string message)
        {
            if (_warningLogged)
                return;

            _warningLogged = true;
            MelonLogger.Warning($"[Void Engine] {message}");
        }
    }

    [HarmonyPatch(typeof(Poolee), nameof(Poolee.OnSpawnEvent))]
    internal static class DevToolsPooleeSpawnPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Poolee __instance)
        {
            DevChanges.TrackSpawnedPoolee(__instance);
        }
    }

    [HarmonyPatch(typeof(Poolee), nameof(Poolee.OnSpawn))]
    internal static class DevToolsPooleeOnSpawnPatch
    {
        [HarmonyPostfix]
        private static void Postfix(Poolee __instance)
        {
            DevChanges.TrackSpawnedPoolee(__instance);
        }
    }

    [HarmonyPatch(typeof(Poolee), nameof(Poolee.OnEnable))]
    internal static class DevToolsPooleeEnablePatch
    {
        [HarmonyPostfix]
        private static void Postfix(Poolee __instance)
        {
            DevChanges.TrackSpawnedPoolee(__instance);
        }
    }

    [HarmonyPatch(typeof(SpawnGun), nameof(SpawnGun.OnEnable))]
    internal static class DevToolsSpawnGunEnablePatch
    {
        [HarmonyPostfix]
        private static void Postfix(SpawnGun __instance)
        {
            DevChanges.TrackSpawnGun(__instance);
        }
    }

    [HarmonyPatch(typeof(DebugCheats), nameof(DebugCheats.Start))]
    internal static class DevToolsDebugCheatsPatch
    {
        [HarmonyPostfix]
        private static void Postfix(DebugCheats __instance)
        {
            DevChanges.RegisterDebugCheats(__instance);
        }
    }

    [HarmonyPatch(typeof(PopUpMenuView), nameof(PopUpMenuView.AddDevMenu))]
    internal static class DevToolsRadialMenuPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        private static void Prefix(PopUpMenuView __instance, ref Il2CppSystem.Action spawnDelegate)
        {
            DevChanges.RegisterRadialMenuCrates(__instance);
            Il2CppSystem.Action originalSpawnDelegate = spawnDelegate;

            spawnDelegate = (Il2CppSystem.Action)(Action)delegate
            {
                if (!DevChanges.SpawnDevTools(__instance))
                    originalSpawnDelegate?.Invoke();
            };
        }
    }

    [HarmonyPatch(typeof(CheatTool), nameof(CheatTool.Start))]
    internal static class DevToolsCheatToolPatch
    {
        [HarmonyPostfix]
        private static void Postfix(CheatTool __instance)
        {
            DevChanges.RegisterCheatToolCrates(__instance);
        }
    }
}
