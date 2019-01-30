using AuthorizeNet.Api.Contracts.V1;
using AuthorizeNet.Api.Controllers;
using AuthorizeNet.Api.Controllers.Bases;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;

namespace TeaCommerce.PaymentProviders.Classic
{
    [PaymentProvider("AuthorizeNet")]
    public class AuthorizeNet : APaymentProvider
    {
        public override string DocumentationLink {
            get {
                return "https://developer.authorize.net/api/reference/features/accept_hosted.html";
            }
        }

        public override bool SupportsRetrievalOfPaymentStatus { get { return false; } }

        public override IDictionary<string, string> DefaultSettings
        {
            get
            {
                return new Dictionary<string, string>
                {
                    { "continue_url", "" },
                    { "cancel_url", "" },
                    { "sandbox_api_login_id", "" },
                    { "sandbox_transaction_key", "" },
                    { "live_api_login_id", "" },
                    { "live_transaction_key", "" },
                    { "capture", "true" },
                    { "mode", "sandbox" }
                };
            }
        }

        public override PaymentHtmlForm GenerateHtmlForm(Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, string teaCommerceCommunicationUrl, IDictionary<string, string> settings)
        {
            order.MustNotBeNull("order");
            settings.MustNotBeNull("settings");
            settings.MustContainKey("capture", "settings");
            settings.MustContainKey("mode", "settings");

            // Ensure TLS 1.2
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

            // Configrue the environment
            ApiOperationBase<ANetApiRequest, ANetApiResponse>.RunEnvironment = settings["mode"] == "sandbox"
                ? global::AuthorizeNet.Environment.SANDBOX
                : global::AuthorizeNet.Environment.PRODUCTION;

            // Setup merchant auth settings
            ApiOperationBase<ANetApiRequest, ANetApiResponse>.MerchantAuthentication = new merchantAuthenticationType
            {
                name = settings[settings["mode"] + "_api_login_id"],
                ItemElementName = ItemChoiceType.transactionKey,
                Item = settings[settings["mode"] + "_transaction_key"],
            };

            // Create the transition request
            var transactionRequest = new transactionRequestType
            {
                transactionType = settings["capture"] == "true"
                    ? transactionTypeEnum.authCaptureTransaction.ToString()
                    : transactionTypeEnum.authOnlyTransaction.ToString(),
                amount = ToTwoDecimalPlaces(order.TotalPrice.Value.WithVat),
                tax = new extendedAmountType
                {
                    name = VatGroupService.Instance.Get(order.StoreId, order.VatGroupId).Name,
                    amount = ToTwoDecimalPlaces(order.TotalPrice.Value.Vat)
                },
                customer = new customerDataType
                {
                    id = order.CustomerId,
                    email = order.PaymentInformation.Email
                },
                billTo = new customerAddressType
                {
                    firstName = order.PaymentInformation.FirstName,
                    lastName = order.PaymentInformation.LastName,
                    email = order.PaymentInformation.Email
                },
                order = new orderType
                {
                    invoiceNumber = order.CartNumber
                }
            };

            // Configure payment page settings
            var paymentSettings = new List<settingType>();

            var returnOptions = GetOptionsFromSettings(settings, "return_options_");
            if (!returnOptions.ContainsKey("url"))
            {
                returnOptions.Add("url", teaCommerceContinueUrl); // teaCommerceCallBackUrl
            }
            if (!returnOptions.ContainsKey("cancelUrl"))
            {
                returnOptions.Add("cancelUrl", teaCommerceCancelUrl);
            }
            if (!returnOptions.ContainsKey("showReceipt"))
            {
                returnOptions.Add("showReceipt", false);
            }
            AddOptionsToPaymentSettings(paymentSettings, settingNameEnum.hostedPaymentReturnOptions, returnOptions);

            var buttonOptions = GetOptionsFromSettings(settings, "button_options_");
            AddOptionsToPaymentSettings(paymentSettings, settingNameEnum.hostedPaymentButtonOptions, buttonOptions);

            var orderOptions = GetOptionsFromSettings(settings, "order_options_");
            AddOptionsToPaymentSettings(paymentSettings, settingNameEnum.hostedPaymentOrderOptions, orderOptions);

            var styleOptions = GetOptionsFromSettings(settings, "style_options_");
            AddOptionsToPaymentSettings(paymentSettings, settingNameEnum.hostedPaymentStyleOptions, styleOptions);

            var paymentOptions = GetOptionsFromSettings(settings, "payment_options_");
            AddOptionsToPaymentSettings(paymentSettings, settingNameEnum.hostedPaymentPaymentOptions, paymentOptions);

            var securityOptions = GetOptionsFromSettings(settings, "security_options_");
            AddOptionsToPaymentSettings(paymentSettings, settingNameEnum.hostedPaymentSecurityOptions, securityOptions);

            var shippingAddressOptions = GetOptionsFromSettings(settings, "shipping_address_options_");
            AddOptionsToPaymentSettings(paymentSettings, settingNameEnum.hostedPaymentShippingAddressOptions, shippingAddressOptions);

            var billingAddressOptions = GetOptionsFromSettings(settings, "billing_address_options_");
            if (!billingAddressOptions.ContainsKey("show"))
            {
                billingAddressOptions.Add("show", false);
            }
            AddOptionsToPaymentSettings(paymentSettings, settingNameEnum.hostedPaymentBillingAddressOptions, billingAddressOptions);

            var customerOptions = GetOptionsFromSettings(settings, "customer_options_");
            AddOptionsToPaymentSettings(paymentSettings, settingNameEnum.hostedPaymentCustomerOptions, customerOptions);

            // Configure payment page request
            var hostedPaymentPageRequest = new getHostedPaymentPageRequest
            {
                transactionRequest = transactionRequest,
                hostedPaymentSettings = paymentSettings.ToArray()
            };

            // Instantiate the controller that will call the service
            var hostedPaymentPageController = new getHostedPaymentPageController(hostedPaymentPageRequest);
            hostedPaymentPageController.Execute();

            // Get the response from the service (errors contained if any)
            var hostedPaymentPageResponse = hostedPaymentPageController.GetApiResponse();
            if (hostedPaymentPageResponse == null && hostedPaymentPageResponse.messages.resultCode != messageTypeEnum.Ok)
            {
                throw new ApplicationException("Unable to retrieve Authorize.NET token");
            }

            PaymentHtmlForm htmlForm = new PaymentHtmlForm
            {
                Action = settings["mode"] == "sandbox"
                    ? "https://test.authorize.net/payment/payment"
                    : "https://accept.authorize.net/payment/payment"
            };

            htmlForm.InputFields["token"] = hostedPaymentPageResponse.token;

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
            string cartNumber = "";

            try
            {
                request.MustNotBeNull("request");
                settings.MustNotBeNull("settings");
                settings.MustContainKey("md5HashKey", "settings"); 
                settings.MustContainKey("x_login", "settings");

                //Write data when testing
                if (settings.ContainsKey("mode") && settings["mode"] == "sandbox")
                {
                    LogRequest<AuthorizeNet>(request, logPostData: true);
                }

                string responseCode = request.Form["x_response_code"];
                if (responseCode == "1")
                {

                    string amount = request.Form["x_amount"];
                    string transaction = request.Form["x_trans_id"];

                    string gatewayMd5Hash = request.Form["x_MD5_Hash"];

                    MD5CryptoServiceProvider x = new MD5CryptoServiceProvider();
                    string calculatedMd5Hash = Regex.Replace(BitConverter.ToString(x.ComputeHash(Encoding.ASCII.GetBytes(settings["md5HashKey"] + settings["x_login"] + transaction + amount))), "-", string.Empty);

                    if (gatewayMd5Hash == calculatedMd5Hash)
                    {
                        cartNumber = request.Form["x_invoice_num"];
                    }
                    else
                    {
                        LoggingService.Instance.Warn<AuthorizeNet>("Authorize.net - MD5Sum security check failed - " + gatewayMd5Hash + " - " + calculatedMd5Hash + " - " + settings["md5HashKey"]);
                    }
                }
                else
                {
                    LoggingService.Instance.Warn<AuthorizeNet>("Authorize.net - Payment not approved: " + responseCode);
                }
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<AuthorizeNet>("Authorize.net - Get cart number", exp);
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
                settings.MustContainKey("md5HashKey", "settings");
                settings.MustContainKey("x_login", "settings");

                //Write data when testing
                if (settings.ContainsKey("mode") && settings["mode"] == "sandbox")
                {
                    LogRequest<AuthorizeNet>(request, logPostData: true);
                }

                string responseCode = request.Form["x_response_code"];
                if (responseCode == "1")
                {
                    
                }
                else
                {
                    LoggingService.Instance.Warn<AuthorizeNet>("Authorize.net(" + order.CartNumber + ") - Payment not approved: " + responseCode);
                }
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<AuthorizeNet>("Authorize.net(" + order.CartNumber + ") - Process callback", exp);
            }

            return callbackInfo;
        }

