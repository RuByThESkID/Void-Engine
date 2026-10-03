using Il2CppSLZ.Marrow.Data;
using Il2CppSLZ.Marrow.Pool;
using Il2CppSLZ.Marrow.SceneStreaming;
using Il2CppSLZ.Marrow.Warehouse;
using LabFusion.Entities;
using LabFusion.Marrow.Extenders;
using LabFusion.Marrow.Integration;
using LabFusion.MonoBehaviours;
using LabFusion.Network;
using LabFusion.Player;
using LabFusion.RPC;
using LabFusion.Scene;
using LabFusion.Senders;
using LabFusion.Utilities;
using MelonLoader;
using UnityEngine;

namespace VoidEngine
{
    internal static class FusionDevToolSpawnBridge
    {
        private static bool _desktopStatsHookInstalled;

        public static bool InstallDesktopStatsHook()
        {
            if (_desktopStatsHookInstalled)
                return true;

            MultiplayerHooking.OnPlayerAction -= OnPlayerAction;
            MultiplayerHooking.OnPlayerAction += OnPlayerAction;
            _desktopStatsHookInstalled = true;
            return true;
        }

        private static void OnPlayerAction(PlayerID player, PlayerActionType action, PlayerID source)
        {
            int victimId = player == null ? -1 : player.SmallID;
            if (victimId < 0)
                return;

            bool localPlayerIsKiller = source != null && source.IsMe;
            DesktopStatsOverlay.RecordFusionPlayerAction(victimId, action.ToString(), localPlayerIsKiller);
        }

        public static int TryTakeOwnership(Poolee poolee)
        {
            if (poolee == null || !NetworkSceneManager.IsLevelNetworked)
                return -1;

            if (!TryGetRegisteredNetworkEntity(poolee, out NetworkEntity entity))
                return 0;

            if (entity.IsOwner)
                return 2;

            if (entity.IsOwnerLocked)
                return 3;

            NetworkEntityManager.TakeOwnership(entity);
            return 1;
        }

        public static int GetOwnershipStatus(Poolee poolee)
        {
            if (poolee == null || !NetworkSceneManager.IsLevelNetworked)
                return -1;

            if (!TryGetRegisteredNetworkEntity(poolee, out NetworkEntity entity))
                return 0;
            if (entity.IsOwner)
                return 2;
            return entity.IsOwnerLocked ? 3 : 1;
        }

        public static bool TryLoadLevel(string barcode, string levelName)
        {
            if (!NetworkInfo.HasServer)
                return false;

            if (!NetworkInfo.IsHost)
            {
                MelonLogger.Warning(
                    $"[Void Engine] Map Search: '{levelName}' can only be loaded by the Fusion host."
                );
                return true;
            }

            SceneStreamer.Load(new Barcode(barcode), null);
            MelonLogger.Msg($"[Void Engine] Fusion host loading map: {levelName} ({barcode}).");
            return true;
        }

        public static bool TrySpawn(string barcode, string toolName, Vector3 position, Quaternion rotation)
        {
            if (!NetworkSceneManager.IsLevelNetworked)
                return false;

            Spawnable spawnable = new Spawnable
            {
                crateRef = new SpawnableCrateReference(barcode)
            };

            NetworkAssetSpawner.SpawnRequestInfo request = default;
            request.Spawnable = spawnable;
            request.Position = position;
            request.Rotation = rotation;
            request.SpawnEffect = true;
            request.SpawnSource = (EntitySource)2;
            request.SpawnCallback = delegate(NetworkAssetSpawner.SpawnCallbackInfo info)
            {
                if (info.Spawned == null)
                    return;

                Poolee? poolee = info.Spawned.GetComponentInChildren<Poolee>(true);
                if (poolee != null &&
                    (toolName == "Spawn Gun" || toolName == "Nimbus" || toolName == "Dev Manipulator"))
                    DevChanges.TrackSpawnedPoolee(poolee, toolName);
            };

            NetworkAssetSpawner.Spawn(request);
            return true;
        }

        public static int TryDespawn(Poolee poolee)
        {
            if (poolee == null)
                return 1;

            if (!NetworkSceneManager.IsLevelNetworked)
                return -1;

            if (!TryGetRegisteredNetworkEntity(poolee, out NetworkEntity entity))
                return 0;

            if (!NetworkSceneManager.IsLevelHost && !entity.IsOwner)
            {
                if (entity.IsOwnerLocked)
                    return 3;

                NetworkEntityManager.TakeOwnership(entity);
                return 2;
            }

            NetworkAssetSpawner.Despawn(new NetworkAssetSpawner.DespawnRequestInfo
            {
                EntityID = entity.ID,
                DespawnEffect = false
            });

            return 1;
        }

        private static bool TryGetRegisteredNetworkEntity(Poolee poolee, out NetworkEntity entity)
        {
            entity = null!;
            return poolee != null &&
                   PooleeExtender.Cache.TryGet(poolee, out entity) &&
                   entity != null &&
                   entity.IsRegistered;
        }

        public static int BlockTimedDespawn(Poolee poolee)
        {
            if (!NetworkSceneManager.IsLevelNetworked || poolee == null)
                return -1;

            TimedDespawner[] timers = poolee.GetComponentsInChildren<TimedDespawner>(true);
            int hooked = 0;
            foreach (TimedDespawner timer in timers)
            {
                if (timer == null)
                    continue;

                timer.OnDespawnCheck -= PreventFusionTimedDespawn;
                timer.OnDespawnCheck += PreventFusionTimedDespawn;
                timer.RefreshTimer();
                hooked++;
            }

            return hooked;
        }

        public static int UnblockTimedDespawn(Poolee poolee)
        {
            if (!NetworkSceneManager.IsLevelNetworked || poolee == null)
                return -1;

            TimedDespawner[] timers = poolee.GetComponentsInChildren<TimedDespawner>(true);
            int unhooked = 0;
            foreach (TimedDespawner timer in timers)
            {
                if (timer == null)
                    continue;

                timer.OnDespawnCheck -= PreventFusionTimedDespawn;
                timer.RefreshTimer();
                unhooked++;
            }

            return unhooked;
        }

        private static bool PreventFusionTimedDespawn()
        {
            return false;
        }
    }
}
