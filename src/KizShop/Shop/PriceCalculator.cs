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
            if (recoveryPerHour <= 0m) return storedPressure;

            DateTime lastUtc = lastUpdatedUtc.Kind == DateTimeKind.Local ? lastUpdatedUtc.ToUniversalTime() : lastUpdatedUtc;
            DateTime currentUtc = nowUtc.Kind == DateTimeKind.Local ? nowUtc.ToUniversalTime() : nowUtc;

            if (lastUtc >= currentUtc) return storedPressure;

            double elapsedHours = (currentUtc - lastUtc).TotalHours;
            if (elapsedHours <= 0) return storedPressure;

            decimal recovered = (decimal)elapsedHours * recoveryPerHour;
            return Math.Max(0m, storedPressure - recovered);
        }

        public static decimal ComputeEffectiveSellMultiplier(ItemDynamicPricingConfig cfg, decimal sellPressure)
        {
            if (cfg == null || !cfg.Enabled) return 1.0m;
            decimal mult = 1.0m - (Math.Max(0m, cfg.SellDecayPerUnit) * Math.Max(0m, sellPressure));
            decimal minSell = Math.Clamp(cfg.MinSellMultiplier, 0m, 1.0m);
            return Math.Clamp(mult, minSell, 1.0m);
        }

        public static decimal ComputeEffectiveBuyMultiplier(ItemDynamicPricingConfig cfg, decimal buyPressure)
        {
            if (cfg == null || !cfg.Enabled) return 1.0m;
            if (cfg.BuyIncreasePerUnit <= 0m) return 1.0m;
            decimal mult = 1.0m + (cfg.BuyIncreasePerUnit * Math.Max(0m, buyPressure));
            decimal maxBuy = Math.Max(1.0m, cfg.MaxBuyMultiplier);
            return Math.Clamp(mult, 1.0m, maxBuy);
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
            bool allowSellAboveBuy,
            decimal buyMultiplier = 1.0m,
            decimal sellMultiplier = 1.0m)
        {
            decimal sellMult = ComputeEffectiveSellMultiplier(cfg, sellPressure);
            decimal effSell = baseSellPrice * sellMult * sellMultiplier;

            if (!allowSellAboveBuy && baseBuyPrice > 0m)
            {
                decimal buyMult = ComputeEffectiveBuyMultiplier(cfg, buyPressure);
                decimal effBuy = baseBuyPrice * buyMult * buyMultiplier;
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
