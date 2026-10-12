using System;
using System.Collections.Generic;
using System.Linq;
using KizShop.Common;
using KizShop.Shop.Models;

namespace KizShop.Shop
{
    public static class BossRewardCalculator
    {
        public static Dictionary<ulong, Money> CalculateDistribution(
            Money pool,
            IReadOnlyDictionary<ulong, float> damageByPlayer,
            ulong lastHitSteamId,
            int topDamagePercent,
            int lastHitPercent,
            int participationPercent,
            float minDamageSharePercent)
        {
            var result = new Dictionary<ulong, Money>();
            if (pool.Cents <= 0 || damageByPlayer == null || damageByPlayer.Count == 0)
            {
                return result;
            }

            // Filter out non-positive damage
            var validParticipants = damageByPlayer
                .Where(kvp => kvp.Value > 0f && kvp.Key != 0)
                .OrderByDescending(kvp => kvp.Value)
                .ThenBy(kvp => kvp.Key)
                .ToList();

            if (validParticipants.Count == 0)
            {
                return result;
            }

            // Percentage validation: fallback to 30, 10, 60 if sum != 100
            if (topDamagePercent + lastHitPercent + participationPercent != 100)
            {
                topDamagePercent = 30;
                lastHitPercent = 10;
                participationPercent = 60;
            }

            float totalDamage = validParticipants.Sum(x => x.Value);
            if (totalDamage <= 0f) return result;

            // Top damage player (highest damage, ties by lower steamId)
            ulong topDamagerId = validParticipants[0].Key;

            // Last hitter check
            bool lastHitterIsParticipant = lastHitSteamId != 0 && validParticipants.Any(x => x.Key == lastHitSteamId);
            if (!lastHitterIsParticipant)
            {
                // If last hitter not a participant, that share goes to participation pool
                participationPercent += lastHitPercent;
                lastHitPercent = 0;
            }

            long totalPoolCents = pool.Cents;
            long topShareCents = (totalPoolCents * topDamagePercent) / 100;
            long lastHitShareCents = (totalPoolCents * lastHitPercent) / 100;
            long participationShareCents = totalPoolCents - topShareCents - lastHitShareCents;

            // Calculate participation eligible players
            var eligibleParticipants = validParticipants
                .Where(p => ((p.Value / totalDamage) * 100f) >= minDamageSharePercent)
                .ToList();

            // If no one meets min damage share, fall back to all participants
            if (eligibleParticipants.Count == 0)
            {
                eligibleParticipants = validParticipants;
            }

            float eligibleTotalDamage = eligibleParticipants.Sum(x => x.Value);

            // Calculate floor participation payouts and remainders
            var partList = new List<(ulong SteamId, long FloorCents, decimal Remainder)>(eligibleParticipants.Count);
            long floorSum = 0;

            foreach (var p in eligibleParticipants)
            {
                decimal unrounded = (participationShareCents * (decimal)p.Value) / (decimal)eligibleTotalDamage;
                long floor = (long)Math.Floor(unrounded);
                decimal remainder = unrounded - floor;
                partList.Add((p.Key, floor, remainder));
                floorSum += floor;
            }

            long remainderCents = participationShareCents - floorSum;
            // Distribute remainder cents deterministically: largest remainder first, ties by lower SteamID64
            var orderedByRemainder = partList
                .OrderByDescending(x => x.Remainder)
                .ThenBy(x => x.SteamId)
                .ToList();

            var finalPayouts = new Dictionary<ulong, long>();
            for (int i = 0; i < orderedByRemainder.Count; i++)
            {
                var entry = orderedByRemainder[i];
                long cents = entry.FloorCents;
                if (i < remainderCents)
                {
                    cents += 1;
                }
                finalPayouts[entry.SteamId] = cents;
            }

            // Add top damager share
            if (topShareCents > 0)
            {
                if (finalPayouts.ContainsKey(topDamagerId))
                    finalPayouts[topDamagerId] += topShareCents;
                else
                    finalPayouts[topDamagerId] = topShareCents;
            }

            // Add last hitter share
            if (lastHitShareCents > 0 && lastHitterIsParticipant)
            {
                if (finalPayouts.ContainsKey(lastHitSteamId))
                    finalPayouts[lastHitSteamId] += lastHitShareCents;
                else
                    finalPayouts[lastHitSteamId] = lastHitShareCents;
            }

            foreach (var kvp in finalPayouts)
            {
                result[kvp.Key] = Money.FromCents(kvp.Value);
            }

            return result;
        }
    }
}
