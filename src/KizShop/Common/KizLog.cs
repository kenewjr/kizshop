using System;

namespace KizShop.Common
{
    public static class KizLog
    {
        public static void Info(string message)
        {
            try
            {
                Rocket.Core.Logging.Logger.Log(message);
            }
            catch
            {
                Console.WriteLine(message);
            }
        }

        public static void Warning(string message)
        {
            try
            {
                Rocket.Core.Logging.Logger.LogWarning(message);
            }
            catch
            {
                Console.WriteLine(message);
            }
        }

        public static void Error(string message)
        {
            try
            {
                Rocket.Core.Logging.Logger.LogError(message);
            }
            catch
            {
                Console.Error.WriteLine(message);
            }
        }
    }
}
