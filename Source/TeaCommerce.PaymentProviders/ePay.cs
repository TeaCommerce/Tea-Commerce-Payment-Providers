using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.ServiceModel;
using System.Web;
using TeaCommerce.Data;
using TeaCommerce.Data.Payment;
using TeaCommerce.PaymentProviders.ePayService;
using umbraco.BusinessLogic;

namespace TeaCommerce.PaymentProviders {

  public class ePay : APaymentProvider {

    public override Dictionary<string, string> DefaultSettings {
      get {
        if ( defaultSettings == null ) {
          defaultSettings = new Dictionary<string, string>();
          defaultSettings[ "merchantnumber" ] = string.Empty;
          defaultSettings[ "language" ] = "2";
          defaultSettings[ "accepturl" ] = string.Empty;
          defaultSettings[ "declineurl" ] = string.Empty;
          defaultSettings[ "instantcapture" ] = "0";
          defaultSettings[ "cardtype" ] = "0";
          defaultSettings[ "windowstate" ] = "1";
          defaultSettings[ "md5securitykey" ] = string.Empty;
          defaultSettings[ "webservicepassword" ] = string.Empty;
        }
        return defaultSettings;
      }
    }

    public override string FormPostUrl { get { return "https://ssl.ditonlinebetalingssystem.dk/popup/default.asp"; } }
    public override string FormAttributes { get { return @"id=""ePay"" name=""ePay"" target=""ePay_window"""; } }
    public override string SubmitJavascriptFunction { get { return @"open_ePay_window();"; } }
    public override string DocumentationLink { get { return "http://anders.burla.dk/umbraco/tea-commerce/using-epay-with-tea-commerce/"; } }

    public override Dictionary<string, string> GenerateForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, Dictionary<string, string> settings ) {
      List<string> settingsToExclude = new string[] { "md5securitykey", "webservicepassword" }.ToList();
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
      inputFields[ "declineurl" ] = teaCommerceCancelUrl;
      inputFields[ "callbackurl" ] = teaCommerceCallBackUrl;

      //instantcallback
      inputFields[ "instantcallback" ] = "1";

      //instantcapture
      if ( inputFields.ContainsKey( "instantcapture" ) && string.IsNullOrEmpty( inputFields[ "instantcapture" ] ) )
        inputFields.Remove( "instantcapture" );

      //md5securitykey
      if ( settings.ContainsKey( "md5securitykey" ) && !string.IsNullOrEmpty( settings[ "md5securitykey" ] ) )
        inputFields[ "md5key" ] = GetMD5Hash( currency + strAmount + order.Name + settings[ "md5securitykey" ] );

      //cardtype
      if ( inputFields.ContainsKey( "cardtype" ) && string.IsNullOrEmpty( inputFields[ "cardtype" ] ) )
        inputFields.Remove( "cardtype" );

      //windowstate
      if ( inputFields.ContainsKey( "windowstate" ) && string.IsNullOrEmpty( inputFields[ "windowstate" ] ) )
        inputFields.Remove( "windowstate" );

      inputFields[ "ownreceipt" ] = "1";

      //ePay dont support to show order line information to the shopper

      return inputFields;
    }

    public override string GetContinueUrl( Dictionary<string, string> settings ) {
      return settings[ "accepturl" ];
    }

    public override string GetCancelUrl( Dictionary<string, string> settings ) {
      return settings[ "declineurl" ];
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

      string transaction = request.QueryString[ "tid" ];
      string orderName = request.QueryString[ "orderid" ];
      string strAmount = request.QueryString[ "amount" ];
      string eKey = request.QueryString[ "eKey" ];

      string md5CheckValue = string.Empty;
      md5CheckValue += strAmount;
      md5CheckValue += orderName;
      md5CheckValue += transaction;
      md5CheckValue += settings[ "md5securitykey" ];

      if ( string.IsNullOrEmpty( eKey ) || GetMD5Hash( md5CheckValue ).Equals( eKey ) ) {

        string fee = request.QueryString[ "transfee" ];
        string cardid = request.QueryString[ "cardid" ];
        string cardnopostfix = request.QueryString[ "cardnopostfix" ];

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
