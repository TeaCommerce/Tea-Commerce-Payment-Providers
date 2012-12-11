using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web;
using System.Xml.Linq;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.PaymentProviders;
using TeaCommerce.PaymentProviders.Extensions;
using TeaCommerce.Api.Infrastructure.Logging;

namespace TeaCommerce.PaymentProviders {

  [PaymentProvider( "Payer" )]
  public class Payer : APaymentProvider {

    public override bool SupportsRetrievalOfPaymentStatus { get { return false; } }
    public override bool SupportsCancellationOfPayment { get { return false; } }
    public override bool SupportsCapturingOfPayment { get { return false; } }
    public override bool SupportsRefundOfPayment { get { return false; } }

    public override IDictionary<string, string> DefaultSettings {
      get {
        if ( defaultSettings == null ) {
          defaultSettings = new Dictionary<string, string>();
          defaultSettings[ "payer_agentid" ] = string.Empty;
          defaultSettings[ "language" ] = "us";
          defaultSettings[ "success_redirect_url" ] = string.Empty;
          defaultSettings[ "redirect_back_to_shop_url" ] = string.Empty;
          defaultSettings[ "payment_methods" ] = "auto";
          defaultSettings[ "md5Key1" ] = string.Empty;
          defaultSettings[ "md5Key2" ] = string.Empty;
          defaultSettings[ "test_mode" ] = "false";
          defaultSettings[ "productNumberPropertyAlias" ] = "productNumber";
          defaultSettings[ "productNamePropertyAlias" ] = "productName";
          defaultSettings[ "shippingMethodProductNumber" ] = "1000";
          defaultSettings[ "shippingMethodFormatString" ] = "Shipping fee ({0})";
          defaultSettings[ "paymentMethodProductNumber" ] = "2000";
          defaultSettings[ "paymentMethodFormatString" ] = "Payment fee ({0})";
        }
        return defaultSettings;
      }
    }

    public override string FormPostUrl { get { return "https://secure.pay-read.se/PostAPI_V1/InitPayFlow"; } }
    public override string DocumentationLink { get { return "http://anders.burla.dk/umbraco/tea-commerce/using-payer-with-tea-commerce/"; } }

    public override IDictionary<string, string> GenerateForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, IDictionary<string, string> settings ) {
      HttpServerUtility server = HttpContext.Current.Server;

      Dictionary<string, string> inputFields = new Dictionary<string, string>();

      //Shop id
      inputFields[ "payer_agentid" ] = server.HtmlEncode( settings[ "payer_agentid" ] );

      //API version
      inputFields[ "payer_xml_writer" ] = "payread_php_0_2_v08";

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

      //Purchase
      XElement purchaseList = new XElement( "purchase_list" );
      int lineCounter = 1;
      foreach ( OrderLine orderLine in order.OrderLines ) {
        OrderLineProperty productNameProp = orderLine.Properties.SingleOrDefault( op => op.Alias.Equals( settings[ "productNamePropertyAlias" ] ) );
        OrderLineProperty productNumberProp = orderLine.Properties.SingleOrDefault( op => op.Alias.Equals( settings[ "productNumberPropertyAlias" ] ) );

        purchaseList.Add( new XElement( "freeform_purchase",
          new XElement( "line_number", lineCounter.ToString() ),
          new XElement( "description", server.HtmlEncode( productNameProp != null ? productNameProp.Value : string.Empty ) ),
          new XElement( "item_number", server.HtmlEncode( productNumberProp != null ? productNumberProp.Value : string.Empty ) ),
          new XElement( "price_including_vat", server.HtmlEncode( orderLine.TotalPrice.WithVat.ToString( CultureInfo.InvariantCulture ) ) ),
          new XElement( "vat_percentage", server.HtmlEncode( ( orderLine.VatRate * 100M ).ToString( CultureInfo.InvariantCulture ) ) ),
          new XElement( "quantity", server.HtmlEncode( orderLine.Quantity.ToString( CultureInfo.InvariantCulture ) ) )
        ) );
        lineCounter++;
      }

      //Shipping fee
      if ( order.ShipmentInformation.TotalPrice.WithVat != 0 ) {
        purchaseList.Add( new XElement( "freeform_purchase",
          new XElement( "line_number", lineCounter.ToString() ),
          new XElement( "description", server.HtmlEncode( string.Format( settings[ "shippingMethodFormatString" ], order.ShippingMethod.Name ) ) ),
          new XElement( "item_number", server.HtmlEncode( settings[ "shippingMethodProductNumber" ] ) ),
          new XElement( "price_including_vat", server.HtmlEncode( order.ShipmentInformation.TotalPrice.WithVat.ToString( CultureInfo.InvariantCulture ) ) ),
          new XElement( "vat_percentage", server.HtmlEncode( ( order.ShipmentInformation.VatRate * 100M ).ToString( CultureInfo.InvariantCulture ) ) ),
          new XElement( "quantity", "1" )
        ) );
        lineCounter++;
      }

      //Payment fee
      if ( order.PaymentInformation.TotalPrice.WithVat != 0 ) {
        purchaseList.Add( new XElement( "freeform_purchase",
          new XElement( "line_number", lineCounter.ToString() ),
          new XElement( "description", server.HtmlEncode( string.Format( settings[ "paymentMethodFormatString" ], order.PaymentMethod.Name ) ) ),
          new XElement( "item_number", server.HtmlEncode( settings[ "paymentMethodProductNumber" ] ) ),
          new XElement( "price_including_vat", server.HtmlEncode( order.PaymentInformation.TotalPrice.WithVat.ToString( CultureInfo.InvariantCulture ) ) ),
          new XElement( "vat_percentage", server.HtmlEncode( ( order.PaymentInformation.VatRate * 100M ).ToString( CultureInfo.InvariantCulture ) ) ),
          new XElement( "quantity", "1" )
        ) );
        lineCounter++;
      }

      payerData.Add( new XElement( "purchase",
        new XElement( "currency", server.HtmlEncode( order.Currency.ISOCode ) ),
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
          settings[ "payment_methods" ].Split( new string[] { "," }, StringSplitOptions.RemoveEmptyEntries ).Select( i =>
            new XElement( "payment_method", server.HtmlEncode( i ) )
          )
        ),
        new XElement( "debug_mode", server.HtmlEncode( settings[ "test_mode" ] == "true" ? "verbose" : "silent" ) ),
        new XElement( "test_mode", server.HtmlEncode( settings[ "test_mode" ] ) ),
        new XElement( "language", server.HtmlEncode( settings[ "language" ] ) )
      ) );

