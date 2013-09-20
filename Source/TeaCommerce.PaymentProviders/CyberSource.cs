using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using System.Web.Hosting;
using System.Web.Script.Serialization;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;

namespace TeaCommerce.PaymentProviders
{
    [PaymentProvider("CyberSource")]
    public class CyberSource : APaymentProvider
    {
        protected const string TestTransactionEndpoint = "https://testsecureacceptance.cybersource.com/silent/pay";
        protected const string LiveTransactionEndpoint = "https://secureacceptance.cybersource.com/silent/pay";

        public override IDictionary<string, string> DefaultSettings
        {
            get
            {
                return new Dictionary<string, string>
                {
                    { "form_url", "" },
                    { "continue_url", "" },
                    { "cancel_url", "" },
                    { "profile_id", "" },
                    { "access_key", "" },
                    { "secret_key", "" },
                    { "mode", "test" }
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

            var currency = CurrencyService.Instance.Get(order.StoreId, order.CurrencyId);

            form.InputFields.Add("form_url", settings["mode"] == "live" ? LiveTransactionEndpoint : TestTransactionEndpoint);
            form.InputFields.Add("callback_url", teaCommerceCallBackUrl);
            form.InputFields.Add("continue_url", teaCommerceContinueUrl);
            form.InputFields.Add("cancel_url", teaCommerceCancelUrl);
            form.InputFields.Add("profile_id", settings["profile_id"]);
            form.InputFields.Add("access_key", settings["access_key"]);

            form.InputFields.Add("cart_number", order.CartNumber);
            form.InputFields.Add("amount", order.TotalPrice.WithVat.ToString("0.00"));
            form.InputFields.Add("currency", currency.IsoCode);

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

        public override CallbackInfo ProcessCallback(Order order, HttpRequest request, 
            IDictionary<string, string> settings)
        {
            try
            {
                order.MustNotBeNull("order");
                request.MustNotBeNull("request");
                settings.MustNotBeNull("settings");

                // Check to see if it's a signature request
                if (request.QueryString.AllKeys.Any(x => x == "sign"))
                {
                    return ProcessCallback_Sign(order, request, settings);
                }
                else
                {
                    return ProcessCallback_Receipt(order, request, settings);
                }
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Log(exp, "CyberSource(" + order.CartNumber + ") - ProcessCallback");
            }

            return null;
        }

        protected CallbackInfo ProcessCallback_Sign(Order order, HttpRequest request,
            IDictionary<string, string> settings)
        {
            if (settings["mode"] != "live")
                LogCallback(order, request, settings, "cybersource-callback-sign.txt");

            try
            {
                var signatureTimestamp = DateTime.Now.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
                var signature = Sign(request.Form, signatureTimestamp, settings["secret_key"]);

                OutputJson(new
                {
                    error = false,
                    signature_timestamp = signatureTimestamp,
                    signature = signature
                });
            }
            catch (Exception ex)
            {
                OutputJson(new
                {
                    error = true,
                    error_message = ex.Message
                });
            }

            return null;
        }

        protected CallbackInfo ProcessCallback_Receipt(Order order, HttpRequest request,
            IDictionary<string, string> settings)
        {
            if (settings["mode"] != "live")
                LogCallback(order, request, settings, "cybersource-callback-receipt.txt");

            bool error = false;
            string errorMessage = null;

            // Check the signature
            var signatureTimestamp = request.Form["signed_date_time"];
            var signature = Sign(request.Form, signatureTimestamp, settings["secret_key"]);
            if (signature != request.Form["signature"])
            {
                error = true;
                errorMessage = "Payment signature cannot be verified.";
            }

            // Check for any negative decisions
            if (!error)
            {
                //TODO: Maybe show more friendlier errors
                switch (request.Form["decision"])
                {
                    case "ERROR":
                    case "DECLINE":
                    case "CANCEL":
                    case "REVIEW":
                        error = true;
                        errorMessage = request.Form["message"];
                        break;
                }
            }

            // Check to see if any errors were recorded
            if (error)
            {
                // Because both errors and success messages all come through
                // this callback handler, we need a way to get back to the 
                // payment page. As this page requires coming from the payment
                // form, we redraw the payment form but with an auto submit
                // script to force it to jump straight to the payment page.
                // It's not the nicest solution, but short of introducing
                // an interim page, unfortunately we don't have much option.

                // Pass through request fields
                var additionalFields = new StringBuilder("<input type=\"hidden\" name=\"error_message\" value=\"" + errorMessage + "\" />");
                foreach (var formFieldKey in request.Form.AllKeys)
                {
                    additionalFields.AppendFormat("<input type=\"hidden\" name=\"{0}\" value=\"{1}\" />",
                        formFieldKey,
                        request.Form[formFieldKey]);
                }

                // Regenerate the payment form appending additional fields
                var form = PaymentMethodService.Instance.Get(order.StoreId, order.PaymentInformation.PaymentMethodId.Value)
                    .GeneratePaymentForm(order, additionalFields.ToString());

                // Force the form to auto submit
                form += "<script type=\"text/javascript\">document.forms[0].submit();</script>";

                // Write out the form
                HttpContext.Current.Response.Clear();
                HttpContext.Current.Response.Write(form);

                //TODO: Maybe show a processing graphic incase postback lags?

                return null;
            }

            // Redirect to continue URL (but don't end processing just yet)
            HttpContext.Current.Response.Redirect(GetContinueUrl(order, settings), false);

            // If we've got this far, we assume the payment was successfull
            return new CallbackInfo(decimal.Parse(request.Form["auth_amount"]),
                request.Form["req_transaction_uuid"],
                PaymentState.Captured);
        }

        public override string GetLocalizedSettingsKey(string settingsKey, CultureInfo culture)
        {
            switch (settingsKey)
            {
                case "form_url":
                    return settingsKey + "<br/><small>The url of the page with the CyberSource payment form on.</small>";
                case "continue_url":
                    return settingsKey + "<br/><small>The url to navigate to after payment is processed.</small>";
                case "cancel_url":
                    return settingsKey + "<br/><small>The url to navigate to if the user wants to cancel the payment process.</small>";
                case "profile_id":
                    return settingsKey + "<br/><small>The CyberSource profile id.</small>";
                case "access_key":
                    return settingsKey + "<br/><small>The CyberSource access key.</small>";
                case "secret_key":
                    return settingsKey + "<br/><small>The CyberSource secret key.</small>";
                case "mode":
                    return settingsKey + "<br/><small>The mode of the provider.<br />Can be either 'test' or 'live'.</small>";
                default:
                    return base.GetLocalizedSettingsKey(settingsKey, culture);
            }
        }

        protected String Sign(NameValueCollection paramsArray,
            string signatureTimestamp,
            string secretKey)
        {
            return Sign(BuildDataToSign(paramsArray.AllKeys.ToDictionary(k => k,
                        k => paramsArray[k]), signatureTimestamp), secretKey);
        }

        protected String Sign(IDictionary<string, string> paramsArray,
            string signatureTimestamp,
            string secretKey)
        {
            return Sign(BuildDataToSign(paramsArray, signatureTimestamp), secretKey);
        }

        protected String Sign(string data, 
            string secretKey)
        {
            var encoding = new UTF8Encoding();
            var keyByte = encoding.GetBytes(secretKey);

            var hmacsha256 = new HMACSHA256(keyByte);
            var messageBytes = encoding.GetBytes(data);

            return Convert.ToBase64String(hmacsha256.ComputeHash(messageBytes));
        }

        protected String BuildDataToSign(IDictionary<string, string> paramsArray,
            string signatureTimestamp)
        {
            var signedFieldNames = paramsArray["signed_field_names"].Split(',');

            var dataToSign = signedFieldNames.Select(signedFieldName =>
                signedFieldName + "=" + (signedFieldName == "signed_date_time" ? signatureTimestamp : paramsArray[signedFieldName]))
                .ToList();

            return String.Join(",", dataToSign);
        }

        protected void OutputJson(object obj)
        {
            HttpContext.Current.Response.Clear();
            HttpContext.Current.Response.ContentType = "application/json";
            HttpContext.Current.Response.Write(new JavaScriptSerializer().Serialize(obj));
        }

        protected void LogCallback(Order order, HttpRequest request,
            IDictionary<string, string> settings,
            string fileName)
        {
            using (var sw = new StreamWriter(File.Create(HostingEnvironment.MapPath("~/" + fileName))))
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
                sw.WriteLine("");
                sw.WriteLine("-----------------------------------------------------");
                sw.WriteLine("");
                sw.WriteLine("Settings:");
                foreach (string k in settings.Keys)
                {
                    sw.WriteLine(k + " : " + settings[k]);
                }
                sw.Flush();
            }
        }
    }
}
