using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.ServiceModel;
using System.Web;
using System.Xml.Linq;
using System.Xml.XPath;
using TeaCommerce.Data;
using TeaCommerce.Data.Payment;
using TeaCommerce.PaymentProviders.ePayService;
using umbraco.BusinessLogic;
using TeaCommerce.PaymentProviders.Extensions;
using System.IO;

namespace TeaCommerce.PaymentProviders {

  public class Netaxept : APaymentProvider {

    public override bool AllowsGetStatus { get { return false; } }
    public override bool AllowsCancelPayment { get { return false; } }
    public override bool AllowsCapturePayment { get { return false; } }
    public override bool AllowsRefundPayment { get { return false; } }

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
      inputFields[ "currencyCode" ] = ISO4217CurrencyCodes[ order.CurrencyISOCode ];

      //amount
      inputFields[ "amount" ] = ( order.TotalPrice * 100M ).ToString( "0", CultureInfo.InvariantCulture );

      //redirectUrl
      inputFields[ "redirectUrl" ] = teaCommerceCallBackUrl;

      //redirectOnError
      inputFields[ "redirectOnError" ] = "false";

      //paymentMethodList
      if ( inputFields.ContainsKey( "paymentMethodList" ) && string.IsNullOrEmpty( inputFields[ "paymentMethodList" ] ) )
        inputFields.Remove( "paymentMethodList" );

      string response = MakePostRequest( ( settings[ "testMode" ] != "1" ? "https://epayment.bbs.no/Netaxept/Register.aspx" : "https://epayment-test.bbs.no/Netaxept/Register.aspx" ), inputFields );
      XDocument xmlResponse = XDocument.Parse( response, LoadOptions.PreserveWhitespace );
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
      using ( StreamWriter writer = new StreamWriter( File.Create( HttpContext.Current.Server.MapPath( "~/NetaxceptCallback.txt" ) ) ) ) {
        writer.WriteLine( "QueryString:" );
        foreach ( string k in request.QueryString.Keys ) {
          writer.WriteLine( k + " : " + request.QueryString[ k ] );
        }
        writer.Flush();
      }

      string errorMessage = string.Empty;

      HttpContext.Current.Response.Redirect( order.GetPropertyValue( "teaCommerceContinueUrl" ) );
      //HttpContext.Current.Response.Redirect( order.GetPropertyValue( "teaCommerceCancelUrl" ) );

      //if ( GetMD5Hash( md5CheckValue ) == hash ) {

      //  string fee = request.QueryString[ "txnfee" ];
      //  string cardid = request.QueryString[ "paymenttype" ];
      //  string cardnopostfix = request.QueryString[ "cardno" ];

      //  decimal totalAmount = ( decimal.Parse( strAmount, CultureInfo.InvariantCulture ) + decimal.Parse( fee, CultureInfo.InvariantCulture ) );

      //  bool autoCaptured = settings.ContainsKey( "instantcapture" ) && settings[ "instantcapture" ].Equals( "1" );

      //  return new CallbackInfo( orderName, totalAmount / 100M, transaction, !autoCaptured ? PaymentStatus.Authorized : PaymentStatus.Captured, cardid, cardnopostfix );
      //} else
      //  errorMessage = "Tea Commerce - ePay - MD5Sum security check failed";

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new CallbackInfo( errorMessage );
    }

    public override APIInfo GetStatus( Order order, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      Dictionary<string, string> inputFields = new Dictionary<string, string>();
      inputFields[ "merchantId" ] = settings[ "merchantId" ];
      inputFields[ "token" ] = settings[ "token" ];
      inputFields[ "transactionId" ] = order.TransactionPaymentTransactionId;

      XDocument xmlResponse = XDocument.Parse( MakePostRequest( ( settings[ "testMode" ] != "1" ? "https://epayment.bbs.no/Netaxept/Query.aspx" : "https://epayment-test.bbs.no/Netaxept/Query.aspx" ), inputFields ), LoadOptions.PreserveWhitespace );

      //TransactionInformationType tit = new TransactionInformationType();
      //int ePayResponse = 0;

      //if ( GetEPayServiceClient().gettransaction( int.Parse( settings[ "merchantnumber" ] ), long.Parse( order.TransactionPaymentTransactionId ), settings.ContainsKey( "webservicepassword" ) ? settings[ "webservicepassword" ] : string.Empty, ref tit, ref ePayResponse ) )
      //  return new APIInfo( tit.transactionid.ToString(), GetPaymentStatus( tit.status, tit.creditedamount ) );
      //else
      //  errorMessage = "Tea Commerce - ePay - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_ePay_error" ), ePayResponse );

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

      //int pbsResponse = 0;
      //int ePayResponse = 0;

      //if ( GetEPayServiceClient().capture( int.Parse( settings[ "merchantnumber" ] ), long.Parse( order.TransactionPaymentTransactionId ), (int)( order.TotalPrice * 100M ), string.Empty, settings.ContainsKey( "webservicepassword" ) ? settings[ "webservicepassword" ] : string.Empty, ref pbsResponse, ref ePayResponse ) )
      //  return new APIInfo( order.TransactionPaymentTransactionId, PaymentStatus.Captured );
      //else
      //  errorMessage = "Tea Commerce - ePay - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_ePay_errorAdvanced" ), ePayResponse, pbsResponse );

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

      //int pbsResponse = 0;
      //int ePayResponse = 0;

      //if ( GetEPayServiceClient().credit( int.Parse( settings[ "merchantnumber" ] ), long.Parse( order.TransactionPaymentTransactionId ), (int)( order.TotalPrice * 100M ), string.Empty, settings.ContainsKey( "webservicepassword" ) ? settings[ "webservicepassword" ] : string.Empty, ref pbsResponse, ref ePayResponse ) )
      //  return new APIInfo( order.TransactionPaymentTransactionId, PaymentStatus.Refunded );
      //else
      //  errorMessage = "Tea Commerce - ePay - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_ePay_errorAdvanced" ), ePayResponse, pbsResponse );

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

      //int ePayResponse = 0;

      //if ( GetEPayServiceClient().delete( int.Parse( settings[ "merchantnumber" ] ), long.Parse( order.TransactionPaymentTransactionId ), string.Empty, settings.ContainsKey( "webservicepassword" ) ? settings[ "webservicepassword" ] : string.Empty, ref ePayResponse ) )
      //  return new APIInfo( order.TransactionPaymentTransactionId, PaymentStatus.Cancelled );
      //else
      //  errorMessage = "Tea Commerce - ePay - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_ePay_error" ), ePayResponse );

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }



  }
}
