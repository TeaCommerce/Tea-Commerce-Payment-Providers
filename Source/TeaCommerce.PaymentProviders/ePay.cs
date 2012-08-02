using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.ServiceModel;
using System.Web;
using TeaCommerce.Data;
using TeaCommerce.Data.Payment;
using TeaCommerce.PaymentProviders.ePayService;
using umbraco.BusinessLogic;
using TeaCommerce.PaymentProviders.Extensions;
using System.IO;

namespace TeaCommerce.PaymentProviders {

  public class ePay : APaymentProvider {

    public override Dictionary<string, string> DefaultSettings {
      get {
        if ( defaultSettings == null ) {
          defaultSettings = new Dictionary<string, string>();
          defaultSettings[ "merchantnumber" ] = string.Empty;
          defaultSettings[ "language" ] = "2";
          defaultSettings[ "accepturl" ] = string.Empty;
          defaultSettings[ "cancelurl" ] = string.Empty;
          defaultSettings[ "instantcapture" ] = "0";
          defaultSettings[ "paymenttype" ] = "";
          defaultSettings[ "windowstate" ] = "1";
          defaultSettings[ "iframeelement" ] = "";
          defaultSettings[ "md5securitykey" ] = string.Empty;
          defaultSettings[ "webservicepassword" ] = string.Empty;
        }
        return defaultSettings;
      }
    }

    public override string FormPostUrl { get { return "https://ssl.ditonlinebetalingssystem.dk/integration/ewindow/Default.aspx"; } }
    public override string DocumentationLink { get { return "http://anders.burla.dk/umbraco/tea-commerce/using-epay-with-tea-commerce/"; } }

    public override Dictionary<string, string> GenerateForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, Dictionary<string, string> settings ) {
      List<string> settingsToExclude = new string[] { "md5securitykey", "webservicepassword", "iframeelement" }.ToList();
      Dictionary<string, string> inputFields = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      //orderid
      inputFields[ "orderid" ] = order.Name;

      //currency
      string currency = ISO4217CurrencyCodes[ order.CurrencyISOCode ];
      inputFields[ "currency" ] = currency;

      //amount
      string strAmount = ( order.TotalPrice * 100M ).ToString( "0", CultureInfo.InvariantCulture );
      inputFields[ "amount" ] = strAmount;

      inputFields[ "accepturl" ] = teaCommerceContinueUrl;
      inputFields[ "cancelurl" ] = teaCommerceCancelUrl;
      inputFields[ "callbackurl" ] = teaCommerceCallBackUrl;

      //instantcallback
      inputFields[ "instantcallback" ] = "1";

      //instantcapture
      if ( inputFields.ContainsKey( "instantcapture" ) && string.IsNullOrEmpty( inputFields[ "instantcapture" ] ) )
        inputFields.Remove( "instantcapture" );

      //cardtype
      if ( inputFields.ContainsKey( "paymenttype" ) && string.IsNullOrEmpty( inputFields[ "paymenttype" ] ) )
        inputFields.Remove( "paymenttype" );

      //windowstate
      if ( inputFields.ContainsKey( "windowstate" ) && string.IsNullOrEmpty( inputFields[ "windowstate" ] ) )
        inputFields.Remove( "windowstate" );

      inputFields[ "ownreceipt" ] = "1";

      //ePay dont support to show order line information to the shopper

      //md5securitykey
      if ( !string.IsNullOrEmpty( settings[ "md5securitykey" ] ) )
        inputFields[ "hash" ] = GetMD5Hash( inputFields.Values.Join( "" ) + settings[ "md5securitykey" ] );

      return inputFields;
    }

    public override string SubmitJavascriptFunction( Dictionary<string, string> inputFields, Dictionary<string, string> settings ) {
      string rtnString = string.Empty;

      //If its state 3 (fullscreen) we return empty string because it's not supported by the JavaScript
      if ( !inputFields.ContainsKey( "windowstate" ) || inputFields[ "windowstate" ] != "3" ) {

        //Check if its iFrame mode (2) and check if an html element is specified - else fallback to overlay (1)
        if ( inputFields.ContainsKey( "windowstate" ) && inputFields[ "windowstate" ] == "2" && !settings.ContainsKey( "iframeelement" ) ) {
          inputFields[ "windowstate" ] = "1";
        }

        rtnString += "var paymentwindow = new PaymentWindow({";
        foreach ( var kvp in inputFields ) {
          rtnString += "'" + kvp.Key + "': '" + kvp.Value + "',";
        }
        rtnString = rtnString.Remove( rtnString.Length - 1, 1 );
        rtnString += "});";

        //Check if it's iFrame mode
        if ( inputFields.ContainsKey( "windowstate" ) && inputFields[ "windowstate" ] == "2" ) {
          rtnString += "paymentwindow.append('" + settings[ "iframeelement" ] + "');";
        }

        rtnString += "paymentwindow.open();";
      }

      return rtnString;
    }

    public override string GetContinueUrl( Dictionary<string, string> settings ) {
      return settings[ "accepturl" ];
    }

