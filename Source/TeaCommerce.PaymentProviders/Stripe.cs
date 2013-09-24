/*
 * FileName: Stripe.cs
 * Description: A payment provider for handling payments via Stripe
 * Author: Matt Brailsford (@mattbrailsford)
 * Create Date: 2013-09-12
 */
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Web;
using System.Web.Hosting;
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
        public override bool SupportsCancellationOfPayment { get { return true; } }

        public override bool AllowsCallbackWithoutOrderId { get { return true; } }

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

        public override string GetCartNumber(HttpRequest request, IDictionary<string, string> settings)
        {
            var stripeEvent = GetStripeEvent(request);

            if (stripeEvent == null)
            {
                HttpContext.Current.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                return "";
            }

            if (stripeEvent.Type.StartsWith("charge."))
            {
                // We are only interested in charge events
                StripeCharge charge = Mapper<StripeCharge>.MapFromJson(stripeEvent.Data.Object.ToString());
                return charge.Description;
            }

            return "";
        }

        public override CallbackInfo ProcessCallback(Order order, HttpRequest request,
            IDictionary<string, string> settings)
        {
            try
            {
                order.MustNotBeNull("order");
                request.MustNotBeNull("request");
                settings.MustNotBeNull("settings");

                // If in test mode, write out the form data to a text file
                if (settings["mode"].ToLower() == "test")
                {
                    using (var sw = new StreamWriter(File.Create(HostingEnvironment.MapPath("~/stripe-callback-data.txt"))))
                    {
                        sw.WriteLine("QueryString:");
                        foreach (string k in request.QueryString.Keys)
                        {
                            sw.WriteLine(k + " : " + request.QueryString[k]);
                        }
                        sw.WriteLine("");
                        sw.WriteLine("-----------------------------------------------------");
                        sw.WriteLine("");
                        sw.WriteLine("Form:");
                        foreach (string k in request.Form.Keys)
                        {
                            sw.WriteLine(k + " : " + request.Form[k]);
                        }
                        sw.Flush();
                    }
                }

                // Check to see if being called as a result of a
                // stripe web hook request or not
                var stripeEvent = GetStripeEvent(request);
                if (stripeEvent == null)
                {
                    // Not a web hook request so process new order
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
                        Description = order.CartNumber,
                        Capture = bool.TryParse(settings["capture"], out capture) && capture
                    };

                    var chargeService = new StripeChargeService(stripeApiKey);
                    var result = chargeService.Create(chargeOptions);

                    if (result.AmountInCents.HasValue &&
                        result.Paid.HasValue && result.Paid.Value)
                    {
                        return new CallbackInfo((decimal)result.AmountInCents.Value / 100,
                            result.Id,
                            result.Captured.HasValue && result.Captured.Value
                                ? PaymentState.Captured
                                : PaymentState.Authorized);
                    }
                }
                else
                {
                    // A web hook request so update existing order
                    switch (stripeEvent.Type)
                    {
                        case "charge.refunded":
                            StripeCharge refundedCharge = Mapper<StripeCharge>.MapFromJson(stripeEvent.Data.Object.ToString());
                            if (order.TransactionInformation.PaymentState != PaymentState.Refunded)
                            {
                                order.TransactionInformation.TransactionId = refundedCharge.Id;
                                order.TransactionInformation.PaymentState = PaymentState.Refunded;
                                order.Save();
                            }
                            break;
                        case "charge.captured":
                            StripeCharge capturedCharge = Mapper<StripeCharge>.MapFromJson(stripeEvent.Data.Object.ToString());
                            if (order.TransactionInformation.PaymentState != PaymentState.Captured)
                            {
                                order.TransactionInformation.TransactionId = capturedCharge.Id;
                                order.TransactionInformation.PaymentState = PaymentState.Captured;
                                order.Save();
                            }
                            break;
                    }
                }
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

        public override ApiInfo CancelPayment(Order order, IDictionary<string, string> settings)
        {
            return RefundPayment(order, settings);
        }

        public override string GetLocalizedSettingsKey(string settingsKey, CultureInfo culture)
        {
            switch (settingsKey)
            {
                case "form_url":
                    return settingsKey + "<br/><small>The url of the page with the stripe payment form on.</small>";
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

        protected ApiInfo InternalGetStatus(Order order, StripeCharge charge)
        {
            var paymentState = PaymentState.Initialized;

            if (charge.Paid.HasValue && charge.Paid.Value)
            {
                paymentState = PaymentState.Authorized;

                if (charge.Captured.HasValue && charge.Captured.Value)
                {
                    paymentState = PaymentState.Captured;

                    if (charge.Refunded.HasValue && charge.Refunded.Value)
                    {
                        paymentState = PaymentState.Refunded;
                    }
                }
                else
                {
                    if (charge.Refunded.HasValue && charge.Refunded.Value)
                    {
                        paymentState = PaymentState.Cancelled;
                    }
                }
            }

            return new ApiInfo(charge.Id, paymentState);
        }

        protected StripeEvent GetStripeEvent(HttpRequest request)
        {
            if (HttpContext.Current.Items["TC_StripeEvent"] != null)
                return (StripeEvent)HttpContext.Current.Items["TC_StripeEvent"];

            if (request.InputStream.CanSeek)
                request.InputStream.Seek(0, SeekOrigin.Begin);

            var json = new StreamReader(request.InputStream).ReadToEnd();

            try
            {
                var stripEvent = StripeEventUtility.ParseEvent(json);

                HttpContext.Current.Items["TC_StripeEvent"] = stripEvent;

                return stripEvent;
            }
            catch (Exception ex)
            {
                return null;
            }
        }
    }
}
