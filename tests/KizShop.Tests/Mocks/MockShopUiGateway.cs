using System.Collections.Generic;
using KizShop.UI;

namespace KizShop.Tests.Mocks
{
    public class MockShopUiGateway : IShopUiGateway
    {
        public List<(ulong steamId, ushort effectId, short key)> OpenCalls { get; } = new List<(ulong, ushort, short)>();
        public List<(ulong steamId, ushort effectId, short key)> CloseCalls { get; } = new List<(ulong, ushort, short)>();
        public Dictionary<string, string> TextUpdates { get; } = new Dictionary<string, string>();
        public Dictionary<string, bool> VisibilityUpdates { get; } = new Dictionary<string, bool>();

        public void OpenUi(ulong steamId, ushort effectId, short key)
        {
            OpenCalls.Add((steamId, effectId, key));
        }

        public void CloseUi(ulong steamId, ushort effectId, short key)
        {
            CloseCalls.Add((steamId, effectId, key));
        }

        public void UpdateText(ulong steamId, short key, string elementName, string text)
        {
            TextUpdates[elementName] = text;
        }

        public void UpdateVisibility(ulong steamId, short key, string elementName, bool visible)
        {
            VisibilityUpdates[elementName] = visible;
        }
    }
}
