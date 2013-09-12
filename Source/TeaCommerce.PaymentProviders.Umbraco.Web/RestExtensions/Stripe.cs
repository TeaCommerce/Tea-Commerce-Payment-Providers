/*
 * FileName: Stripe.cs
 * Description: An Umbraco web hook handler for the Stripe payment provider
 * Author: Matt Brailsford (@mattbrailsford)
 * Create Date: 2013-09-12
 */
using System.Net;
using System.Web;
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
            var ctx = new HttpContextWrapper(HttpContext.Current);

            if (!PaymentProviders.Stripe.ProcessWebHookRequest(storeId,
                paymentProviderAlias, mode,
                ctx.Request.InputStream))
            {
                ctx.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                ctx.Response.End();
                return;
            }

            ctx.Response.StatusCode = (int)HttpStatusCode.OK;
            ctx.Response.End();
        }
    }
}
