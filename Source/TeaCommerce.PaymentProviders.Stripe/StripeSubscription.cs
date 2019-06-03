using Newtonsoft.Json;
using Stripe;
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
using Order = TeaCommerce.Api.Models.Order;

namespace TeaCommerce.PaymentProviders.Inline
{
    [PaymentProvider("StripeSubscription - inline")]
    public class StripeSubscription : BaseStripeProvider
    {
        public static event EventHandler<StripeSubscriptionEventArgs> StripeSubscriptionEvent;

        public void OnStripeSubscriptionEvent(StripeSubscriptionEventArgs args)
        {
            StripeSubscriptionEvent?.Invoke(this, args);
        }

        public override bool SupportsRetrievalOfPaymentStatus => false;
        public override bool SupportsCapturingOfPayment => false;
        public override bool SupportsRefundOfPayment => false;
        public override bool SupportsCancellationOfPayment => false;

        public override bool FinalizeAtContinueUrl => true;

        public override IDictionary<string, string> DefaultSettings
        {
            get
            {
                return base.DefaultSettings
                    .Union(new Dictionary<string, string> {
                        { "billing_mode", "charge" },
                        { "invoice_days_until_due", "30" }
                    })
                    .ToDictionary(k => k.Key, v => v.Value);
            }
        }

        public override PaymentHtmlForm GenerateHtmlForm(Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, string teaCommerceCommunicationUrl, IDictionary<string, string> settings)
        {
            settings.MustNotBeNull("settings");
            settings.MustContainKey("billing_mode", "settings");

            var billingMode = settings["billing_mode"];

            ValidateBillingModeSetting(billingMode);

            if (billingMode == "invoice")
            {
                return new PaymentHtmlForm { Action = teaCommerceContinueUrl };
            }

            var htmlForm = base.GenerateHtmlForm(order, teaCommerceContinueUrl, teaCommerceCancelUrl, teaCommerceCallBackUrl, teaCommerceCommunicationUrl, settings);
            htmlForm.InputFields["requires_initial_payment_method"] = "true";
            return htmlForm;
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
                    LogRequest<StripeSubscription>(request, logPostData: true);
                }

                // Get the current stripe api key based on mode
                var apiKey = settings[settings["mode"] + "_secret_key"];

                var webhookSecret = settings["webhook_secret"];
                var stripeEvent = GetWebhookStripeEvent(request, webhookSecret);

                // We are only interested in charge events
                if (stripeEvent != null && stripeEvent.Type.StartsWith("invoice."))
                {
                    var invoice = (Invoice)stripeEvent.Data.Object;
                    if (!string.IsNullOrWhiteSpace(invoice.SubscriptionId))
                    {
                        var subscriptionService = new SubscriptionService(apiKey);
                        var subscription = subscriptionService.Get(invoice.SubscriptionId);
                        if (subscription?.Metadata != null && subscription.Metadata.ContainsKey("cartNumber"))
                        {
                            cartNumber = subscription.Metadata["cartNumber"];
                        }
                    }
                }
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<StripeSubscription>("StripeSubscription - GetCartNumber", exp);
            }

