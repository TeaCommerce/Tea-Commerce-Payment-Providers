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

namespace TeaCommerce.PaymentProviders.Classic
{

    [PaymentProvider("SagePay")]
    public class SagePay : APaymentProvider
    {
        public override string DocumentationLink { get { return "http://anders.burla.dk/umbraco/tea-commerce/using-sage-pay-with-tea-commerce/"; } }

        public override bool SupportsCapturingOfPayment { get { return true; } }
        public override bool SupportsRefundOfPayment { get { return true; } }
        public override bool SupportsCancellationOfPayment { get { return true; } }

        public override IDictionary<string, string> DefaultSettings
        {
            get
            {
                Dictionary<string, string> defaultSettings = new Dictionary<string, string>();
                defaultSettings["Vendor"] = string.Empty;
                defaultSettings["SuccessURL"] = string.Empty;
                defaultSettings["FailureURL"] = string.Empty;
                defaultSettings["TxType"] = "AUTHENTICATE";
                defaultSettings["streetAddressPropertyAlias"] = "streetAddress";
                defaultSettings["cityPropertyAlias"] = "city";
                defaultSettings["zipCodePropertyAlias"] = "zipCode";
                defaultSettings["Description"] = "A description";
                defaultSettings["testMode"] = "SIMULATOR";
                return defaultSettings;
            }
        }

