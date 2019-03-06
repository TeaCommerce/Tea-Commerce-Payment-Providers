using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web;
using TeaCommerce.Api;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;
using Order = TeaCommerce.Api.Models.Order;
using KlarnaClient = Klarna.Rest.Client;
using KlarnaOrderLine = Klarna.Rest.Models.OrderLine;
using Klarna.Rest.Models;
using Klarna.Rest.Checkout;

namespace TeaCommerce.PaymentProviders.Inline
{
    [PaymentProvider("Klarna3")]
    public class Klarna3 : APaymentProvider
    {
        public override IDictionary<string, string> DefaultSettings
        {
            get
            {
                return new Dictionary<string, string>
                {
                    ["merchant.id"] = "",
                    ["locale"] = "sv-se",
                    ["paymentFormUrl"] = "",
                    ["merchant.confirmation_uri"] = "",
                    ["merchant.terms_uri"] = "",
                    ["sharedSecret"] = "",
                    ["zipCodePropAlias"] = "zipCode",
                    ["totalSku"] = "0001",
                    ["totalName"] = "Totala",
                    ["testMode"] = "1"
                };
            }
        }

        public override PaymentHtmlForm GenerateHtmlForm(Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, string teaCommerceCommunicationUrl, IDictionary<string, string> settings)
        {
            order.MustNotBeNull("order");
            settings.MustNotBeNull("settings");
            settings.MustContainKey("paymentFormUrl", "settings");

            var htmlForm = new PaymentHtmlForm
            {
                Action = settings["paymentFormUrl"]
            };

            order.Properties.AddOrUpdate(new CustomProperty("teaCommerceCommunicationUrl", teaCommerceCommunicationUrl) { ServerSideOnly = true });
            order.Properties.AddOrUpdate(new CustomProperty("teaCommerceContinueUrl", teaCommerceContinueUrl) { ServerSideOnly = true });
            order.Properties.AddOrUpdate(new CustomProperty("teaCommerceCallbackUrl", teaCommerceCallBackUrl) { ServerSideOnly = true });

            return htmlForm;
        }

        public override string GetContinueUrl(Order order, IDictionary<string, string> settings)
        {
            settings.MustNotBeNull("settings");
            settings.MustContainKey("merchant.confirmation_uri", "settings");

            return settings["merchant.confirmation_uri"];
        }

        public override string GetCancelUrl(Order order, IDictionary<string, string> settings)
        {
            return ""; // not used in Klarna
        }

        public override CallbackInfo ProcessCallback(Order order, HttpRequest request, IDictionary<string, string> settings)
        {
            CallbackInfo callbackInfo = null;

            try
            { 
                order.MustNotBeNull("order");
                request.MustNotBeNull("request");
                settings.MustNotBeNull("settings");
                settings.MustContainKey("merchant.id", "settings");
                settings.MustContainKey("sharedSecret", "settings");

                var client = CreateKlarnaClientFromSettings(settings);
                var klarnaOrder = client.NewCheckoutOrder(order.TransactionInformation.TransactionId);
                var klarnaOrderData = klarnaOrder.Fetch();

                if (klarnaOrderData.Status == "checkout_complete")
                {
                    // We need to populate the order with the information entered into Klarna.
                    SaveOrderPropertiesFromKlarnaCallback(order, klarnaOrderData);

                    var amount = klarnaOrderData.OrderAmount.Value / 100M; // ((JObject)klarnaOrder.GetValue("cart"))["total_price_including_tax"].Value<decimal>() / 100M;
                    var klarnaId = klarnaOrderData.OrderId;

                    callbackInfo = new CallbackInfo(amount, klarnaId, PaymentState.Captured);
                }
                else
                {
                    throw new Exception("Trying to process a callback from Klarna with an order that isn't completed");
                }
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<Klarna3>("Klarna(" + order.CartNumber + ") - Process callback", exp);
            }

            return callbackInfo;
        }

