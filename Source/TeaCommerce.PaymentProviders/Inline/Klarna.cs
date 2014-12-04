using Klarna.Checkout;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web;
using TeaCommerce.Api;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;
using KlarnaOrder = Klarna.Checkout.Order;
using Order = TeaCommerce.Api.Models.Order;

namespace TeaCommerce.PaymentProviders.Inline {
  [PaymentProvider( "Klarna" )]
  public class Klarna : APaymentProvider {

    protected const string KlarnaApiRequestContentType = "application/vnd.klarna.checkout.aggregated-order-v2+json";

    public override IDictionary<string, string> DefaultSettings {
      get {
        Dictionary<string, string> defaultSettings = new Dictionary<string, string>();
        defaultSettings[ "merchant.id" ] = "";
        defaultSettings[ "locale" ] = "sv-se";
        defaultSettings[ "paymentFormUrl" ] = "";
        defaultSettings[ "merchant.confirmation_uri" ] = "";
        defaultSettings[ "merchant.terms_uri" ] = "";
        defaultSettings[ "sharedSecret" ] = "";
        defaultSettings[ "testMode" ] = "1";
        return defaultSettings;
      }
    }

    public override PaymentHtmlForm GenerateHtmlForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, string teaCommerceCommunicationUrl, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "paymentFormUrl", "settings" );

      PaymentHtmlForm htmlForm = new PaymentHtmlForm {
        Action = settings[ "paymentFormUrl" ]
      };

      if ( order.PaymentInformation != null && order.PaymentInformation.TotalPrice != null && order.PaymentInformation.TotalPrice.Value.WithVat != 0 ) {
        throw new ArgumentException( "The Klarna payment provider does not accept a payment provider price." );
      }

      order.Properties.AddOrUpdate( new CustomProperty( "teaCommerceCommunicationUrl", teaCommerceCommunicationUrl ) { ServerSideOnly = true } );
      order.Properties.AddOrUpdate( new CustomProperty( "teaCommerceContinueUrl", teaCommerceContinueUrl ) { ServerSideOnly = true } );
      order.Properties.AddOrUpdate( new CustomProperty( "teaCommerceCallbackUrl", teaCommerceCallBackUrl ) { ServerSideOnly = true } );

