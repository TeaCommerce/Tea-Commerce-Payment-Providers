using Paynova.Api.Client;
using Paynova.Api.Client.Model;
using Paynova.Api.Client.Requests;
using Paynova.Api.Client.Responses;
using Paynova.Api.Client.Security;
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
using PaynovaPaymentMethod = Paynova.Api.Client.Model.PaymentMethod;

namespace TeaCommerce.PaymentProviders.Classic
{
    [PaymentProvider("Paynova")]
    public class Paynova : APaymentProvider
    {
        public override bool SupportsCapturingOfPayment { get { return true; } }
        public override bool SupportsRefundOfPayment { get { return true; } }
        public override bool SupportsCancellationOfPayment { get { return true; } }

        public override bool FinalizeAtContinueUrl { get { return true; } }

        public override IDictionary<string, string> DefaultSettings
        {
            get
            {
                Dictionary<string, string> defaultSettings = new Dictionary<string, string>();
                defaultSettings["merchantId"] = "";
                defaultSettings["customerLanguageCode"] = "ENG";
                defaultSettings["urlRedirectSuccess"] = "";
                defaultSettings["urlRedirectCancel"] = "";
                defaultSettings["paymentMethods"] = "";
                defaultSettings["secretKey"] = "";
                defaultSettings["apiPassword"] = "";
                defaultSettings["testMode"] = "1";
                return defaultSettings;
            }
        }

