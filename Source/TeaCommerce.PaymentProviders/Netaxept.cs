using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web;
using System.Xml.Linq;
using System.Xml.XPath;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.PaymentProviders;
using TeaCommerce.Api.Infrastructure.Logging;

namespace TeaCommerce.PaymentProviders {

  public class Netaxept : APaymentProvider {

    protected const string apiErrorFormatString = "Error making API request - Error message: {0}";

    public override IDictionary<string, string> DefaultSettings {
      get {
        if ( defaultSettings == null ) {
          defaultSettings = new Dictionary<string, string>();
          defaultSettings[ "merchantId" ] = string.Empty;
          defaultSettings[ "token" ] = string.Empty;
          defaultSettings[ "language" ] = "en_GB";
          defaultSettings[ "accepturl" ] = string.Empty;
          defaultSettings[ "cancelurl" ] = string.Empty;
          defaultSettings[ "instantcapture" ] = "0";
          defaultSettings[ "paymentMethodList" ] = "";
          defaultSettings[ "testMode" ] = "1";
        }
        return defaultSettings;
      }
    }

    protected string formPostUrl;
    public override string FormPostUrl { get { return formPostUrl; } }
    public override string DocumentationLink { get { return "http://anders.burla.dk/umbraco/tea-commerce/using-netaxept-with-tea-commerce/"; } }

    public override IDictionary<string, string> GenerateForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, IDictionary<string, string> settings ) {
      List<string> settingsToExclude = new string[] { "accepturl", "cancelurl", "instantcapture", "testMode" }.ToList();
      Dictionary<string, string> inputFields = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      //orderNumber
      inputFields[ "orderNumber" ] = order.CartNumber;

      //currencyCode
      inputFields[ "currencyCode" ] = order.CurrencyISOCode;

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
        order.AddProperty( new OrderProperty( "teaCommerceContinueUrl", teaCommerceContinueUrl, true, true ) );
        order.AddProperty( new OrderProperty( "teaCommerceCancelUrl", teaCommerceCancelUrl, true, true ) );
        order.Save();

        formPostUrl = ( settings[ "testMode" ] != "1" ? "https://epayment.bbs.no/Terminal/default.aspx" : "https://epayment-test.bbs.no/Terminal/default.aspx" ) + "?merchantId=" + settings[ "merchantId" ] + "&transactionId=" + xmlResponse.XPathSelectElement( "//TransactionId" ).Value;
      } else {
        LoggingService.Instance.Log( "Tea Commerce - Netaxept - GenerateForm error - " + xmlResponse.XPathSelectElement( "//Error/Message" ).Value );
      }

