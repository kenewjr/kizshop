using System;

namespace KizShop.Common
{
    public class WithdrawResult
    {
        public bool Success { get; }
        public Money OldBalance { get; }
        public Money NewBalance { get; }

        public WithdrawResult(bool success, Money oldBalance, Money newBalance)
        {
            Success = success;
            OldBalance = oldBalance;
            NewBalance = newBalance;
        }

        public static WithdrawResult Failed(Money currentBalance) => new WithdrawResult(false, currentBalance, currentBalance);
        public static WithdrawResult Succeeded(Money oldBalance, Money newBalance) => new WithdrawResult(true, oldBalance, newBalance);
    }

    public class DepositResult
    {
        public bool Success { get; }
        public Money OldBalance { get; }
        public Money NewBalance { get; }

        public DepositResult(bool success, Money oldBalance, Money newBalance)
        {
            Success = success;
            OldBalance = oldBalance;
            NewBalance = newBalance;
        }
    }

    public class TransferResult
    {
        public bool Success { get; }
        public Money FromOldBalance { get; }
        public Money FromNewBalance { get; }
        public Money ToOldBalance { get; }
        public Money ToNewBalance { get; }

        public TransferResult(bool success, Money fromOld, Money fromNew, Money toOld, Money toNew)
        {
            Success = success;
            FromOldBalance = fromOld;
            FromNewBalance = fromNew;
            ToOldBalance = toOld;
            ToNewBalance = toNew;
        }

        public static TransferResult Failed(Money fromCurrent, Money toCurrent) =>
            new TransferResult(false, fromCurrent, fromCurrent, toCurrent, toCurrent);

        public static TransferResult Succeeded(Money fromOld, Money fromNew, Money toOld, Money toNew) =>
            new TransferResult(true, fromOld, fromNew, toOld, toNew);
    }
}