        public override PaymentHtmlForm GenerateHtmlForm(Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, string teaCommerceCommunicationUrl, IDictionary<string, string> settings)
        {
            order.MustNotBeNull("order");
            settings.MustNotBeNull("settings");
            settings.MustContainKey("customerLanguageCode", "settings");
            settings.MustContainKey("testMode", "settings");

            PaymentHtmlForm htmlForm = new PaymentHtmlForm();

            IPaynovaClient client = GetClient(settings);

            Currency currency = CurrencyService.Instance.Get(order.StoreId, order.CurrencyId);
            if (!Iso4217CurrencyCodes.ContainsKey(currency.IsoCode))
            {
                throw new Exception("You must specify an ISO 4217 currency code for the " + currency.Name + " currency");
            }
            try
            {
                //Create order request
                CreateOrderRequest createOrderRequest = new CreateOrderRequest(order.CartNumber, currency.IsoCode, order.TotalPrice.Value.WithVat)
                {
                    Customer = new Customer(),
                    BillTo = new NameAndAddress(),
                    ShipTo = new NameAndAddress()
                };

                #region Customer information

                createOrderRequest.Customer.EmailAddress = order.PaymentInformation.Email;
                createOrderRequest.Customer.Name.CompanyName = createOrderRequest.BillTo.Name.CompanyName = order.Properties[settings.ContainsKey("companyPropertyAlias") && !string.IsNullOrEmpty(settings["companyPropertyAlias"]) ? settings["companyPropertyAlias"] : "company"];
                createOrderRequest.Customer.Name.Title = createOrderRequest.BillTo.Name.Title = order.Properties[settings.ContainsKey("titlePropertyAlias") && !string.IsNullOrEmpty(settings["titlePropertyAlias"]) ? settings["titlePropertyAlias"] : "title"];
                createOrderRequest.Customer.Name.FirstName = createOrderRequest.BillTo.Name.FirstName = order.PaymentInformation.FirstName;
                createOrderRequest.Customer.Name.MiddleNames = createOrderRequest.BillTo.Name.MiddleNames = order.Properties[settings.ContainsKey("middleNamesPropertyAlias") && !string.IsNullOrEmpty(settings["middleNamesPropertyAlias"]) ? settings["middleNamesPropertyAlias"] : "middleNames"];
                createOrderRequest.Customer.Name.LastName = createOrderRequest.BillTo.Name.LastName = order.PaymentInformation.LastName;
                createOrderRequest.Customer.Name.Suffix = createOrderRequest.BillTo.Name.Suffix = order.Properties[settings.ContainsKey("suffixPropertyAlias") && !string.IsNullOrEmpty(settings["suffixPropertyAlias"]) ? settings["suffixPropertyAlias"] : "suffix"];
                createOrderRequest.Customer.HomeTelephone = order.Properties[settings.ContainsKey("homeTelephonePropertyAlias") && !string.IsNullOrEmpty(settings["homeTelephonePropertyAlias"]) ? settings["homeTelephonePropertyAlias"] : "phone"];
                createOrderRequest.Customer.WorkTelephone = order.Properties[settings.ContainsKey("workTelephonePropertyAlias") && !string.IsNullOrEmpty(settings["workTelephonePropertyAlias"]) ? settings["workTelephonePropertyAlias"] : "workPhone"];
                createOrderRequest.Customer.MobileTelephone = order.Properties[settings.ContainsKey("mobileTelephonePropertyAlias") && !string.IsNullOrEmpty(settings["mobileTelephonePropertyAlias"]) ? settings["mobileTelephonePropertyAlias"] : "mobile"];
                createOrderRequest.BillTo.Address.Street1 = order.Properties[settings.ContainsKey("street1PropertyAlias") && !string.IsNullOrEmpty(settings["street1PropertyAlias"]) ? settings["street1PropertyAlias"] : "streetAddress"];
                createOrderRequest.BillTo.Address.Street2 = order.Properties[settings.ContainsKey("street2PropertyAlias") && !string.IsNullOrEmpty(settings["street2PropertyAlias"]) ? settings["street2PropertyAlias"] : "streetAddress2"];
                createOrderRequest.BillTo.Address.Street3 = order.Properties[settings.ContainsKey("street3PropertyAlias") && !string.IsNullOrEmpty(settings["street3PropertyAlias"]) ? settings["street3PropertyAlias"] : "streetAddress3"];
                createOrderRequest.BillTo.Address.Street4 = order.Properties[settings.ContainsKey("street4PropertyAlias") && !string.IsNullOrEmpty(settings["street4PropertyAlias"]) ? settings["street4PropertyAlias"] : "streetAddress4"];
                createOrderRequest.BillTo.Address.City = order.Properties[settings.ContainsKey("cityPropertyAlias") && !string.IsNullOrEmpty(settings["cityPropertyAlias"]) ? settings["cityPropertyAlias"] : "city"];
                createOrderRequest.BillTo.Address.PostalCode = order.Properties[settings.ContainsKey("postalCodePropertyAlias") && !string.IsNullOrEmpty(settings["postalCodePropertyAlias"]) ? settings["postalCodePropertyAlias"] : "zipCode"];
                if (order.PaymentInformation.CountryRegionId != null)
                {
                    createOrderRequest.BillTo.Address.RegionCode = CountryRegionService.Instance.Get(order.StoreId, order.PaymentInformation.CountryRegionId.Value).RegionCode;
                }
                createOrderRequest.BillTo.Address.CountryCode = CountryService.Instance.Get(order.StoreId, order.PaymentInformation.CountryId).RegionCode;

                createOrderRequest.ShipTo.Name.CompanyName = order.Properties[settings.ContainsKey("shipping_companyPropertyAlias") && !string.IsNullOrEmpty(settings["shipping_companyPropertyAlias"]) ? settings["shipping_companyPropertyAlias"] : "shipping_company"];
                createOrderRequest.ShipTo.Name.Title = order.Properties[settings.ContainsKey("shipping_titlePropertyAlias") && !string.IsNullOrEmpty(settings["shipping_titlePropertyAlias"]) ? settings["shipping_titlePropertyAlias"] : "shipping_title"];
                createOrderRequest.ShipTo.Name.FirstName = order.Properties[settings.ContainsKey("shipping_firstNamePropertyAlias") && !string.IsNullOrEmpty(settings["shipping_firstNamePropertyAlias"]) ? settings["shipping_firstNamePropertyAlias"] : "shipping_firstName"];
                createOrderRequest.ShipTo.Name.MiddleNames = order.Properties[settings.ContainsKey("shipping_middleNamesPropertyAlias") && !string.IsNullOrEmpty(settings["shipping_middleNamesPropertyAlias"]) ? settings["shipping_middleNamesPropertyAlias"] : "shipping_middleNames"];
                createOrderRequest.ShipTo.Name.LastName = order.Properties[settings.ContainsKey("shipping_lastNamePropertyAlias") && !string.IsNullOrEmpty(settings["shipping_lastNamePropertyAlias"]) ? settings["shipping_lastNamePropertyAlias"] : "shipping_lastName"];
                createOrderRequest.ShipTo.Name.Suffix = order.Properties[settings.ContainsKey("shipping_suffixPropertyAlias") && !string.IsNullOrEmpty(settings["shipping_suffixPropertyAlias"]) ? settings["shipping_suffixPropertyAlias"] : "shipping_suffix"];
                createOrderRequest.ShipTo.Address.Street1 = order.Properties[settings.ContainsKey("shipping_street1PropertyAlias") && !string.IsNullOrEmpty(settings["shipping_street1PropertyAlias"]) ? settings["shipping_street1PropertyAlias"] : "shipping_streetAddress"];
                createOrderRequest.ShipTo.Address.Street2 = order.Properties[settings.ContainsKey("shipping_street2PropertyAlias") && !string.IsNullOrEmpty(settings["shipping_street2PropertyAlias"]) ? settings["shipping_street2PropertyAlias"] : "shipping_streetAddress2"];
                createOrderRequest.ShipTo.Address.Street3 = order.Properties[settings.ContainsKey("shipping_street3PropertyAlias") && !string.IsNullOrEmpty(settings["shipping_street3PropertyAlias"]) ? settings["shipping_street3PropertyAlias"] : "shipping_streetAddress3"];
                createOrderRequest.ShipTo.Address.Street4 = order.Properties[settings.ContainsKey("shipping_street4PropertyAlias") && !string.IsNullOrEmpty(settings["shipping_street4PropertyAlias"]) ? settings["shipping_street4PropertyAlias"] : "shipping_streetAddress4"];
                createOrderRequest.ShipTo.Address.City = order.Properties[settings.ContainsKey("shipping_cityPropertyAlias") && !string.IsNullOrEmpty(settings["shipping_cityPropertyAlias"]) ? settings["shipping_cityPropertyAlias"] : "shipping_city"];
                createOrderRequest.ShipTo.Address.PostalCode = order.Properties[settings.ContainsKey("shipping_postalCodePropertyAlias") && !string.IsNullOrEmpty(settings["shipping_postalCodePropertyAlias"]) ? settings["shipping_postalCodePropertyAlias"] : "shipping_zipCode"];
                if (order.ShipmentInformation.CountryRegionId != null)
                {
                    createOrderRequest.ShipTo.Address.RegionCode = CountryRegionService.Instance.Get(order.StoreId, order.ShipmentInformation.CountryRegionId.Value).RegionCode;
                }
                if (order.ShipmentInformation.CountryId != null)
                {
                    createOrderRequest.ShipTo.Address.CountryCode = CountryService.Instance.Get(order.StoreId, order.ShipmentInformation.CountryId.Value).RegionCode;
                }

                #endregion

                CreateOrderResponse createOrderResponse = client.CreateOrder(createOrderRequest);

                //Initialize payment request
                InterfaceOptions interfaceOptions = new InterfaceOptions(InterfaceId.Aero, settings["customerLanguageCode"], new Uri(teaCommerceContinueUrl), new Uri(teaCommerceCancelUrl), new Uri(teaCommerceContinueUrl));
                InitializePaymentRequest initializePaymentRequest = new InitializePaymentRequest(createOrderResponse.OrderId, order.TotalPrice.Value.WithVat, PaymentChannelId.Web, interfaceOptions);

                if (settings.ContainsKey("paymentMethods") && !string.IsNullOrEmpty(settings["paymentMethods"]))
                {
                    initializePaymentRequest.WithPaymentMethods(settings["paymentMethods"].Split(',').Select(i => PaynovaPaymentMethod.Custom(int.Parse(i))));
                }


                InitializePaymentResponse initializePaymentResponse = client.InitializePayment(initializePaymentRequest);
                htmlForm.Action = initializePaymentResponse.Url;
            }
            catch (Exception e)
            {

            }

            return htmlForm;
        }

