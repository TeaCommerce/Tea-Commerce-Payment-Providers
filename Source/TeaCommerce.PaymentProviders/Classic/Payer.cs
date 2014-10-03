using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web;
using System.Web.Hosting;
using System.Xml.Linq;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;

namespace TeaCommerce.PaymentProviders.Classic {

  [PaymentProvider( "Payer" )]
  public class Payer : APaymentProvider {

    public override string DocumentationLink { get { return "http://anders.burla.dk/umbraco/tea-commerce/using-payer-with-tea-commerce/"; } }

    public override IDictionary<string, string> DefaultSettings {
      get {
        Dictionary<string, string> defaultSettings = new Dictionary<string, string>();
        defaultSettings[ "payer_agentid" ] = string.Empty;
        defaultSettings[ "language" ] = "us";
        defaultSettings[ "success_redirect_url" ] = string.Empty;
        defaultSettings[ "redirect_back_to_shop_url" ] = string.Empty;
        defaultSettings[ "payment_methods" ] = "auto";
        defaultSettings[ "md5Key1" ] = string.Empty;
        defaultSettings[ "md5Key2" ] = string.Empty;
        defaultSettings[ "test_mode" ] = "true";
        return defaultSettings;
      }
    }

    public override PaymentHtmlForm GenerateHtmlForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, string teaCommerceCommunicationUrl, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "payer_agentid", "settings" );
      settings.MustContainKey( "language", "settings" );
      settings.MustContainKey( "payment_methods", "settings" );
      settings.MustContainKey( "md5Key1", "settings" );
      settings.MustContainKey( "md5Key2", "settings" );

      HttpServerUtility server = HttpContext.Current.Server;

      PaymentHtmlForm htmlForm = new PaymentHtmlForm {
        Action = "https://secure.pay-read.se/PostAPI_V1/InitPayFlow"
      };

      //Shop id
      htmlForm.InputFields[ "payer_agentid" ] = server.HtmlEncode( settings[ "payer_agentid" ] );

      //API version
      htmlForm.InputFields[ "payer_xml_writer" ] = "payread_php_0_2_v08";

      XNamespace ns = "http://www.w3.org/2001/XMLSchema-instance";
      XElement payerData = new XElement( "payread_post_api_0_2",
        new XAttribute( XNamespace.Xmlns + "xsi", "http://www.w3.org/2001/XMLSchema-instance" ),
        new XAttribute( ns + "noNamespaceSchemaLocation", "payread_post_api_0_2.xsd" )
      );

      //Seller details
      payerData.Add( new XElement( "seller_details",
        new XElement( "agent_id", server.HtmlEncode( settings[ "payer_agentid" ] ) )
      ) );

      //Buyer details
      payerData.Add( new XElement( "buyer_details",
        new XElement( "first_name", server.HtmlEncode( order.PaymentInformation.FirstName ) ),
        new XElement( "last_name", server.HtmlEncode( order.PaymentInformation.LastName ) ),
        new XElement( "address_line_1", server.HtmlEncode( string.Empty ) ),
        new XElement( "address_line_2", server.HtmlEncode( string.Empty ) ),
        new XElement( "postal_code", server.HtmlEncode( string.Empty ) ),
        new XElement( "city", server.HtmlEncode( string.Empty ) ),
        new XElement( "country_code", server.HtmlEncode( string.Empty ) ),
        new XElement( "phone_home", server.HtmlEncode( string.Empty ) ),
        new XElement( "phone_work", server.HtmlEncode( string.Empty ) ),
        new XElement( "phone_mobile", server.HtmlEncode( string.Empty ) ),
        new XElement( "email", server.HtmlEncode( order.PaymentInformation.Email ) ),
        new XElement( "organisation", server.HtmlEncode( string.Empty ) ),
        new XElement( "orgnr", server.HtmlEncode( string.Empty ) ),
        new XElement( "customer_id", server.HtmlEncode( string.Empty ) )
        //new XElement( "your_reference", server.HtmlEncode( string.Empty ) )
        //new XElement( "options", server.HtmlEncode( string.Empty ) )
      ) );