        //public override ApiInfo GetStatus(Order order, IDictionary<string, string> settings)
        //{
        //    ApiInfo apiInfo = null;

        //    try
        //    {
        //        order.MustNotBeNull("order");
        //        settings.MustNotBeNull("settings");
        //        settings.MustContainKey("x_login", "settings");
        //        settings.MustContainKey("transactionKey", "settings");

        //        GetTransactionDetailsResponseType result = GetAuthorizeNetServiceClient(settings).GetTransactionDetails(new MerchantAuthenticationType { name = settings["x_login"], transactionKey = settings["transactionKey"] }, order.TransactionInformation.TransactionId);

        //        if (result.resultCode == MessageTypeEnum.Ok)
        //        {
        //            PaymentState paymentState = PaymentState.Initialized;
        //            switch (result.transaction.transactionStatus)
        //            {
        //                case "authorizedPendingCapture":
        //                    paymentState = PaymentState.Authorized;
        //                    break;
        //                case "capturedPendingSettlement":
        //                case "settledSuccessfully":
        //                    paymentState = PaymentState.Captured;
        //                    break;
        //                case "voided":
        //                    paymentState = PaymentState.Cancelled;
        //                    break;
        //                case "refundSettledSuccessfully":
        //                case "refundPendingSettlement":
        //                    paymentState = PaymentState.Refunded;
        //                    break;
        //            }

