using Stripe;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;
using Order = TeaCommerce.Api.Models.Order;

namespace TeaCommerce.PaymentProviders.Inline
{
    public abstract class BaseStripeProvider : APaymentProvider
    {
        public override string DocumentationLink { get { return "https://stripe.com/docs"; } }

        public override bool FinalizeAtContinueUrl { get { return true; } }

        public override IDictionary<string, string> DefaultSettings
        {
            get
            {
                return new Dictionary<string, string> {
                    { "form_url", "" },
                    { "continue_url", "" },
                    { "cancel_url", "" },
                    { "billing_address_line1_property_alias", "streetAddress" },
                    { "billing_address_line2_property_alias", "" },
                    { "billing_city_property_alias", "city" },
                    { "billing_state_property_alias", "" },
                    { "billing_zip_code_property_alias", "zipCode" },
                    { "test_secret_key", "" },
                    { "test_public_key", "" },
                    { "live_secret_key", "" },
                    { "live_public_key", "" },
                    { "webhook_secret", "" },
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

            var htmlForm = new PaymentHtmlForm
            {
                Action = settings["form_url"]
            };

            // Copy all settings except those in default settings list
            // as all default settings are handled explicitly below
            htmlForm.InputFields = settings.Where(i => !DefaultSettings.ContainsKey(i.Key)).ToDictionary(i => i.Key, i => i.Value);

            htmlForm.InputFields["api_key"] = settings[settings["mode"] + "_public_key"];
            htmlForm.InputFields["continue_url"] = teaCommerceContinueUrl;
            htmlForm.InputFields["cancel_url"] = teaCommerceCancelUrl;

            htmlForm.InputFields["api_url"] = teaCommerceCommunicationUrl;

            htmlForm.InputFields["billing_firstname"] = order.PaymentInformation.FirstName;
            htmlForm.InputFields["billing_lastname"] = order.PaymentInformation.LastName;
            htmlForm.InputFields["billing_email"] = order.PaymentInformation.Email;

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

        protected Event GetWebhookStripeEvent(HttpRequest request, string webhookSecret)
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
                        var json = reader.ReadToEnd();

                        stripeEvent = EventUtility.ConstructEvent(json, request.Headers["Stripe-Signature"], webhookSecret);

                        HttpContext.Current.Items["TC_StripeEvent"] = stripeEvent;
                    }
                }
                catch
                { }
            }

            return stripeEvent;
        }

        protected static long DollarsToCents(decimal val)
        {
            return (long)Math.Round(val * 100M, MidpointRounding.AwayFromZero);
        }

        protected static decimal CentsToDollars(long val)
        {
            return (decimal)val / 100;
        }
    }
}