      //TODO: kast exception hvis der er rabat på nogle total priser
      //Purchase
      XElement purchaseList = new XElement( "purchase_list" );
      int lineCounter = 1;
      foreach ( OrderLine orderLine in order.OrderLines ) {
        purchaseList.Add( new XElement( "freeform_purchase",
          new XElement( "line_number", lineCounter ),
          new XElement( "description", server.HtmlEncode( orderLine.Name ) ),
          new XElement( "item_number", server.HtmlEncode( orderLine.Sku ) ),
          new XElement( "price_including_vat", server.HtmlEncode( orderLine.UnitPrice.Value.WithVat.ToString( CultureInfo.InvariantCulture ) ) ),
          new XElement( "vat_percentage", server.HtmlEncode( ( orderLine.VatRate * 100M ).ToString( CultureInfo.InvariantCulture ) ) ),
          new XElement( "quantity", server.HtmlEncode( orderLine.Quantity.ToString( CultureInfo.InvariantCulture ) ) )
        ) );
        lineCounter++;
      }

      //Shipping fee
      if ( order.ShipmentInformation.ShippingMethodId != null ) {
        ShippingMethod shippingMethod = ShippingMethodService.Instance.Get( order.StoreId, order.ShipmentInformation.ShippingMethodId.Value );
        purchaseList.Add( new XElement( "freeform_purchase",
          new XElement( "line_number", lineCounter ),
          new XElement( "description", server.HtmlEncode( shippingMethod.Name ) ),
          new XElement( "item_number", server.HtmlEncode( shippingMethod.Sku ) ),
          new XElement( "price_including_vat", server.HtmlEncode( order.ShipmentInformation.TotalPrice.Value.WithVat.ToString( CultureInfo.InvariantCulture ) ) ),
          new XElement( "vat_percentage", server.HtmlEncode( ( order.ShipmentInformation.VatRate * 100M ).ToString( CultureInfo.InvariantCulture ) ) ),
          new XElement( "quantity", "1" )
        ) );
        lineCounter++;
      }

      //Payment fee
      if ( order.PaymentInformation.PaymentMethodId != null ) {
        PaymentMethod paymentMethod = PaymentMethodService.Instance.Get( order.StoreId, order.PaymentInformation.PaymentMethodId.Value );
        purchaseList.Add( new XElement( "freeform_purchase",
          new XElement( "line_number", lineCounter ),
          new XElement( "description", server.HtmlEncode( paymentMethod.Name ) ),
          new XElement( "item_number", server.HtmlEncode( paymentMethod.Sku ) ),
          new XElement( "price_including_vat", server.HtmlEncode( order.PaymentInformation.TotalPrice.Value.WithVat.ToString( CultureInfo.InvariantCulture ) ) ),
          new XElement( "vat_percentage", server.HtmlEncode( ( order.PaymentInformation.VatRate * 100M ).ToString( CultureInfo.InvariantCulture ) ) ),
          new XElement( "quantity", "1" )
        ) );
      }