        public override string GetContinueUrl(Order order, IDictionary<string, string> settings)
        {
            settings.MustNotBeNull("settings");
            settings.MustContainKey("urlRedirectSuccess", "settings");

            return settings["urlRedirectSuccess"];
        }

        public override string GetCancelUrl(Order order, IDictionary<string, string> settings)
        {
            settings.MustNotBeNull("settings");
            settings.MustContainKey("urlRedirectCancel", "settings");

            return settings["urlRedirectCancel"];
        }

        public override CallbackInfo ProcessCallback(Order order, HttpRequest request, IDictionary<string, string> settings)
        {
            CallbackInfo callbackInfo = null;

            try
            {
                order.MustNotBeNull("order");
                request.MustNotBeNull("request");
                settings.MustNotBeNull("settings");
                settings.MustContainKey("secretKey", "settings");

                //Write data when testing
                if (settings.ContainsKey("testMode") && settings["testMode"] == "1")
                {
                    LogRequest<Paynova>(request, logPostData: true);
                }

                PostbackDigest postbackDigest = new PostbackDigest(settings["secretKey"]);
                if (order.CartNumber == request.Form["ORDER_NUMBER"] && postbackDigest.Validate(request.Form))
                {
                    decimal amountAuthorized = decimal.Parse(request.Form["PAYMENT_1_AMOUNT"], CultureInfo.InvariantCulture);
                    string transaction = request.Form["PAYMENT_1_TRANSACTION_ID"];
                    string paymentType = request.Form["PAYMENT_1_PAYMENT_METHOD_NAME"];
                    string paymentIdentifier = request.Form["PAYMENT_1_CARD_LAST_FOUR"];

                    PaymentState? paymentState = null;
                    switch (request.Form["PAYMENT_1_STATUS"])
                    {
                        case "Pending":
                            paymentState = PaymentState.PendingExternalSystem;
                            break;
                        case "Completed":
                        case "PartiallyCompleted":
                            paymentState = PaymentState.Captured;
                            break;
                        case "Authorized":
                            paymentState = PaymentState.Authorized;
                            break;
                    }

                    if (paymentState != null)
                    {
                        callbackInfo = new CallbackInfo(amountAuthorized, transaction, paymentState.Value, paymentType, paymentIdentifier);
                    }
                }
                else
                {
                    LoggingService.Instance.Warn<Paynova>("Paynova(" + order.CartNumber + ") - digest check failed");
                }
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<Paynova>("Paynova(" + order.CartNumber + ") - Process callback", exp);
            }

            return callbackInfo;
        }

