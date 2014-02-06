using System.Diagnostics;
using Klarna.Checkout;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using TeaCommerce.Data;
using TeaCommerce.Data.Payment;
using umbraco.BusinessLogic;
using KlarnaOrder = Klarna.Checkout.Order;
using Order = TeaCommerce.Data.Order;

namespace TeaCommerce.PaymentProviders {

  public class Klarna : APaymentProvider {

    protected const string KlarnaApiRequestContentType = "application/vnd.klarna.checkout.aggregated-order-v2+json";
    protected string formPostUrl;

    public override bool AllowsGetStatus { get { return false; } }
    public override bool AllowsCancelPayment { get { return false; } }
    public override bool AllowsCapturePayment { get { return false; } }
    public override bool AllowsRefundPayment { get { return false; } }

    public override Dictionary<string, string> DefaultSettings {
      get {
        if ( defaultSettings == null ) {
          defaultSettings = new Dictionary<string, string>();
          defaultSettings[ "merchant.id" ] = "";
          defaultSettings[ "locale" ] = "sv-se";
          defaultSettings[ "paymentFormUrl" ] = "";
          defaultSettings[ "merchant.confirmation_uri" ] = "";
          defaultSettings[ "merchant.terms_uri" ] = "";
          defaultSettings[ "sharedSecret" ] = "";
          defaultSettings[ "productNumberPropertyAlias" ] = "productNumber";
          defaultSettings[ "productNamePropertyAlias" ] = "productName";
          defaultSettings[ "shippingMethodProductNumber" ] = "1000";
          defaultSettings[ "shippingMethodFormatString" ] = "Shipping fee ({0})";
          defaultSettings[ "paymentMethodProductNumber" ] = "2000";
          defaultSettings[ "paymentMethodFormatString" ] = "Payment fee ({0})";
          defaultSettings[ "testMode" ] = "1";
        }
        return defaultSettings;
      }
    }

    public override string FormPostUrl { get { return formPostUrl; } }

    public override Dictionary<string, string> GenerateForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, string teaCommerceCommunicationUrl, Dictionary<string, string> settings ) {
      Dictionary<string, string> inputFields = new Dictionary<string, string>();

      formPostUrl = settings[ "paymentFormUrl" ];
      if ( order.PaymentFeeVAT != 0 ) {
        throw new ArgumentException( "The Klarna payment provider does not accept a payment provider price." );
      }

      order.AddProperty( new OrderProperty( "teaCommerceCommunicationUrl", teaCommerceCommunicationUrl, true ) );
      order.AddProperty( new OrderProperty( "teaCommerceContinueUrl", teaCommerceContinueUrl, true ) );
      order.AddProperty( new OrderProperty( "teaCommerceCallbackUrl", teaCommerceCallBackUrl, true ) );
      order.Save();

      return inputFields;
    }

    public override string GetContinueUrl( Dictionary<string, string> settings ) {
      return settings[ "merchant.confirmation_uri" ];
    }

    public override string GetCancelUrl( Dictionary<string, string> settings ) {
      return ""; //not used in Klarna
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, Dictionary<string, string> settings ) {
      CallbackInfo callbackInfo = null;

      IConnector connector = Connector.Create( settings[ "sharedSecret" ] );
      KlarnaOrder klarnaOrder = new KlarnaOrder( connector, new Uri( order.Properties.First( i => i.Alias.Equals( "klarnaLocation" ) ).Value ) ) {
        ContentType = KlarnaApiRequestContentType
      };
      klarnaOrder.Fetch();

      if ( (string)klarnaOrder.GetValue( "status" ) == "checkout_complete" ) {

        //We need to populate the order with the information entered into Klarna.
        SaveOrderPropertiesFromKlarnaCallback( order, klarnaOrder );
        decimal amount = ( (JObject)klarnaOrder.GetValue( "cart" ) )[ "total_price_including_tax" ].Value<decimal>() / 100M;
        string klarnaId = klarnaOrder.GetValue( "id" ).ToString();

        callbackInfo = new CallbackInfo( order.Name, amount, klarnaId, PaymentStatus.Authorized, string.Empty, string.Empty );

        klarnaOrder.Update( new Dictionary<string, object>() { { "status", "created" } } );
      } else {
        string errorMessage = "Tea Commerce - Klarna - Trying to process a callback from Klarna with an order that isn't completed";
        callbackInfo = new CallbackInfo( errorMessage );
        Log.Add( LogTypes.Error, -1, errorMessage );
      }

      return callbackInfo;
    }

