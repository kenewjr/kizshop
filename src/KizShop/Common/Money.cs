using System;
using System.Globalization;

namespace KizShop.Common
{
    public readonly struct Money : IEquatable<Money>, IComparable<Money>
    {
        public long Cents { get; }

        public Money(long cents)
        {
            Cents = cents;
        }

        public static Money Zero => new Money(0);
        public static Money MinValue => new Money(long.MinValue);
        public static Money MaxValue => new Money(long.MaxValue);

        public static Money FromCents(long cents) => new Money(cents);

        public static Money FromDecimal(decimal amount)
        {
            if (!TryFromDecimal(amount, 0, out var money, out var error))
            {
                throw new ArgumentException(error, nameof(amount));
            }
            return money;
        }

        public static bool TryFromDecimal(decimal amount, decimal maxTransactionAmount, out Money money, out string error)
        {
            decimal centsDecimal = amount * 100m;
            if (centsDecimal != decimal.Truncate(centsDecimal))
            {
                money = Zero;
                error = "Amount cannot have more than 2 decimal places.";
                return false;
            }

            if (amount < 0.01m)
            {
                money = Zero;
                error = "Amount must be at least 0.01.";
                return false;
            }

            if (maxTransactionAmount > 0 && amount > maxTransactionAmount)
            {
                money = Zero;
                error = $"Amount exceeds maximum transaction limit of {maxTransactionAmount:N2}.";
                return false;
            }

            try
            {
                long cents = Convert.ToInt64(decimal.Truncate(centsDecimal));
                money = new Money(cents);
                error = null;
                return true;
            }
            catch (OverflowException)
            {
                money = Zero;
                error = "Amount causes numeric overflow.";
                return false;
            }
        }

        public static bool TryFromDecimalAllowZero(decimal amount, out Money money, out string error)
        {
            if (amount < 0m)
            {
                money = Zero;
                error = "Amount cannot be negative.";
                return false;
            }

            decimal centsDecimal = amount * 100m;
            if (centsDecimal != decimal.Truncate(centsDecimal))
            {
                money = Zero;
                error = "Amount cannot have more than 2 decimal places.";
                return false;
            }

            try
            {
                long cents = Convert.ToInt64(decimal.Truncate(centsDecimal));
                money = new Money(cents);
                error = null;
                return true;
            }
            catch (OverflowException)
            {
                money = Zero;
                error = "Amount causes numeric overflow.";
                return false;
            }
        }

        public decimal ToDecimal() => Cents / 100m;

        public override string ToString() => ToDecimal().ToString("F2", CultureInfo.InvariantCulture);

        public string ToString(string format, IFormatProvider formatProvider) => ToDecimal().ToString(format, formatProvider);

        public bool Equals(Money other) => Cents == other.Cents;

        public override bool Equals(object obj) => obj is Money other && Equals(other);

        public override int GetHashCode() => Cents.GetHashCode();

        public int CompareTo(Money other) => Cents.CompareTo(other.Cents);

        public static Money operator +(Money a, Money b) => new Money(checked(a.Cents + b.Cents));

        public static Money operator -(Money a, Money b) => new Money(checked(a.Cents - b.Cents));

        public static Money operator *(Money a, int multiplier) => new Money(checked(a.Cents * multiplier));

        public static Money operator *(int multiplier, Money a) => new Money(checked(a.Cents * multiplier));

        public static bool operator ==(Money a, Money b) => a.Cents == b.Cents;

        public static bool operator !=(Money a, Money b) => a.Cents != b.Cents;

        public static bool operator <(Money a, Money b) => a.Cents < b.Cents;

        public static bool operator <=(Money a, Money b) => a.Cents <= b.Cents;

        public static bool operator >(Money a, Money b) => a.Cents > b.Cents;

        public static bool operator >=(Money a, Money b) => a.Cents >= b.Cents;
    }
}
