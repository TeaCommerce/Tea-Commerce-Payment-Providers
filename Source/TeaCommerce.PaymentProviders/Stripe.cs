/*
 * FileName: Stripe.cs
 * Description: A payment provider for handling payments via Stripe
 * Author: Matt Brailsford (@mattbrailsford)
 * Create Date: 2013-09-12
 */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web;
using Stripe;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;

namespace TeaCommerce.PaymentProviders
{
    [PaymentProvider("Stripe")]
    public class Stripe : APaymentProvider
    {
        public override bool FinalizeAtContinueUrl { get { return true; } }

        public override string DocumentationLink { get { return "https://stripe.com/docs"; } }

        public override bool SupportsRetrievalOfPaymentStatus { get { return true; } }
        public override bool SupportsCapturingOfPayment { get { return true; } }
        public override bool SupportsRefundOfPayment { get { return true; } }

        public override IDictionary<string, string> DefaultSettings
        {
            get
            {
                return new Dictionary<string, string>
                {
                    { "form_url", "" },
                    { "continue_url", "" },
                    { "cancel_url", "" },
                    { "test_secret_key", "" },
                    { "test_public_key", "" },
                    { "live_secret_key", "" },
                    { "live_public_key", "" },
                    { "mode", "test" },
                    { "capture", "true" } 
                };
            }
        }

        public override PaymentHtmlForm GenerateHtmlForm(Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl,
            string teaCommerceCallBackUrl, IDictionary<string, string> settings)
        {
            var form = new PaymentHtmlForm
            {
                Action = settings["form_url"]
            };

            form.InputFields.Add("api_key", settings[settings["mode"] + "_public_key"]);
            form.InputFields.Add("continue_url", teaCommerceContinueUrl);
            form.InputFields.Add("cancel_url", teaCommerceCancelUrl);

            return form;
        }

        public override string GetContinueUrl(Order order, IDictionary<string, string> settings)
        {
            settings.MustNotBeNull("settings");
            settings.MustContainKey("continue_url", "settings");

            return settings["continue_url"];
        }

        public override string GetCancelUrl(Order order, IDictionary<string, string> settings)
        {
            settings.MustNotBeNull("settings");
            settings.MustContainKey("cancel_url", "settings");

            return settings["cancel_url"];
        }

        public override CallbackInfo ProcessCallback(Order order, HttpRequest request,
            IDictionary<string, string> settings)
        {
            try
            {
                order.MustNotBeNull("order");
                request.MustNotBeNull("request");
                settings.MustNotBeNull("settings");

                var orderCurrency = CurrencyService.Instance.Get(order.StoreId,
                    order.CurrencyId);

                var stripeApiKey = settings[settings["mode"] + "_secret_key"];
                var stripeToken = request.Form["stripeToken"];

                bool capture;
                var chargeOptions = new StripeChargeCreateOptions
                {
                    AmountInCents = (int)(order.TotalPrice.WithVat * 100),
                    Currency = orderCurrency.IsoCode,
                    TokenId = stripeToken,
                    Description = order.Id.ToString(),
                    Capture = bool.TryParse(settings["capture"], out capture) && capture
                };

                var chargeService = new StripeChargeService(stripeApiKey);
                var result = chargeService.Create(chargeOptions);

                if (result.AmountInCents.HasValue &&
                    result.Captured.HasValue && result.Captured.Value)
                {
                    return new CallbackInfo((decimal)result.AmountInCents.Value / 100,
                        result.Id,
                        PaymentState.Captured);
                }

                LoggingService.Instance.Log("Stripe(" + order.CartNumber + ") - Payment not captured");
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Log(exp, "Stripe(" + order.CartNumber + ") - ProcessCallback");
            }

            return null;
        }

        public override ApiInfo GetStatus(Order order, IDictionary<string, string> settings)
        {
            try
            {
                order.MustNotBeNull("order");
                settings.MustNotBeNull("settings");

                var stripeApiKey = settings[settings["mode"] + "_secret_key"];

                var chargeService = new StripeChargeService(stripeApiKey);
                var charge = chargeService.Get(order.TransactionInformation.TransactionId);

                return InternalGetStatus(order, charge);
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Log(exp, "Stripe(" + order.OrderNumber + ") - GetStatus");
            }

            return null;
        }

        public override ApiInfo CapturePayment(Order order, IDictionary<string, string> settings)
        {
            try
            {
                order.MustNotBeNull("order");
                settings.MustNotBeNull("settings");

                var stripeApiKey = settings[settings["mode"] + "_secret_key"];

                var chargeService = new StripeChargeService(stripeApiKey);
                var charge = chargeService.Capture(order.TransactionInformation.TransactionId,
                    (int)order.TransactionInformation.AmountAuthorized.Value * 100);

                return InternalGetStatus(order, charge);
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Log(exp, "Stripe(" + order.OrderNumber + ") - GetStatus");
            }

            return null;
        }

        public override ApiInfo RefundPayment(Order order, IDictionary<string, string> settings)
        {
            try
            {
                order.MustNotBeNull("order");
                settings.MustNotBeNull("settings");

                var stripeApiKey = settings[settings["mode"] + "_secret_key"];

                var chargeService = new StripeChargeService(stripeApiKey);
                var charge = chargeService.Refund(order.TransactionInformation.TransactionId);

                return InternalGetStatus(order, charge);

            }
            catch (Exception exp)
            {
                LoggingService.Instance.Log(exp, "Stripe(" + order.OrderNumber + ") - RefundPayment");
            }

            return null;
        }

        private ApiInfo InternalGetStatus(Order order, StripeCharge charge)
        {
            var paymentState = PaymentState.Initialized;

            if (charge.Refunded.HasValue && charge.Refunded.Value)
            {
                paymentState = PaymentState.Refunded;
            }
            else if (charge.Captured.HasValue && charge.Captured.Value)
            {
                paymentState = PaymentState.Captured;
            }
            else if (charge.Paid.HasValue && charge.Paid.Value)
            {
                // TODO: Not sure if this check is right. Waiting on feedback
                // from stipe
                paymentState = PaymentState.Authorized;
            }

            return new ApiInfo(charge.Id, paymentState);
        }

        public override string GetLocalizedSettingsKey(string settingsKey, CultureInfo culture)
        {
            switch (settingsKey)
            {
                case "form_url":
                    return settingsKey + "<br/><small>The url of the page with the swipe payment form on.</small>";
                case "continue_url":
                    return settingsKey + "<br/><small>The url to navigate to after payment is processed.</small>";
                case "cancel_url":
                    return settingsKey + "<br/><small>The url to navigate to if the user wants to cancel the payment process.</small>";
                case "test_secret_key":
                    return settingsKey + "<br/><small>Your test stripe secret key.</small>";
                case "test_public_key":
                    return settingsKey + "<br/><small>Your test stripe public key.</small>";
                case "live_secret_key":
                    return settingsKey + "<br/><small>Your live stripe secret key.</small>";
                case "live_public_key":
                    return settingsKey + "<br/><small>Your live stripe public key.</small>";
                case "mode":
                    return settingsKey + "<br/><small>The mode of the provider.<br />Can be either 'test' or 'live'.</small>";
                case "capture":
                    return settingsKey + "<br/><small>Flag to indicate whether payment should be captured instantly.<br />Can be either 'true' or 'false'.</small>";
                default:
                    return base.GetLocalizedSettingsKey(settingsKey, culture);
            }
        }
    }
}