    protected virtual void SaveOrderPropertiesFromKlarnaCallback( Order order, KlarnaOrder klarnaOrder ) {

      //Some order properties in Tea Commerce comes with a special alias, 
      //defining a mapping of klarna propteries to these aliases.
      //Store store = StoreService.Instance.Get( order.StoreId );
      Dictionary<string, string> magicOrderPropertyAliases = new Dictionary<string, string>{
		    { "billing_address.given_name", TeaCommerceSettings.FirstNamePropertyAlias },
		    { "billing_address.family_name", TeaCommerceSettings.LastNamePropertyAlias },
		    { "billing_address.email", TeaCommerceSettings.EmailPropertyAlias },
	    };


      //The klarna properties we wish to save on the order.

      List<string> klarnaPropertyAliases = new List<string>{ 
		    "billing_address.given_name",
		    "billing_address.family_name",
		    "billing_address.care_of",
		    "billing_address.street_address",
		    "billing_address.postal_code",
		    "billing_address.city",
		    "billing_address.email",
		    "billing_address.phone",
		    "shipping_address.given_name",
		    "shipping_address.family_name",            
		    "shipping_address.care_of",
		    "shipping_address.street_address",
		    "shipping_address.postal_code",
		    "shipping_address.city",
		    "shipping_address.email",
		    "shipping_address.phone" ,
  	  };

      Dictionary<string, object> klarnaProperties = klarnaOrder.Marshal();

      foreach ( string klarnaPropertyAlias in klarnaPropertyAliases ) {
        //if a property mapping exists then use the magic alias, otherwise use the property name itself.
        string tcOrderPropertyAlias = magicOrderPropertyAliases.ContainsKey( klarnaPropertyAlias ) ? magicOrderPropertyAliases[ klarnaPropertyAlias ] : klarnaPropertyAlias;

        string klarnaPropertyValue = "";
        /* Some klarna properties are of the form parent.child 
         * in which case the lookup in klarnaProperties 
         * needs to be (in pseudocode) 
         * klarnaProperties[parent].getValue(child) .
         * In the case that there is no '.' we assume that 
         * klarnaProperties[klarnaPropertyAlias].ToString() 
         * contains what we need. 
         */
        string[] klarnaPropertyParts = klarnaPropertyAlias.Split( '.' );
        if ( klarnaPropertyParts.Length == 1 && klarnaProperties.ContainsKey( klarnaPropertyAlias ) ) {
          klarnaPropertyValue = klarnaProperties[ klarnaPropertyAlias ].ToString();
        } else if ( klarnaPropertyParts.Length == 2 && klarnaProperties.ContainsKey( klarnaPropertyParts[ 0 ] ) ) {
          JObject parent = klarnaProperties[ klarnaPropertyParts[ 0 ] ] as JObject;
          if ( parent != null ) {
            JToken value = parent.GetValue( klarnaPropertyParts[ 1 ] );
            klarnaPropertyValue = value != null ? value.ToString() : "";
          }
        }

        if ( !string.IsNullOrEmpty( klarnaPropertyValue ) ) {
          order.AddProperty( new OrderProperty( tcOrderPropertyAlias, klarnaPropertyValue ) );
        }
      }
      // order was passed as reference and updated. Saving it now.
      order.Save();
    }