        protected virtual void SaveOrderPropertiesFromKlarnaCallback(Order order, CheckoutOrderData klarnaOrderData)
        {
            // First name
            if (!string.IsNullOrWhiteSpace(klarnaOrderData.BillingAddress.GivenName))
                order.Properties.AddOrUpdate(Constants.OrderPropertyAliases.FirstNamePropertyAlias, klarnaOrderData.BillingAddress.GivenName);

            // Last name
            if (!string.IsNullOrWhiteSpace(klarnaOrderData.BillingAddress.FamilyName)) 
                order.Properties.AddOrUpdate(Constants.OrderPropertyAliases.LastNamePropertyAlias, klarnaOrderData.BillingAddress.FamilyName);

            // Email
            if (!string.IsNullOrWhiteSpace(klarnaOrderData.BillingAddress.Email))
                 order.Properties.AddOrUpdate(Constants.OrderPropertyAliases.EmailPropertyAlias, klarnaOrderData.BillingAddress.Email);

            // Billing address
            if (!string.IsNullOrWhiteSpace(klarnaOrderData.BillingAddress.StreetAddress))
                order.Properties.AddOrUpdate("billing_street_address", klarnaOrderData.BillingAddress.StreetAddress);
            if (!string.IsNullOrWhiteSpace(klarnaOrderData.BillingAddress.StreetAddress2))
                order.Properties.AddOrUpdate("billing_street_address2", klarnaOrderData.BillingAddress.StreetAddress2);
            if (!string.IsNullOrWhiteSpace(klarnaOrderData.BillingAddress.PostalCode))
                order.Properties.AddOrUpdate("billing_postal_code", klarnaOrderData.BillingAddress.PostalCode);
            if (!string.IsNullOrWhiteSpace(klarnaOrderData.BillingAddress.City))
                order.Properties.AddOrUpdate("billing_city", klarnaOrderData.BillingAddress.City);
            if (!string.IsNullOrWhiteSpace(klarnaOrderData.BillingAddress.Phone))
                order.Properties.AddOrUpdate("billing_phone", klarnaOrderData.BillingAddress.Phone);

            // Shipping address
            if (!string.IsNullOrWhiteSpace(klarnaOrderData.ShippingAddress.StreetAddress))
                order.Properties.AddOrUpdate("shipping_street_address", klarnaOrderData.ShippingAddress.StreetAddress);
            if (!string.IsNullOrWhiteSpace(klarnaOrderData.ShippingAddress.StreetAddress2))
                order.Properties.AddOrUpdate("shipping_street_address2", klarnaOrderData.ShippingAddress.StreetAddress2);
            if (!string.IsNullOrWhiteSpace(klarnaOrderData.ShippingAddress.PostalCode))
                order.Properties.AddOrUpdate("shipping_postal_code", klarnaOrderData.ShippingAddress.PostalCode);
            if (!string.IsNullOrWhiteSpace(klarnaOrderData.ShippingAddress.City))
                order.Properties.AddOrUpdate("shipping_city", klarnaOrderData.ShippingAddress.City);
            if (!string.IsNullOrWhiteSpace(klarnaOrderData.ShippingAddress.Phone))
                order.Properties.AddOrUpdate("shipping_phone", klarnaOrderData.ShippingAddress.Phone);

            // Order was passed as reference and updated. Saving it now.
            order.Save();
        }

