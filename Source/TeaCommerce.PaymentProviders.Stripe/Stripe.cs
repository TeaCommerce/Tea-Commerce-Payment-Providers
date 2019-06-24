using Newtonsoft.Json;
using Stripe;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;
using Order = TeaCommerce.Api.Models.Order;

namespace TeaCommerce.PaymentProviders.Inline
{
    [PaymentProvider("Stripe - inline")]
    public class Stripe : BaseStripeProvider
    {
        public override bool SupportsRetrievalOfPaymentStatus => true;
        public override bool SupportsRefundOfPayment => true;
        public override bool SupportsCapturingOfPayment => true;
        public override bool SupportsCancellationOfPayment => true;

        public override bool FinalizeAtContinueUrl => false;

        public override IDictionary<string, string> DefaultSettings
        {
            get
            {
                return base.DefaultSettings
                    .Union(new Dictionary<string, string> {
                        { "send_stripe_receipt", "false" }
                    })
                    .ToDictionary(k => k.Key, v => v.Value);
            }
        }

        public override PaymentHtmlForm GenerateHtmlForm(Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, string teaCommerceCommunicationUrl, IDictionary<string, string> settings)
        {
            var htmlForm =  base.GenerateHtmlForm(order, teaCommerceContinueUrl, teaCommerceCancelUrl, teaCommerceCallBackUrl, teaCommerceCommunicationUrl, settings);

            htmlForm.InputFields["api_key"] = settings[settings["mode"] + "_public_key"];

            return htmlForm;
        }

        public override string GetCartNumber(HttpRequest request, IDictionary<string, string> settings)
        {
            var cartNumber = "";

            try
            {
                request.MustNotBeNull("request");
                settings.MustNotBeNull("settings");
                settings.MustContainKey("mode", "settings");
                settings.MustContainKey(settings["mode"] + "_secret_key", "settings");
                settings.MustContainKey(settings["mode"] + "_webhook_secret", "settings");

                // If in test mode, write out the form data to a text file
                if (settings.ContainsKey("mode") && settings["mode"] == "test")
                {
                    LogRequest<Stripe>(request, logPostData: true);
                }

                var apiKey = settings[settings["mode"] + "_secret_key"];

                var webhookSecret = settings[settings["mode"] + "_webhook_secret"];
                var stripeEvent = GetWebhookStripeEvent(request, webhookSecret);
                if (stripeEvent != null && stripeEvent.Type.StartsWith("payment_intent."))
                {
                    var stripeIntent = (PaymentIntent)stripeEvent.Data.Object;

                    // Get cart number from meta data
                    if (stripeIntent?.Metadata != null && stripeIntent.Metadata.ContainsKey("cartNumber"))
                    {
                        cartNumber = stripeIntent.Metadata["cartNumber"];
                    }
                }
                else if (stripeEvent != null && stripeEvent.Type.StartsWith("charge."))
                {
                    var stripeCharge = (Charge)stripeEvent.Data.Object;
                    var stripeIntent = stripeCharge.PaymentIntent;

                    if (stripeIntent == null && !string.IsNullOrWhiteSpace(stripeCharge.PaymentIntentId))
                    {
                        stripeIntent = new PaymentIntentService(apiKey).Get(stripeCharge.PaymentIntentId);
                    }

                    // Get cart number from meta data
                    if (stripeIntent?.Metadata != null && stripeIntent.Metadata.ContainsKey("cartNumber"))
                    {
                        cartNumber = stripeIntent.Metadata["cartNumber"];
                    }
                    else if (stripeCharge?.Metadata != null && stripeCharge.Metadata.ContainsKey("cartNumber"))
                    {
                        cartNumber = stripeCharge.Metadata["cartNumber"];
                    }
                }
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<Stripe>("Stripe - GetCartNumber", exp);
            }

            return cartNumber;
        }

        public override CallbackInfo ProcessCallback(Order order, HttpRequest request, IDictionary<string, string> settings)
        {
            // Because we need more fine grained control over the callback process
            // we actually perform all the callback functionality from within the
            // ProcessRequest method instead.
            return null;
        }

        public override string ProcessRequest(Order order, HttpRequest request, IDictionary<string, string> settings)
        {
            var response = "";

            try
            {
                order.MustNotBeNull("order");
                request.MustNotBeNull("request");
                settings.MustNotBeNull("settings");

                // If in test mode, write out the form data to a text file
                if (settings.ContainsKey("mode") && settings["mode"] == "test")
                {
                    LogRequest<Stripe>(request, logPostData: true);
                }

                if (request.QueryString["action"] == "capture")
                {
                    return ProcessCaptureRequest(order, request, settings);
                }
                else
                {
                    // No action defined so assume it's a webhook request
                    return ProcessWebhookRequest(order, request, settings);
                }
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<Stripe>("Stripe(" + order.CartNumber + ") - ProcessRequest", exp);
            }

            return response;
        }

        private string ProcessCaptureRequest(Order order, HttpRequest request, IDictionary<string, string> settings)
        {
            var apiKey = settings[settings["mode"] + "_secret_key"];

            try
            {
                var intentService = new PaymentIntentService(apiKey);
                var intentOptions = new PaymentIntentCreateOptions
                {
                    Amount = DollarsToCents(order.TotalPrice.Value.WithVat),
                    Currency = CurrencyService.Instance.Get(order.StoreId, order.CurrencyId).IsoCode,
                    Description = $"{order.CartNumber} - {order.PaymentInformation.Email}",
                    Metadata = new Dictionary<string, string>
                    {
                        { "orderId", order.Id.ToString() },
                        { "cartNumber", order.CartNumber }
                    }
                };

                if (settings.ContainsKey("send_stripe_receipt") && settings["send_stripe_receipt"] == "true")
                {
                    intentOptions.ReceiptEmail = order.PaymentInformation.Email;
                }

                var intent = intentService.Create(intentOptions);

                order.Properties.AddOrUpdate(new CustomProperty("stripePaymentIntentId", intent.Id) { ServerSideOnly = true });
                order.Save();

                return JsonConvert.SerializeObject(new
                {
                    payment_intent_client_secret = intent.ClientSecret
                });
            }
            catch (Exception ex)
            {
                LoggingService.Instance.Error<Stripe>("Stripe(" + order.CartNumber + ") - ProcessCaptureRequest", ex);

                return JsonConvert.SerializeObject(new
                {
                    error = ex.Message
                });
            }
        }

