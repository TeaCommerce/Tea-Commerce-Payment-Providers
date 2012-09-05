using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web;
using System.Xml.Linq;
using System.Xml.XPath;
using TeaCommerce.Data;
using TeaCommerce.Data.Payment;
using umbraco.BusinessLogic;

namespace TeaCommerce.PaymentProviders {

  public class Netaxept : APaymentProvider {

    public override Dictionary<string, string> DefaultSettings {
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

    public override Dictionary<string, string> GenerateForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, Dictionary<string, string> settings ) {
      List<string> settingsToExclude = new string[] { "accepturl", "cancelurl", "instantcapture", "testMode" }.ToList();
      Dictionary<string, string> inputFields = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      //orderNumber
      inputFields[ "orderNumber" ] = order.Name;

      //currencyCode
      inputFields[ "currencyCode" ] = order.CurrencyISOCode;

      //amount
      inputFields[ "amount" ] = ( order.TotalPrice * 100M ).ToString( "0", CultureInfo.InvariantCulture );

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
        Log.Add( LogTypes.Error, -1, "Tea Commerce - Netaxept - GenerateForm error - " + xmlResponse.XPathSelectElement( "//Error/Message" ).Value );
      }

      return new Dictionary<string, string>();
    }

    public override string GetContinueUrl( Dictionary<string, string> settings ) {
      return settings[ "accepturl" ];
    }

    public override string GetCancelUrl( Dictionary<string, string> settings ) {
      return settings[ "cancelurl" ];
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, Dictionary<string, string> settings ) {
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
            return new CallbackInfo( order.Name, totalAmount, transactionId, !autoCapture ? PaymentStatus.Authorized : PaymentStatus.Captured, cardType, cardNumber );
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

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new CallbackInfo( errorMessage );
    }

