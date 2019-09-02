using Stripe;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
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
                    { "test_webhook_id", "" },
                    { "test_webhook_secret", "" },
                    { "live_secret_key", "" },
                    { "live_public_key", "" },
                    { "live_webhook_id", "" },
                    { "live_webhook_secret", "" },
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

            htmlForm.InputFields["store_id"] = order.StoreId.ToString();
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
                case "test_base_url":
                    return settingsKey + "<br/><small>An explicit base URL to use when generating the test webhook notification URL.</small>";
                case "test_webhook_id":
                    return settingsKey + "<br/><small>ID of the auto created test Stripe webhook. Automatically generated.</small>";
                case "test_webhook_secret":
                    return settingsKey + "<br/><small>Tive webhook signing secret for validating webhook requests. Automatically generated.</small>";
                case "live_secret_key":
                    return settingsKey + "<br/><small>Your live stripe secret key.</small>";
                case "live_public_key":
                    return settingsKey + "<br/><small>Your live stripe public key.</small>";
                case "tlive_base_url":
                    return settingsKey + "<br/><small>An explicit base URL to use when generating the live webhook notification URL.</small>";
                case "live_webhook_id":
                    return settingsKey + "<br/><small>ID of the auto created live Stripe webhook. Automatically generated.</small>";
                case "live_webhook_secret":
                    return settingsKey + "<br/><small>Live webhook signing secret for validating webhook requests. Automatically generated.</small>";
                case "mode":
                    return settingsKey + "<br/><small>The mode of the provider - test/live.</small>";
                default:
                    return base.GetLocalizedSettingsKey(settingsKey, culture);
            }
        }

        protected static void ConfigureStripe(string apiKey)
        {
            StripeConfiguration.ApiKey = apiKey;
            StripeConfiguration.MaxNetworkRetries = 2;
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

                        stripeEvent = EventUtility.ConstructEvent(json, request.Headers["Stripe-Signature"], webhookSecret, throwOnApiVersionMismatch: false);

                        HttpContext.Current.Items["TC_StripeEvent"] = stripeEvent;
                    }
                }
                catch (Exception exp)
                {
                    LoggingService.Instance.Error<BaseStripeProvider>("BaseStripeProvider - GetWebhookStripeEvent", exp);
                }
            }

            return stripeEvent;
        }

        protected static long DollarsToCents(decimal val)
        {
            var cents = val * 100M;
            var centsRounded = Math.Round(cents, MidpointRounding.AwayFromZero);
            return Convert.ToInt64(centsRounded);
        }

        protected static decimal CentsToDollars(long val)
        {
            return val / 100M;
        }

        public static void EnsureWebhookEndpointFor(Api.Models.PaymentMethod paymentMethod, string[] events)
        {
            // Check to see if we have run already in this request for this provider
            if (HttpContext.Current == null || HttpContext.Current.Items[$"{nameof(BaseStripeProvider)}_{nameof(EnsureWebhookEndpointFor)}_{paymentMethod.Id}"] != null)
                return;

            // We set a cache item so that this only runs once per request
            // because when we call .Save() in a moment, it's going to trigger
            // the Updated event handler again
            HttpContext.Current.Items[$"{nameof(BaseStripeProvider)}_{nameof(EnsureWebhookEndpointFor)}_{paymentMethod.Id}"] = 1;

            // Check to see if we have a configured mode
            var mode = paymentMethod.Settings.SingleOrDefault(x => x.Key == "mode")?.Value;
            if (string.IsNullOrWhiteSpace(mode))
                return;

            // Configure stripe
            var secretKey = paymentMethod.Settings.SingleOrDefault(x => x.Key == $"{mode}_secret_key")?.Value;
            if (string.IsNullOrWhiteSpace(secretKey))
                return;

            ConfigureStripe(secretKey);

            // Create the webhook service now as we'll need it a couple of times
            var service = new WebhookEndpointService();

            // Build the webhook URL
            var req = HttpContext.Current.Request;
            if (req == null)
                return;

            var baseUrlSetting = paymentMethod.Settings.SingleOrDefault(x => x.Key == $"{mode}_base_url")?.Value;
            var baseUrl = !string.IsNullOrWhiteSpace(baseUrlSetting)
                ? new Uri(baseUrlSetting)
                : new UriBuilder(req.Url.Scheme, req.Url.Host, req.Url.Port).Uri;
            var webhookUrl = new Uri(baseUrl, "/base/TC/PaymentCommunicationWithoutOrderId/" + paymentMethod.StoreId + "/" + paymentMethod.PaymentProviderAlias + "/" + paymentMethod.Id + ".aspx").AbsoluteUri;

            // Check to see if we already have a registered webhook
            var webhookIdKey = $"{mode}_webhook_id";
            var webhookSecretKey = $"{mode}_webhook_secret";

            var webhookIdSetting = paymentMethod.Settings.SingleOrDefault(x => x.Key == webhookIdKey);
            var webhookSecretSetting = paymentMethod.Settings.SingleOrDefault(x => x.Key == webhookSecretKey);

            var webhookId = webhookIdSetting?.Value;
            var webhookSecret = webhookSecretSetting?.Value;

            if (!string.IsNullOrWhiteSpace(webhookId) && !string.IsNullOrWhiteSpace(webhookSecret))
            {
                // We've found some credentials, so lets validate them
                try
                {
                    var webhookEndpoint = service.Get(webhookId);
                    if (webhookEndpoint != null
                        && webhookEndpoint.ApiVersion == StripeConfiguration.ApiVersion
                        && webhookEndpoint.Url == webhookUrl)
                        return;
                }
                catch (StripeException ex)
                {
                    // Somethings wrong with the webhook so lets keep going and create a new one
                }
            }

            // Create the webhook
            try
            {
                var options = new WebhookEndpointCreateOptions
                {
                    Url = webhookUrl,
                    EnabledEvents = new List<string>(events),
                    ApiVersion = StripeConfiguration.ApiVersion
                };
                var newWebhookEndpoint = service.Create(options);

                // Remove settings if they exist (if we got here, it must mean they are invalid in some way)
                if (webhookIdSetting != null)
                    paymentMethod.Settings.Remove(webhookIdSetting);
                if (webhookSecretSetting != null)
                    paymentMethod.Settings.Remove(webhookSecretSetting);

                // Save the settings
                paymentMethod.Settings.Add(new Api.Models.PaymentMethodSetting(webhookIdKey, newWebhookEndpoint.Id));
                paymentMethod.Settings.Add(new Api.Models.PaymentMethodSetting(webhookSecretKey, newWebhookEndpoint.Secret));

                paymentMethod.Save();
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<BaseStripeProvider>("BaseStripeProvider - EnsureWebhookEndpoint", exp);
            }
        }
    }
}
