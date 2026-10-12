using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Rocket.Unturned.Player;
using SDG.Unturned;
using Steamworks;
using UnityEngine;
using KizShop.Common;

namespace KizShop.Shop.Gateways
{
    public class UnturnedVehicleGateway : IVehicleGateway
    {
        private readonly IMainThreadDispatcher _dispatcher;

        public UnturnedVehicleGateway(IMainThreadDispatcher dispatcher = null)
        {
            _dispatcher = dispatcher ?? KizShopPlugin.Instance?.Dispatcher ?? new RocketMainThreadDispatcher();
        }

        private void DebugAssertMainThread(string methodName)
        {
            if (_dispatcher != null && !_dispatcher.IsMainThread)
            {
                KizLog.Error($"[KizShop] THREAD ASSERTION ERROR: {methodName} called off main thread (Thread {Thread.CurrentThread.ManagedThreadId})!");
            }
        }

        public async Task<int> GetPlayerVehicleCountAsync(ulong steamId)
        {
            return await _dispatcher.RunOnMainThreadAsync(() =>
            {
                DebugAssertMainThread(nameof(GetPlayerVehicleCountAsync));
                var vehicles = VehicleManager.vehicles;
                if (vehicles == null) return 0;

                int count = 0;
                for (int i = 0; i < vehicles.Count; i++)
                {
                    var v = vehicles[i];
                    if (v != null && v.lockedOwner.m_SteamID == steamId)
                    {
                        count++;
                    }
                }
                return count;
            }).ConfigureAwait(false);
        }

        public async Task<VehicleSpawnResult> TrySpawnVehicleAsync(ulong steamId, ushort vehicleId, float desiredDistance)
        {
            return await _dispatcher.RunOnMainThreadAsync(() =>
            {
                DebugAssertMainThread(nameof(TrySpawnVehicleAsync));
                var player = UnturnedPlayer.FromCSteamID(new CSteamID(steamId));
                if (player == null || player.Player == null)
                {
                    return VehicleSpawnResult.Failed("Player is not in game.");
                }

                var forward = player.Player.transform.forward;
                var right = player.Player.transform.right;
                var basePos = player.Player.transform.position;
                var rotation = player.Player.transform.rotation;

                // Try 4 alternative offsets
                Vector3[] candidates = new Vector3[]
                {
                    basePos + (forward * desiredDistance),
                    basePos + (forward * (desiredDistance + 3.0f)),
                    basePos + (forward * desiredDistance) + (right * 3.5f),
                    basePos + (forward * desiredDistance) - (right * 3.5f)
                };

                Vector3? validSpawnPos = null;

                foreach (var candidate in candidates)
                {
                    // Ground raycast check
                    Vector3 rayOrigin = candidate + (Vector3.up * 10f);
                    if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, 20f, RayMasks.BLOCK_COLLISION))
                    {
                        Vector3 testPos = hit.point + (Vector3.up * 0.75f);

                        // Overlap sphere check for blocking obstacles/buildings
                        var colliders = Physics.OverlapSphere(testPos, 1.8f, RayMasks.BLOCK_COLLISION);
                        if (colliders == null || colliders.Length == 0)
                        {
                            validSpawnPos = testPos;
                            break;
                        }
                    }
                }

                if (!validSpawnPos.HasValue)
                {
                    return VehicleSpawnResult.Failed("No clear ground space found to spawn vehicle.");
                }

                try
                {
                    var vehicle = VehicleManager.spawnLockedVehicleForPlayerV2(vehicleId, validSpawnPos.Value, rotation, player.Player);
                    if (vehicle == null)
                    {
                        return VehicleSpawnResult.Failed("Game engine failed to instantiate vehicle.");
                    }
                    return VehicleSpawnResult.Succeeded(vehicle);
                }
                catch (Exception ex)
                {
                    return VehicleSpawnResult.Failed($"Vehicle spawn error: {ex.Message}");
                }
            }).ConfigureAwait(false);
        }

        public async Task DestroyVehicleAsync(object vehicleHandle)
        {
            await _dispatcher.RunOnMainThreadAsync(() =>
            {
                DebugAssertMainThread(nameof(DestroyVehicleAsync));
                if (vehicleHandle is InteractableVehicle vehicle && vehicle != null)
                {
                    VehicleManager.askVehicleDestroy(vehicle);
                }
            }).ConfigureAwait(false);
        }
    }
}
