using System;
using System.Collections.Generic;
using System.Globalization;
using System.Web;
using Klarna.Checkout;
using Newtonsoft.Json.Linq;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;
using Order = TeaCommerce.Api.Models.Order;

namespace TeaCommerce.PaymentProviders.Inline {
  [PaymentProvider( "Klarna" )]
  public class Klarna : APaymentProvider {

    public override string DocumentationLink {
      get { return "http://anders.burla.dk/umbraco/tea-commerce/using-klarna-with-tea-commerce/"; }
    } //todo: add documentation

    public override IDictionary<string, string> DefaultSettings {
      get {
        Dictionary<string, string> defaultSettings = new Dictionary<string, string>();
        defaultSettings[ "merchant.id" ] = "";
        defaultSettings[ "merchant.terms_uri" ] = "";
        defaultSettings[ "merchant.checkout_uri" ] = "";
        defaultSettings[ "sharedSecret" ] = "";
        defaultSettings[ "merchant.confirmation_uri" ] = "";
        defaultSettings[ "merchant.validation_uri" ] = ""; //todo: add validation support
        defaultSettings[ "locale" ] = "sv-se";
        defaultSettings[ "ContentType" ] = "";
        defaultSettings[ "BaseUri" ] = "";
        defaultSettings[ "PaymentFormUrl" ] = "";
        return defaultSettings;
      }
    }

    public override PaymentHtmlForm GenerateHtmlForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, string teaCommerceCommunicationUrl, IDictionary<string, string> settings ) {
      PaymentHtmlForm htmlForm = new PaymentHtmlForm();
      htmlForm.Action = settings[ "PaymentFormUrl" ];
      order.Properties.AddOrUpdate( new CustomProperty( "teaCommerceCummunicationUrl", teaCommerceCommunicationUrl ) { ServerSideOnly = true } );
      order.Properties.AddOrUpdate( new CustomProperty( "teaCommerceCallBackUrl", teaCommerceCallBackUrl ) { ServerSideOnly = true } );
      order.Properties.AddOrUpdate( new CustomProperty( "teaCommerceContinueUrl", teaCommerceContinueUrl ) { ServerSideOnly = true } );

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

        string klarnaLocation = request.QueryString[ "klarna_order" ];
        IConnector connector = Connector.Create( settings[ "sharedSecret" ] );

        global::Klarna.Checkout.Order klarnaOrder = new global::Klarna.Checkout.Order( connector,
          new Uri( klarnaLocation ) ) { ContentType = settings[ "ContentType" ] };

        klarnaOrder.Fetch();

        if ( (string)klarnaOrder.GetValue( "status" ) == "checkout_complete" ) {
          //create order
          decimal amount = Convert.ToDecimal( klarnaOrder.GetValue( "cart.total_price_including_tax" ) );
          string klarnaId = klarnaOrder.GetValue( "id" ).ToString();
          klarnaOrder.Update( new Dictionary<string, object>() { {"status", "created"} } );
          PaymentState paymentState = PaymentState.Authorized;
          callbackInfo = new CallbackInfo( amount, klarnaId, paymentState );
        }
      } catch ( Exception ex ) {
        LoggingService.Instance.Log( "Klarna(" + order.OrderNumber + ") - Error recieving callback - Error: " + ex );
      }

