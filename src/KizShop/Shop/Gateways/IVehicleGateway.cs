using System;
using SDG.Unturned;

namespace KizShop.Shop.Gateways
{
    public class VehicleSpawnResult
    {
        public bool Success { get; }
        public object VehicleHandle { get; }
        public string ErrorMessage { get; }

        public VehicleSpawnResult(bool success, object vehicleHandle, string errorMessage = null)
        {
            Success = success;
            VehicleHandle = vehicleHandle;
            ErrorMessage = errorMessage;
        }

        public static VehicleSpawnResult Failed(string error) => new VehicleSpawnResult(false, null, error);
        public static VehicleSpawnResult Succeeded(object handle) => new VehicleSpawnResult(true, handle);
    }

    public interface IVehicleGateway
    {
        int GetPlayerVehicleCount(ulong steamId);
        VehicleSpawnResult TrySpawnVehicle(ulong steamId, ushort vehicleId, float desiredDistance);
        void DestroyVehicle(object vehicleHandle);
    }
}