      //Add all data to the xml document
      XDocument xmlDocument = new XDocument(
        new XDeclaration( "1.0", "ISO-8859-1", "yes" ),
        payerData
      );

      inputFields[ "payer_data" ] = xmlDocument.ToString().Base64Encode();

      inputFields[ "payer_checksum" ] = GetMD5Hash( settings[ "md5Key1" ] + inputFields[ "payer_data" ] + settings[ "md5Key2" ] );

      return inputFields;
    }

    public override string GetContinueUrl( IDictionary<string, string> settings ) {
      return settings[ "success_redirect_url" ];
    }

    public override string GetCancelUrl( IDictionary<string, string> settings ) {
      return settings[ "redirect_back_to_shop_url" ];
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      //using ( StreamWriter writer = new StreamWriter( File.Create( HttpContext.Current.Server.MapPath( "~/PayerTestCallback.txt" ) ) ) ) {
      //  writer.WriteLine( "QueryString:" );
      //  foreach ( string k in request.QueryString.Keys ) {
      //    writer.WriteLine( k + " : " + request.QueryString[ k ] );
      //  }
      //  writer.Flush();
      //}

      string errorMessage = string.Empty;

      //Check for payer IP addresses
      string remoteServerIPAddress = request.ServerVariables[ "REMOTE_ADDR" ];

      if ( remoteServerIPAddress == "217.151.207.84" || remoteServerIPAddress == "79.136.103.5" || remoteServerIPAddress == "79.136.103.9" || remoteServerIPAddress == "94.140.57.180" || remoteServerIPAddress == "94.140.57.181" || remoteServerIPAddress == "94.140.57.184" || remoteServerIPAddress == "192.168.100.1" ) {

        string url = request.Url.Scheme + "://" + request.Url.Host + request.ServerVariables[ "REQUEST_URI" ];
        string urlExceptMD5Sum = url.Substring( 0, url.IndexOf( "&md5sum" ) );

        string md5CheckValue = GetMD5Hash( settings[ "md5Key1" ] + urlExceptMD5Sum + settings[ "md5Key2" ] ).ToUpperInvariant();

        if ( md5CheckValue == request.QueryString[ "md5sum" ] ) {
          HttpContext.Current.Response.Output.Write( "TRUE" );

          string transaction = request.QueryString[ "payread_payment_id" ];
          string paymentType = request.QueryString[ "payer_payment_type" ];
          string callbackType = request.QueryString[ "payer_callback_type" ];
          PaymentState paymentState = callbackType == "auth" ? PaymentState.Authorized : PaymentState.Captured;

          return new CallbackInfo( order.TotalPrice.WithVat, transaction, paymentState, paymentType );
        } else {
          errorMessage = "Tea Commerce - Payer - MD5Sum security check failed";
        }
      } else {
        errorMessage = "Tea Commerce - Payer - IP security check failed - IP: " + remoteServerIPAddress;
      }

      HttpContext.Current.Response.Output.Write( "FALSE" );
      LoggingService.Instance.Log( errorMessage );
      return new CallbackInfo( errorMessage );
    }

    public override ApiInfo GetStatus( Order order, IDictionary<string, string> settings ) {
      throw new NotImplementedException();
    }

    public override ApiInfo CapturePayment( Order order, IDictionary<string, string> settings ) {
      throw new NotImplementedException();
    }

    public override ApiInfo RefundPayment( Order order, IDictionary<string, string> settings ) {
      throw new NotImplementedException();
    }

    public override ApiInfo CancelPayment( Order order, IDictionary<string, string> settings ) {
      throw new NotImplementedException();
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
          return settingsKey + "<br/><small>1 = true; 0 = false</small>";
        default:
          return base.GetLocalizedSettingsKey( settingsKey, culture );
      }
    }

  }
}