        //            apiInfo = new ApiInfo(result.transaction.transId, paymentState);
        //        }
        //        else
        //        {
        //            LoggingService.Instance.Warn<AuthorizeNet>("Authorize.net(" + order.OrderNumber + ") - Error making API request - error code: " + result.messages[0].code + " | description: " + result.messages[0].text);
        //        }

        //    }
        //    catch (Exception exp)
        //    {
        //        LoggingService.Instance.Error<AuthorizeNet>("Authorize.net(" + order.OrderNumber + ") - Get status", exp);
        //    }

        //    return apiInfo;
        //}

        public override string GetLocalizedSettingsKey(string settingsKey, CultureInfo culture)
        {
            switch (settingsKey)
            {
                case "x_receipt_link_url":
                    return settingsKey + "<br/><small>e.g. /continue/</small>";
                case "x_cancel_url":
                    return settingsKey + "<br/><small>e.g. /cancel/</small>";
                case "x_type":
                    return settingsKey + "<br/><small>e.g. AUTH_ONLY or AUTH_CAPTURE</small>";
                case "mode":
                    return settingsKey + "<br/><small>sandbox/live</small>";
                default:
                    return base.GetLocalizedSettingsKey(settingsKey, culture);
            }
        }

        #region Helper methods

        protected void AddOptionsToPaymentSettings(IList<settingType> paymentSettings, settingNameEnum paymentSetting, IDictionary<string, object> options)
        {
            if (options.Count > 0)
            {
                paymentSettings.Add(new settingType
                {
                    settingName = paymentSetting.ToString(),
                    settingValue = JsonConvert.SerializeObject(options)
                });
            }
        }

        protected IDictionary<string, object> GetOptionsFromSettings(IDictionary<string, string> settings, string keyPrefix)
        {
            var options = new Dictionary<string, object>();

            foreach (var key in settings.Keys)
            {
                if (key.StartsWith(keyPrefix))
                {
                    var targetKey = SnakeCaseToCamcelCase(key.Replace(keyPrefix, ""));

                    if (settings[key].ToLowerInvariant() == "true")
                    {
                        options.Add(targetKey, true);
                    }
                    else if (settings[key].ToLowerInvariant() == "false")
                    {
                        options.Add(targetKey, false);
                    }
                    else
                    {
                        options.Add(targetKey, settings[key]);
                    }
                }
            }

            return options;
        }

        protected string SnakeCaseToCamcelCase(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return "";

            var parts = input.Split(new[] { "_" }, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length == 0) return "";
            if (parts.Length == 1) return parts[0].ToLowerInvariant();

            return parts.Skip(1)
                .Select(s => char.ToUpperInvariant(s[0]) + s.Substring(1, s.Length - 1))
                .Aggregate(parts[0].ToLowerInvariant(), (s1, s2) => s1 + s2);
        }

        protected decimal ToTwoDecimalPlaces(decimal input)
        {
            return Convert.ToDecimal(string.Format("{0:0.00}", input));
        }

        #endregion

    }
}
