using Stripe;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
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
        public override bool SupportsRetrievalOfPaymentStatus { get { return true; } }
        public override bool SupportsCapturingOfPayment { get { return true; } }
        public override bool SupportsRefundOfPayment { get { return true; } }
        public override bool SupportsCancellationOfPayment { get { return true; } }

        public override IDictionary<string, string> DefaultSettings
        {
            get
            {
                return base.DefaultSettings
                    .Union(new Dictionary<string, string> {
                        { "capture", "false" },
                        { "send_stripe_receipt", "false" }
                    })
                    .ToDictionary(k => k.Key, v => v.Value);
            }
        }

        public override string GetCartNumber(HttpRequest request, IDictionary<string, string> settings)
        {
            var cartNumber = "";

            try
            {
                request.MustNotBeNull("request");
                settings.MustNotBeNull("settings");

                // If in test mode, write out the form data to a text file
                if (settings.ContainsKey("mode") && settings["mode"] == "test")
                {
                    LogRequest<Stripe>(request, logPostData: true);
                }

                var stripeEvent = GetStripeEvent(request);

                // We are only interested in charge events
                if (stripeEvent != null && stripeEvent.Type.StartsWith("charge."))
                {
                    var stripeCharge = Mapper<Charge>.MapFromJson(stripeEvent.Data.Object.ToString());

                    // Get cart number from meta data or description (legacy)
                    cartNumber = stripeCharge.Metadata != null && stripeCharge.Metadata.ContainsKey("cartNumber")
                        ? stripeCharge.Metadata["cartNumber"] 
                        : stripeCharge.Description;
                }
                else
                {
                    HttpContext.Current.Response.StatusCode = (int)HttpStatusCode.BadRequest;
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
            CallbackInfo callbackInfo = null;

            try
            {
                order.MustNotBeNull("order");
                request.MustNotBeNull("request");
                settings.MustNotBeNull("settings");
                settings.MustContainKey("mode", "settings");
                settings.MustContainKey(settings["mode"] + "_secret_key", "settings");

                // If in test mode, write out the form data to a text file
                if (settings.ContainsKey("mode") && settings["mode"] == "test")
                {
                    LogRequest<Stripe>(request, logPostData: true);
                }

                var apiKey = settings[settings["mode"] + "_secret_key"];
                var capture = settings["capture"].TryParse<bool>() ?? false;

                // TODO: Create a flag to decide whether to create customers
                // or maybe we should create them if order.customerId is set?
                // var customerService = new CustomerService(apiKey);

                var chargeService = new ChargeService(apiKey);

                var chargeOptions = new ChargeCreateOptions {
                    Amount = DollarsToCents(order.TotalPrice.Value.WithVat),
                    Currency = CurrencyService.Instance.Get(order.StoreId, order.CurrencyId).IsoCode,
                    SourceId = request.Form["stripeToken"],
                    Description = $"{order.CartNumber} - {order.PaymentInformation.Email}",
                    Capture = capture,
                    Metadata = new Dictionary<string, string>
                    {
                        { "orderId", order.Id.ToString() },
                        { "cartNumber", order.CartNumber }
                    }
                };

                if (settings.ContainsKey("send_stripe_receipt") && settings["send_stripe_receipt"] == "true")
                {
                    chargeOptions.ReceiptEmail = order.PaymentInformation.Email;
                }

                var charge = chargeService.Create(chargeOptions);

                // Check payment ammount
                if (charge.Amount != chargeOptions.Amount)
                {
                    throw new StripeException(HttpStatusCode.Unauthorized,
                        new StripeError {
                            ChargeId = charge.Id,
                            Code = "TEA_ERROR",
                            Message = "Payment ammount differs from authorized payment ammount"
                        }, "Payment ammount differs from authorized payment ammount");
                }

                // Check paid status
                if (!charge.Paid)
                {
                    throw new StripeException(HttpStatusCode.Unauthorized,
                        new StripeError {
                            ChargeId = charge.Id,
                            Code = "TEA_ERROR",
                            Message = "Payment failed"
                        }, "Payment failed");
                }

                callbackInfo = new CallbackInfo(CentsToDollars(charge.Amount), charge.Id, capture ? PaymentState.Captured : PaymentState.Authorized);

            }
            catch (StripeException e)
            {
                ReturnToPaymentFormWithException(order, request, e);
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<Stripe>("Stripe(" + order.CartNumber + ") - ProcessCallback", exp);
            }

            return callbackInfo;
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

                // Stripe supports webhooks
                var stripeEvent = GetStripeEvent(request);

                if (stripeEvent.Type.StartsWith("charge."))
                {
                    var charge = Mapper<Charge>.MapFromJson(stripeEvent.Data.Object.ToString());

                    var paymentState = GetPaymentState(charge);
                    if (order.TransactionInformation.PaymentState != paymentState)
                    {
                        order.TransactionInformation.TransactionId = charge.Id;
                        order.TransactionInformation.PaymentState = paymentState;
                        order.Save();
                    }
                }

            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<Stripe>("Stripe(" + order.CartNumber + ") - ProcessRequest", exp);
            }

            return response;
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
                case "capture":
                    return settingsKey + "<br/><small>Flag indicating if a payment should be captured instantly - true/false.</small>";
                case "send_stripe_receipt":
                    return settingsKey + "<br/><small>Flag indicating whether to send a Stripe receipt to the customer - true/false. Receipts are only sent when in live mode.</small>";
                default:
                    return base.GetLocalizedSettingsKey(settingsKey, culture);
            }
        }

        protected PaymentState GetPaymentState(Charge charge)
        {
            PaymentState paymentState = PaymentState.Initialized;

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
    }
}
