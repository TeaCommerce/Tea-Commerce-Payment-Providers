using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace TeaCommerce.PaymentProviders.Helpers
{
    public static class PriceValueConverter
    {
        public static int ToCents(this decimal value)
        {
            return (int)Math.Round(value * 100M, MidpointRounding.AwayFromZero);            
        }
    }
}
