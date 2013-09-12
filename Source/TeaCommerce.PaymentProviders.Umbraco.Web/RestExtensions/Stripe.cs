/*
 * FileName: Stripe.cs
 * Description: A web hook handler for the Stripe payment provider
 * Author: Matt Brailsford (@mattbrailsford)
 * Create Date: 2013-09-12
 */
using System;
using System.IO;
using System.Net;
using System.Web;
using Stripe;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Services;
using umbraco.presentation.umbracobase;

namespace TeaCommerce.PaymentProviders.Umbraco.Web.RestExtensions
{
    [RestExtension("Stripe")]
    public class Stripe
    {
        [RestExtensionMethod(returnXml = false)]
        public static void WebHook(int storeId,
            string paymentProviderAlias,
            string mode)
        {
            var ctx = HttpContext.Current;
            var req = ctx.Request.InputStream;
            req.Seek(0, SeekOrigin.Begin);

            var json = new StreamReader(req).ReadToEnd();
            StripeEvent stripeEvent;

            try
            {
                stripeEvent = StripeEventUtility.ParseEvent(json);
            }
            catch (Exception ex)
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                ctx.Response.Write("Unable to parse incoming event");
                ctx.Response.End();
                return;
            }

            if (stripeEvent == null)
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                ctx.Response.Write("Incoming event empty");
                ctx.Response.End();
                return;
            }

            switch (stripeEvent.Type)
            {
                case "charge.refunded":
                    StripeCharge refundedCharge = Mapper<StripeCharge>.MapFromJson(stripeEvent.Data.Object.ToString());
                    var refundedOrder = OrderService.Instance.Get(storeId, Guid.Parse(refundedCharge.Description));
                    if (refundedOrder.TransactionInformation.PaymentState != PaymentState.Refunded)
                    {
                        refundedOrder.TransactionInformation.TransactionId = refundedCharge.Id;
                        refundedOrder.TransactionInformation.PaymentState = PaymentState.Refunded;
                        refundedOrder.Save();
                    }
                    break;
                case "charge.captured":
                    StripeCharge capturedCharge = Mapper<StripeCharge>.MapFromJson(stripeEvent.Data.Object.ToString());
                    var capturedOrder = OrderService.Instance.Get(storeId, Guid.Parse(capturedCharge.Description));
                    if (capturedOrder.TransactionInformation.PaymentState != PaymentState.Captured)
                    {
                        capturedOrder.TransactionInformation.TransactionId = capturedCharge.Id;
                        capturedOrder.TransactionInformation.PaymentState = PaymentState.Captured;
                        capturedOrder.Save();
                    }
                    break;
                case "charge.succeeded":
                case "charge.failed":
                    // We can hook up these up if we want to, but I don't think it makes sense
                    // these should get handled during the checkout process so I think it
                    // should only be the events that can happen "after" the sale that 
                    // should be proccessed (MB)
                    break;
            }

            ctx.Response.StatusCode = (int)HttpStatusCode.OK;
            ctx.Response.End();
        }
    }
}