    public override APIInfo GetStatus( Order order, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      Dictionary<string, string> inputFields = new Dictionary<string, string>();
      inputFields[ "merchantId" ] = settings[ "merchantId" ];
      inputFields[ "token" ] = settings[ "token" ];
      inputFields[ "transactionId" ] = order.TransactionPaymentTransactionId;

      XDocument xmlResponse = QueryTransaction( order.TransactionPaymentTransactionId, settings );

      if ( xmlResponse.XPathSelectElement( "//PaymentInfo" ) != null ) {
        string transactionId = xmlResponse.XPathSelectElement( "//PaymentInfo/TransactionId" ).Value;
        bool authorized = bool.Parse( xmlResponse.XPathSelectElement( "//PaymentInfo/Summary/Authorized" ).Value );
        bool cancelled = bool.Parse( xmlResponse.XPathSelectElement( "//PaymentInfo/Summary/Annulled" ).Value );
        bool captured = decimal.Parse( xmlResponse.XPathSelectElement( "//PaymentInfo/Summary/AmountCaptured" ).Value, CultureInfo.InvariantCulture ) > 0;
        bool refunded = decimal.Parse( xmlResponse.XPathSelectElement( "//PaymentInfo/Summary/AmountCredited" ).Value, CultureInfo.InvariantCulture ) > 0;

        PaymentStatus paymentStatus = PaymentStatus.Initial;
        if ( refunded )
          paymentStatus = PaymentStatus.Refunded;
        else if ( captured )
          paymentStatus = PaymentStatus.Captured;
        else if ( cancelled )
          paymentStatus = PaymentStatus.Cancelled;
        else if ( authorized )
          paymentStatus = PaymentStatus.Authorized;

        return new APIInfo( transactionId, paymentStatus );
      } else {
        errorMessage = "Tea Commerce - Netaxept - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_Netaxept_error" ), xmlResponse.XPathSelectElement( "//Error/Message" ).Value );
      }

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    public override APIInfo CapturePayment( Order order, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      Dictionary<string, string> inputFields = new Dictionary<string, string>();
      inputFields[ "merchantId" ] = settings[ "merchantId" ];
      inputFields[ "token" ] = settings[ "token" ];
      inputFields[ "operation" ] = "CAPTURE";
      inputFields[ "transactionId" ] = order.TransactionPaymentTransactionId;
      inputFields[ "transactionAmount" ] = ( order.TotalPrice * 100M ).ToString( "0", CultureInfo.InvariantCulture );

      XDocument xmlResponse = XDocument.Parse( MakePostRequest( ( settings[ "testMode" ] != "1" ? "https://epayment.bbs.no/Netaxept/Process.aspx" : "https://epayment-test.bbs.no/Netaxept/Process.aspx" ), inputFields ), LoadOptions.PreserveWhitespace );

      if ( xmlResponse.XPathSelectElement( "//ProcessResponse" ) != null && xmlResponse.XPathSelectElement( "//ProcessResponse/ResponseCode" ).Value == "OK" ) {
        return new APIInfo( order.TransactionPaymentTransactionId, PaymentStatus.Captured );
      } else {
        errorMessage = "Tea Commerce - Netaxept - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_Netaxept_error" ), xmlResponse.XPathSelectElement( "//Error/Message" ).Value );
      }

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    public override APIInfo RefundPayment( Order order, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      Dictionary<string, string> inputFields = new Dictionary<string, string>();
      inputFields[ "merchantId" ] = settings[ "merchantId" ];
      inputFields[ "token" ] = settings[ "token" ];
      inputFields[ "operation" ] = "CREDIT";
      inputFields[ "transactionId" ] = order.TransactionPaymentTransactionId;
      inputFields[ "transactionAmount" ] = ( order.TotalPrice * 100M ).ToString( "0", CultureInfo.InvariantCulture );

      XDocument xmlResponse = XDocument.Parse( MakePostRequest( ( settings[ "testMode" ] != "1" ? "https://epayment.bbs.no/Netaxept/Process.aspx" : "https://epayment-test.bbs.no/Netaxept/Process.aspx" ), inputFields ), LoadOptions.PreserveWhitespace );

      if ( xmlResponse.XPathSelectElement( "//ProcessResponse" ) != null && xmlResponse.XPathSelectElement( "//ProcessResponse/ResponseCode" ).Value == "OK" ) {
        return new APIInfo( order.TransactionPaymentTransactionId, PaymentStatus.Refunded );
      } else {
        errorMessage = "Tea Commerce - Netaxept - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_Netaxept_error" ), xmlResponse.XPathSelectElement( "//Error/Message" ).Value );
      }

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    public override APIInfo CancelPayment( Order order, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      Dictionary<string, string> inputFields = new Dictionary<string, string>();
      inputFields[ "merchantId" ] = settings[ "merchantId" ];
      inputFields[ "token" ] = settings[ "token" ];
      inputFields[ "operation" ] = "ANNUL";
      inputFields[ "transactionId" ] = order.TransactionPaymentTransactionId;

      XDocument xmlResponse = XDocument.Parse( MakePostRequest( ( settings[ "testMode" ] != "1" ? "https://epayment.bbs.no/Netaxept/Process.aspx" : "https://epayment-test.bbs.no/Netaxept/Process.aspx" ), inputFields ), LoadOptions.PreserveWhitespace );

      if ( xmlResponse.XPathSelectElement( "//ProcessResponse" ) != null && xmlResponse.XPathSelectElement( "//ProcessResponse/ResponseCode" ).Value == "OK" ) {
        return new APIInfo( order.TransactionPaymentTransactionId, PaymentStatus.Cancelled );
      } else {
        errorMessage = "Tea Commerce - Netaxept - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_Netaxept_error" ), xmlResponse.XPathSelectElement( "//Error/Message" ).Value );
      }

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    private XDocument QueryTransaction( string transactionId, Dictionary<string, string> settings ) {
      Dictionary<string, string> inputFields = new Dictionary<string, string>();
      inputFields[ "merchantId" ] = settings[ "merchantId" ];
      inputFields[ "token" ] = settings[ "token" ];
      inputFields[ "transactionId" ] = transactionId;

      return XDocument.Parse( MakePostRequest( ( settings[ "testMode" ] != "1" ? "https://epayment.bbs.no/Netaxept/Query.aspx" : "https://epayment-test.bbs.no/Netaxept/Query.aspx" ), inputFields ), LoadOptions.PreserveWhitespace );
    }

  }
}