      return callbackInfo;
    }

    public override string ProcessRequest( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      string contentType = settings[ "ContentType" ];

      string callbackType = request[ "teaCommerceCommunicationType" ];
      global::Klarna.Checkout.Order klarnaOrder = null;
      IConnector connector = Connector.Create( settings[ "sharedSecret" ] );

      if ( callbackType == "checkout" ) {
        //Get a checkout response
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "merchant.id", "settings" );
        settings.MustContainKey( "sharedSecret", "settings" );
        settings.MustContainKey( "BaseUri", "settings" );
        settings.MustContainKey( "merchant.terms_uri", "settings" );
        settings.MustContainKey( "merchant.checkout_uri", "settings" );
        settings.MustContainKey( "merchant.confirmation_uri", "settings" );

        string callback = order.Properties[ "teaCommerceCallBackUrl" ];

        //Cart information
        List<Dictionary<string, object>> cartItems = new List<Dictionary<string, object>>();
        IEnumerable<OrderLine> orderLines = order.OrderLines.GetAll();
        foreach ( OrderLine orderLine in orderLines ) {
          cartItems.Add( new Dictionary<string, object> {
            {"reference", orderLine.Sku},
            {"name", orderLine.Name},
            {"quantity", (int) orderLine.Quantity},
            {"unit_price", (int) orderLine.UnitPrice.WithVat*100},
            {"tax_rate", (int) orderLine.VatRate.Value*100}
          } );
        }

        Dictionary<string, object> cart = new Dictionary<string, object> { { "items", cartItems } };
        Dictionary<string, object> data = new Dictionary<string, object> { { "cart", cart } };

        CustomProperty klarnaLocation = order.Properties.Get( "klarnaLocation" );
        if ( klarnaLocation != null ) {
          //Try to update an old order
          try {
            klarnaOrder = new global::Klarna.Checkout.Order( connector, new Uri( klarnaLocation.Value ) ) {
              ContentType = contentType
            };
            klarnaOrder.Fetch();

            //update order with new cartitems
            klarnaOrder.Update( data );
          } catch ( Exception ex ) {
            //try and create a new order instead
            klarnaOrder = null;
            order.Properties.Remove( klarnaLocation );
          }
        }

        if ( klarnaOrder == null ) {
          //Merchant information
          Dictionary<string, object> merchant = new Dictionary<string, object> {
            {"id", settings[ "merchant.id" ]},
            {"terms_uri", settings[ "merchant.terms_uri" ]},
            {"checkout_uri", settings[ "merchant.checkout_uri" ]},
            {"confirmation_uri", order.Properties[ "teaCommerceContinueUrl" ]},
            {"push_uri", callback}
          };

          //Combined data
          data[ "purchase_country" ] =
            CountryService.Instance.Get( order.StoreId, order.PaymentInformation.CountryId ).RegionCode;
          data[ "purchase_currency" ] = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId ).IsoCode;
          data[ "locale" ] = "sv-se";
          data[ "merchant" ] = merchant;

          Uri klarnaCheckoutUri = new Uri( settings[ "BaseUri" ] );
          klarnaOrder = new global::Klarna.Checkout.Order( connector,
            klarnaCheckoutUri ) {
              BaseUri = klarnaCheckoutUri,
              ContentType = contentType
            };

          //Create new order
          klarnaOrder.Create( data );
          klarnaOrder.Fetch();
          order.Properties.AddOrUpdate( new CustomProperty( "klarnaLocation",
            klarnaOrder.Location.ToString() ) { ServerSideOnly = true } );
        }
      } else if ( callbackType == "confirmation" ) {
        //get confirmation response
        string klarnaLocation = order.Properties[ "klarnaLocation" ];
        if ( klarnaLocation != null ) {
          //Fetch and show confirmation page if status is not checkout_incomplete
          klarnaOrder = new global::Klarna.Checkout.Order( connector, new Uri( klarnaLocation ) ) {
            ContentType = contentType
          };
          klarnaOrder.Fetch();
          if ( (string)klarnaOrder.GetValue( "status" ) == "checkout_incomplete" ) {
            LoggingService.Instance.Log( "Order with orderid: '" + order.Id + "' reached confirmation page without finishing Klarna checkout." );
            throw new HttpException( 500, "Error in the checkout flow." );
          }
        }
      }

      JObject gui = klarnaOrder.GetValue( "gui" ) as JObject;

      return gui[ "snippet" ].ToString();
    }

    public override string GetLocalizedSettingsKey( string settingsKey, CultureInfo culture ) {
      switch ( settingsKey ) {
        case "accepturl":
          return settingsKey + "<br/><small>e.g. /continue/</small>";
        case "declineurl":
          return settingsKey + "<br/><small>e.g. /cancel/</small>";
        case "cardtype":
          return settingsKey + "<br/><small>e.g. VISA,MC</small>";
        case "testmode":
          return settingsKey + "<br/><small>1 = true; 0 = false</small>";
        default:
          return base.GetLocalizedSettingsKey( settingsKey, culture );
      }
    }
  }
}
