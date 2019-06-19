using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;

namespace TeaCommerce.PaymentProviders.Classic
{
    [PaymentProvider("OnPay")]
    public class OnPay : APaymentProvider
    {
        public override bool SupportsRetrievalOfPaymentStatus => false;
        public override bool SupportsCapturingOfPayment => false;
        public override bool SupportsRefundOfPayment => false;
        public override bool SupportsCancellationOfPayment => false;

        public override bool FinalizeAtContinueUrl => false;

        public override IDictionary<string, string> DefaultSettings => new Dictionary<string, string>
        {
            {"gatewayid", string.Empty},
            {"secret", string.Empty},
            {"accepturl", string.Empty},
            {"declineurl", string.Empty},
            {"paymenttype", "payment "},
            {"paymentmethod", "card"},
            {"lang", "en"},
            {"design", string.Empty},
            {"testmode", string.Empty}
        };

        public override PaymentHtmlForm GenerateHtmlForm(Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, string teaCommerceCommunicationUrl, IDictionary<string, string> settings)
        {
            order.MustNotBeNull("order");
            settings.MustNotBeNull("settings");
            settings.MustContainKey("gatewayid", "settings");
            settings.MustContainKey("secret", "settings");

            PaymentHtmlForm htmlForm = new PaymentHtmlForm
            {
                Action = "https://onpay.io/window/v3/"
            };

            var currency = CurrencyService.Instance.Get(order.StoreId, order.CurrencyId);
            if (!Iso4217CurrencyCodes.ContainsKey(currency.IsoCode))
            {
                throw new Exception("You must specify an ISO 4217 currency code for the " + currency.Name + " currency");
            }

            htmlForm.InputFields["onpay_gatewayid"] = settings["gatewayid"];
            htmlForm.InputFields["onpay_currency"] = Iso4217CurrencyCodes[currency.IsoCode];
            htmlForm.InputFields["onpay_amount"] = (order.TotalPrice.Value.WithVat * 100M).ToString("0", CultureInfo.InvariantCulture);
            htmlForm.InputFields["onpay_reference"] = order.CartNumber;
            htmlForm.InputFields["onpay_language"] = settings["lang"] ?? "en";
            htmlForm.InputFields["onpay_method"] = settings["paymentmethod"] ?? "card";
            htmlForm.InputFields["onpay_type"] = settings["paymenttype"] ?? "payment";
            htmlForm.InputFields["onpay_3dsecure"] = "forced";
            htmlForm.InputFields["onpay_accepturl"] = teaCommerceContinueUrl;
            htmlForm.InputFields["onpay_declineurl"] = teaCommerceCancelUrl;
            htmlForm.InputFields["onpay_callbackurl"] = teaCommerceCallBackUrl;

            htmlForm.InputFields["onpay_hmac_sha1"] = GenerateOnPayInputsHash(htmlForm.InputFields, settings["secret"]);

            order.Properties.AddOrUpdate(new CustomProperty("OnPay_HMAC_SHA1", htmlForm.InputFields["onpay_hmac_sha1"]) { ServerSideOnly = true });
            order.Save();

            return htmlForm;
        }

        public override string GetContinueUrl(Order order, IDictionary<string, string> settings)
        {
            settings.MustNotBeNull("settings");
            settings.MustContainKey("accepturl", "settings");

            return settings["accepturl"];
        }

        public override string GetCancelUrl(Order order, IDictionary<string, string> settings)
        {
            settings.MustNotBeNull("settings");
            settings.MustContainKey("declineurl", "settings");

            return settings["declineurl"];
        }

        public override string GetCartNumber(HttpRequest request, IDictionary<string, string> settings)
        {
            return request.QueryString["onpay_reference"];
        }

