using Rocket.API;
using Rocket.Unturned.Chat;
using UnityEngine;

namespace KizShop.Common
{
    public static class KizChat
    {
        public static void Say(IRocketPlayer player, string message, Color color)
        {
            if (player == null || string.IsNullOrEmpty(message)) return;
            UnturnedChat.Say(player, message, color, true);
        }

        public static void Say(IRocketPlayer player, string message)
        {
            if (player == null || string.IsNullOrEmpty(message)) return;
            UnturnedChat.Say(player, message, Color.white, true);
        }
    }
}