            return cartNumber;
        }

        public override CallbackInfo ProcessCallback(Order order, HttpRequest request, IDictionary<string, string> settings)
        {
            try
            {
                order.MustNotBeNull("order");
                request.MustNotBeNull("request");
                settings.MustNotBeNull("settings");
                settings.MustContainKey("billing_mode", "settings");

                // Because we need more fine grained control over the callback process
                // we actually perform all the callback functionality from within the
                // ProcessRequest method instead. We only really use the callback
                // function for when the billing mode is invoice as this means
                // that the payment screen won't have been displayed and so the 
                // stripe subscription won't have been setup yet and so we need to 
                // do it now.
                var billingMode = settings["billing_mode"];

                ValidateBillingModeSetting(billingMode);

                if (billingMode == "invoice")
                {
                    ProcessCaptureRequest(order, request, settings);

                    var apiKey = settings[settings["mode"] + "_secret_key"];
                    var subscriptionService = new SubscriptionService(apiKey);
                    var subscription = subscriptionService.Get(order.Properties["stripeSubscriptionId"]);
                    var invoice = subscription.LatestInvoice ?? new InvoiceService(apiKey).Get(subscription.LatestInvoiceId);

                    return new CallbackInfo(CentsToDollars(invoice.AmountDue), invoice.Id, PaymentState.Authorized);
                }
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<StripeSubscription>("StripeSubscription(" + order.CartNumber + ") - ProcessCallback", exp);
            }

            return null;
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
                    LogRequest<StripeSubscription>(request, logPostData: true);
                }

                if (request.QueryString["action"] == "capture")
                {
                    // Init request so create the stripe entity
                    return ProcessCaptureRequest(order, request, settings);
                }
                else
                {
                    // No action defined so assume it's a webhook request
                    return ProcessWebhookRequest(order, request, settings);
                }
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<StripeSubscription>("StripeSubscription(" + order.CartNumber + ") - ProcessRequest", exp);
            }

            return response;
        }

        private string ProcessCaptureRequest(Order order, HttpRequest request, IDictionary<string, string> settings)
        {
            var apiKey = settings[settings["mode"] + "_secret_key"];
            var billingMode = settings["billing_mode"];

            ValidateBillingModeSetting(billingMode);

            var customerService = new CustomerService(apiKey);
            var subscriptionService = new SubscriptionService(apiKey);
            var invoiceService = new InvoiceService(apiKey);

            // Create the stripe customer
            var customer = customerService.Create(new CustomerCreateOptions
            {
                PaymentMethodId = billingMode == "charge"
                    ? request.Form["stripePaymentMethodId"]
                    : null,
                Email = order.PaymentInformation.Email,
                Metadata = new Dictionary<string, string>
                {
                    { "customerId", order.CustomerId }
                }
            });

            // Create subscription for customer (will auto attempt payment)
            var subscriptionOptions = new SubscriptionCreateOptions
            {
                CustomerId = customer.Id,
                Items = order.OrderLines.Select(x => new SubscriptionItemOption
                {
                    PlanId = !string.IsNullOrWhiteSpace(x.Properties["planId"])
                        ? x.Properties["planId"]
                        : x.Sku,
                    Quantity = (long)x.Quantity
                }).ToList(),
                TaxPercent = order.VatRate.Value,
                Metadata = new Dictionary<string, string>
                {
                    { "orderId", order.Id.ToString() },
                    { "cartNumber", order.CartNumber }
                },
                Expand = new[] { "latest_invoice.payment_intent" }.ToList()
            };

            if (billingMode == "charge")
            {
                subscriptionOptions.Billing = Billing.ChargeAutomatically;
                subscriptionOptions.DefaultPaymentMethodId = request.Form["stripePaymentMethodId"];
            }
            else
            {
                subscriptionOptions.Billing = Billing.SendInvoice;
                subscriptionOptions.DaysUntilDue = settings.ContainsKey("invoice_days_until_due") 
                    ? int.Parse("0" + settings["invoice_days_until_due"]) 
                    : 30;
            }

            foreach (var prop in order.Properties)
            {
                subscriptionOptions.Metadata.Add(prop.Alias, prop.Value);
            }

            var subscription = subscriptionService.Create(subscriptionOptions);
            var invoice = subscription.LatestInvoice;

            // Stash the stripe info in the order
            order.Properties.AddOrUpdate(new CustomProperty("stripeCustomerId", customer.Id) { ServerSideOnly = true });
            order.Properties.AddOrUpdate(new CustomProperty("stripeSubscriptionId", subscription.Id) { ServerSideOnly = true });
            order.Save();

            if (subscription.Status == "active" || subscription.Status == "trialing")
            {
                return JsonConvert.SerializeObject(new { success = true });
            }
            else if (invoice.PaymentIntent.Status == "requires_action")
            {
                return JsonConvert.SerializeObject(new
                {
                    requires_action = true,
                    payment_intent_client_secret = invoice.PaymentIntent.ClientSecret
                });
            }

            return JsonConvert.SerializeObject(new { error = "Invalid payment intent status" });
        }

        private string ProcessWebhookRequest(Order order, HttpRequest request, IDictionary<string, string> settings)
        {
            // Stripe supports webhooks
            var webhookSecret = settings["webhook_secret"];
            var stripeEvent = GetWebhookStripeEvent(request, webhookSecret);

            // With subscriptions, Stripe creates an invoice for each payment
            // so to ensure subscription is live, we'll listen for successful invoice payment
            if (stripeEvent.Type.StartsWith("invoice."))
            {
                var invoice = (Invoice)stripeEvent.Data.Object;
                if (order.Properties["stripeCustomerId"] == invoice.CustomerId
                    && order.Properties["stripeSubscriptionId"] == invoice.SubscriptionId)
                {
                    var eventArgs = new StripeSubscriptionEventArgs
                    {
                        Order = order,
                        Subscription = invoice.Subscription,
                        Invoice = invoice
                    };

                    switch (stripeEvent.Type)
                    {
                        case "invoice.payment_succeeded":
                            if (order.TransactionInformation.PaymentState != PaymentState.Authorized
                                && order.TransactionInformation.PaymentState != PaymentState.Captured)
                            {
                                FinalizeOrUpdateOrder(order, invoice);

                                eventArgs.Type = StripeSubscriptionEventType.SubscriptionStarted;
                            }
                            else
                            {
                                eventArgs.Type = StripeSubscriptionEventType.SubscriptionRenewed;
                            }
                            break;
                        case "invoice.payment_failed":
                            if (!string.IsNullOrWhiteSpace(order.TransactionInformation.TransactionId)
                                && invoice.Status == "past_due")
                            {
                                eventArgs.Type = StripeSubscriptionEventType.SubscriptionPastDue;
                            }
                            break;
                        case "invoice.upcoming":
                            eventArgs.Type = StripeSubscriptionEventType.SubscriptionRenewing;
                            break;
                    }

                    OnStripeSubscriptionEvent(eventArgs);
                }
            }
            else if (stripeEvent.Type.StartsWith("customer.subscription."))
            {
                var subscription = Mapper<Subscription>.MapFromJson(stripeEvent.Data.Object.ToString());
                if (order.Properties["stripeCustomerId"] == subscription.CustomerId
                    && order.Properties["stripeSubscriptionId"] == subscription.Id)
                {
                    var eventArgs = new StripeSubscriptionEventArgs
                    {
                        Order = order,
                        Subscription = subscription
                    };

                    switch (stripeEvent.Type)
                    {
                        case "customer.subscription.trial_will_end":
                            eventArgs.Type = StripeSubscriptionEventType.SubscriptionTrialEnding;
                            break;
                        case "customer.subscription.created":
                            eventArgs.Type = StripeSubscriptionEventType.SubscriptionCreated;
                            break;
                        case "customer.subscription.updated":
                            eventArgs.Type = StripeSubscriptionEventType.SubscriptionUpdated;
                            break;
                        case "customer.subscription.deleted":
                            eventArgs.Type = StripeSubscriptionEventType.SubscriptionDeleted;
                            break;
                    }

                    OnStripeSubscriptionEvent(eventArgs);
                }
            }

            return null;
        }

        public override string GetLocalizedSettingsKey(string settingsKey, CultureInfo culture)
        {
            switch (settingsKey)
            {
                case "billing_mode":
                    return settingsKey + "<br/><small>Whether to charge payments instantly via credit card or to send out Stripe invoices - charge/invoice.</small>";
                case "invoice_days_until_due":
                    return settingsKey + "<br/><small>If billing mode is set to 'invoice', the number of days untill the invoice is due.</small>";
                default:
                    return base.GetLocalizedSettingsKey(settingsKey, culture);
            }
        }

        protected void ValidateBillingModeSetting(string billingMode)
        {
            if (billingMode != "invoice" && billingMode != "charge")
            {
                throw new ArgumentException("Argument billing_mode is invalid. Must be either 'invoice' or 'charge'.");
            }
        }

        protected void FinalizeOrUpdateOrder(Order order, Invoice invoice)
        {
            var amount = CentsToDollars(invoice.AmountDue);
            var paymentState = invoice.Paid ? PaymentState.Captured : PaymentState.Authorized;

            if (!order.IsFinalized)
            {
                order.Finalize(amount, invoice.Id, paymentState);
            }
            else if (order.TransactionInformation.PaymentState != paymentState)
            {
                var currency = CurrencyService.Instance.Get(order.StoreId, order.CurrencyId);
                order.TransactionInformation.AmountAuthorized = new Amount(amount, currency);
                order.TransactionInformation.TransactionId = invoice.Id;
                order.TransactionInformation.PaymentState = paymentState;
                order.Save();
            }
        }
    }

    public class StripeSubscriptionEventArgs : EventArgs
    {
        public Order Order { get; set; }
        public Subscription Subscription { get; set; }
        public Invoice Invoice { get; set; }
        public StripeSubscriptionEventType Type { get; set; }
    }

    public enum StripeSubscriptionEventType
    {
        SubscriptionCreated,
        SubscriptionStarted,
        SubscriptionRenewing,
        SubscriptionRenewed,
        SubscriptionPastDue,
        SubscriptionTrialEnding,
        SubscriptionUpdated,
        SubscriptionDeleted
    }
}
