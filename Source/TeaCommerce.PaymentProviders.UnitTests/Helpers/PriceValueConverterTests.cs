using Microsoft.VisualStudio.TestTools.UnitTesting;
using TeaCommerce.Api.Models;
using TeaCommerce.PaymentProviders.Helpers;

namespace TeaCommerce.PaymentProviders.UnitTests.Helpers
{
    [TestClass]
    public class PriceValueConverterTests
    {
        #region ToCents Tests

        [TestMethod]
        public void ToCentsShouldConvertValueToCents()
        {
            var price = new Price(1.44M, 0M, 1.44M, new Currency() { Name = "GBP", CultureName = "en-GB" });
            var fromPrice = price.FormattedWithoutSymbol.Replace(".", string.Empty);
            var actual = price.Value.ToCents();

            Assert.AreEqual(144, actual);
            Assert.AreEqual(fromPrice, actual.ToString());
        }

        [TestMethod]
        public void ToCentsShouldRoundUpHalfCentForEvenCentAmounts()
        {
            var price = new Price(1.445M, 0M, 1.445M, new Currency() { Name = "GBP", CultureName = "en-GB" });
            var fromPrice = price.FormattedWithoutSymbol.Replace(".", string.Empty);
            var actual = price.Value.ToCents();

            Assert.AreEqual(145, actual);
            Assert.AreEqual(fromPrice, actual.ToString());
        }

        [TestMethod]
        public void ToCentsShouldRoundUpHalfCentForOddCentAmounts()
        {
            var price = new Price(1.455M, 0M, 1.455M, new Currency() { Name = "GBP", CultureName = "en-GB" });
            var fromPrice = price.FormattedWithoutSymbol.Replace(".", string.Empty);
            var actual = price.Value.ToCents();

            Assert.AreEqual(146, actual);
            Assert.AreEqual(fromPrice, actual.ToString());
        }

        [TestMethod]
        public void ToCentsShouldRoundDown()
        {
            var price = new Price(1.454M, 0M, 1.454M, new Currency() { Name = "GBP", CultureName = "en-GB" });
            var fromPrice = price.FormattedWithoutSymbol.Replace(".", string.Empty);
            var actual = price.Value.ToCents();

            Assert.AreEqual(145, actual);
            Assert.AreEqual(fromPrice, actual.ToString());
        }

        [TestMethod]
        public void ToCentsShouldRoundUp()
        {
            var price = new Price(1.456M, 0M, 1.456M, new Currency() { Name = "GBP", CultureName = "en-GB" });
            var fromPrice = price.FormattedWithoutSymbol.Replace(".", string.Empty);
            var actual = price.Value.ToCents();

            Assert.AreEqual(146, actual);
            Assert.AreEqual(fromPrice, actual.ToString());
        }

        #endregion
    }
}
