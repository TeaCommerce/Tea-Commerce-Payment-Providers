using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web;
using System.Xml.Linq;
using System.Xml.XPath;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;
using TeaCommerce.PaymentProviders.Extensions;

namespace TeaCommerce.PaymentProviders {

  [PaymentProvider( "Netaxept" )]
  public class Netaxept : APaymentProvider {

    protected const string apiErrorFormatString = "Error making API request - Error message: {0}";

    public override IDictionary<string, string> DefaultSettings {
      get {
        Dictionary<string, string> defaultSettings = new Dictionary<string, string>();
        defaultSettings[ "merchantId" ] = string.Empty;
        defaultSettings[ "token" ] = string.Empty;
        defaultSettings[ "language" ] = "en_GB";
        defaultSettings[ "accepturl" ] = string.Empty;
        defaultSettings[ "cancelurl" ] = string.Empty;
        defaultSettings[ "instantcapture" ] = "0";
        defaultSettings[ "paymentMethodList" ] = "";
        defaultSettings[ "testMode" ] = "1";
        return defaultSettings;
      }
    }
    public override string DocumentationLink { get { return "http://anders.burla.dk/umbraco/tea-commerce/using-netaxept-with-tea-commerce/"; } }

    protected string formPostUrl;
    public override string FormPostUrl { get { return formPostUrl; } }

    public override bool SupportsRetrievalOfPaymentStatus { get { return true; } }
    public override bool SupportsCapturingOfPayment { get { return true; } }
    public override bool SupportsRefundOfPayment { get { return true; } }
    public override bool SupportsCancellationOfPayment { get { return true; } }

    public override IDictionary<string, string> GenerateForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "merchantId", "settings" );
      settings.MustContainKey( "token", "settings" );
      settings.MustContainKey( "testMode", "settings" );

      List<string> settingsToExclude = new string[] { "accepturl", "cancelurl", "instantcapture", "testMode" }.ToList();
      Dictionary<string, string> inputFields = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      //orderNumber
      inputFields[ "orderNumber" ] = order.CartNumber;

