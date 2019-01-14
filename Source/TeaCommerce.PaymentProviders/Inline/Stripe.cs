using Stripe;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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
    public class Stripe : APaymentProvider
    {
        public override string DocumentationLink { get { return "https://stripe.com/docs"; } }

        public override bool SupportsRetrievalOfPaymentStatus { get { return true; } }
        public override bool SupportsCapturingOfPayment { get { return true; } }
        public override bool SupportsRefundOfPayment { get { return true; } }
        public override bool SupportsCancellationOfPayment { get { return true; } }

        public override bool FinalizeAtContinueUrl { get { return true; } }

        public override IDictionary<string, string> DefaultSettings
        {
            get
            {
                return new Dictionary<string, string> {
                    { "form_url", "" },
                    { "continue_url", "" },
                    { "cancel_url", "" },
                    { "capture", "false" },
                    { "billing_address_line1_property_alias", "streetAddress" },
                    { "billing_address_line2_property_alias", "" },
                    { "billing_city_property_alias", "city" },
                    { "billing_state_property_alias", "" },
                    { "billing_zip_code_property_alias", "zipCode" },
                    { "test_secret_key", "" },
                    { "test_public_key", "" },
                    { "live_secret_key", "" },
                    { "live_public_key", "" },
                    { "mode", "test" },
                };
            }
        }

        public override PaymentHtmlForm GenerateHtmlForm(Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, string teaCommerceCommunicationUrl, IDictionary<string, string> settings)
        {
            order.MustNotBeNull("order");
            settings.MustNotBeNull("settings");
            settings.MustContainKey("form_url", "settings");
            settings.MustContainKey("mode", "settings");
            settings.MustContainKey(settings["mode"] + "_public_key", "settings");

            var htmlForm = new PaymentHtmlForm {
                Action = settings["form_url"]
            };

            var settingsToExclude = new[] {
                "form_url",
                "capture",
                "billing_address_line1_property_alias",
                "billing_address_line2_property_alias",
                "billing_city_property_alias",
                "billing_state_property_alias",
                "billing_zip_code_property_alias",
                "test_secret_key",
                "test_public_key",
                "live_secret_key",
                "live_public_key",
                "mode"
            };

            htmlForm.InputFields = settings.Where(i => !settingsToExclude.Contains(i.Key)).ToDictionary(i => i.Key, i => i.Value);

            htmlForm.InputFields["api_key"] = settings[settings["mode"] + "_public_key"];
            htmlForm.InputFields["continue_url"] = teaCommerceContinueUrl;
            htmlForm.InputFields["cancel_url"] = teaCommerceCancelUrl;

            if (settings.ContainsKey("billing_address_line1_property_alias") && !string.IsNullOrWhiteSpace(settings["billing_address_line1_property_alias"]))
                htmlForm.InputFields["billing_address_line1"] = order.Properties.First(x => x.Alias == settings["billing_address_line1_property_alias"]).Value;

            if (settings.ContainsKey("billing_address_line2_property_alias") && !string.IsNullOrWhiteSpace(settings["billing_address_line2_property_alias"]))
                htmlForm.InputFields["billing_address_line2"] = order.Properties.First(x => x.Alias == settings["billing_address_line2_property_alias"]).Value;

            if (settings.ContainsKey("billing_city_property_alias") && !string.IsNullOrWhiteSpace(settings["billing_city_property_alias"]))
                htmlForm.InputFields["billing_city"] = order.Properties.First(x => x.Alias == settings["billing_city_property_alias"]).Value;

            if (settings.ContainsKey("billing_state_property_alias") && !string.IsNullOrWhiteSpace(settings["billing_state_property_alias"]))
                htmlForm.InputFields["billing_state"] = order.Properties.First(x => x.Alias == settings["billing_state_property_alias"]).Value;

            if (settings.ContainsKey("billing_zip_code_property_alias") && !string.IsNullOrWhiteSpace(settings["billing_zip_code_property_alias"]))
                htmlForm.InputFields["billing_zip_code"] = order.Properties.First(x => x.Alias == settings["billing_zip_code_property_alias"]).Value;

            if (order.PaymentInformation != null && order.PaymentInformation.CountryId > 0)
            {
                var country = CountryService.Instance.Get(order.StoreId, order.PaymentInformation.CountryId);
                htmlForm.InputFields["billing_country"] = country.RegionCode.ToLowerInvariant();
            }

            return htmlForm;
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
                    cartNumber = stripeCharge.Description;
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

                var chargeService = new ChargeService(apiKey);

                var chargeOptions = new ChargeCreateOptions {
                    Amount = DollarsToCents(order.TotalPrice.Value.WithVat),
                    Currency = CurrencyService.Instance.Get(order.StoreId, order.CurrencyId).IsoCode,
                    SourceId = request.Form["stripeToken"],
                    Description = order.CartNumber,
                    Capture = capture
                };

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
                // Pass through request fields
                var requestFields = string.Join("", request.Form.AllKeys.Select(k => "<input type=\"hidden\" name=\"" + k + "\" value=\"" + request.Form[k] + "\" />"));

                //Add error details from the exception.
                requestFields = requestFields + "<input type=\"hidden\" name=\"TransactionFailed\" value=\"true\" />";
                requestFields = requestFields + "<input type=\"hidden\" name=\"FailureReason.chargeId\" value=\"" + e.StripeError.ChargeId + "\" />";
                requestFields = requestFields + "<input type=\"hidden\" name=\"FailureReason.Code\" value=\"" + e.StripeError.Code + "\" />";
                requestFields = requestFields + "<input type=\"hidden\" name=\"FailureReason.Error\" value=\"" + e.StripeError.Error + "\" />";
                requestFields = requestFields + "<input type=\"hidden\" name=\"FailureReason.ErrorDescription\" value=\"" + e.StripeError.ErrorDescription + "\" />";
                requestFields = requestFields + "<input type=\"hidden\" name=\"FailureReason.ErrorType\" value=\"" + e.StripeError.ErrorType + "\" />";
                requestFields = requestFields + "<input type=\"hidden\" name=\"FailureReason.Message\" value=\"" + e.StripeError.Message + "\" />";
                requestFields = requestFields + "<input type=\"hidden\" name=\"FailureReason.Parameter\" value=\"" + e.StripeError.Parameter + "\" />";

                var paymentForm = PaymentMethodService.Instance.Get(order.StoreId, order.PaymentInformation.PaymentMethodId.Value).GeneratePaymentForm(order, requestFields);

                //Force the form to auto submit
                paymentForm += "<script type=\"text/javascript\">document.forms[0].submit();</script>";

                //Write out the form
                HttpContext.Current.Response.Clear();
                HttpContext.Current.Response.Write(paymentForm);
                HttpContext.Current.Response.End();
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
                case "form_url":
                    return settingsKey + "<br/><small>The url of the page with the Stripe payment form on - e.g. /payment/</small>";
                case "continue_url":
                    return settingsKey + "<br/><small>The url to navigate to after payment is processed - e.g. /confirmation/</small>";
                case "cancel_url":
                    return settingsKey + "<br/><small>The url to navigate to if the customer cancels the payment - e.g. /cancel/</small>";
                case "capture":
                    return settingsKey + "<br/><small>Flag indicating if a payment should be captured instantly - true/false.</small>";
                case "billing_address_line1_property_alias":
                    return settingsKey + "<br/><small>The alias of the property containing line 1 of the billing address - e.g. addressLine1. Used by Stripe for Radar verification.</small>";
                case "billing_address_line2_property_alias":
                    return settingsKey + "<br/><small>The alias of the property containing line 2 of the billing address - e.g. addressLine2. Used by Stripe for Radar verification.</small>";
                case "billing_city_property_alias":
                    return settingsKey + "<br/><small>The alias of the property containing the billing address city - e.g. city. Used by Stripe for Radar verification.</small>";
                case "billing_state_property_alias":
                    return settingsKey + "<br/><small>The alias of the property containing the billing address state - e.g. state. Used by Stripe for Radar verification.</small>";
                case "billing_zip_code_property_alias":
                    return settingsKey + "<br/><small>The alias of the property containing the billing address zip code - e.g. zipCode. Used by Stripe for Radar verification.</small>";
                case "test_secret_key":
                    return settingsKey + "<br/><small>Your test stripe secret key.</small>";
                case "test_public_key":
                    return settingsKey + "<br/><small>Your test stripe public key.</small>";
                case "live_secret_key":
                    return settingsKey + "<br/><small>Your live stripe secret key.</small>";
                case "live_public_key":
                    return settingsKey + "<br/><small>Your live stripe public key.</small>";
                case "mode":
                    return settingsKey + "<br/><small>The mode of the provider - test/live.</small>";
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

        protected Event GetStripeEvent(HttpRequest request)
        {
            Event stripeEvent = null;

            if (HttpContext.Current.Items["TC_StripeEvent"] != null)
            {
                stripeEvent = (Event)HttpContext.Current.Items["TC_StripeEvent"];
            }
            else
            {
                try
                {
                    if (request.InputStream.CanSeek)
                    {
                        request.InputStream.Seek(0, SeekOrigin.Begin);
                    }

                    using (StreamReader reader = new StreamReader(request.InputStream))
                    {
                        stripeEvent = EventUtility.ParseEvent(reader.ReadToEnd());

                        HttpContext.Current.Items["TC_StripeEvent"] = stripeEvent;
                    }
                }
                catch
                {
                }
            }

            return stripeEvent;
        }

        private static long DollarsToCents(decimal val)
        {
            return (long)Math.Round(val * 100M, MidpointRounding.AwayFromZero);
        }

        private static decimal CentsToDollars(long val)
        {
            return (decimal)val / 100;
        }
    }
}
