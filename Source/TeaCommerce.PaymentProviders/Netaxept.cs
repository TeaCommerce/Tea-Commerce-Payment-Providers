using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web;
using System.Web.Hosting;
using System.Xml.Linq;
using System.Xml.XPath;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;

namespace TeaCommerce.PaymentProviders {

  [PaymentProvider( "Netaxept" )]
  public class Netaxept : APaymentProvider {

    public override string DocumentationLink { get { return "http://anders.burla.dk/umbraco/tea-commerce/using-netaxept-with-tea-commerce/"; } }

    public override bool SupportsRetrievalOfPaymentStatus { get { return true; } }
    public override bool SupportsCapturingOfPayment { get { return true; } }
    public override bool SupportsRefundOfPayment { get { return true; } }
    public override bool SupportsCancellationOfPayment { get { return true; } }

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
        defaultSettings[ "testMode" ] = "0";
        return defaultSettings;
      }
    }

    public override PaymentHtmlForm GenerateHtmlForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "merchantId", "settings" );

      PaymentHtmlForm htmlForm = new PaymentHtmlForm();

      string[] settingsToExclude = new[] { "accepturl", "cancelurl", "instantcapture", "testMode" };
      htmlForm.InputFields = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      //orderNumber
      htmlForm.InputFields[ "orderNumber" ] = order.CartNumber;

      //currencyCode
      Currency currency = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId );
      if ( !Iso4217CurrencyCodes.ContainsKey( currency.IsoCode ) ) {
        throw new Exception( "You must specify an ISO 4217 currency code for the " + currency.Name + " currency" );
      }
      htmlForm.InputFields[ "currencyCode" ] = currency.IsoCode;

      //amount
      htmlForm.InputFields[ "amount" ] = ( order.TotalPrice.WithVat * 100M ).ToString( "0", CultureInfo.InvariantCulture );

      //redirectUrl
      htmlForm.InputFields[ "redirectUrl" ] = teaCommerceCallBackUrl;

      //redirectOnError
      htmlForm.InputFields[ "redirectOnError" ] = "false";

      //paymentMethodList
      if ( htmlForm.InputFields.ContainsKey( "paymentMethodList" ) && string.IsNullOrEmpty( htmlForm.InputFields[ "paymentMethodList" ] ) )
        htmlForm.InputFields.Remove( "paymentMethodList" );

      XDocument xmlResponse = XDocument.Parse( MakePostRequest( settings.ContainsKey( "testMode" ) && settings[ "testMode" ] == "1" ? "https://epayment-test.bbs.no/Netaxept/Register.aspx" : "https://epayment.bbs.no/Netaxept/Register.aspx", htmlForm.InputFields ), LoadOptions.PreserveWhitespace );
      if ( xmlResponse.XPathSelectElement( "//RegisterResponse" ) != null ) {
        //Save the Tea Commerce continue and cancel url so we have access to them in the "Callback"
        order.Properties.AddOrUpdate( new CustomProperty( "teaCommerceContinueUrl", teaCommerceContinueUrl ) { ServerSideOnly = true } );
        order.Properties.AddOrUpdate( new CustomProperty( "teaCommerceCancelUrl", teaCommerceCancelUrl ) { ServerSideOnly = true } );
        order.Save();

        htmlForm.Action = ( settings.ContainsKey( "testMode" ) && settings[ "testMode" ] == "1" ? "https://epayment-test.bbs.no/Terminal/default.aspx" : "https://epayment.bbs.no/Terminal/default.aspx" ) + "?merchantId=" + settings[ "merchantId" ] + "&transactionId=" + xmlResponse.XPathSelectElement( "//TransactionId" ).Value;
      } else {
        throw new Exception( "Generate html failed - error message: " + xmlResponse.XPathSelectElement( "//Error/Message" ).Value );
      }

      return htmlForm;
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

        //Write data when testing
        if ( settings.ContainsKey( "testmode" ) && settings[ "testmode" ] == "1" ) {
          using ( StreamWriter writer = new StreamWriter( File.Create( HostingEnvironment.MapPath( "~/netaxcept-callback-data.txt" ) ) ) ) {
            writer.WriteLine( "Query string:" );
            foreach ( string k in request.QueryString.Keys ) {
              writer.WriteLine( k + " : " + request.QueryString[ k ] );
            }
            writer.Flush();
          }
        }

        string responseCode = request.QueryString[ "responseCode" ];

        if ( responseCode != null && responseCode == "OK" ) {
          bool autoCapture = settings.ContainsKey( "instantcapture" ) && settings[ "instantcapture" ] == "1";
          string transactionId = request.QueryString[ "transactionId" ];

          Dictionary<string, string> inputFields = new Dictionary<string, string>();
          inputFields[ "merchantId" ] = settings[ "merchantId" ];
          inputFields[ "token" ] = settings[ "token" ];
          inputFields[ "operation" ] = !autoCapture ? "AUTH" : "SALE";
          inputFields[ "transactionId" ] = transactionId;

          XDocument xmlResponse = XDocument.Parse( MakePostRequest( settings.ContainsKey( "testMode" ) && settings[ "testMode" ] == "1" ? "https://epayment-test.bbs.no/Netaxept/Process.aspx" : "https://epayment.bbs.no/Netaxept/Process.aspx", inputFields ), LoadOptions.PreserveWhitespace );

          if ( xmlResponse.XPathSelectElement( "//ProcessResponse" ) != null && xmlResponse.XPathSelectElement( "//ProcessResponse/ResponseCode" ).Value == "OK" ) {

            //Get details from the transaction
            xmlResponse = QueryTransaction( transactionId, settings );

            if ( xmlResponse.XPathSelectElement( "//PaymentInfo" ) != null ) {
              decimal totalAmount = decimal.Parse( xmlResponse.XPathSelectElement( "//PaymentInfo/OrderInformation/Total" ).Value, CultureInfo.InvariantCulture ) / 100M;
              string cardType = xmlResponse.XPathSelectElement( "//PaymentInfo/CardInformation/PaymentMethod" ).Value;
              string cardNumber = xmlResponse.XPathSelectElement( "//PaymentInfo/CardInformation/MaskedPAN" ).Value;

              callbackInfo = new CallbackInfo( totalAmount, transactionId, !autoCapture ? PaymentState.Authorized : PaymentState.Captured, cardType, cardNumber );
            } else {
              LoggingService.Instance.Log( "Netaxept(" + order.CartNumber + ") - ProcessCallback error - " + xmlResponse.XPathSelectElement( "//Error/Message" ).Value );
            }
          } else {
            string errorMessage = "Netaxept(" + order.CartNumber + ") - ProcessCallback error - " + xmlResponse.XPathSelectElement( "//Error/Message" ).Value;
            if ( xmlResponse.XPathSelectElement( "//Error/Result" ) != null ) {
              errorMessage += " response code: " + xmlResponse.XPathSelectElement( "//Error/Result/ResponseCode" ).Value + " transactionId: " + xmlResponse.XPathSelectElement( "//Error/Result/TransactionId" ).Value;
            }
            LoggingService.Instance.Log( errorMessage );
          }

        } else {
          LoggingService.Instance.Log( "Netaxept(" + order.CartNumber + ") - Response code isn't valid - response code: " + responseCode );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "Netaxept(" + order.CartNumber + ") - Process callback" );
      }

      HttpContext.Current.Response.Redirect( order.Properties[ callbackInfo != null ? "teaCommerceContinueUrl" : "teaCommerceCancelUrl" ], false );

      return callbackInfo;
    }

    public override ApiInfo GetStatus( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "merchantId", "settings" );
        settings.MustContainKey( "token", "settings" );

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
          LoggingService.Instance.Log( "Netaxept(" + order.OrderNumber + ") - Error making API request - error message: " + xmlResponse.XPathSelectElement( "//Error/Message" ).Value );
        }

      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "Netaxept(" + order.OrderNumber + ") - Get status" );
      }

      return apiInfo;
    }

    public override ApiInfo CapturePayment( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "merchantId", "settings" );
        settings.MustContainKey( "token", "settings" );

        Dictionary<string, string> inputFields = new Dictionary<string, string>();
        inputFields[ "merchantId" ] = settings[ "merchantId" ];
        inputFields[ "token" ] = settings[ "token" ];
        inputFields[ "operation" ] = "CAPTURE";
        inputFields[ "transactionId" ] = order.TransactionInformation.TransactionId;
        inputFields[ "transactionAmount" ] = ( order.TransactionInformation.AmountAuthorized.Value * 100M ).ToString( "0", CultureInfo.InvariantCulture );

        apiInfo = MakeApiRequest( order.OrderNumber, inputFields, order.TransactionInformation.TransactionId, PaymentState.Captured, settings.ContainsKey( "testMode" ) && settings[ "testMode" ] == "1" ? "https://epayment-test.bbs.no/Netaxept/Process.aspx" : "https://epayment.bbs.no/Netaxept/Process.aspx" );
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "Netaxept(" + order.OrderNumber + ") - Capture payment" );
      }

      return apiInfo;
    }

    public override ApiInfo RefundPayment( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "merchantId", "settings" );
        settings.MustContainKey( "token", "settings" );

        Dictionary<string, string> inputFields = new Dictionary<string, string>();
        inputFields[ "merchantId" ] = settings[ "merchantId" ];
        inputFields[ "token" ] = settings[ "token" ];
        inputFields[ "operation" ] = "CREDIT";
        inputFields[ "transactionId" ] = order.TransactionInformation.TransactionId;
        inputFields[ "transactionAmount" ] = ( order.TransactionInformation.AmountAuthorized.Value * 100M ).ToString( "0", CultureInfo.InvariantCulture );

        apiInfo = MakeApiRequest( order.OrderNumber, inputFields, order.TransactionInformation.TransactionId, PaymentState.Refunded, settings.ContainsKey( "testMode" ) && settings[ "testMode" ] == "1" ? "https://epayment-test.bbs.no/Netaxept/Process.aspx" : "https://epayment.bbs.no/Netaxept/Process.aspx" );
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "Netaxept(" + order.OrderNumber + ") - Refund payment" );
      }

      return apiInfo;
    }

    public override ApiInfo CancelPayment( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "merchantId", "settings" );
        settings.MustContainKey( "token", "settings" );

        Dictionary<string, string> inputFields = new Dictionary<string, string>();
        inputFields[ "merchantId" ] = settings[ "merchantId" ];
        inputFields[ "token" ] = settings[ "token" ];
        inputFields[ "operation" ] = "ANNUL";
        inputFields[ "transactionId" ] = order.TransactionInformation.TransactionId;

        apiInfo = MakeApiRequest( order.OrderNumber, inputFields, order.TransactionInformation.TransactionId, PaymentState.Cancelled, settings.ContainsKey( "testMode" ) && settings[ "testMode" ] == "1" ? "https://epayment-test.bbs.no/Netaxept/Process.aspx" : "https://epayment.bbs.no/Netaxept/Process.aspx" );
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "Netaxept(" + order.OrderNumber + ") - Cancel payment" );
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

    #region Helper methods

    protected ApiInfo MakeApiRequest( string orderNumber, IDictionary<string, string> inputFields, string transactionId, PaymentState paymentState, string url ) {
      ApiInfo apiInfo = null;

      try {
        inputFields.MustNotBeNull( "inputFields" );

        XDocument xmlResponse = XDocument.Parse( MakePostRequest( url, inputFields ), LoadOptions.PreserveWhitespace );

        if ( xmlResponse.XPathSelectElement( "//ProcessResponse" ) != null && xmlResponse.XPathSelectElement( "//ProcessResponse/ResponseCode" ).Value == "OK" ) {
          apiInfo = new ApiInfo( transactionId, paymentState );
        } else {
          LoggingService.Instance.Log( "Netaxept(" + orderNumber + ") - Error making API request - error message: " + xmlResponse.XPathSelectElement( "//Error/Message" ).Value );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "Netaxept(" + orderNumber + ") - Make API request" );
      }

      return apiInfo;
    }

    protected XDocument QueryTransaction( string transactionId, IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "merchantId", "settings" );
      settings.MustContainKey( "token", "settings" );

      Dictionary<string, string> inputFields = new Dictionary<string, string>();
      inputFields[ "merchantId" ] = settings[ "merchantId" ];
      inputFields[ "token" ] = settings[ "token" ];
      inputFields[ "transactionId" ] = transactionId;

      return XDocument.Parse( MakePostRequest( settings.ContainsKey( "testMode" ) && settings[ "testMode" ] == "1" ? "https://epayment-test.bbs.no/Netaxept/Query.aspx" : "https://epayment.bbs.no/Netaxept/Query.aspx", inputFields ), LoadOptions.PreserveWhitespace );
    }

    #endregion

  }
}
