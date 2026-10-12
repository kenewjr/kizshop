using System;
using KizShop.Common;
using KizShop.Shop.Models;

namespace KizShop.Shop
{
    public static class PriceCalculator
    {
        public static decimal ComputeRecoveredPressure(decimal storedPressure, DateTime lastUpdatedUtc, DateTime nowUtc, decimal recoveryPerHour)
        {
            if (storedPressure <= 0m) return 0m;
            if (lastUpdatedUtc >= nowUtc || recoveryPerHour <= 0m) return storedPressure;

            double elapsedHours = (nowUtc - lastUpdatedUtc).TotalHours;
            if (elapsedHours <= 0) return storedPressure;

            decimal recovered = (decimal)elapsedHours * recoveryPerHour;
            return Math.Max(0m, storedPressure - recovered);
        }

        public static decimal ComputeEffectiveSellMultiplier(ItemDynamicPricingConfig cfg, decimal sellPressure)
        {
            if (cfg == null || !cfg.Enabled) return 1.0m;
            decimal mult = 1.0m - (cfg.SellDecayPerUnit * sellPressure);
            return Math.Clamp(mult, cfg.MinSellMultiplier, 1.0m);
        }

        public static decimal ComputeEffectiveBuyMultiplier(ItemDynamicPricingConfig cfg, decimal buyPressure)
        {
            if (cfg == null || !cfg.Enabled) return 1.0m;
            if (cfg.BuyIncreasePerUnit <= 0m) return 1.0m;
            decimal mult = 1.0m + (cfg.BuyIncreasePerUnit * buyPressure);
            return Math.Clamp(mult, 1.0m, cfg.MaxBuyMultiplier);
        }

        public static Money CalculateSingleUnitBuyPrice(
            decimal baseBuyPrice,
            decimal baseSellPrice,
            ItemDynamicPricingConfig cfg,
            decimal buyPressure,
            decimal sellPressure,
            bool allowSellAboveBuy)
        {
            decimal buyMult = ComputeEffectiveBuyMultiplier(cfg, buyPressure);
            decimal effBuy = baseBuyPrice * buyMult;
            long cents = (long)Math.Round(effBuy * 100m, MidpointRounding.AwayFromZero);
            return Money.FromCents(Math.Max(0, cents));
        }

        public static Money CalculateSingleUnitSellPrice(
            decimal baseBuyPrice,
            decimal baseSellPrice,
            ItemDynamicPricingConfig cfg,
            decimal buyPressure,
            decimal sellPressure,
            bool allowSellAboveBuy)
        {
            decimal sellMult = ComputeEffectiveSellMultiplier(cfg, sellPressure);
            decimal effSell = baseSellPrice * sellMult;

            if (!allowSellAboveBuy && baseBuyPrice > 0m)
            {
                decimal buyMult = ComputeEffectiveBuyMultiplier(cfg, buyPressure);
                decimal effBuy = baseBuyPrice * buyMult;
                if (effSell > effBuy)
                {
                    effSell = effBuy;
                }
            }

            long cents = (long)Math.Round(effSell * 100m, MidpointRounding.AwayFromZero);
            return Money.FromCents(Math.Max(0, cents));
        }

        public static (Money Total, Money FirstUnitPrice) CalculateSequentialBuyPrice(
            decimal baseBuyPrice,
            decimal baseSellPrice,
            ItemDynamicPricingConfig cfg,
            decimal startingBuyPressure,
            decimal startingSellPressure,
            int quantity,
            bool allowSellAboveBuy)
        {
            if (quantity <= 0) return (Money.Zero, Money.Zero);

            long totalCents = 0;
            Money firstUnit = Money.Zero;
            decimal currentBuyPressure = startingBuyPressure;

            for (int i = 0; i < quantity; i++)
            {
                var unitPrice = CalculateSingleUnitBuyPrice(baseBuyPrice, baseSellPrice, cfg, currentBuyPressure, startingSellPressure, allowSellAboveBuy);
                if (i == 0) firstUnit = unitPrice;
                totalCents += unitPrice.Cents;
                if (cfg != null && cfg.Enabled && cfg.BuyIncreasePerUnit > 0m)
                {
                    currentBuyPressure += 1.0m;
                }
            }

            return (Money.FromCents(totalCents), firstUnit);
        }

        public static (Money Total, Money FirstUnitPrice) CalculateSequentialSellPrice(
            decimal baseBuyPrice,
            decimal baseSellPrice,
            ItemDynamicPricingConfig cfg,
            decimal startingBuyPressure,
            decimal startingSellPressure,
            int quantity,
            bool allowSellAboveBuy)
        {
            if (quantity <= 0) return (Money.Zero, Money.Zero);

            long totalCents = 0;
            Money firstUnit = Money.Zero;
            decimal currentSellPressure = startingSellPressure;

            for (int i = 0; i < quantity; i++)
            {
                var unitPrice = CalculateSingleUnitSellPrice(baseBuyPrice, baseSellPrice, cfg, startingBuyPressure, currentSellPressure, allowSellAboveBuy);
                if (i == 0) firstUnit = unitPrice;
                totalCents += unitPrice.Cents;
                if (cfg != null && cfg.Enabled)
                {
                    currentSellPressure += 1.0m;
                }
            }

            return (Money.FromCents(totalCents), firstUnit);
        }
    }
}