    public override string ProcessRequest( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      string response = "";

      string communicationType = request[ "communicationType" ];

      KlarnaOrder klarnaOrder = null;
      IConnector connector = Connector.Create( settings[ "sharedSecret" ] );

      if ( communicationType == "checkout" ) {

        //Cart information
        List<Dictionary<string, object>> cartItems = order.OrderLines.Select( orderLine =>
          new Dictionary<string, object> {
              { "reference", orderLine.Properties.Single( op => op.Alias.Equals( settings[ "productNumberPropertyAlias" ] ) ).Value }, 
              { "name", orderLine.Properties.Single( op => op.Alias.Equals( settings[ "productNamePropertyAlias" ] ) ).Value }, 
              { "quantity", (int) orderLine.Quantity },
              { "unit_price", (int) (orderLine.UnitPrice * 100M) },
              { "tax_rate", (int) (orderLine.VAT * 10000M) }
            } )
        .ToList();

        if ( order.ShippingFee != 0 ) {
          cartItems.Add( new Dictionary<string, object> {
              { "type", "shipping_fee" },
              { "reference", settings[ "shippingMethodProductNumber" ]},
              { "name", string.Format( settings[ "shippingMethodFormatString" ], order.ShippingMethod.Name )},
              { "quantity", 1},
              { "unit_price", (int) (order.ShippingFee * 100M) },
              { "tax_rate",  (int) (order.ShippingVAT * 10000M) }
            } );
        }

        Dictionary<string, object> data = new Dictionary<string, object> { { "cart", new Dictionary<string, object> { { "items", cartItems } } } };
        OrderProperty klarnaLocation = order.Properties.FirstOrDefault(i => i.Alias == "klarnaLocation" );

        //Check if the order has a Klarna location URI property - then we try and update the order
        if ( klarnaLocation != null && !string.IsNullOrEmpty( klarnaLocation.Value ) ) {
          try {
            klarnaOrder = new KlarnaOrder( connector, new Uri( klarnaLocation.Value ) ) {
              ContentType = KlarnaApiRequestContentType
            };
            klarnaOrder.Fetch();
            klarnaOrder.Update( data );
          } catch ( Exception ) {
            //Klarna cart session has expired and we make sure to remove the Klarna location URI property
            klarnaOrder = null;
          }
        }

        //If no Klarna order was found to update or the session expired - then create new Klarna order
        if ( klarnaOrder == null ) {
          string termsUrl = settings[ "merchant.terms_uri" ];
          if ( !termsUrl.StartsWith( "http" ) ) {
            termsUrl = new Uri( new UriBuilder( HttpContext.Current.Request.Url.Scheme, HttpContext.Current.Request.Url.Host, HttpContext.Current.Request.Url.Port ).Uri, termsUrl ).AbsoluteUri;
          }

          //Merchant information
          data[ "merchant" ] = new Dictionary<string, object> {
            {"id", settings[ "merchant.id" ]},
            {"terms_uri", termsUrl},
            {"checkout_uri", request.UrlReferrer.ToString()},
            {"confirmation_uri", order.Properties.First( i => i.Alias.Equals( "teaCommerceContinueUrl" ) ).Value},
            {"push_uri", order.Properties.First( i => i.Alias.Equals( "teaCommerceCallbackUrl" ) ).Value}
          };

          data[ "merchant_reference" ] = new Dictionary<string, object>() {
            {"orderid1", order.Name}
          };

          data[ "purchase_country" ] = order.Country.CountryCode;
          data[ "purchase_currency" ] = order.CurrencyISOCode;
          data[ "locale" ] = settings[ "locale" ];

          klarnaOrder = new KlarnaOrder( connector ) {
            BaseUri = settings.ContainsKey( "testMode" ) && settings[ "testMode" ] == "1" ? new Uri( "https://checkout.testdrive.klarna.com/checkout/orders" ) : new Uri( "https://checkout.klarna.com/checkout/orders" ),
            ContentType = KlarnaApiRequestContentType
          };

          //Create new order
          klarnaOrder.Create( data );
          klarnaOrder.Fetch();
          order.AddProperty( new OrderProperty( "klarnaLocation", klarnaOrder.Location.ToString(), true ) );
          order.Save();
        }
      } else if ( communicationType == "confirmation" ) {
        //get confirmation response
        string klarnaLocation = order.Properties.First( i => i.Alias.Equals( "klarnaLocation" ) ).Value;

        if ( !string.IsNullOrEmpty( klarnaLocation ) ) {
          //Fetch and show confirmation page if status is not checkout_incomplete
          klarnaOrder = new KlarnaOrder( connector, new Uri( klarnaLocation ) ) {
            ContentType = KlarnaApiRequestContentType
          };
          klarnaOrder.Fetch();

          if ( (string)klarnaOrder.GetValue( "status" ) == "checkout_incomplete" ) {
            throw new Exception( "Confirmation page reached without a Klarna order that is finished" );
          }
        }
      }

      //Get the JavaScript snippet from the Klarna order
      if ( klarnaOrder != null ) {
        JObject guiElement = klarnaOrder.GetValue( "gui" ) as JObject;
        if ( guiElement != null ) {
          response = guiElement[ "snippet" ].ToString();
        }
      }

      return response;
    }

    public override APIInfo GetStatus( Order order, Dictionary<string, string> settings ) {
      throw new NotImplementedException();
    }

    public override APIInfo CapturePayment( Order order, Dictionary<string, string> settings ) {
      throw new NotImplementedException();
    }

    public override APIInfo RefundPayment( Order order, Dictionary<string, string> settings ) {
      throw new NotImplementedException();
    }

    public override APIInfo CancelPayment( Order order, Dictionary<string, string> settings ) {
      throw new NotImplementedException();
    }

  }
}