      return htmlForm;
    }

    public override string GetContinueUrl( Order order, IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "merchant.confirmation_uri", "settings" );

      return settings[ "merchant.confirmation_uri" ];
    }

    public override string GetCancelUrl( Order order, IDictionary<string, string> settings ) {
      return ""; //not used in Klarna
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      CallbackInfo callbackInfo = null;

      try {
        order.MustNotBeNull( "order" );
        request.MustNotBeNull( "request" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "sharedSecret", "settings" );

        IConnector connector = Connector.Create( settings[ "sharedSecret" ] );
        KlarnaOrder klarnaOrder = new KlarnaOrder( connector, new Uri( order.Properties[ "klarnaLocation" ] ) ) {
          ContentType = KlarnaApiRequestContentType
        };
        klarnaOrder.Fetch();

        if ( (string)klarnaOrder.GetValue( "status" ) == "checkout_complete" ) {

          //We need to populate the order with the information entered into Klarna.
          SaveOrderPropertiesFromKlarnaCallback( order, klarnaOrder );

          decimal amount = ( (JObject)klarnaOrder.GetValue( "cart" ) )[ "total_price_including_tax" ].Value<decimal>() / 100M;
          string klarnaId = klarnaOrder.GetValue( "id" ).ToString();

          callbackInfo = new CallbackInfo( amount, klarnaId, PaymentState.Authorized );

          klarnaOrder.Update( new Dictionary<string, object>() { { "status", "created" } } );
        } else {
          throw new Exception( "Trying to process a callback from Klarna with an order that isn't completed" );
        }
      } catch ( Exception ex ) {
        LoggingService.Instance.Log( "Klarna(" + order.CartNumber + ") - Error recieving callback - Error: " + ex );
      }

      return callbackInfo;
    }

    protected virtual void SaveOrderPropertiesFromKlarnaCallback( Order order, KlarnaOrder klarnaOrder ) {

      //Some order properties in Tea Commerce comes with a special alias, 
      //defining a mapping of klarna propteries to these aliases.
      Store store = StoreService.Instance.Get( order.StoreId );
      Dictionary<string, string> magicOrderPropertyAliases = new Dictionary<string, string>{
            { "billing_address.given_name", Constants.OrderPropertyAliases.FirstNamePropertyAlias },
            { "billing_address.family_name", Constants.OrderPropertyAliases.LastNamePropertyAlias },
            { "billing_address.email", Constants.OrderPropertyAliases.EmailPropertyAlias },
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
          order.Properties.AddOrUpdate( tcOrderPropertyAlias, klarnaPropertyValue );
        }
      }
      // order was passed as reference and updated. Saving it now.
      order.Save();
    }

    public override string ProcessRequest( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      string response = "";

      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "sharedSecret", "settings" );

        string communicationType = request[ "communicationType" ];

        KlarnaOrder klarnaOrder = null;
        IConnector connector = Connector.Create( settings[ "sharedSecret" ] );

        if ( communicationType == "checkout" ) {
          settings.MustContainKey( "merchant.id", "settings" );
          settings.MustContainKey( "merchant.terms_uri", "settings" );
          settings.MustContainKey( "locale", "settings" );
          //TODO: find ud af rabat
          //Cart information
          List<Dictionary<string, object>> cartItems = order.OrderLines.Select( orderLine =>
            new Dictionary<string, object> {
              { "reference", orderLine.Sku }, 
              { "name", orderLine.Name }, 
              { "quantity", (int) orderLine.Quantity },
              { "unit_price", (int) (orderLine.UnitPrice.Value.WithVat * 100M) },
              { "tax_rate", (int) (orderLine.VatRate.Value * 10000M) }
            } )
          .ToList();

          if ( order.ShipmentInformation.ShippingMethodId != null ) {
            ShippingMethod shippingMethod = ShippingMethodService.Instance.Get( order.StoreId, order.ShipmentInformation.ShippingMethodId.Value );
            cartItems.Add( new Dictionary<string, object> {
              { "type", "shipping_fee" },
              { "reference", shippingMethod.Sku},
              { "name", shippingMethod.Name},
              { "quantity", 1},
              { "unit_price", (int) (order.ShipmentInformation.TotalPrice.Value.WithVat * 100M) },
              { "tax_rate",  (int) (order.ShipmentInformation.VatRate * 10000M) }
            } );
          }

          Dictionary<string, object> data = new Dictionary<string, object> { { "cart", new Dictionary<string, object> { { "items", cartItems } } } };
          string klarnaLocation = order.Properties[ "klarnaLocation" ];

          //Check if the order has a Klarna location URI property - then we try and update the order
          if ( !string.IsNullOrEmpty( klarnaLocation ) ) {
            try {
              klarnaOrder = new KlarnaOrder( connector, new Uri( klarnaLocation ) ) {
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
            string merchantTermsUri = settings[ "merchant.terms_uri" ];
            if ( !merchantTermsUri.StartsWith( "http" ) ) {
              Uri baseUrl = new UriBuilder( HttpContext.Current.Request.Url.Scheme, HttpContext.Current.Request.Url.Host, HttpContext.Current.Request.Url.Port ).Uri;
              merchantTermsUri = new Uri( baseUrl, merchantTermsUri ).AbsoluteUri;
            }

            //Merchant information
            data[ "merchant" ] = new Dictionary<string, object> {
              {"id", settings[ "merchant.id" ]},
              {"terms_uri", merchantTermsUri},
              {"checkout_uri", request.UrlReferrer.ToString()},
              {"confirmation_uri", order.Properties[ "teaCommerceContinueUrl" ]},
              {"push_uri", order.Properties[ "teaCommerceCallbackUrl" ]}
            };

            data[ "merchant_reference" ] = new Dictionary<string, object>() {
              {"orderid1", order.CartNumber}
            };

            //Combined data
            Currency currency = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId );

            //If the currency is not a valid iso4217 currency then throw an error
            if ( !Iso4217CurrencyCodes.ContainsKey( currency.IsoCode ) ) {
              throw new Exception( "You must specify an ISO 4217 currency code for the " + currency.Name + " currency" );
            }

            data[ "purchase_country" ] = CountryService.Instance.Get( order.StoreId, order.PaymentInformation.CountryId ).RegionCode;
            data[ "purchase_currency" ] = currency.IsoCode;
            data[ "locale" ] = settings[ "locale" ];

            klarnaOrder = new KlarnaOrder( connector ) {
              BaseUri = settings.ContainsKey( "testMode" ) && settings[ "testMode" ] == "1" ? new Uri( "https://checkout.testdrive.klarna.com/checkout/orders" ) : new Uri( "https://checkout.klarna.com/checkout/orders" ),
              ContentType = KlarnaApiRequestContentType
            };

            //Create new order
            klarnaOrder.Create( data );
            klarnaOrder.Fetch();
            order.Properties.AddOrUpdate( new CustomProperty( "klarnaLocation", klarnaOrder.Location.ToString() ) { ServerSideOnly = true } );
            order.Save();
          }
        } else if ( communicationType == "confirmation" ) {
          //get confirmation response
          string klarnaLocation = order.Properties[ "klarnaLocation" ];

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

      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "Klarna(" + order.CartNumber + ") - ProcessRequest" );
      }

      return response;
    }

    public override string GetLocalizedSettingsKey( string settingsKey, CultureInfo culture ) {
      switch ( settingsKey ) {
        case "paymentFormUrl":
          return settingsKey + "<br/><small>e.g. /payment/</small>";
        case "merchant.confirmation_uri":
          return settingsKey + "<br/><small>e.g. /continue/</small>";
        case "merchant.terms_uri":
          return settingsKey + "<br/><small>e.g. /terms/</small>";
        case "testMode":
          return settingsKey + "<br/><small>1 = true; 0 = false</small>";
        default:
          return base.GetLocalizedSettingsKey( settingsKey, culture );
      }
    }
  }
}
