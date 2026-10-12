using System.Collections.Generic;
using System.Threading.Tasks;
using KizShop.UI;

namespace KizShop.Tests.Mocks
{
    public class MockShopUiGateway : IShopUiGateway
    {
        public List<(ulong steamId, ushort effectId, short key)> OpenCalls { get; } = new List<(ulong, ushort, short)>();
        public List<(ulong steamId, ushort effectId, short key)> CloseCalls { get; } = new List<(ulong, ushort, short)>();
        public Dictionary<string, string> TextUpdates { get; } = new Dictionary<string, string>();
        public Dictionary<string, bool> VisibilityUpdates { get; } = new Dictionary<string, bool>();

        public bool OpenUiSuccess { get; set; } = true;

        public Task<bool> OpenUiAsync(ulong steamId, ushort effectId, short key)
        {
            OpenCalls.Add((steamId, effectId, key));
            return Task.FromResult(OpenUiSuccess);
        }

        public Task CloseUiAsync(ulong steamId, ushort effectId, short key)
        {
            CloseCalls.Add((steamId, effectId, key));
            return Task.CompletedTask;
        }

        public Task UpdateTextAsync(ulong steamId, short key, string elementName, string text)
        {
            TextUpdates[elementName] = text;
            return Task.CompletedTask;
        }

        public Task UpdateVisibilityAsync(ulong steamId, short key, string elementName, bool visible)
        {
            VisibilityUpdates[elementName] = visible;
            return Task.CompletedTask;
        }
    }
}