        public override string ProcessRequest(Order order, HttpRequest request, IDictionary<string, string> settings)
        {
            var response = "";

            try
            {
                order.MustNotBeNull("order");
                settings.MustNotBeNull("settings");
                settings.MustContainKey("merchant.id", "settings");
                settings.MustContainKey("sharedSecret", "settings");

                var communicationType = request["communicationType"];
                var client = CreateKlarnaClientFromSettings(settings);

                ICheckoutOrder klarnaOrder = null;
                CheckoutOrderData klarnaOrderData = null;

                if (communicationType == "checkout")
                {
                    settings.MustContainKey("merchant.terms_uri", "settings");
                    settings.MustContainKey("locale", "settings");

                    var currency = CurrencyService.Instance.Get(order.StoreId, order.CurrencyId);
                    if (!Iso4217CurrencyCodes.ContainsKey(currency.IsoCode))
                    {
                        throw new Exception("You must specify an ISO 4217 currency code for the " + currency.Name + " currency");
                    }

                    var merchantTermsUri = settings["merchant.terms_uri"];
                    if (!merchantTermsUri.StartsWith("http"))
                    {
                        var baseUrl = new UriBuilder(HttpContext.Current.Request.Url.Scheme, HttpContext.Current.Request.Url.Host, HttpContext.Current.Request.Url.Port).Uri;
                        merchantTermsUri = new Uri(baseUrl, merchantTermsUri).AbsoluteUri;
                    }

                    klarnaOrderData = new CheckoutOrderData
                    {
                        PurchaseCountry = CountryService.Instance.Get(order.StoreId, order.PaymentInformation.CountryId).RegionCode,
                        PurchaseCurrency = currency.IsoCode,
                        Locale = settings["locale"],
                        OrderAmount = (int)(order.TotalPrice.Value.WithVat * 100M),
                        OrderTaxAmount = (int)(order.TotalPrice.Value.Vat * 100M),
                        BillingAddress = new Address
                        {
                            Email = order.PaymentInformation.Email,
                            PostalCode = settings.ContainsKey("zipCodePropAlias") && !string.IsNullOrWhiteSpace(settings["zipCodePropAlias"])
                                ? order.Properties[settings["zipCodePropAlias"]]
                                : null
                        },
                        MerchantUrls = new MerchantUrls
                        {
                            Terms = new Uri(merchantTermsUri),
                            Checkout = new Uri(request.UrlReferrer.ToString()),
                            Confirmation = new Uri(order.Properties["teaCommerceContinueUrl"]),
                            Push = new Uri(order.Properties["teaCommerceCallbackUrl"])
                        },
                        OrderLines = new List<KlarnaOrderLine>()
                        {
                            new KlarnaOrderLine
                            {
                                Reference = settings.ContainsKey( "totalSku" ) ? settings[ "totalSku" ] : "0001",
                                Name = settings.ContainsKey( "totalName" ) ? settings[ "totalName" ] : "Total",
                                Quantity = 1,
                                UnitPrice =  (int) ( order.TotalPrice.Value.WithVat * 100M ),
                                TaxRate = (int) ( order.VatRate.Value * 10000M ),
                                TotalAmount = (int) ( order.TotalPrice.Value.WithVat * 100M ),
                                TotalTaxAmount = (int) ( order.TotalPrice.Value.Vat * 100M ),
                            }
                        },
                        MerchantReference1 = order.CartNumber
                    };

                    // Check if the order has a Klarna location URI property - then we try and update the order
                    var klarnaOrderId = order.TransactionInformation.TransactionId;
                    if (!string.IsNullOrEmpty(klarnaOrderId))
                    {
                        try
                        {
                            klarnaOrder = client.NewCheckoutOrder(klarnaOrderId);
                            klarnaOrder.Update(klarnaOrderData);
                            klarnaOrderData = klarnaOrder.Fetch();
                        }
                        catch (Exception)
                        {
                            // Klarna cart session has expired and we make sure to remove the Klarna location URI property
                            klarnaOrder = null;
                        }
                    }

                    // If no Klarna order was found to update or the session expired - then create new Klarna order
                    if (klarnaOrder == null)
                    {
                        klarnaOrder = client.NewCheckoutOrder();
                        klarnaOrder.Create(klarnaOrderData);
                        klarnaOrderData = klarnaOrder.Fetch();

                        order.TransactionInformation.TransactionId = klarnaOrderData.OrderId;
                        order.TransactionInformation.PaymentState = PaymentState.Initialized;
                        order.Save();
                    }
                }
                else if (communicationType == "confirmation")
                {
                    // Get Klarna order id
                    var klarnaOrderId = order.TransactionInformation.TransactionId;
                    if (!string.IsNullOrEmpty(klarnaOrderId))
                    {
                        // Fetch and show confirmation page if status is not checkout_incomplete
                        klarnaOrder = client.NewCheckoutOrder(klarnaOrderId);
                        klarnaOrderData = klarnaOrder.Fetch();

                        if (klarnaOrderData.Status == "checkout_complete")
                        {
                            order.TransactionInformation.PaymentState = PaymentState.Authorized;
                            order.TransactionInformation.AmountAuthorized = new Amount(order.TotalPrice.Value.WithVat, CurrencyService.Instance.Get(order.StoreId, order.CurrencyId));
                            order.Save();
                        }
                        else
                        {
                            throw new Exception("Confirmation page reached without a Klarna order that is finished");
                        }
                    }
                }

                // Get the JavaScript snippet from the Klarna order
                if (klarnaOrderData != null && !string.IsNullOrWhiteSpace(klarnaOrderData.HtmlSnippet))
                {
                    response = klarnaOrderData.HtmlSnippet;
                }
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<Klarna3>("Klarna(" + order.CartNumber + ") - ProcessRequest", exp);
            }

            return response;
        }

        public override string GetLocalizedSettingsKey(string settingsKey, CultureInfo culture)
        {
            switch (settingsKey)
            {
                case "paymentFormUrl":
                    return settingsKey + "<br/><small>e.g. /payment/</small>";
                case "merchant.confirmation_uri":
                    return settingsKey + "<br/><small>e.g. /continue/</small>";
                case "merchant.terms_uri":
                    return settingsKey + "<br/><small>e.g. /terms/</small>";
                case "testMode":
                    return settingsKey + "<br/><small>1 = true; 0 = false</small>";
                default:
                    return base.GetLocalizedSettingsKey(settingsKey, culture);
            }
        }

        private KlarnaClient CreateKlarnaClientFromSettings(IDictionary<string, string> settings)
        {
            return new KlarnaClient(settings["merchant.id"], 
                settings["sharedSecret"], 
                settings.ContainsKey("testMode") && settings["testMode"] == "1" 
                    ? KlarnaClient.EuTestBaseUrl 
                    : KlarnaClient.EuBaseUrl);
        }
    }
}
