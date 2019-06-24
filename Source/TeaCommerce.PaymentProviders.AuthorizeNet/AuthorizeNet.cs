using AuthorizeNet.Api.Contracts.V1;
using AuthorizeNet.Api.Controllers;
using AuthorizeNet.Api.Controllers.Bases;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Models;
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

        public override bool SupportsRetrievalOfPaymentStatus { get { return true; } }
        public override bool SupportsCapturingOfPayment { get { return true; } }
        public override bool SupportsRefundOfPayment { get { return true; } }
        public override bool SupportsCancellationOfPayment { get { return true; } }

        public override IDictionary<string, string> DefaultSettings
        {
            get
            {
                return new Dictionary<string, string>
                {
                    { "continue_url", "" },
                    { "cancel_url", "" },
                    { "order_options_merchant_name", "" },
                    { "capture", "true" },
                    { "sandbox_api_login_id", "" },
                    { "sandbox_transaction_key", "" },
                    { "sandbox_signature_key", "" },
                    { "live_api_login_id", "" },
                    { "live_transaction_key", "" },
                    { "live_signature_key", "" },
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
            settings.MustContainKey(settings["mode"] + "_api_login_id", "settings");
            settings.MustContainKey(settings["mode"] + "_transaction_key", "settings");

            // Configure AuthorizeNet
            ConfigureAuthorizeNet(settings);

            // Create the transition request
            var transactionRequest = new transactionRequestType
            {
                transactionType = settings["capture"] == "true"
                    ? transactionTypeEnum.authCaptureTransaction.ToString()
                    : transactionTypeEnum.authOnlyTransaction.ToString(),
                amount = ToTwoDecimalPlaces(order.TotalPrice.Value.WithVat),
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
                returnOptions.Add("url", teaCommerceContinueUrl);
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
            if (orderOptions.ContainsKey("merchantName") && !orderOptions.ContainsKey("show"))
            {
                orderOptions.Add("show", true);
            }
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
                settings.MustContainKey("mode", "settings");
                settings.MustContainKey(settings["mode"] + "_api_login_id", "settings");
                settings.MustContainKey(settings["mode"] + "_transaction_key", "settings");
                settings.MustContainKey(settings["mode"] + "_signature_key", "settings");

                // Write data when testing
                if (settings.ContainsKey("mode") && settings["mode"] == "sandbox")
                {
                    LogRequest<AuthorizeNet>(request, logPostData: true);
                }

                var authorizeNetEvent = GetValidatedWebhookEvent(settings[settings["mode"] + "_signature_key"]);
                if (authorizeNetEvent != null && authorizeNetEvent.eventType.StartsWith("net.authorize.payment."))
                {
                    var paymentPayload = authorizeNetEvent.payload.ToObject<AuthorizeNetWebhookPaymentPayload>();
                    if (paymentPayload != null && paymentPayload.entityName == "transaction")
                    {
                        var transactionId = paymentPayload.id;

                        // Configure AuthorizeNet
                        ConfigureAuthorizeNet(settings);

                        // Fetch the transaction
                        var transactionRequest = new getTransactionDetailsRequest { transId = transactionId };
                        var controller = new getTransactionDetailsController(transactionRequest);
                        controller.Execute();
                        
                        var transactionResponse = controller.GetApiResponse();
                        if (transactionResponse != null 
                            && transactionResponse.messages.resultCode == messageTypeEnum.Ok
                            && transactionResponse.transaction != null
                            && transactionResponse.transaction.order != null)
                        {
                            // Stash the transaction
                            authorizeNetEvent.transaction = transactionResponse.transaction;

                            // Get the cart number
                            cartNumber = transactionResponse.transaction.order.invoiceNumber;
                        }
                    }
                }
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<AuthorizeNet>("Authorize.net - GetCartNumber", exp);
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

                // If we get to here, GetCartNumber must have been called and a valid cart number
                // returned, thus we can trust that the AuthorizeNet webhook event must be valid

                // Write data when testing
                if (settings.ContainsKey("mode") && settings["mode"] == "sandbox")
                {
                    LogRequest<AuthorizeNet>(request, logPostData: true);
                }

                var authorizeNetEvent = GetValidatedWebhookEvent(settings[settings["mode"] + "_signature_key"]);
                if (authorizeNetEvent != null && authorizeNetEvent.eventType.StartsWith("net.authorize.payment."))
                {
                    var paymentPayload = authorizeNetEvent.payload.ToObject<AuthorizeNetWebhookPaymentPayload>();
                    if (paymentPayload != null 
                        && paymentPayload.entityName == "transaction"
                        && paymentPayload.responseCode == 1)
                    {
                        var eventType = authorizeNetEvent.eventType.ToLower();
                        var paymentState = PaymentState.Initialized;

                        if (eventType.Contains(".authorization."))
                        {
                            paymentState = PaymentState.Authorized;
                        }
                        else if (eventType.Contains(".authcapture.") 
                            || eventType.Contains(".capture.")
                            || eventType.Contains(".priorauthcapture."))
                        {
                            paymentState = PaymentState.Captured;
                        }
                        else if (eventType.Contains(".refund."))
                        {
                            paymentState = PaymentState.Refunded;
                        }
                        else if (eventType.Contains(".void."))
                        {
                            paymentState = PaymentState.Cancelled;
                        }

                        var cardType = order.TransactionInformation.PaymentType;
                        var cardNoMask = order.TransactionInformation.PaymentIdentifier;

                        if (authorizeNetEvent.transaction != null
                            && authorizeNetEvent.transaction.payment != null
                            && authorizeNetEvent.transaction.payment.Item != null)
                        {
                            var maskedCreditCard = authorizeNetEvent.transaction.payment.Item as creditCardMaskedType;
                            if (maskedCreditCard != null)
                            {
                                cardType = maskedCreditCard.cardType;
                                cardNoMask = maskedCreditCard.cardNumber;
                            }
                        }

                        callbackInfo = new CallbackInfo(paymentPayload.authAmount, paymentPayload.id, paymentState, cardType, cardNoMask);
                    }
                }
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<AuthorizeNet>("Authorize.net(" + order.CartNumber + ") - ProcessCallback", exp);
            }

            return callbackInfo;
        }

        public override ApiInfo GetStatus(Order order, IDictionary<string, string> settings)
        {
            ApiInfo apiInfo = null;

            try
            {
                order.MustNotBeNull("order");
                settings.MustNotBeNull("settings");
                settings.MustContainKey("mode", "settings");
                settings.MustContainKey(settings["mode"] + "_api_login_id", "settings");
                settings.MustContainKey(settings["mode"] + "_transaction_key", "settings");

                // Configure AuthorizeNet
                ConfigureAuthorizeNet(settings);

                // Fetch the transaction
                var transactionRequest = new getTransactionDetailsRequest { transId = order.TransactionInformation.TransactionId };
                var controller = new getTransactionDetailsController(transactionRequest);
                controller.Execute();
                
                var transactionResponse = controller.GetApiResponse();
                if (transactionResponse != null
                    && transactionResponse.messages.resultCode == messageTypeEnum.Ok
                    && transactionResponse.transaction != null)
                {
                    var paymentState = GetPaymentStateFromTransaction(transactionResponse.transaction);
                    
                    apiInfo = new ApiInfo(transactionResponse.transaction.transId, paymentState);
                }

            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<AuthorizeNet>("Authorize.net(" + order.OrderNumber + ") - GetStatus", exp);
            }

            return apiInfo;
        }

        public override ApiInfo CapturePayment(Order order, IDictionary<string, string> settings)
        {
            ApiInfo apiInfo = null;

            try
            {
                order.MustNotBeNull("order");
                settings.MustNotBeNull("settings");
                settings.MustContainKey("mode", "settings");
                settings.MustContainKey(settings["mode"] + "_api_login_id", "settings");
                settings.MustContainKey(settings["mode"] + "_transaction_key", "settings");

                // Configure AuthorizeNet
                ConfigureAuthorizeNet(settings);

                // Charge the transaction
                var transactionRequest = new createTransactionRequest
                {
                    transactionRequest = new transactionRequestType
                    {
                        transactionType = transactionTypeEnum.priorAuthCaptureTransaction.ToString(),
                        amount = ToTwoDecimalPlaces(order.TotalPrice.Value.WithVat),
                        refTransId = order.TransactionInformation.TransactionId
                    }
                };

                var controller = new createTransactionController(transactionRequest);
                controller.Execute();
                
                var transactionResponse = controller.GetApiResponse();
                if (transactionResponse != null
                    && transactionResponse.messages.resultCode == messageTypeEnum.Ok)
                {
                    apiInfo = new ApiInfo(order.TransactionInformation.TransactionId, PaymentState.Captured);
                }
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<AuthorizeNet>("Authorize.net(" + order.OrderNumber + ") - CapturePayment", exp);
            }

            return apiInfo;
        }

        public override ApiInfo RefundPayment(Order order, IDictionary<string, string> settings)
        {
            ApiInfo apiInfo = null;

            try
            {
                order.MustNotBeNull("order");
                settings.MustNotBeNull("settings");
                settings.MustContainKey("mode", "settings");
                settings.MustContainKey(settings["mode"] + "_api_login_id", "settings");
                settings.MustContainKey(settings["mode"] + "_transaction_key", "settings");

                // Configure AuthorizeNet
                ConfigureAuthorizeNet(settings);

                // Refund the transaction
                var transactionRequest = new createTransactionRequest
                {
                    transactionRequest = new transactionRequestType
                    {
                        transactionType = transactionTypeEnum.refundTransaction.ToString(),
                        amount = ToTwoDecimalPlaces(order.TransactionInformation.AmountAuthorized.Value),
                        refTransId = order.TransactionInformation.TransactionId,
                        payment = new paymentType
                        {
                            Item = new creditCardType
                            {
                                cardNumber = order.TransactionInformation.PaymentIdentifier.TrimStart('X'),
                                expirationDate = "XXXX"
                            }
                        }
                    }
                };

                var controller = new createTransactionController(transactionRequest);
                controller.Execute();

                var transactionResponse = controller.GetApiResponse();
                if (transactionResponse != null
                    && transactionResponse.messages.resultCode == messageTypeEnum.Ok)
                {
                    apiInfo = new ApiInfo(order.TransactionInformation.TransactionId, PaymentState.Refunded);
                }
                else
                {
                    // Payment might not have settled yet so try canceling instead
                    return CancelPayment(order, settings);
                }
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<AuthorizeNet>("Authorize.net(" + order.OrderNumber + ") - RefundPayment", exp);
            }

            return apiInfo;
        }

        public override ApiInfo CancelPayment(Order order, IDictionary<string, string> settings)
        {
            ApiInfo apiInfo = null;

            try
            {
                order.MustNotBeNull("order");
                settings.MustNotBeNull("settings");
                settings.MustContainKey("mode", "settings");
                settings.MustContainKey(settings["mode"] + "_api_login_id", "settings");
                settings.MustContainKey(settings["mode"] + "_transaction_key", "settings");

                // Configure AuthorizeNet
                ConfigureAuthorizeNet(settings);

                // Void the transaction
                var transactionRequest = new createTransactionRequest
                {
                    transactionRequest = new transactionRequestType
                    {
                        transactionType = transactionTypeEnum.voidTransaction.ToString(),
                        refTransId = order.TransactionInformation.TransactionId
                    }
                };

                var controller = new createTransactionController(transactionRequest);
                controller.Execute();

                var transactionResponse = controller.GetApiResponse();
                if (transactionResponse != null
                    && transactionResponse.messages.resultCode == messageTypeEnum.Ok)
                {
                    apiInfo = new ApiInfo(order.TransactionInformation.TransactionId, PaymentState.Cancelled);
                }
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<AuthorizeNet>("Authorize.net(" + order.OrderNumber + ") - CancelPayment", exp);
            }

            return apiInfo;
        }

        public override string GetLocalizedSettingsKey(string settingsKey, CultureInfo culture)
        {
            switch (settingsKey)
            {
                case "continue_url":
                    return settingsKey + "<br/><small>The URL to return to once payment is complete. e.g. /continue/</small>";
                case "cancel_url":
                    return settingsKey + "<br/><small>The URL to return to if a payment is canceled. e.g. /cancel/</small>";
                case "order_options_merchant_name":
                    return settingsKey + "<br/><small>The merchant name to appear on the payment gateway.</small>";
                case "capture":
                    return settingsKey + "<br/><small>Whether to capture or just authorise the payment. true/false</small>";
                case "sandbox_api_login_id":
                    return settingsKey + "<br/><small>The API Login ID for the sandbox test account.</small>";
                case "sandbox_transaction_key":
                    return settingsKey + "<br/><small>The Transaction Key for the sandbox test account.</small>";
                case "sandbox_signature_key":
                    return settingsKey + "<br/><small>The Signature Key for the sandbox test account.</small>";
                case "live_api_login_id":
                    return settingsKey + "<br/><small>The API Login ID for the live account.</small>";
                case "live_transaction_key":
                    return settingsKey + "<br/><small>The Transaction Key for the live account.</small>";
                case "live_signature_key":
                    return settingsKey + "<br/><small>The Signature Key for the live account.</small>";
                case "mode":
                    return settingsKey + "<br/><small>The mode of the provider. sandbox/live</small>";
                default:
                    return base.GetLocalizedSettingsKey(settingsKey, culture);
            }
        }

        #region Helper methods

        protected void ConfigureAuthorizeNet(IDictionary<string, string> settings)
        {
            settings.MustNotBeNull("settings");
            settings.MustContainKey("mode", "settings");
            settings.MustContainKey(settings["mode"] + "_api_login_id", "settings");
            settings.MustContainKey(settings["mode"] + "_transaction_key", "settings");

            // Ensure TLS 1.2
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072; //TLS 1.2

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

        protected PaymentState GetPaymentStateFromTransaction(transactionDetailsType transaction)
        {
            var paymentState = PaymentState.Initialized;

            switch (transaction.transactionStatus)
            {
                case "authorizedPendingCapture":
                    paymentState = PaymentState.Authorized;
                    break;
                case "capturedPendingSettlement":
                case "settledSuccessfully":
                    paymentState = PaymentState.Captured;
                    break;
                case "voided":
                    paymentState = PaymentState.Cancelled;
                    break;
                case "refundSettledSuccessfully":
                case "refundPendingSettlement":
                    paymentState = PaymentState.Refunded;
                    break;
            }

            return paymentState;
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

        protected string ComputeSHA512Hash(string text, string secretKey)
        {
            byte[] _key = Encoding.ASCII.GetBytes(secretKey);
            using (var myhmacsha1 = new HMACSHA1(_key))
            {
                var hashArray = new HMACSHA512(_key).ComputeHash(Encoding.ASCII.GetBytes(text));
                return hashArray.Aggregate("", (s, e) => s + String.Format("{0:x2}", e), s => s);
            }
        }

        public AuthorizeNetWebhookEvent GetValidatedWebhookEvent(string signatureKey)
        {
            AuthorizeNetWebhookEvent authorizeNetEvent = null;

            if (HttpContext.Current.Items["TC_AuthorizeNetEvent"] != null)
            {
                authorizeNetEvent = (AuthorizeNetWebhookEvent) HttpContext.Current.Items["TC_AuthorizeNetEvent"];
            }
            else
            {
                try
                {
                    var rawBody = GetRequestBodyAsString(HttpContext.Current.Request);

                    // Compare signatures
                    var gatewaySignature = HttpContext.Current.Request.Headers["X-ANET-Signature"];
                    var calculatedSignature = ComputeSHA512Hash(rawBody, signatureKey);
                    
                    if (string.Equals(gatewaySignature.Split('=').Last(), calculatedSignature, StringComparison.CurrentCultureIgnoreCase))
                    {
                        // Deserialize event body
                        authorizeNetEvent = JsonConvert.DeserializeObject<AuthorizeNetWebhookEvent>(rawBody);
                    }

                    HttpContext.Current.Items["TC_AuthorizeNetEvent"] = authorizeNetEvent;
                }
                catch
                { }
            }

            return authorizeNetEvent;
        }

        protected string GetRequestBodyAsString(HttpRequest request)
        {
            var bodyStream = new StreamReader(request.InputStream);
            bodyStream.BaseStream.Seek(0, SeekOrigin.Begin);
            var bodyText = bodyStream.ReadToEnd();
            return bodyText;
        }

        public class AuthorizeNetWebhookEvent
        {
            public string notificationId { get; set; }
            public string eventType { get; set; }
            public string eventDate { get; set; }
            public string webhookId { get; set; }
            public JObject payload { get; set; }
            
            [JsonIgnore] // Set during GetCartNumber
            public transactionDetailsType transaction { get; set; }
        }

        public class AuthorizeNetWebhookPaymentPayload
        {
            public int responseCode { get; set; }
            public string authCode { get; set; }
            public string avsResponse { get; set; }
            public decimal authAmount { get; set; }
            public string entityName { get; set; }
            public string id { get; set; }
        }

        #endregion

    }

}
