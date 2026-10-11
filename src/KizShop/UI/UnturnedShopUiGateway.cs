using System;
using SDG.NetTransport;
using SDG.Unturned;
using Steamworks;

namespace KizShop.UI
{
    public class UnturnedShopUiGateway : IShopUiGateway
    {
        public void OpenUi(ulong steamId, ushort effectId, short key)
        {
            try
            {
                var player = PlayerTool.getPlayer(new CSteamID(steamId));
                if (player == null) return;

                EffectManager.sendUIEffect(effectId, key, player.channel.GetOwnerTransportConnection(), true);
                player.enablePluginWidgetFlag(EPluginWidgetFlags.Modal);
            }
            catch { }
        }

        public void CloseUi(ulong steamId, ushort effectId, short key)
        {
            try
            {
                var player = PlayerTool.getPlayer(new CSteamID(steamId));
                if (player == null) return;

                EffectManager.askEffectClearByID(effectId, player.channel.GetOwnerTransportConnection());
                player.disablePluginWidgetFlag(EPluginWidgetFlags.Modal);
            }
            catch { }
        }

        public void UpdateText(ulong steamId, short key, string elementName, string text)
        {
            try
            {
                var player = PlayerTool.getPlayer(new CSteamID(steamId));
                if (player == null) return;

                EffectManager.sendUIEffectText(key, player.channel.GetOwnerTransportConnection(), true, elementName, text ?? string.Empty);
            }
            catch { }
        }

        public void UpdateVisibility(ulong steamId, short key, string elementName, bool visible)
        {
            try
            {
                var player = PlayerTool.getPlayer(new CSteamID(steamId));
                if (player == null) return;

                EffectManager.sendUIEffectVisibility(key, player.channel.GetOwnerTransportConnection(), true, elementName, visible);
            }
            catch { }
        }
    }
}