    public override string GetCancelUrl( Dictionary<string, string> settings ) {
      return settings[ "cancelurl" ];
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, Dictionary<string, string> settings ) {
      //using ( StreamWriter writer = new StreamWriter( File.Create( HttpContext.Current.Server.MapPath( "~/ePayTestCallback.txt" ) ) ) ) {
      //  writer.WriteLine( "QueryString:" );
      //  foreach ( string k in request.QueryString.Keys ) {
      //    writer.WriteLine( k + " : " + request.QueryString[ k ] );
      //  }
      //  writer.Flush();
      //}

      string errorMessage = string.Empty;

      string transaction = request.QueryString[ "txnid" ];
      string orderName = request.QueryString[ "orderid" ];
      string strAmount = request.QueryString[ "amount" ];
      string hash = request.QueryString[ "hash" ];

      string md5CheckValue = string.Empty;

      foreach ( string k in request.QueryString.Keys ) {
        if ( k != "hash" ) {
          md5CheckValue += request.QueryString[ k ];
        }
      }
      md5CheckValue += settings[ "md5securitykey" ];

      if ( GetMD5Hash( md5CheckValue ) == hash ) {

        string fee = request.QueryString[ "txnfee" ];
        string cardid = request.QueryString[ "paymenttype" ];
        string cardnopostfix = request.QueryString[ "cardno" ];

        decimal totalAmount = ( decimal.Parse( strAmount, CultureInfo.InvariantCulture ) + decimal.Parse( fee, CultureInfo.InvariantCulture ) );

        bool autoCaptured = settings.ContainsKey( "instantcapture" ) && settings[ "instantcapture" ].Equals( "1" );

        return new CallbackInfo( orderName, totalAmount / 100M, transaction, !autoCaptured ? PaymentStatus.Authorized : PaymentStatus.Captured, cardid, cardnopostfix );
      } else
        errorMessage = "Tea Commerce - ePay - MD5Sum security check failed";

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new CallbackInfo( errorMessage );
    }

    public override APIInfo GetStatus( Order order, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      TransactionInformationType tit = new TransactionInformationType();
      int ePayResponse = 0;

      if ( GetEPayServiceClient().gettransaction( int.Parse( settings[ "merchantnumber" ] ), long.Parse( order.TransactionPaymentTransactionId ), settings.ContainsKey( "webservicepassword" ) ? settings[ "webservicepassword" ] : string.Empty, ref tit, ref ePayResponse ) )
        return new APIInfo( tit.transactionid.ToString(), GetPaymentStatus( tit.status, tit.creditedamount ) );
      else
        errorMessage = "Tea Commerce - ePay - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_ePay_error" ), ePayResponse );

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    public override APIInfo CapturePayment( Order order, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      int pbsResponse = 0;
      int ePayResponse = 0;

      if ( GetEPayServiceClient().capture( int.Parse( settings[ "merchantnumber" ] ), long.Parse( order.TransactionPaymentTransactionId ), (int)( order.TotalPrice * 100M ), string.Empty, settings.ContainsKey( "webservicepassword" ) ? settings[ "webservicepassword" ] : string.Empty, ref pbsResponse, ref ePayResponse ) )
        return new APIInfo( order.TransactionPaymentTransactionId, PaymentStatus.Captured );
      else
        errorMessage = "Tea Commerce - ePay - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_ePay_errorAdvanced" ), ePayResponse, pbsResponse );

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    public override APIInfo RefundPayment( Order order, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      int pbsResponse = 0;
      int ePayResponse = 0;

      if ( GetEPayServiceClient().credit( int.Parse( settings[ "merchantnumber" ] ), long.Parse( order.TransactionPaymentTransactionId ), (int)( order.TotalPrice * 100M ), string.Empty, settings.ContainsKey( "webservicepassword" ) ? settings[ "webservicepassword" ] : string.Empty, ref pbsResponse, ref ePayResponse ) )
        return new APIInfo( order.TransactionPaymentTransactionId, PaymentStatus.Refunded );
      else
        errorMessage = "Tea Commerce - ePay - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_ePay_errorAdvanced" ), ePayResponse, pbsResponse );

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    public override APIInfo CancelPayment( Order order, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      int ePayResponse = 0;

      if ( GetEPayServiceClient().delete( int.Parse( settings[ "merchantnumber" ] ), long.Parse( order.TransactionPaymentTransactionId ), string.Empty, settings.ContainsKey( "webservicepassword" ) ? settings[ "webservicepassword" ] : string.Empty, ref ePayResponse ) )
        return new APIInfo( order.TransactionPaymentTransactionId, PaymentStatus.Cancelled );
      else
        errorMessage = "Tea Commerce - ePay - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_ePay_error" ), ePayResponse );

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    protected PaymentSoapClient GetEPayServiceClient() {
      return new PaymentSoapClient( new BasicHttpBinding( BasicHttpSecurityMode.Transport ), new EndpointAddress( "https://ssl.ditonlinebetalingssystem.dk/remote/payment.asmx" ) );
    }

    protected PaymentStatus GetPaymentStatus( TransactionStatus transactionStatus, int refundAmount ) {
      PaymentStatus paymentStatus = PaymentStatus.Initial;
      if ( transactionStatus == TransactionStatus.PAYMENT_NEW )
        paymentStatus = PaymentStatus.Authorized;
      else if ( transactionStatus == TransactionStatus.PAYMENT_CAPTURED && refundAmount == 0 )
        paymentStatus = PaymentStatus.Captured;
      else if ( transactionStatus == TransactionStatus.PAYMENT_DELETED )
        paymentStatus = PaymentStatus.Cancelled;
      else if ( transactionStatus == TransactionStatus.PAYMENT_CAPTURED && refundAmount != 0 )
        paymentStatus = PaymentStatus.Refunded;
      else if ( transactionStatus == TransactionStatus.PAYMENT_EUROLINE_WAIT_CAPTURE || transactionStatus == TransactionStatus.PAYMENT_EUROLINE_WAIT_CREDIT )
        paymentStatus = PaymentStatus.PendingExternalSystem;
      return paymentStatus;
    }

  }
}