        private string ProcessWebhookRequest(Order order, HttpRequest request, IDictionary<string, string> settings)
        {
            var webhookSecret = settings[settings["mode"] + "_webhook_secret"];
            var stripeEvent = GetWebhookStripeEvent(request, webhookSecret); 

            if (stripeEvent.Type.StartsWith("charge."))
            {
                var charge = (Charge)stripeEvent.Data.Object;
                FinalizeOrUpdateOrder(order, charge);
            }

            return null;
        }

        public override ApiInfo GetStatus(Order order, IDictionary<string, string> settings)
        {
            try
            {
                order.MustNotBeNull("order");
                settings.MustNotBeNull("settings");
                settings.MustContainKey("mode", "settings");
                settings.MustContainKey(settings["mode"] + "_secret_key", "settings");

                // If there is no transction id yet, just return null
                if (order.TransactionInformation.TransactionId == null)
                    return null;

                var apiKey = settings[settings["mode"] + "_secret_key"];

                var chargeService = new ChargeService(apiKey);
                var charge = chargeService.Get(order.TransactionInformation.TransactionId);

                return new ApiInfo(charge.Id, GetPaymentState(charge));
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<Stripe>("Stripe(" + order.OrderNumber + ") - GetStatus", exp);
            }

            return null;
        }

        public override ApiInfo CapturePayment(Order order, IDictionary<string, string> settings)
        {
            try
            {
                order.MustNotBeNull("order");
                settings.MustNotBeNull("settings");
                settings.MustContainKey("mode", "settings");
                settings.MustContainKey(settings["mode"] + "_secret_key", "settings");

                // If there is no transction id yet, just return null
                if (order.TransactionInformation.TransactionId == null)
                    return null;

                var apiKey = settings[settings["mode"] + "_secret_key"];

                var chargeService = new ChargeService(apiKey);

                var captureOptions = new ChargeCaptureOptions() {
                    Amount = DollarsToCents(order.TransactionInformation.AmountAuthorized.Value)
                };

                var charge = chargeService.Capture(order.TransactionInformation.TransactionId, captureOptions);

                return new ApiInfo(charge.Id, GetPaymentState(charge));
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<Stripe>("Stripe(" + order.OrderNumber + ") - GetStatus", exp);
            }

            return null;
        }

        public override ApiInfo RefundPayment(Order order, IDictionary<string, string> settings)
        {
            try
            {
                order.MustNotBeNull("order");
                settings.MustNotBeNull("settings");
                settings.MustContainKey("mode", "settings");
                settings.MustContainKey(settings["mode"] + "_secret_key", "settings");

                // If there is no transction id yet, just return null
                if (order.TransactionInformation.TransactionId == null)
                    return null;

                var apiKey = settings[settings["mode"] + "_secret_key"];

                var refundService = new RefundService(apiKey);

                var refundCreateOptions = new RefundCreateOptions() {
                    ChargeId = order.TransactionInformation.TransactionId
                };

                var refund = refundService.Create(refundCreateOptions);
                var charge = refund.Charge;

                if (charge == null)
                {
                    var chargeService = new ChargeService(apiKey);
                    charge = chargeService.Get(order.TransactionInformation.TransactionId);
                }

                return new ApiInfo(charge.Id, GetPaymentState(charge));
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<Stripe>("Stripe(" + order.OrderNumber + ") - RefundPayment", exp);
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
                case "send_stripe_receipt":
                    return settingsKey + "<br/><small>Flag indicating whether to send a Stripe receipt to the customer - true/false. Receipts are only sent when in live mode.</small>";
                default:
                    return base.GetLocalizedSettingsKey(settingsKey, culture);
            }
        }

        protected PaymentState GetPaymentState(Charge charge)
        {
            PaymentState paymentState = PaymentState.Initialized;

            if (charge == null)
                return paymentState;

            if (charge.Paid)
            {
                paymentState = PaymentState.Authorized;

                if (charge.Captured != null && charge.Captured.Value)
                {
                    paymentState = PaymentState.Captured;

                    if (charge.Refunded)
                    {
                        paymentState = PaymentState.Refunded;
                    }
                }
                else
                {
                    if (charge.Refunded)
                    {
                        paymentState = PaymentState.Cancelled;
                    }
                }
            }

            return paymentState;
        }

        protected void FinalizeOrUpdateOrder(Order order, Charge charge)
        {
            var amount = CentsToDollars(charge.Amount);
            var paymentState = GetPaymentState(charge);

            if (!order.IsFinalized && (paymentState == PaymentState.Authorized || paymentState == PaymentState.Captured))
            {
                order.Finalize(amount, charge.Id, paymentState);
            }
            else if (order.TransactionInformation.PaymentState != paymentState)
            {
                var currency = CurrencyService.Instance.Get(order.StoreId, order.CurrencyId);
                order.TransactionInformation.AmountAuthorized = new Amount(amount, currency);
                order.TransactionInformation.TransactionId = charge.Id;
                order.TransactionInformation.PaymentState = paymentState;
                order.Save();
            }
        }
    }
}
