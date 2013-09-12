/*
 * FileName: StripeController.cs
 * Description: A MVC web hook handler for the Stripe payment provider
 * Author: Matt Brailsford (@mattbrailsford)
 * Create Date: 2013-09-12
 */
using System.Net;
using System.Net.Http;
using System.Web;
using System.Web.Http;

namespace TeaCommerce.PaymentProviders.Mvc.Web.Controllers
{
    public class StripeController : ApiController 
    {
        public HttpResponseMessage  WebHook(int storeId,
            string paymentProviderAlias,
            string mode)
        {
            if (!PaymentProviders.Stripe.ProcessWebHookRequest(storeId, 
                paymentProviderAlias, mode,
                HttpContext.Current.Request.InputStream))
            {
                return  new HttpResponseMessage(HttpStatusCode.BadRequest);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
