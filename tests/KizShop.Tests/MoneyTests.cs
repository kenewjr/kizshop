using Xunit;
using KizShop.Common;

namespace KizShop.Tests
{
    public class MoneyTests
    {
        [Fact]
        public void TryFromDecimal_RejectsMoreThanTwoDecimalsWithoutRounding()
        {
            Assert.False(Money.TryFromDecimal(0.005m, 0, out _, out var err1));
            Assert.Equal("Amount cannot have more than 2 decimal places.", err1);

            Assert.False(Money.TryFromDecimal(0.0000001m, 0, out _, out var err2));
            Assert.Equal("Amount cannot have more than 2 decimal places.", err2);

            Assert.False(Money.TryFromDecimal(12.345m, 0, out _, out var err3));
            Assert.Equal("Amount cannot have more than 2 decimal places.", err3);
        }

        [Fact]
        public void TryFromDecimal_RejectsBelowMinimumOneCent()
        {
            Assert.False(Money.TryFromDecimal(0.00m, 0, out _, out var err1));
            Assert.Equal("Amount must be at least 0.01.", err1);

            Assert.False(Money.TryFromDecimal(-5.00m, 0, out _, out var err2));
            Assert.Equal("Amount must be at least 0.01.", err2);
        }

        [Fact]
        public void TryFromDecimal_RejectsAboveMaxTransactionAmount()
        {
            decimal maxTx = 1000m;
            Assert.False(Money.TryFromDecimal(1000.01m, maxTx, out _, out var err));
            Assert.Contains("exceeds maximum transaction limit", err);

            Assert.True(Money.TryFromDecimal(1000.00m, maxTx, out var valid, out _));
            Assert.Equal(100000L, valid.Cents);
        }

        [Fact]
        public void TryFromDecimal_AcceptsValidTwoDecimalValues()
        {
            Assert.True(Money.TryFromDecimal(19.99m, 0, out var money, out _));
            Assert.Equal(1999L, money.Cents);
            Assert.Equal(19.99m, money.ToDecimal());
        }

        [Fact]
        public void ArithmeticOperators_MaintainExactMinorUnits()
        {
            var a = Money.FromCents(1050); // 10.50
            var b = Money.FromCents(425);  // 4.25

            var sum = a + b;
            Assert.Equal(1475L, sum.Cents);
            Assert.Equal(14.75m, sum.ToDecimal());

            var diff = a - b;
            Assert.Equal(625L, diff.Cents);
            Assert.Equal(6.25m, diff.ToDecimal());

            var mult = b * 3;
            Assert.Equal(1275L, mult.Cents);
            Assert.Equal(12.75m, mult.ToDecimal());
        }
    }
}