        public override CallbackInfo ProcessCallback(Order order, HttpRequest request, IDictionary<string, string> settings)
        {
            CallbackInfo callbackInfo = null;

            try
            {
                order.MustNotBeNull("order");
                request.MustNotBeNull("request");
                settings.MustNotBeNull("settings");

                // Write data when testing
                if (settings.ContainsKey("testmode") && settings["testmode"] == "1")
                {
                    LogRequest<OnPay>(request, logGetData: true);
                }
                
                if (order.Properties["OnPay_HMAC_SHA1"] == request.QueryString["onpay_hmac_sha1"])
                {
                    var transaction = request.QueryString["onpay_uuid"];
                    var cardtype = request.QueryString["onpay_method"];
                    var cardnomask = request.QueryString["onpay_cardmask"];
                    var amount = request.QueryString["onpay_amount"];
                    var totalAmount = decimal.Parse(amount, CultureInfo.InvariantCulture);

                    callbackInfo = new CallbackInfo(totalAmount / 100M, transaction, PaymentState.Captured, cardtype, cardnomask);
                }
                else
                {
                    LoggingService.Instance.Warn<OnPay>("OnPay(" + order.CartNumber + ") - Security check failed");
                }
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<OnPay>("OnPay(" + order.CartNumber + ") - Process callback", exp);
            }

            return callbackInfo;
        }

        //public override ApiInfo GetStatus(Order order, IDictionary<string, string> settings)
        //{
        //    ApiInfo apiInfo = null;

        //    try
        //    {
        //        try
        //        {
        //            returnArray returnData = GetWannafindServiceClient(settings).checkTransaction(int.Parse(order.TransactionInformation.TransactionId), string.Empty, order.CartNumber, string.Empty, string.Empty);

        //            PaymentState paymentState = PaymentState.Initialized;

        //            switch (returnData.returncode)
        //            {
        //                case 5:
        //                    paymentState = PaymentState.Authorized;
        //                    break;
        //                case 6:
        //                    paymentState = PaymentState.Captured;
        //                    break;
        //                case 7:
        //                    paymentState = PaymentState.Cancelled;
        //                    break;
        //                case 8:
        //                    paymentState = PaymentState.Refunded;
        //                    break;
        //            }

        //            apiInfo = new ApiInfo(order.TransactionInformation.TransactionId, paymentState);

        //        }
        //        catch (WebException)
        //        {
        //            LoggingService.Instance.Warn<OnPay>("OnPay(" + order.OrderNumber + ") - Error making API request - Wrong credentials or IP address not allowed");
        //        }
        //    }
        //    catch (Exception exp)
        //    {
        //        LoggingService.Instance.Error<OnPay>("OnPay(" + order.OrderNumber + ") - Get status", exp);
        //    }

        //    return apiInfo;
        //}

        //public override ApiInfo CapturePayment(Order order, IDictionary<string, string> settings)
        //{
        //    ApiInfo apiInfo = null;

        //    try
        //    {
        //        try
        //        {
        //            //When capturing of the complete amount - send 0 as parameter for amount
        //            int returnCode = GetWannafindServiceClient(settings).captureTransaction(int.Parse(order.TransactionInformation.TransactionId), 0);
        //            if (returnCode == 0)
        //            {
        //                apiInfo = new ApiInfo(order.TransactionInformation.TransactionId, PaymentState.Captured);
        //            }
        //            else
        //            {
        //                LoggingService.Instance.Warn<OnPay>("OnPay(" + order.OrderNumber + ") - Error making API request - Error code: " + returnCode);
        //            }
        //        }
        //        catch (WebException)
        //        {
        //            LoggingService.Instance.Warn<OnPay>("OnPay(" + order.OrderNumber + ") - Error making API request - Wrong credentials or IP address not allowed");
        //        }
        //    }
        //    catch (Exception exp)
        //    {
        //        LoggingService.Instance.Error<OnPay>("OnPay(" + order.OrderNumber + ") - Capture payment", exp);
        //    }

        //    return apiInfo;
        //}

        //public override ApiInfo RefundPayment(Order order, IDictionary<string, string> settings)
        //{
        //    ApiInfo apiInfo = null;

