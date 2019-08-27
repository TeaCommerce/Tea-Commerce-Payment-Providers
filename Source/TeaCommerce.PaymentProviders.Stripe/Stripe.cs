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

[assembly: PreApplicationStartMethod(typeof(TeaCommerce.PaymentProviders.Inline.Stripe), "OnStartup")]

namespace TeaCommerce.PaymentProviders.Inline
{
    [PaymentProvider(PROVIDER_ALIAS)]
    public class Stripe : BaseStripeProvider
    {
        public const string PROVIDER_ALIAS = "Stripe - inline";

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
                        { "capture", "true" },
                        { "send_stripe_receipt", "false" }
                    })
                    .ToDictionary(k => k.Key, v => v.Value);
            }
        }

        public override PaymentHtmlForm GenerateHtmlForm(Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, string teaCommerceCommunicationUrl, IDictionary<string, string> settings)
        {
            var htmlForm = base.GenerateHtmlForm(order, teaCommerceContinueUrl, teaCommerceCancelUrl, teaCommerceCallBackUrl, teaCommerceCommunicationUrl, settings);

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

                ConfigureStripe(apiKey);

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
                        stripeIntent = new PaymentIntentService().Get(stripeCharge.PaymentIntentId);
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

            ConfigureStripe(apiKey);

            try
            {
                var capture = settings.ContainsKey("capture") && settings["capture"].Trim().ToLower() == "true";

                var intentService = new PaymentIntentService();
                var intentOptions = new PaymentIntentCreateOptions
                {
                    Amount = DollarsToCents(order.TotalPrice.Value.WithVat),
                    Currency = CurrencyService.Instance.Get(order.StoreId, order.CurrencyId).IsoCode,
                    Description = $"{order.CartNumber} - {order.PaymentInformation.Email}",
                    Metadata = new Dictionary<string, string>
                    {
                        { "orderId", order.Id.ToString() },
                        { "cartNumber", order.CartNumber }
                    },
                    CaptureMethod = capture ? "automatic" : "manual"
                };

                if (settings.ContainsKey("send_stripe_receipt") && settings["send_stripe_receipt"] == "true")
                {
                    intentOptions.ReceiptEmail = order.PaymentInformation.Email;
                }

                var intent = intentService.Create(intentOptions);

                order.Properties.Add(new CustomProperty("stripePaymentIntentId", intent.Id) { ServerSideOnly = true });
                order.TransactionInformation.PaymentState = PaymentState.Initialized;
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
            var apiKey = settings[settings["mode"] + "_secret_key"];
            var webhookSecret = settings[settings["mode"] + "_webhook_secret"];

            ConfigureStripe(apiKey);

            var stripeEvent = GetWebhookStripeEvent(request, webhookSecret);
            if (stripeEvent.Type == "payment_intent.amount_capturable_updated")  // Occurs when payments are not auto captured and funds are authorized
            {
                var paymentIntent = (PaymentIntent)stripeEvent.Data.Object;

                FinalizeOrUpdateOrder(order, paymentIntent);
            }
            else if (stripeEvent.Type.StartsWith("charge."))
            {
                var charge = (Charge)stripeEvent.Data.Object;

                if (!string.IsNullOrWhiteSpace(charge.PaymentIntentId))
                {
                    var paymentIntentService = new PaymentIntentService();
                    var paymentIntentGetOptions = new PaymentIntentGetOptions { };
                    var paymentIntent = paymentIntentService.Get(charge.PaymentIntentId, paymentIntentGetOptions);

                    FinalizeOrUpdateOrder(order, paymentIntent);
                }
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

                var apiKey = settings[settings["mode"] + "_secret_key"];

                ConfigureStripe(apiKey);

                // See if we have a payment intent ID to work from
                var paymentIntentId = order.Properties["stripePaymentIntentId"];
                if (!string.IsNullOrWhiteSpace(paymentIntentId))
                {
                    var paymentIntentService = new PaymentIntentService();
                    var paymentIntentGetOptions = new PaymentIntentGetOptions
                    {
                        Expand = new List<string> { "Charges" }
                    };
                    var paymentIntent = paymentIntentService.Get(paymentIntentId, paymentIntentGetOptions);
                    return new ApiInfo(GetTransactionId(paymentIntent), GetPaymentState(paymentIntent));
                }

                // No payment intent, so look for a charge ID
                if (!string.IsNullOrWhiteSpace(order.TransactionInformation.TransactionId))
                {
                    var chargeService = new ChargeService();
                    var charge = chargeService.Get(order.TransactionInformation.TransactionId);
                    return new ApiInfo(GetTransactionId(charge), GetPaymentState(charge));
                }
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

                // We can only capture a payment intent, so make sure we have one
                // otherwise there is nothing we can do
                var paymentIntentId = order.Properties["stripePaymentIntentId"];
                if (string.IsNullOrWhiteSpace(paymentIntentId))
                    return null;

                var apiKey = settings[settings["mode"] + "_secret_key"];

                ConfigureStripe(apiKey);

                var paymentIntentService = new PaymentIntentService();
                var paymentIntentOptions = new PaymentIntentCaptureOptions
                {
                    Expand = new List<string> { "Charges" },
                    AmountToCapture = DollarsToCents(order.TransactionInformation.AmountAuthorized.Value),
                };
                var paymentIntent = paymentIntentService.Capture(paymentIntentId, paymentIntentOptions);

                return new ApiInfo(GetTransactionId(paymentIntent), GetPaymentState(paymentIntent));
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

                // We can only refund a captured charge, so make sure we have one
                // otherwise there is nothing we can do
                if (order.TransactionInformation.TransactionId == null)
                    return null;

                var apiKey = settings[settings["mode"] + "_secret_key"];

                ConfigureStripe(apiKey);

                var refundService = new RefundService();
                var refundCreateOptions = new RefundCreateOptions() {
                    Expand = new List<string> { "Charge" },
                    ChargeId = order.TransactionInformation.TransactionId
                };

                var refund = refundService.Create(refundCreateOptions);
                var charge = refund.Charge;

                if (charge == null)
                {
                    var chargeService = new ChargeService();
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
            try
            {
                order.MustNotBeNull("order");
                settings.MustNotBeNull("settings");
                settings.MustContainKey("mode", "settings");
                settings.MustContainKey(settings["mode"] + "_secret_key", "settings");

                // If there is a transaction ID (a charge) then it's too late to cancel
                // so we just refund it
                if (order.TransactionInformation.TransactionId != null)
                    return RefundPayment(order, settings);

                // If there is not transaction id, then try canceling a payment intent
                var stripePaymentIntentId = order.Properties["stripePaymentIntentId"];
                if (!string.IsNullOrWhiteSpace(stripePaymentIntentId))
                {
                    var apiKey = settings[settings["mode"] + "_secret_key"];

                    ConfigureStripe(apiKey);

                    var service = new PaymentIntentService();
                    var options = new PaymentIntentCancelOptions
                    {
                        Expand = new List<string> { "Charges" },
                    };
                    var intent = service.Cancel(stripePaymentIntentId, options);

                    return new ApiInfo(GetTransactionId(intent), GetPaymentState(intent));
                }

            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<Stripe>("Stripe(" + order.OrderNumber + ") - RefundPayment", exp);
            }

            return null;
        }

        public override string GetLocalizedSettingsKey(string settingsKey, CultureInfo culture)
        {
            switch (settingsKey)
            {
                case "capture":
                    return settingsKey + "<br/><small>Flag indicating whether to immediately capture the payment, or whether to just authorize the payment for later (manual) capture. - true/false.</small>";
                case "send_stripe_receipt":
                    return settingsKey + "<br/><small>Flag indicating whether to send a Stripe receipt to the customer - true/false. Receipts are only sent when in live mode.</small>";
                default:
                    return base.GetLocalizedSettingsKey(settingsKey, culture);
            }
        }

        protected string GetTransactionId(PaymentIntent paymentIntent)
        {
            return (paymentIntent.Charges?.Data?.Count ?? 0) > 0
                ? GetTransactionId(paymentIntent.Charges.Data[0])
                : null;
        }

        protected string GetTransactionId(Charge charge)
        {
            return charge?.Id;
        }

        protected PaymentState GetPaymentState(PaymentIntent paymentIntent) {

            // Possible PaymentIntent statuses:
            // - requires_payment_method
            // - requires_confirmation
            // - requires_action
            // - processing
            // - requires_capture
            // - canceled
            // - succeeded

            if (paymentIntent.Status == "canceled")
                return PaymentState.Cancelled;

            if (paymentIntent.Status == "requires_capture")
                return PaymentState.Authorized;

            if (paymentIntent.Status == "succeeded")
            {
                if (paymentIntent.Charges.Data.Any())
                {
                    return GetPaymentState(paymentIntent.Charges.Data[0]);
                }
                else
                {
                    return PaymentState.Captured;
                }
            }

            return PaymentState.Initialized;
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

        protected void FinalizeOrUpdateOrder(Order order, PaymentIntent paymentIntent)
        {
            var amount = CentsToDollars(paymentIntent.Amount.Value);
            var transactionId = GetTransactionId(paymentIntent);
            var paymentState = GetPaymentState(paymentIntent);

            if (!order.IsFinalized && (paymentState == PaymentState.Authorized || paymentState == PaymentState.Captured))
            {
                order.Finalize(amount, transactionId, paymentState);
            }
            else if (order.TransactionInformation.PaymentState != paymentState)
            {
                var currency = CurrencyService.Instance.Get(order.StoreId, order.CurrencyId);
                order.TransactionInformation.AmountAuthorized = new Amount(amount, currency);
                order.TransactionInformation.TransactionId = transactionId;
                order.TransactionInformation.PaymentState = paymentState;
                order.Save();
            }
        }

        public static void OnStartup()
        {
            var webhookEvents = new[] {
                "charge.succeeded",
                "charge.refunded",
                "charge.failed",
                "charge.captured",
                "payment_intent.amount_capturable_updated"
            };

            Api.Notifications.NotificationCenter.PaymentMethod.Created += (paymentMethod, args) =>
            {
                if (paymentMethod.PaymentProviderAlias == PROVIDER_ALIAS)
                    EnsureWebhookEndpointFor(paymentMethod, webhookEvents);
            };

            Api.Notifications.NotificationCenter.PaymentMethod.Updated += (paymentMethod, args) =>
            {
                if (paymentMethod.PaymentProviderAlias == PROVIDER_ALIAS)
                    EnsureWebhookEndpointFor(paymentMethod, webhookEvents);
            };

            // TODO: Remove webhook endpoint?
        }
    }
}