        public override ApiInfo CapturePayment(Order order, IDictionary<string, string> settings)
        {
            ApiInfo apiInfo = null;

            try
            {
                order.MustNotBeNull("order");
                settings.MustNotBeNull("settings");

                FinalizeAuthorizationRequest request = new FinalizeAuthorizationRequest(order.TransactionInformation.TransactionId, order.TransactionInformation.AmountAuthorized.Value);
                FinalizeAuthorizationResponse response = GetClient(settings).FinalizeAuthorization(request);

                apiInfo = new ApiInfo(response.TransactionId, PaymentState.Captured);
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<Paynova>("Paynova(" + order.OrderNumber + ") - Capture payment", exp);
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

                RefundPaymentRequest request = new RefundPaymentRequest(order.TransactionInformation.TransactionId, order.TransactionInformation.AmountAuthorized.Value);
                RefundPaymentResponse response = GetClient(settings).RefundPayment(request);

                apiInfo = new ApiInfo(response.TransactionId, PaymentState.Refunded);
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<Paynova>("Paynova(" + order.OrderNumber + ") - Refund payment", exp);
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

                AnnulAuthorizationRequest request = new AnnulAuthorizationRequest(order.TransactionInformation.TransactionId, order.TransactionInformation.AmountAuthorized.Value);
                GetClient(settings).AnnulAuthorization(request);

                apiInfo = new ApiInfo(order.TransactionInformation.TransactionId, PaymentState.Cancelled);
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<Paynova>("Paynova(" + order.OrderNumber + ") - Refund payment", exp);
            }

            return apiInfo;
        }

        public override string GetLocalizedSettingsKey(string settingsKey, CultureInfo culture)
        {
            switch (settingsKey)
            {
                case "urlRedirectSuccess":
                    return settingsKey + "<br/><small>e.g. /continue/</small>";
                case "urlRedirectCancel":
                    return settingsKey + "<br/><small>e.g. /cancel/</small>";
                case "paymentMethods":
                    return settingsKey + "<br/><small>e.g. 1,101</small>";
                case "testMode":
                    return settingsKey + "<br/><small>1 = true; 0 = false</small>";
                default:
                    return base.GetLocalizedSettingsKey(settingsKey, culture);
            }
        }

        protected IPaynovaClient GetClient(IDictionary<string, string> settings)
        {
            settings.MustNotBeNull("settings");
            settings.MustContainKey("merchantId", "settings");
            settings.MustContainKey("apiPassword", "settings");

            return new PaynovaClient(settings.ContainsKey("testMode") && settings["testMode"] == "1" ? "https://testapi.paynova.com/" : "https://api.paynova.com", settings["merchantId"], settings["apiPassword"]);
        }
    }
}