        //    try
        //    {
        //        try
        //        {
        //            int returnCode = GetWannafindServiceClient(settings).creditTransaction(int.Parse(order.TransactionInformation.TransactionId), (int)(order.TransactionInformation.AmountAuthorized.Value * 100M));
        //            if (returnCode == 0)
        //            {
        //                apiInfo = new ApiInfo(order.TransactionInformation.TransactionId, PaymentState.Refunded);
        //            }
        //            else
        //            {
        //                LoggingService.Instance.Warn<OnPay>("OnPay(" + order.OrderNumber + ") - Error making API request - Error code: " + returnCode);
        //            }
        //        }
        //        catch (WebException)
        //        {
        //            LoggingService.Instance.Warn<OnPay>("OnPay(" + order.OrderNumber + ") - Error making API request - Wrong credentials or IP address not allowed");
        //        }
        //    }
        //    catch (Exception exp)
        //    {
        //        LoggingService.Instance.Error<OnPay>("OnPay(" + order.OrderNumber + ") - Refund payment", exp);
        //    }

        //    return apiInfo;
        //}

        //public override ApiInfo CancelPayment(Order order, IDictionary<string, string> settings)
        //{
        //    ApiInfo apiInfo = null;

        //    try
        //    {
        //        try
        //        {
        //            int returnCode = GetWannafindServiceClient(settings).cancelTransaction(int.Parse(order.TransactionInformation.TransactionId));
        //            if (returnCode == 0)
        //            {
        //                apiInfo = new ApiInfo(order.TransactionInformation.TransactionId, PaymentState.Cancelled);
        //            }
        //            else
        //            {
        //                LoggingService.Instance.Warn<OnPay>("OnPay(" + order.OrderNumber + ") - Error making API request - Error code: " + returnCode);
        //            }
        //        }
        //        catch (WebException)
        //        {
        //            LoggingService.Instance.Warn<OnPay>("OnPay(" + order.OrderNumber + ") - Error making API request - Wrong credentials or IP address not allowed");
        //        }
        //    }
        //    catch (Exception exp)
        //    {
        //        LoggingService.Instance.Error<OnPay>("OnPay(" + order.OrderNumber + ") - Cancel payment", exp);
        //    }

        //    return apiInfo;
        //}

        public override string GetLocalizedSettingsKey(string settingsKey, CultureInfo culture)
        {
            switch (settingsKey)
            {
                case "gatewayid":
                    return settingsKey + "<br/><small>The unique gatewayid for your paymentgateway</small>";
                case "secret":
                    return settingsKey + "<br/><small>The SHA1 HMAC shared secret set in the management panel</small>";
                case "accepturl":
                    return settingsKey + "<br/><small>The URL of the page to redirect to on successful payment e.g. /continue/</small>";
                case "declineurl":
                    return settingsKey + "<br/><small>The URL of the page to redirect to on declined payment e.g. /cancel/</small>";
                case "paymenttype":
                    return settingsKey + "<br/><small>payment or subscription</small>";
                case "paymentmethod":
                    return settingsKey + "<br/><small>card or mobilepay</small>";
                case "lang":
                    return settingsKey + "<br/><small>The language of the payment window</small>";
                case "design":
                    return settingsKey + "<br/><small>The name of the window design to use</small>";
                case "testmode":
                    return settingsKey + "<br/><small>1 = true; 0 = false</small>";
                default:
                    return base.GetLocalizedSettingsKey(settingsKey, culture);
            }
        }

        #region Helper methods

        protected string GenerateOnPayInputsHash(IDictionary<string, string> inputs, string secret)
        {
            var keys = inputs.Keys.Where(x => x.StartsWith("onpay_")).OrderBy(x => x);
            var qs = string.Join("&", keys.Select(k => $"{k}={inputs[k]}".ToLowerInvariant()));
            var hash = GenerateHMACSHA1Hash(qs, secret);
            return hash;
        }

        protected string GenerateHMACSHA1Hash(string input, string secret)
        {
            using (var sha1 = new HMACSHA1(Encoding.UTF8.GetBytes(secret)))
            {
                return sha1.ComputeHash(Encoding.UTF8.GetBytes(input))
                    .Aggregate("", (s, e) => s + string.Format("{0:x2}", e), s => s);
            }
        }

        #endregion

    }
}
