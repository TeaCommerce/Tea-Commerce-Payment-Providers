using Stripe;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Web;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Models;
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

        public override bool SupportsRetrievalOfPaymentStatus { get { return false; } }
        public override bool SupportsCapturingOfPayment { get { return false; } }
        public override bool SupportsRefundOfPayment { get { return false; } }
        public override bool SupportsCancellationOfPayment { get { return false; } }

        public override IDictionary<string, string> DefaultSettings
        {
            get
            {
                return base.DefaultSettings
                    .Union(new Dictionary<string, string> {
                        { "billing_mode", "charge" }
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

            return base.GenerateHtmlForm(order, teaCommerceContinueUrl, teaCommerceCancelUrl, teaCommerceCallBackUrl, teaCommerceCommunicationUrl, settings);
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

                var stripeEvent = GetStripeEvent(request);

                // We are only interested in charge events
                if (stripeEvent != null && stripeEvent.Type.StartsWith("invoice."))
                {
                    var invoice = Mapper<Invoice>.MapFromJson(stripeEvent.Data.Object.ToString());
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
                else
                {
                    HttpContext.Current.Response.StatusCode = (int)HttpStatusCode.BadRequest;
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
            CallbackInfo callbackInfo = null;

            try
            {
                order.MustNotBeNull("order");
                request.MustNotBeNull("request");
                settings.MustNotBeNull("settings");
                settings.MustContainKey("mode", "settings");
                settings.MustContainKey("billing_mode", "settings");
                settings.MustContainKey(settings["mode"] + "_secret_key", "settings");

                // If in test mode, write out the form data to a text file
                if (settings.ContainsKey("mode") && settings["mode"] == "test")
                {
                    LogRequest<StripeSubscription>(request, logPostData: true);
                }

                var billingMode = settings["billing_mode"];

                ValidateBillingModeSetting(billingMode);

                var apiKey = settings[settings["mode"] + "_secret_key"];

                // Create the stripe customer
                var customerService = new CustomerService(apiKey);
                var customer = customerService.Create(new CustomerCreateOptions
                {
                    Email = order.PaymentInformation.Email,
                    SourceToken = billingMode == "charge"
                        ? request.Form["stripeToken"]
                        : null,
                    Metadata = new Dictionary<string, string>
                    {
                        { "customerId", order.CustomerId }
                    }
                });

                // Subscribe customer to plan(s)
                var subscriptionService = new SubscriptionService(apiKey);
                var subscriptionOptions = new SubscriptionCreateOptions
                {
                    CustomerId = customer.Id,
                    Billing = billingMode == "charge"
                        ? Billing.ChargeAutomatically
                        : Billing.SendInvoice,
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
                    }
                };

                foreach(var prop in order.Properties)
                {
                    subscriptionOptions.Metadata.Add(prop.Alias, prop.Value);
                }

                var subscription = subscriptionService.Create(subscriptionOptions);

                // Stash the stripe info in the order
                order.Properties.AddOrUpdate("stripeCustomerId", customer.Id);
                order.Properties.AddOrUpdate("stripeSubscriptionId", subscription.Id);
                order.Save();

                // Authorize the payment. We'll capture it on a successful webhook callback
                callbackInfo = new CallbackInfo(CentsToDollars(subscription.Plan.Amount.Value), subscription.Id, PaymentState.Authorized);

            }
            catch (StripeException e)
            {
                ReturnToPaymentFormWithException(order, request, e);
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<StripeSubscription>("StripeSubscription(" + order.CartNumber + ") - ProcessCallback", exp);
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
                    LogRequest<StripeSubscription>(request, logPostData: true);
                }

                // Stripe supports webhooks
                var stripeEvent = GetStripeEvent(request);

                // With subscriptions, Stripe creates an invoice for each payment
                // so to ensure subscription is live, we'll listen for successful invoice payment
                if (stripeEvent.Type.StartsWith("invoice."))
                {
                    var invoice = Mapper<Invoice>.MapFromJson(stripeEvent.Data.Object.ToString());
                    if (order.Properties["stripeCustomerId"] == invoice.CustomerId
                        && order.Properties["stripeSubscriptionId"] == invoice.SubscriptionId)
                    {
                        var eventArgs = new StripeSubscriptionEventArgs
                        {
                            Order = order,
                            Subscription = invoice.Subscription,
                            Invoice = invoice
                        };

                        switch(stripeEvent.Type)
                        {
                            case "invoice.payment_succeeded":
                                if (order.TransactionInformation.PaymentState == PaymentState.Initialized
                                    || order.TransactionInformation.PaymentState == PaymentState.Authorized)
                                {
                                    order.TransactionInformation.TransactionId = invoice.ChargeId;
                                    order.TransactionInformation.PaymentState = PaymentState.Captured;
                                    order.Save();

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
            }
            catch (Exception exp)
            {
                LoggingService.Instance.Error<StripeSubscription>("StripeSubscription(" + order.CartNumber + ") - ProcessRequest", exp);
            }

            return response;
        }

        public override string GetLocalizedSettingsKey(string settingsKey, CultureInfo culture)
        {
            switch (settingsKey)
            {
                case "billing_mode":
                    return settingsKey + "<br/><small>Whether to charge payments instantly via credit card or to send out Stripe invoices - charge/invoice.</small>";
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
