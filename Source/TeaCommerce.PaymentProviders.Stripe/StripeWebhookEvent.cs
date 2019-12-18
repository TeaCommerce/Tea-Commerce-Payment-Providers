using Newtonsoft.Json;

namespace TeaCommerce.PaymentProviders
{
    // Stripped down Stripe webhook event which should
    // hopefully work regardless of webhook API version.
    // We are essentially grabbing the most basic info
    // and then we use the API to fetch the entity in 
    // question so that it is fetched using the payment
    // providers API version.
    public class StripeWebhookEvent
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("data")]
        public StripeWebhookEventData Data { get; set; }
    }

    public class StripeWebhookEventData
    {
        [JsonProperty("object")]
        public StripeWebhookEventObject Object { get; set; }
    }

    public class StripeWebhookEventObject
    {
        [JsonProperty("id")]
        public string Id { get; set; }

        [JsonProperty("object")]
        public string Type { get; set; }

        [JsonIgnore]
        public object Instance { get; set; }
    }
}