        public override PaymentHtmlForm GenerateHtmlForm(Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, string teaCommerceCommunicationUrl, IDictionary<string, string> settings)
        {
            order.MustNotBeNull("order");
            settings.MustNotBeNull("settings");
            settings.MustContainKey("streetAddressPropertyAlias", "settings");
            settings.MustContainKey("cityPropertyAlias", "settings");
            settings.MustContainKey("zipCodePropertyAlias", "settings");
            settings.MustContainKey("Description", "settings");

            PaymentHtmlForm htmlForm = new PaymentHtmlForm();

            string[] settingsToExclude = new[] { "streetAddressPropertyAlias", "cityPropertyAlias", "zipCodePropertyAlias", "phonePropertyAlias", "shipping_firstNamePropertyAlias", "shipping_lastNamePropertyAlias", "shipping_streetAddressPropertyAlias", "shipping_cityPropertyAlias", "shipping_zipCodePropertyAlias", "shipping_phonePropertyAlias", "testMode" };
            Dictionary<string, string> inputFields = settings.Where(i => !settingsToExclude.Contains(i.Key)).ToDictionary(i => i.Key, i => i.Value);

            inputFields["VPSProtocol"] = "2.23";

            #region Address properties

            string streetAddress = order.Properties[settings["streetAddressPropertyAlias"]];
            string city = order.Properties[settings["cityPropertyAlias"]];
            string zipCode = order.Properties[settings["zipCodePropertyAlias"]];

            streetAddress.MustNotBeNullOrEmpty("streetAddress");
            city.MustNotBeNullOrEmpty("city");
            zipCode.MustNotBeNullOrEmpty("zipCode");

            string shippingFirstName = settings.ContainsKey("shipping_firstNamePropertyAlias") ? order.Properties[settings["shipping_firstNamePropertyAlias"]] : "";
            if (string.IsNullOrEmpty(shippingFirstName))
            {
                shippingFirstName = order.PaymentInformation.FirstName;
            }

            string shippingLastName = settings.ContainsKey("shipping_lastNamePropertyAlias") ? order.Properties[settings["shipping_lastNamePropertyAlias"]] : "";
            if (string.IsNullOrEmpty(shippingLastName))
            {
                shippingLastName = order.PaymentInformation.LastName;
            }

            string shippingStreetAddress = settings.ContainsKey("shipping_streetAddressPropertyAlias") ? order.Properties[settings["shipping_streetAddressPropertyAlias"]] : "";
            if (string.IsNullOrEmpty(shippingStreetAddress))
            {
                shippingStreetAddress = streetAddress;
            }

            string shippingCity = settings.ContainsKey("shipping_cityPropertyAlias") ? order.Properties[settings["shipping_cityPropertyAlias"]] : "";
            if (string.IsNullOrEmpty(shippingCity))
            {
                shippingCity = city;
            }

            string shippingZipCode = settings.ContainsKey("shipping_zipCodePropertyAlias") ? order.Properties[settings["shipping_zipCodePropertyAlias"]] : "";
            if (string.IsNullOrEmpty(shippingZipCode))
            {
                shippingZipCode = zipCode;
            }

            #endregion

            if (order.CartNumber.Length > 40)
            {
                throw new Exception("Cart number of the order can not exceed 40 characters.");
            }
            inputFields["VendorTxCode"] = order.CartNumber;
            inputFields["Amount"] = order.TotalPrice.Value.WithVat.ToString("0.00", CultureInfo.InvariantCulture);

            //Check that the Iso code exists
            Currency currency = CurrencyService.Instance.Get(order.StoreId, order.CurrencyId);
            if (!Iso4217CurrencyCodes.ContainsKey(currency.IsoCode))
            {
                throw new Exception("You must specify an ISO 4217 currency code for the " + currency.Name + " currency");
            }
            inputFields["Currency"] = currency.IsoCode;
            inputFields["Description"] = inputFields["Description"].Truncate(100);
            inputFields["SuccessURL"] = teaCommerceContinueUrl;
            inputFields["FailureURL"] = teaCommerceCancelUrl;
            inputFields["NotificationURL"] = teaCommerceCallBackUrl;
            inputFields["BillingSurname"] = order.PaymentInformation.LastName.Truncate(20);
            inputFields["BillingFirstnames"] = order.PaymentInformation.FirstName.Truncate(20);
            inputFields["BillingAddress1"] = streetAddress.Truncate(100);
            inputFields["BillingCity"] = city.Truncate(40);
            inputFields["BillingPostCode"] = zipCode.Truncate(10);

            Country country = CountryService.Instance.Get(order.StoreId, order.PaymentInformation.CountryId);
            inputFields["BillingCountry"] = country.RegionCode;
            if (country.RegionCode.ToUpperInvariant() == "US" && order.PaymentInformation.CountryRegionId != null)
            {
                CountryRegion countryRegion = CountryRegionService.Instance.Get(order.StoreId, order.PaymentInformation.CountryRegionId.Value);
                inputFields["BillingState"] = countryRegion.RegionCode.Truncate(2);
            }
            if (settings.ContainsKey("phonePropertyAlias"))
            {
                inputFields["BillingPhone"] = order.Properties[settings["phonePropertyAlias"]].Truncate(20);
            }

            inputFields["DeliverySurname"] = shippingLastName.Truncate(20);
            inputFields["DeliveryFirstnames"] = shippingFirstName.Truncate(20);
            inputFields["DeliveryAddress1"] = shippingStreetAddress.Truncate(100);
            inputFields["DeliveryCity"] = shippingCity.Truncate(40);
            inputFields["DeliveryPostCode"] = shippingZipCode.Truncate(10);

            country = CountryService.Instance.Get(order.StoreId, order.ShipmentInformation.CountryId ?? order.PaymentInformation.CountryId);
            inputFields["DeliveryCountry"] = country.RegionCode;

            long? shippingCountryRegionId = order.ShipmentInformation.CountryId != null ? order.ShipmentInformation.CountryRegionId : order.PaymentInformation.CountryRegionId;
            if (country.RegionCode.ToUpperInvariant() == "US" && shippingCountryRegionId != null)
            {
                CountryRegion countryRegion = CountryRegionService.Instance.Get(order.StoreId, shippingCountryRegionId.Value);
                inputFields["DeliveryState"] = countryRegion.RegionCode.Truncate(2);
            }
            if (settings.ContainsKey("shipping_phonePropertyAlias"))
            {
                inputFields["DeliveryPhone"] = order.Properties[settings["shipping_phonePropertyAlias"]].Truncate(20);
            }

            if (!settings.ContainsKey("Apply3DSecure"))
            {
                inputFields["Apply3DSecure"] = "2";
            }

            IDictionary<string, string> responseFields = GetFields(MakePostRequest(GetMethodUrl("PURCHASE", settings), inputFields));
            string status = responseFields["Status"];

            if (status == "OK" || status == "OK REPEATED")
            {
                order.Properties.AddOrUpdate(new CustomProperty("securityKey", responseFields["SecurityKey"]) { ServerSideOnly = true });
                order.Properties.AddOrUpdate(new CustomProperty("teaCommerceContinueUrl", teaCommerceContinueUrl) { ServerSideOnly = true });
                order.Properties.AddOrUpdate(new CustomProperty("teaCommerceCancelUrl", teaCommerceCancelUrl) { ServerSideOnly = true });
                order.Save();
                htmlForm.Action = responseFields["NextURL"];
            }
            else
            {
                htmlForm.Action = teaCommerceCancelUrl;
                LoggingService.Instance.Warn<SagePay>("Sage Pay(" + order.CartNumber + ") - Generate html form error - status: " + status + " | status details: " + responseFields["StatusDetail"]);
            }

            return htmlForm;
        }

