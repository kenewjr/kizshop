using System;
using System.Threading.Tasks;
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
        Task<int> GetPlayerVehicleCountAsync(ulong steamId);
        Task<VehicleSpawnResult> TrySpawnVehicleAsync(ulong steamId, ushort vehicleId, float desiredDistance);
        Task DestroyVehicleAsync(object vehicleHandle);
    }
}