      return new Dictionary<string, string>();
    }

    public override string GetContinueUrl( IDictionary<string, string> settings ) {
      return settings[ "accepturl" ];
    }

    public override string GetCancelUrl( IDictionary<string, string> settings ) {
      return settings[ "cancelurl" ];
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      //using ( StreamWriter writer = new StreamWriter( File.Create( HttpContext.Current.Server.MapPath( "~/NetaxceptCallback.txt" ) ) ) ) {
      //  writer.WriteLine( "QueryString:" );
      //  foreach ( string k in request.QueryString.Keys ) {
      //    writer.WriteLine( k + " : " + request.QueryString[ k ] );
      //  }
      //  writer.Flush();
      //}

      string errorMessage = string.Empty;

      string responseCode = request.QueryString[ "responseCode" ];

      if ( responseCode != null && responseCode == "OK" ) {
        bool autoCapture = settings[ "instantcapture" ] == "1";
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

            HttpContext.Current.Response.Redirect( order.GetPropertyValue( "teaCommerceContinueUrl" ), false );
            return new CallbackInfo( totalAmount, transactionId, !autoCapture ? PaymentState.Authorized : PaymentState.Captured, cardType, cardNumber );
          } else {
            errorMessage = "Tea Commerce - Netaxept - ProcessCallback error - " + xmlResponse.XPathSelectElement( "//Error/Message" ).Value;
          }
        } else {
          errorMessage = "Tea Commerce - Netaxept - ProcessCallback error - " + xmlResponse.XPathSelectElement( "//Error/Message" ).Value;
          if ( xmlResponse.XPathSelectElement( "//Error/Result" ) != null ) {
            errorMessage += " response code: " + xmlResponse.XPathSelectElement( "//Error/Result/ResponseCode" ).Value + " transactionId: " + xmlResponse.XPathSelectElement( "//Error/Result/TransactionId" ).Value;
          }

        }

      } else {
        errorMessage = "Tea Commerce - Netaxept - Response code isn't valid - response code: " + responseCode;
      }

      //If we get here an error occured and we redirect to the cancel url
      HttpContext.Current.Response.Redirect( order.GetPropertyValue( "teaCommerceCancelUrl" ), false );

      LoggingService.Instance.Log( errorMessage );
      return new CallbackInfo( errorMessage );
    }

    public override ApiInfo GetStatus( Order order, IDictionary<string, string> settings ) {
      string errorMessage = string.Empty;

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

        PaymentState paymentState = PaymentState.Initiated;
        if ( refunded )
          paymentState = PaymentState.Refunded;
        else if ( captured )
          paymentState = PaymentState.Captured;
        else if ( cancelled )
          paymentState = PaymentState.Cancelled;
        else if ( authorized )
          paymentState = PaymentState.Authorized;

        return new ApiInfo( transactionId, paymentState );
      } else {
        errorMessage = "Tea Commerce - Netaxept - " + string.Format( apiErrorFormatString, xmlResponse.XPathSelectElement( "//Error/Message" ).Value );
      }

      LoggingService.Instance.Log( errorMessage );
      return new ApiInfo( errorMessage );
    }

    public override ApiInfo CapturePayment( Order order, IDictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      Dictionary<string, string> inputFields = new Dictionary<string, string>();
      inputFields[ "merchantId" ] = settings[ "merchantId" ];
      inputFields[ "token" ] = settings[ "token" ];
      inputFields[ "operation" ] = "CAPTURE";
      inputFields[ "transactionId" ] = order.TransactionInformation.TransactionId;
      inputFields[ "transactionAmount" ] = ( order.TotalPrice.WithVat * 100M ).ToString( "0", CultureInfo.InvariantCulture );

      XDocument xmlResponse = XDocument.Parse( MakePostRequest( ( settings[ "testMode" ] != "1" ? "https://epayment.bbs.no/Netaxept/Process.aspx" : "https://epayment-test.bbs.no/Netaxept/Process.aspx" ), inputFields ), LoadOptions.PreserveWhitespace );

      if ( xmlResponse.XPathSelectElement( "//ProcessResponse" ) != null && xmlResponse.XPathSelectElement( "//ProcessResponse/ResponseCode" ).Value == "OK" ) {
        return new ApiInfo( order.TransactionInformation.TransactionId, PaymentState.Captured );
      } else {
        errorMessage = "Tea Commerce - Netaxept - " + string.Format( apiErrorFormatString, xmlResponse.XPathSelectElement( "//Error/Message" ).Value );
      }

      LoggingService.Instance.Log( errorMessage );
      return new ApiInfo( errorMessage );
    }

    public override ApiInfo RefundPayment( Order order, IDictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      Dictionary<string, string> inputFields = new Dictionary<string, string>();
      inputFields[ "merchantId" ] = settings[ "merchantId" ];
      inputFields[ "token" ] = settings[ "token" ];
      inputFields[ "operation" ] = "CREDIT";
      inputFields[ "transactionId" ] = order.TransactionInformation.TransactionId;
      inputFields[ "transactionAmount" ] = ( order.TotalPrice.WithVat * 100M ).ToString( "0", CultureInfo.InvariantCulture );

      XDocument xmlResponse = XDocument.Parse( MakePostRequest( ( settings[ "testMode" ] != "1" ? "https://epayment.bbs.no/Netaxept/Process.aspx" : "https://epayment-test.bbs.no/Netaxept/Process.aspx" ), inputFields ), LoadOptions.PreserveWhitespace );

      if ( xmlResponse.XPathSelectElement( "//ProcessResponse" ) != null && xmlResponse.XPathSelectElement( "//ProcessResponse/ResponseCode" ).Value == "OK" ) {
        return new ApiInfo( order.TransactionInformation.TransactionId, PaymentState.Refunded );
      } else {
        errorMessage = "Tea Commerce - Netaxept - " + string.Format( apiErrorFormatString, xmlResponse.XPathSelectElement( "//Error/Message" ).Value );
      }

      LoggingService.Instance.Log( errorMessage );
      return new ApiInfo( errorMessage );
    }

    public override ApiInfo CancelPayment( Order order, IDictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      Dictionary<string, string> inputFields = new Dictionary<string, string>();
      inputFields[ "merchantId" ] = settings[ "merchantId" ];
      inputFields[ "token" ] = settings[ "token" ];
      inputFields[ "operation" ] = "ANNUL";
      inputFields[ "transactionId" ] = order.TransactionInformation.TransactionId;

      XDocument xmlResponse = XDocument.Parse( MakePostRequest( ( settings[ "testMode" ] != "1" ? "https://epayment.bbs.no/Netaxept/Process.aspx" : "https://epayment-test.bbs.no/Netaxept/Process.aspx" ), inputFields ), LoadOptions.PreserveWhitespace );

      if ( xmlResponse.XPathSelectElement( "//ProcessResponse" ) != null && xmlResponse.XPathSelectElement( "//ProcessResponse/ResponseCode" ).Value == "OK" ) {
        return new ApiInfo( order.TransactionInformation.TransactionId, PaymentState.Cancelled );
      } else {
        errorMessage = "Tea Commerce - Netaxept - " + string.Format( apiErrorFormatString, xmlResponse.XPathSelectElement( "//Error/Message" ).Value );
      }

      LoggingService.Instance.Log( errorMessage );
      return new ApiInfo( errorMessage );
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
      Dictionary<string, string> inputFields = new Dictionary<string, string>();
      inputFields[ "merchantId" ] = settings[ "merchantId" ];
      inputFields[ "token" ] = settings[ "token" ];
      inputFields[ "transactionId" ] = transactionId;

      return XDocument.Parse( MakePostRequest( ( settings[ "testMode" ] != "1" ? "https://epayment.bbs.no/Netaxept/Query.aspx" : "https://epayment-test.bbs.no/Netaxept/Query.aspx" ), inputFields ), LoadOptions.PreserveWhitespace );
    }

  }
}
