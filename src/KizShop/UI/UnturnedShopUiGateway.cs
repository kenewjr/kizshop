using System;
using System.Threading;
using System.Threading.Tasks;
using SDG.NetTransport;
using SDG.Unturned;
using Steamworks;
using KizShop.Common;

namespace KizShop.UI
{
    public class UnturnedShopUiGateway : IShopUiGateway
    {
        private readonly IMainThreadDispatcher _dispatcher;

        public UnturnedShopUiGateway(IMainThreadDispatcher dispatcher = null)
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

        public async Task<bool> OpenUiAsync(ulong steamId, ushort effectId, short key)
        {
            return await _dispatcher.RunOnMainThreadAsync(() =>
            {
                DebugAssertMainThread(nameof(OpenUiAsync));
                try
                {
                    var player = PlayerTool.getPlayer(new CSteamID(steamId));
                    if (player == null) return false;

                    var effectAsset = Assets.find(EAssetType.EFFECT, effectId);
                    if (effectAsset == null)
                    {
                        KizLog.Warning($"[KizShop] OpenUi: Visual UI Effect ID {effectId} is not loaded on this server. Skipping.");
                        return false;
                    }

                    EffectManager.sendUIEffect(effectId, key, player.channel.GetOwnerTransportConnection(), true);
                    player.enablePluginWidgetFlag(EPluginWidgetFlags.Modal);
                    return true;
                }
                catch (Exception ex)
                {
                    KizLog.Error($"[KizShop] UnturnedShopUiGateway.OpenUi (Effect {effectId}) error: {ex}");
                    return false;
                }
            }).ConfigureAwait(false);
        }

        public async Task CloseUiAsync(ulong steamId, ushort effectId, short key)
        {
            await _dispatcher.RunOnMainThreadAsync(() =>
            {
                DebugAssertMainThread(nameof(CloseUiAsync));
                try
                {
                    var player = PlayerTool.getPlayer(new CSteamID(steamId));
                    if (player == null) return;

                    EffectManager.askEffectClearByID(effectId, player.channel.GetOwnerTransportConnection());
                    player.disablePluginWidgetFlag(EPluginWidgetFlags.Modal);
                }
                catch (Exception ex)
                {
                    KizLog.Error($"[KizShop] UnturnedShopUiGateway.CloseUi (Effect {effectId}) error: {ex}");
                }
            }).ConfigureAwait(false);
        }

        public async Task UpdateTextAsync(ulong steamId, short key, string elementName, string text)
        {
            await _dispatcher.RunOnMainThreadAsync(() =>
            {
                DebugAssertMainThread(nameof(UpdateTextAsync));
                try
                {
                    var player = PlayerTool.getPlayer(new CSteamID(steamId));
                    if (player == null) return;

                    EffectManager.sendUIEffectText(key, player.channel.GetOwnerTransportConnection(), true, elementName, text ?? string.Empty);
                }
                catch (Exception ex)
                {
                    KizLog.Error($"[KizShop] UnturnedShopUiGateway.UpdateText ({elementName}, Key {key}) error: {ex}");
                }
            }).ConfigureAwait(false);
        }

        public async Task UpdateVisibilityAsync(ulong steamId, short key, string elementName, bool visible)
        {
            await _dispatcher.RunOnMainThreadAsync(() =>
            {
                DebugAssertMainThread(nameof(UpdateVisibilityAsync));
                try
                {
                    var player = PlayerTool.getPlayer(new CSteamID(steamId));
                    if (player == null) return;

                    EffectManager.sendUIEffectVisibility(key, player.channel.GetOwnerTransportConnection(), true, elementName, visible);
                }
                catch (Exception ex)
                {
                    KizLog.Error($"[KizShop] UnturnedShopUiGateway.UpdateVisibility ({elementName}, Key {key}) error: {ex}");
                }
            }).ConfigureAwait(false);
        }
    }
}