      //currencyCode
      Currency currency = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId );
      if ( !ISO4217CurrencyCodes.ContainsKey( currency.IsoCode ) ) {
        throw new Exception( "You must specify an ISO 4217 currency code for the " + currency.Name + " currency" );
      }
      inputFields[ "currencyCode" ] = currency.IsoCode;

      //amount
      inputFields[ "amount" ] = ( order.TotalPrice.WithVat * 100M ).ToString( "0", CultureInfo.InvariantCulture );

      //redirectUrl
      inputFields[ "redirectUrl" ] = teaCommerceCallBackUrl;

      //redirectOnError
      inputFields[ "redirectOnError" ] = "false";

      //paymentMethodList
      if ( inputFields.ContainsKey( "paymentMethodList" ) && string.IsNullOrEmpty( inputFields[ "paymentMethodList" ] ) )
        inputFields.Remove( "paymentMethodList" );

      XDocument xmlResponse = XDocument.Parse( MakePostRequest( ( settings[ "testMode" ] != "1" ? "https://epayment.bbs.no/Netaxept/Register.aspx" : "https://epayment-test.bbs.no/Netaxept/Register.aspx" ), inputFields ), LoadOptions.PreserveWhitespace );
      if ( xmlResponse.XPathSelectElement( "//RegisterResponse" ) != null ) {
        //Save the Tea Commerce continue and cancel url so we have access to them in the "Callback"
        order.Properties.AddOrUpdate( new CustomProperty( "teaCommerceContinueUrl", teaCommerceContinueUrl ) { ServerSideOnly = true } );
        order.Properties.AddOrUpdate( new CustomProperty( "teaCommerceCancelUrl", teaCommerceCancelUrl ) { ServerSideOnly = true } );
        order.Save();

        formPostUrl = ( settings[ "testMode" ] != "1" ? "https://epayment.bbs.no/Terminal/default.aspx" : "https://epayment-test.bbs.no/Terminal/default.aspx" ) + "?merchantId=" + settings[ "merchantId" ] + "&transactionId=" + xmlResponse.XPathSelectElement( "//TransactionId" ).Value;
      } else {
        LoggingService.Instance.Log( "Tea Commerce - Netaxept - GenerateForm error - " + xmlResponse.XPathSelectElement( "//Error/Message" ).Value );
      }

      return new Dictionary<string, string>();
    }

    public override string GetContinueUrl( IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "accepturl", "settings" );

      return settings[ "accepturl" ];
    }

    public override string GetCancelUrl( IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "cancelurl", "settings" );

      return settings[ "cancelurl" ];
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      CallbackInfo callbackInfo = null;

      try {
        order.MustNotBeNull( "order" );
        request.MustNotBeNull( "request" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "merchantId", "settings" );
        settings.MustContainKey( "token", "settings" );
        settings.MustContainKey( "testMode", "settings" );

        //using ( StreamWriter writer = new StreamWriter( File.Create( HttpContext.Current.Server.MapPath( "~/NetaxceptCallback.txt" ) ) ) ) {
        //  writer.WriteLine( "QueryString:" );
        //  foreach ( string k in request.QueryString.Keys ) {
        //    writer.WriteLine( k + " : " + request.QueryString[ k ] );
        //  }
        //  writer.Flush();
        //}

        string responseCode = request.QueryString[ "responseCode" ];

        if ( responseCode != null && responseCode == "OK" ) {
          bool autoCapture = settings.ContainsKey( "instantcapture" ) && settings[ "instantcapture" ] == "1";
          string transactionId = request.QueryString[ "transactionId" ];

          Dictionary<string, string> inputFields = new Dictionary<string, string>();
          inputFields[ "merchantId" ] = settings[ "merchantId" ];
          inputFields[ "token" ] = settings[ "token" ];
          inputFields[ "operation" ] = !autoCapture ? "AUTH" : "SALE";
          inputFields[ "transactionId" ] = transactionId;

          XDocument xmlResponse = XDocument.Parse( MakePostRequest( ( settings[ "testMode" ] != "1" ? "https://epayment.bbs.no/Netaxept/Process.aspx" : "https://epayment-test.bbs.no/Netaxept/Process.aspx" ), inputFields ), LoadOptions.PreserveWhitespace );

          if ( xmlResponse.XPathSelectElement( "//ProcessResponse" ) != null && xmlResponse.XPathSelectElement( "//ProcessResponse/ResponseCode" ).Value == "OK" ) {

            //Get details from the transaction
            xmlResponse = QueryTransaction( transactionId, settings );

            if ( xmlResponse.XPathSelectElement( "//PaymentInfo" ) != null ) {
              string orderName = xmlResponse.XPathSelectElement( "//PaymentInfo/OrderInformation/OrderNumber" ).Value;
              decimal totalAmount = decimal.Parse( xmlResponse.XPathSelectElement( "//PaymentInfo/OrderInformation/Total" ).Value, CultureInfo.InvariantCulture ) / 100M;
              string cardType = xmlResponse.XPathSelectElement( "//PaymentInfo/CardInformation/PaymentMethod" ).Value;
              string cardNumber = xmlResponse.XPathSelectElement( "//PaymentInfo/CardInformation/MaskedPAN" ).Value;

              callbackInfo = new CallbackInfo( totalAmount, transactionId, !autoCapture ? PaymentState.Authorized : PaymentState.Captured, cardType, cardNumber );
            } else {
              LoggingService.Instance.Log( "Netaxept - ProcessCallback error - " + xmlResponse.XPathSelectElement( "//Error/Message" ).Value );
            }
          } else {
            string errorMessage = "Netaxept - ProcessCallback error - " + xmlResponse.XPathSelectElement( "//Error/Message" ).Value;
            if ( xmlResponse.XPathSelectElement( "//Error/Result" ) != null ) {
              errorMessage += " response code: " + xmlResponse.XPathSelectElement( "//Error/Result/ResponseCode" ).Value + " transactionId: " + xmlResponse.XPathSelectElement( "//Error/Result/TransactionId" ).Value;
            }
            LoggingService.Instance.Log( errorMessage );
          }

        } else {
          LoggingService.Instance.Log( "Netaxept - Response code isn't valid - response code: " + responseCode );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp );
      }

      HttpContext.Current.Response.Redirect( order.Properties.Get( callbackInfo != null ? "teaCommerceContinueUrl" : "teaCommerceCancelUrl" ).Value, false );

      return callbackInfo;
    }

    public override ApiInfo GetStatus( Order order, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "merchantId", "settings" );
      settings.MustContainKey( "token", "settings" );

      ApiInfo apiInfo = null;

      Dictionary<string, string> inputFields = new Dictionary<string, string>();
      inputFields[ "merchantId" ] = settings[ "merchantId" ];
      inputFields[ "token" ] = settings[ "token" ];
      inputFields[ "transactionId" ] = order.TransactionInformation.TransactionId;

      XDocument xmlResponse = QueryTransaction( order.TransactionInformation.TransactionId, settings );

      if ( xmlResponse.XPathSelectElement( "//PaymentInfo" ) != null ) {
        string transactionId = xmlResponse.XPathSelectElement( "//PaymentInfo/TransactionId" ).Value;
        bool authorized = bool.Parse( xmlResponse.XPathSelectElement( "//PaymentInfo/Summary/Authorized" ).Value );
        bool cancelled = bool.Parse( xmlResponse.XPathSelectElement( "//PaymentInfo/Summary/Annulled" ).Value );
        bool captured = decimal.Parse( xmlResponse.XPathSelectElement( "//PaymentInfo/Summary/AmountCaptured" ).Value, CultureInfo.InvariantCulture ) > 0;
        bool refunded = decimal.Parse( xmlResponse.XPathSelectElement( "//PaymentInfo/Summary/AmountCredited" ).Value, CultureInfo.InvariantCulture ) > 0;

        PaymentState paymentState = PaymentState.Initialized;
        if ( refunded )
          paymentState = PaymentState.Refunded;
        else if ( captured )
          paymentState = PaymentState.Captured;
        else if ( cancelled )
          paymentState = PaymentState.Cancelled;
        else if ( authorized )
          paymentState = PaymentState.Authorized;

        apiInfo = new ApiInfo( transactionId, paymentState );
      } else {
        apiInfo = new ApiInfo( "Netaxept - " + string.Format( apiErrorFormatString, xmlResponse.XPathSelectElement( "//Error/Message" ).Value ) );
      }

      return apiInfo;
    }

    public override ApiInfo CapturePayment( Order order, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "merchantId", "settings" );
      settings.MustContainKey( "token", "settings" );
      settings.MustContainKey( "testMode", "settings" );

      ApiInfo apiInfo = null;

      Dictionary<string, string> inputFields = new Dictionary<string, string>();
      inputFields[ "merchantId" ] = settings[ "merchantId" ];
      inputFields[ "token" ] = settings[ "token" ];
      inputFields[ "operation" ] = "CAPTURE";
      inputFields[ "transactionId" ] = order.TransactionInformation.TransactionId;
      inputFields[ "transactionAmount" ] = ( order.TotalPrice.WithVat * 100M ).ToString( "0", CultureInfo.InvariantCulture );

      XDocument xmlResponse = XDocument.Parse( MakePostRequest( ( settings[ "testMode" ] != "1" ? "https://epayment.bbs.no/Netaxept/Process.aspx" : "https://epayment-test.bbs.no/Netaxept/Process.aspx" ), inputFields ), LoadOptions.PreserveWhitespace );

      if ( xmlResponse.XPathSelectElement( "//ProcessResponse" ) != null && xmlResponse.XPathSelectElement( "//ProcessResponse/ResponseCode" ).Value == "OK" ) {
        apiInfo = new ApiInfo( order.TransactionInformation.TransactionId, PaymentState.Captured );
      } else {
        apiInfo = new ApiInfo( "Netaxept - " + string.Format( apiErrorFormatString, xmlResponse.XPathSelectElement( "//Error/Message" ).Value ) );
      }

      return apiInfo;
    }

    public override ApiInfo RefundPayment( Order order, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "merchantId", "settings" );
      settings.MustContainKey( "token", "settings" );
      settings.MustContainKey( "testMode", "settings" );

      ApiInfo apiInfo = null;

      Dictionary<string, string> inputFields = new Dictionary<string, string>();
      inputFields[ "merchantId" ] = settings[ "merchantId" ];
      inputFields[ "token" ] = settings[ "token" ];
      inputFields[ "operation" ] = "CREDIT";
      inputFields[ "transactionId" ] = order.TransactionInformation.TransactionId;
      inputFields[ "transactionAmount" ] = ( order.TotalPrice.WithVat * 100M ).ToString( "0", CultureInfo.InvariantCulture );

      XDocument xmlResponse = XDocument.Parse( MakePostRequest( ( settings[ "testMode" ] != "1" ? "https://epayment.bbs.no/Netaxept/Process.aspx" : "https://epayment-test.bbs.no/Netaxept/Process.aspx" ), inputFields ), LoadOptions.PreserveWhitespace );

      if ( xmlResponse.XPathSelectElement( "//ProcessResponse" ) != null && xmlResponse.XPathSelectElement( "//ProcessResponse/ResponseCode" ).Value == "OK" ) {
        apiInfo = new ApiInfo( order.TransactionInformation.TransactionId, PaymentState.Refunded );
      } else {
        apiInfo = new ApiInfo( "Netaxept - " + string.Format( apiErrorFormatString, xmlResponse.XPathSelectElement( "//Error/Message" ).Value ) );
      }

      return apiInfo;
    }

    public override ApiInfo CancelPayment( Order order, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "merchantId", "settings" );
      settings.MustContainKey( "token", "settings" );
      settings.MustContainKey( "testMode", "settings" );

      ApiInfo apiInfo = null;

      Dictionary<string, string> inputFields = new Dictionary<string, string>();
      inputFields[ "merchantId" ] = settings[ "merchantId" ];
      inputFields[ "token" ] = settings[ "token" ];
      inputFields[ "operation" ] = "ANNUL";
      inputFields[ "transactionId" ] = order.TransactionInformation.TransactionId;

      XDocument xmlResponse = XDocument.Parse( MakePostRequest( ( settings[ "testMode" ] != "1" ? "https://epayment.bbs.no/Netaxept/Process.aspx" : "https://epayment-test.bbs.no/Netaxept/Process.aspx" ), inputFields ), LoadOptions.PreserveWhitespace );

      if ( xmlResponse.XPathSelectElement( "//ProcessResponse" ) != null && xmlResponse.XPathSelectElement( "//ProcessResponse/ResponseCode" ).Value == "OK" ) {
        apiInfo = new ApiInfo( order.TransactionInformation.TransactionId, PaymentState.Cancelled );
      } else {
        apiInfo = new ApiInfo( "Netaxept - " + string.Format( apiErrorFormatString, xmlResponse.XPathSelectElement( "//Error/Message" ).Value ) );
      }

      return apiInfo;
    }

    public override string GetLocalizedSettingsKey( string settingsKey, CultureInfo culture ) {
      switch ( settingsKey ) {
        case "accepturl":
          return settingsKey + "<br/><small>e.g. /continue/</small>";
        case "cancelurl":
          return settingsKey + "<br/><small>e.g. /cancel/</small>";
        case "instantcapture":
          return settingsKey + "<br/><small>1 = true; 0 = false</small>";
        case "paymentMethodList":
          return settingsKey + "<br/><small>e.g. Visa,MasterCard</small>";
        case "testMode":
          return settingsKey + "<br/><small>1 = true; 0 = false</small>";
        default:
          return base.GetLocalizedSettingsKey( settingsKey, culture );
      }
    }

    private XDocument QueryTransaction( string transactionId, IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "merchantId", "settings" );
      settings.MustContainKey( "token", "settings" );
      settings.MustContainKey( "testMode", "settings" );

      Dictionary<string, string> inputFields = new Dictionary<string, string>();
      inputFields[ "merchantId" ] = settings[ "merchantId" ];
      inputFields[ "token" ] = settings[ "token" ];
      inputFields[ "transactionId" ] = transactionId;

      return XDocument.Parse( MakePostRequest( ( settings[ "testMode" ] != "1" ? "https://epayment.bbs.no/Netaxept/Query.aspx" : "https://epayment-test.bbs.no/Netaxept/Query.aspx" ), inputFields ), LoadOptions.PreserveWhitespace );
    }

  }
}
