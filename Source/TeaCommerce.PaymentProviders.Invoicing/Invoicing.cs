using System.Collections.Generic;
using System.Globalization;
using System.Web;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Models;

namespace TeaCommerce.Api.Web.PaymentProviders
{
    [PaymentProvider("Invoicing")]
    public class Invoicing : APaymentProvider
    {
        public override IDictionary<string, string> DefaultSettings
        {
            get
            {
                Dictionary<string, string> defaultSettings = new Dictionary<string, string>();
                defaultSettings["acceptUrl"] = string.Empty;
                return defaultSettings;
            }
        }

        public override bool FinalizeAtContinueUrl { get { return true; } }

        public override PaymentHtmlForm GenerateHtmlForm(Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, string teaCommerceCommunicationUrl, IDictionary<string, string> settings)
        {
            return new PaymentHtmlForm { Action = teaCommerceContinueUrl };
        }

        public override string GetContinueUrl(Order order, IDictionary<string, string> settings)
        {
            settings.MustNotBeNull("settings");
            settings.MustContainKey("acceptUrl", "settings");

            return settings["acceptUrl"];
        }

        public override string GetCancelUrl(Order order, IDictionary<string, string> settings)
        {
            return "";
        }

        public override CallbackInfo ProcessCallback(Order order, HttpRequest request, IDictionary<string, string> settings)
        {
            return new CallbackInfo(order.TotalPrice.Value.WithVat, "", PaymentState.Authorized);
        }

        public override string GetLocalizedSettingsKey(string settingsKey, CultureInfo culture)
        {
            switch (settingsKey)
            {
                case "acceptUrl":
                    return settingsKey + "<br/><small>e.g. /continue/</small>";
                default:
                    return base.GetLocalizedSettingsKey(settingsKey, culture);
            }
        }

    }
}