        public override string GetContinueUrl(Order order, IDictionary<string, string> settings)
        {
            settings.MustNotBeNull("settings");
            settings.MustContainKey("SuccessURL", "settings");

            return settings["SuccessURL"];
        }

        public override string GetCancelUrl(Order order, IDictionary<string, string> settings)
        {
            settings.MustNotBeNull("settings");
            settings.MustContainKey("FailureURL", "settings");

            return settings["FailureURL"];
        }

        public override CallbackInfo ProcessCallback(Order order, HttpRequest request, IDictionary<string, string> settings)
        {
            CallbackInfo callbackInfo = null;

            try
            {
                order.MustNotBeNull("order");
                request.MustNotBeNull("request");
                settings.MustNotBeNull("settings");
                settings.MustContainKey("Vendor", "settings");

                //Write data when testing
                if (settings.ContainsKey("testMode") && (settings["testMode"] == "SIMULATOR" || settings["testMode"] == "TEST"))
                {
                    LogRequest<SagePay>(request, logPostData: true);
                }

                string transaction = request.Form["VPSTxId"];
                string status = request.Form["Status"];
                string cartNumber = request.Form["VendorTxCode"];
                string txAuthNo = request.Form["TxAuthNo"];
                string cardType = request.Form["CardType"];
                string last4Digits = request.Form["Last4Digits"];

                string md5CheckValue = string.Empty;
                md5CheckValue += transaction;
                md5CheckValue += cartNumber;
                md5CheckValue += status;
                md5CheckValue += txAuthNo;
                md5CheckValue += settings["Vendor"].ToLowerInvariant();
                md5CheckValue += HttpUtility.UrlDecode(request.Form["AVSCV2"]);
                md5CheckValue += order.Properties["securityKey"];
                md5CheckValue += HttpUtility.UrlDecode(request.Form["AddressResult"]);
                md5CheckValue += HttpUtility.UrlDecode(request.Form["PostCodeResult"]);
                md5CheckValue += HttpUtility.UrlDecode(request.Form["CV2Result"]);
                md5CheckValue += request.Form["GiftAid"];
                md5CheckValue += request.Form["3DSecureStatus"];
                md5CheckValue += request.Form["CAVV"];
                md5CheckValue += HttpUtility.UrlDecode(request.Form["AddressStatus"]);
                md5CheckValue += HttpUtility.UrlDecode(request.Form["PayerStatus"]);
                md5CheckValue += cardType;
                md5CheckValue += last4Digits;

                string calcedMd5Hash = GenerateMD5Hash(md5CheckValue).ToUpperInvariant();
                string vpsSignature = request.Form["VPSSignature"];

                if (order.CartNumber == cartNumber && calcedMd5Hash == vpsSignature)
                {

                    Dictionary<string, string> inputFields = new Dictionary<string, string>();

                    if (status == "OK" || status == "AUTHENTICATED" || status == "REGISTERED")
                    {
                        callbackInfo = new CallbackInfo(order.TotalPrice.Value.WithVat, transaction, request.Form["TxType"] != "PAYMENT" ? PaymentState.Authorized : PaymentState.Captured, cardType, last4Digits);

                        if (status == "OK")
                        {
                            order.Properties.AddOrUpdate(new CustomProperty("txAuthNo", txAuthNo) { ServerSideOnly = true });
                        }
                        order.Properties.AddOrUpdate(new CustomProperty("vendorTxCode", cartNumber) { ServerSideOnly = true });
                        order.Save();

                        inputFields["Status"] = "OK";
                        inputFields["RedirectURL"] = order.Properties["teaCommerceContinueUrl"];
                        inputFields["StatusDetail"] = "OK";

                    }
                    else
                    {
                        LoggingService.Instance.Warn<SagePay>("Sage Pay(" + order.CartNumber + ") - Error  in callback - status: " + status + " | status details: " + request.Form["StatusDetail"]);

                        if (status == "ERROR")
                            inputFields["Status"] = "INVALID";
                        else
                            inputFields["Status"] = "OK";

                        inputFields["RedirectURL"] = order.Properties["teaCommerceCancelUrl"];
                        inputFields["StatusDetail"] = "Error: " + status;
                    }

                    HttpContext.Current.Response.Clear();
                    HttpContext.Current.Response.Write(string.Join(Environment.NewLine, inputFields.Select(i => string.Format("{0}={1}", i.Key, i.Value)).ToArray()));
                }
                else
                {
                    LoggingService.Instance.Warn<SagePay>("Sage Pay(" + order.CartNumber + ") - VPSSignature check isn't valid - Calculated signature: " + calcedMd5Hash + " | SagePay VPSSignature: " + vpsSignature);
                }
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<SagePay>("Sage Pay(" + order.CartNumber + ") - Process callback", exp);
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
                settings.MustContainKey("Vendor", "settings");

                Dictionary<string, string> inputFields = new Dictionary<string, string>();

                Guid vendorTxCode = Guid.NewGuid();

                inputFields["VPSProtocol"] = "2.23";
                inputFields["TxType"] = "AUTHORISE";
                inputFields["Vendor"] = settings["Vendor"];
                inputFields["VendorTxCode"] = vendorTxCode.ToString();
                inputFields["Amount"] = order.TransactionInformation.AmountAuthorized.Value.ToString("0.00", CultureInfo.InvariantCulture);
                inputFields["Description"] = settings["Description"].Truncate(100);
                inputFields["RelatedVPSTxId"] = order.TransactionInformation.TransactionId;
                inputFields["RelatedVendorTxCode"] = order.CartNumber;
                inputFields["RelatedSecurityKey"] = order.Properties["securityKey"];
                inputFields["ApplyAVSCV2"] = "0";

                IDictionary<string, string> responseFields = GetFields(MakePostRequest(GetMethodUrl("AUTHORISE", settings), inputFields));

                if (responseFields["Status"] == "OK")
                {
                    order.Properties.AddOrUpdate(new CustomProperty("vendorTxCode", vendorTxCode.ToString()) { ServerSideOnly = true });
                    order.Properties.AddOrUpdate(new CustomProperty("txAuthNo", responseFields["TxAuthNo"]) { ServerSideOnly = true });
                    order.Properties.AddOrUpdate(new CustomProperty("securityKey", responseFields["SecurityKey"]) { ServerSideOnly = true });
                    order.Save();

                    apiInfo = new ApiInfo(responseFields["VPSTxId"], PaymentState.Captured);
                }
                else
                {
                    LoggingService.Instance.Warn<SagePay>("Sage pay(" + order.OrderNumber + ") - Error making API request: " + responseFields["StatusDetail"]);
                }
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<SagePay>("Sage pay(" + order.OrderNumber + ") - Cancel payment", exp);
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
                settings.MustContainKey("Vendor", "settings");
                settings.MustContainKey("Description", "settings");

                Dictionary<string, string> inputFields = new Dictionary<string, string>();

                Guid vendorTxCode = Guid.NewGuid();

                inputFields["VPSProtocol"] = "2.23";
                inputFields["TxType"] = "REFUND";
                inputFields["Vendor"] = settings["Vendor"];
                inputFields["VendorTxCode"] = vendorTxCode.ToString();
                inputFields["Amount"] = order.TransactionInformation.AmountAuthorized.Value.ToString("0.00", CultureInfo.InvariantCulture);
                //Check that the Iso code exists
                Currency currency = CurrencyService.Instance.Get(order.StoreId, order.CurrencyId);
                if (!Iso4217CurrencyCodes.ContainsKey(currency.IsoCode))
                {
                    throw new Exception("You must specify an ISO 4217 currency code for the " + currency.Name + " currency");
                }
                inputFields["Currency"] = currency.IsoCode;
                inputFields["Description"] = settings["Description"].Truncate(100);
                inputFields["RelatedVPSTxId"] = order.TransactionInformation.TransactionId;
                inputFields["RelatedVendorTxCode"] = order.Properties["vendorTxCode"];
                inputFields["RelatedSecurityKey"] = order.Properties["securityKey"];
                inputFields["RelatedTxAuthNo"] = order.Properties["txAuthNo"];
                inputFields["ApplyAVSCV2"] = "0";

                IDictionary<string, string> responseFields = GetFields(MakePostRequest(GetMethodUrl("REFUND", settings), inputFields));

                if (responseFields["Status"] == "OK")
                {
                    order.Properties.AddOrUpdate(new CustomProperty("vendorTxCode", vendorTxCode.ToString()) { ServerSideOnly = true });
                    order.Properties.AddOrUpdate(new CustomProperty("txAuthNo", responseFields["TxAuthNo"]) { ServerSideOnly = true });
                    order.Save();

                    apiInfo = new ApiInfo(responseFields["VPSTxId"], PaymentState.Refunded);
                }
                else
                {
                    LoggingService.Instance.Warn<SagePay>("Sage pay(" + order.OrderNumber + ") - Error making API request: " + responseFields["StatusDetail"]);
                }
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<SagePay>("Sage pay(" + order.OrderNumber + ") - Refund payment", exp);
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
                settings.MustContainKey("Vendor", "settings");

                Dictionary<string, string> inputFields = new Dictionary<string, string>();

                inputFields["VPSProtocol"] = "2.23";
                inputFields["TxType"] = "CANCEL";
                inputFields["Vendor"] = settings["Vendor"];
                inputFields["VendorTxCode"] = order.CartNumber;
                inputFields["VPSTxId"] = order.TransactionInformation.TransactionId;
                inputFields["SecurityKey"] = order.Properties["securityKey"];

                IDictionary<string, string> responseFields = GetFields(MakePostRequest(GetMethodUrl("CANCEL", settings), inputFields));

                if (responseFields["Status"] == "OK")
                {
                    apiInfo = new ApiInfo(order.TransactionInformation.TransactionId, PaymentState.Cancelled);
                }
                else
                {
                    LoggingService.Instance.Warn<SagePay>("Sage pay(" + order.OrderNumber + ") - Error making API request: " + responseFields["StatusDetail"]);
                }
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<SagePay>("Sage pay(" + order.OrderNumber + ") - Cancel payment", exp);
            }

            return apiInfo;
        }

