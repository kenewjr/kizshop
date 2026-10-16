using System;
using System.Text.RegularExpressions;
using Rocket.API;
using Rocket.Unturned.Chat;
using UnityEngine;

namespace KizShop.Common
{
    public static class KizChat
    {
        private static readonly Regex UnsupportedGoldRegex = new Regex(@"<color\s*=\s*['""]?gold['""]?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string SanitizeMessage(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;
            if (message.IndexOf("gold", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                message = UnsupportedGoldRegex.Replace(message, "<color=yellow>");
            }
            return message;
        }

        public static void Say(IRocketPlayer player, string message, Color color)
        {
            if (player == null || string.IsNullOrEmpty(message)) return;
            UnturnedChat.Say(player, SanitizeMessage(message), color, true);
        }

        public static void Say(IRocketPlayer player, string message)
        {
            if (player == null || string.IsNullOrEmpty(message)) return;
            UnturnedChat.Say(player, SanitizeMessage(message), Color.white, true);
        }

        public static void Broadcast(string message, Color color)
        {
            if (string.IsNullOrEmpty(message)) return;
            UnturnedChat.Say(SanitizeMessage(message), color, true);
        }

        public static void Broadcast(string message)
        {
            Broadcast(message, Color.yellow);
        }
    }
}