      //Check that the Iso code exists
      Currency currency = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId );
      if ( !Iso4217CurrencyCodes.ContainsKey( currency.IsoCode ) ) {
        throw new Exception( "You must specify an ISO 4217 currency code for the " + currency.Name + " currency" );
      }
      payerData.Add( new XElement( "purchase",
        new XElement( "currency", server.HtmlEncode( currency.IsoCode ) ),
        new XElement( "reference_id", server.HtmlEncode( order.CartNumber ) ),
        purchaseList
      ) );

      //Processing control
      payerData.Add( new XElement( "processing_control",
        new XElement( "success_redirect_url", server.HtmlEncode( teaCommerceContinueUrl ) ),
        new XElement( "authorize_notification_url", server.HtmlEncode( teaCommerceCallBackUrl ) ),
        new XElement( "settle_notification_url", server.HtmlEncode( teaCommerceCallBackUrl ) ),
        new XElement( "redirect_back_to_shop_url", server.HtmlEncode( teaCommerceCancelUrl ) )
      ) );

      //Database overrides
      payerData.Add( new XElement( "database_overrides",
        new XElement( "accepted_payment_methods",
          settings[ "payment_methods" ].Split( new[] { "," }, StringSplitOptions.RemoveEmptyEntries ).Select( i =>
            new XElement( "payment_method", server.HtmlEncode( i ) )
          )
        ),
        new XElement( "debug_mode", server.HtmlEncode( settings.ContainsKey( "settings" ) && settings[ "test_mode" ] == "true" ? "verbose" : "silent" ) ),
        new XElement( "test_mode", server.HtmlEncode( settings.ContainsKey( "settings" ) ? settings[ "test_mode" ] : "false" ) ),
        new XElement( "language", server.HtmlEncode( settings[ "language" ] ) )
      ) );

      //Add all data to the xml document
      XDocument xmlDocument = new XDocument(
        new XDeclaration( "1.0", "ISO-8859-1", "yes" ),
        payerData
      );

      htmlForm.InputFields[ "payer_data" ] = xmlDocument.ToString().ToBase64();
      htmlForm.InputFields[ "payer_checksum" ] = GenerateMD5Hash( settings[ "md5Key1" ] + htmlForm.InputFields[ "payer_data" ] + settings[ "md5Key2" ] );

      return htmlForm;
    }

    public override string GetContinueUrl( Order order, IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "success_redirect_url", "settings" );

      return settings[ "success_redirect_url" ];
    }

    public override string GetCancelUrl( Order order, IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "redirect_back_to_shop_url", "settings" );

      return settings[ "redirect_back_to_shop_url" ];
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      CallbackInfo callbackInfo = null;

      try {
        order.MustNotBeNull( "order" );
        request.MustNotBeNull( "request" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "md5Key1", "settings" );
        settings.MustContainKey( "md5Key2", "settings" );

        //Write data when testing
        if ( settings.ContainsKey( "test_mode" ) && settings[ "test_mode" ] == "true" ) {
          LogRequestToFile( request, HostingEnvironment.MapPath( "~/payer-callback-data.txt" ), logGetData: true );
        }

        //Check for payer IP addresses
        string remoteServerIpAddress = request.ServerVariables[ "REMOTE_ADDR" ];

        if ( remoteServerIpAddress == "217.151.207.84" || remoteServerIpAddress == "79.136.103.5" || remoteServerIpAddress == "79.136.103.9" || remoteServerIpAddress == "94.140.57.180" || remoteServerIpAddress == "94.140.57.181" || remoteServerIpAddress == "94.140.57.184" || remoteServerIpAddress == "192.168.100.1" ) {

          string url = request.Url.Scheme + "://" + request.Url.Host + request.ServerVariables[ "REQUEST_URI" ];
          string urlExceptMd5Sum = url.Substring( 0, url.IndexOf("&md5sum", StringComparison.Ordinal) );

          string md5CheckValue = GenerateMD5Hash( settings[ "md5Key1" ] + urlExceptMd5Sum + settings[ "md5Key2" ] ).ToUpperInvariant();

          if ( md5CheckValue == request.QueryString[ "md5sum" ] ) {
            HttpContext.Current.Response.Output.Write( "TRUE" );

            string transaction = request.QueryString[ "payread_payment_id" ];
            string paymentType = request.QueryString[ "payer_payment_type" ];
            string callbackType = request.QueryString[ "payer_callback_type" ];
            PaymentState paymentState = callbackType == "auth" ? PaymentState.Authorized : PaymentState.Captured;

            callbackInfo = new CallbackInfo( order.TotalPrice.Value.WithVat, transaction, paymentState, paymentType );
          } else {
            LoggingService.Instance.Log( "Payer(" + order.CartNumber + ") - MD5Sum security check failed" );
          }
        } else {
          LoggingService.Instance.Log( "Payer(" + order.CartNumber + ") - IP security check failed - IP: " + remoteServerIpAddress );
        }

        HttpContext.Current.Response.Output.Write( "FALSE" );
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "QuickPay(" + order.CartNumber + ") - Process callback" );
      }

      return callbackInfo;
    }

    public override string GetLocalizedSettingsKey( string settingsKey, CultureInfo culture ) {
      switch ( settingsKey ) {
        case "success_redirect_url":
          return settingsKey + "<br/><small>e.g. /continue/</small>";
        case "redirect_back_to_shop_url":
          return settingsKey + "<br/><small>e.g. /cancel/</small>";
        case "payment_methods":
          return settingsKey + "<br/><small>e.g. invoice,card</small>";
        case "test_mode":
          return settingsKey + "<br/><small>true/false</small>";
        default:
          return base.GetLocalizedSettingsKey( settingsKey, culture );
      }
    }

  }
}