        public override string GetLocalizedSettingsKey(string settingsKey, CultureInfo culture)
        {
            switch (settingsKey)
            {
                case "SuccessURL":
                    return settingsKey + "<br/><small>e.g. /continue/</small>";
                case "FailureURL":
                    return settingsKey + "<br/><small>e.g. /cancel/</small>";
                case "TxType":
                    return settingsKey + "<br/><small>PAYMENT, AUTHENTICATE</small>";
                case "testMode":
                    return settingsKey + "<br/><small>LIVE, TEST, SIMULATOR</small>";
                default:
                    return base.GetLocalizedSettingsKey(settingsKey, culture);
            }
        }

        #region Helper methods

        protected IDictionary<string, string> GetFields(string response)
        {
            return response.Split(Environment.NewLine.ToCharArray(), StringSplitOptions.RemoveEmptyEntries).ToDictionary(i => i.Substring(0, i.IndexOf("=", StringComparison.Ordinal)), i => i.Substring(i.IndexOf("=", StringComparison.Ordinal) + 1, i.Length - (i.IndexOf("=", StringComparison.Ordinal) + 1)));
        }

        protected string GetMethodUrl(string type, IDictionary<string, string> settings)
        {
            settings.MustNotBeNull("settings");
            settings.MustContainKey("testMode", "settings");

            switch (settings["testMode"].ToUpperInvariant())
            {
                case "LIVE":
                    switch (type.ToUpperInvariant())
                    {
                        case "AUTHORISE":
                            return "https://live.sagepay.com/gateway/service/authorise.vsp";
                        case "PURCHASE":
                            return "https://live.sagepay.com/gateway/service/vspserver-register.vsp";
                        case "CANCEL":
                            return "https://live.sagepay.com/gateway/service/cancel.vsp";
                        case "REFUND":
                            return "https://live.sagepay.com/gateway/service/refund.vsp";
                    }
                    break;
                case "TEST":
                    switch (type.ToUpperInvariant())
                    {
                        case "AUTHORISE":
                            return "https://test.sagepay.com/gateway/service/authorise.vsp";
                        case "PURCHASE":
                            return "https://test.sagepay.com/gateway/service/vspserver-register.vsp";
                        case "CANCEL":
                            return "https://test.sagepay.com/gateway/service/cancel.vsp";
                        case "REFUND":
                            return "https://test.sagepay.com/gateway/service/refund.vsp";
                    }
                    break;
                case "SIMULATOR":
                    switch (type.ToUpperInvariant())
                    {
                        case "AUTHORISE":
                            return "https://test.sagepay.com/simulator/vspserverGateway.asp?Service=VendorAuthoriseTx";
                        case "PURCHASE":
                            return "https://test.sagepay.com/simulator/VSPServerGateway.asp?Service=VendorRegisterTx";
                        case "CANCEL":
                            return "https://test.sagepay.com/simulator/vspserverGateway.asp?Service=VendorCancelTx";
                        case "REFUND":
                            return "https://test.sagepay.com/simulator/vspserverGateway.asp?Service=VendorRefundTx";
                    }
                    break;
            }

            return string.Empty;
        }

        #endregion

    }
}
