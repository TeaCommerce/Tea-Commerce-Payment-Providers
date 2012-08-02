using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Web;
using TeaCommerce.Data;
using TeaCommerce.Data.Payment;
using TeaCommerce.PaymentProviders.wannafindService;
using umbraco.BusinessLogic;

namespace TeaCommerce.PaymentProviders {
  public class Wannafind : APaymentProvider {

    public override Dictionary<string, string> DefaultSettings {
      get {
        if ( defaultSettings == null ) {
          defaultSettings = new Dictionary<string, string>();
          defaultSettings[ "shopid" ] = string.Empty;
          defaultSettings[ "lang" ] = "en";
          defaultSettings[ "accepturl" ] = string.Empty;
          defaultSettings[ "declineurl" ] = string.Empty;
          defaultSettings[ "cardtype" ] = string.Empty;
          defaultSettings[ "md5AuthSecret" ] = string.Empty;
          defaultSettings[ "md5CallbackSecret" ] = string.Empty;
          defaultSettings[ "apiUser" ] = string.Empty;
          defaultSettings[ "apiPassword" ] = string.Empty;
        }
        return defaultSettings;
      }
    }

    public override string FormPostUrl { get { return "https://betaling.wannafind.dk/paymentwindow.php"; } }
    public override string FormAttributes { get { return @" id=""wannafind"" name=""wannafind"" target=""wannafind_paymentwindow"""; } }
    public override string DocumentationLink { get { return "http://anders.burla.dk/umbraco/tea-commerce/using-wannafind-with-tea-commerce/"; } }

    public override Dictionary<string, string> GenerateForm( Data.Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, Dictionary<string, string> settings ) {
      List<string> settingsToExclude = new string[] { "md5AuthSecret", "md5CallbackSecret", "apiUser", "apiPassword" }.ToList();
      Dictionary<string, string> inputFields = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      //orderid
      inputFields[ "orderid" ] = order.Id.ToString();

      //currency
      string currency = ISO4217CurrencyCodes[ order.CurrencyISOCode ];
      inputFields[ "currency" ] = currency;

      //amount
      string amount = ( order.TotalPrice * 100M ).ToString( "0", CultureInfo.InvariantCulture );
      inputFields[ "amount" ] = amount;

      inputFields[ "accepturl" ] = teaCommerceContinueUrl;
      inputFields[ "declineurl" ] = teaCommerceCancelUrl;
      inputFields[ "callbackurl" ] = teaCommerceCallBackUrl;

      //authtype
      inputFields[ "authtype" ] = "auth";

      //paytype
      inputFields[ "paytype" ] = "creditcard";

      //cardtype
      string cardType = string.Empty;
      if ( inputFields.ContainsKey( "cardtype" ) ) {
        cardType = inputFields[ "cardtype" ];
        if ( string.IsNullOrEmpty( cardType ) )
          inputFields.Remove( "cardtype" );
      }

      //uniqueorderid
      inputFields[ "uniqueorderid" ] = "true";

      //cardnomask
      inputFields[ "cardnomask" ] = "true";

      //md5securitykey
      if ( settings.ContainsKey( "md5AuthSecret" ) && !string.IsNullOrEmpty( settings[ "md5AuthSecret" ] ) )
        inputFields[ "checkmd5" ] = GetMD5Hash( currency + order.Id + amount + cardType + settings[ "md5AuthSecret" ] );

      //wannafind dont support to show order line information to the shopper

      return inputFields;
    }

    public override string SubmitJavascriptFunction( Dictionary<string, string> inputFields, Dictionary<string, string> settings ) {
      return @"openPaymenWindow();";
    }

    public override string GetContinueUrl( Dictionary<string, string> settings ) {
      return settings[ "accepturl" ];
    }

    public override string GetCancelUrl( Dictionary<string, string> settings ) {
      return settings[ "declineurl" ];
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, Dictionary<string, string> settings ) {
      //using ( StreamWriter writer = new StreamWriter( File.Create( HttpContext.Current.Server.MapPath( "~/wannafindTestCallback.txt" ) ) ) ) {
      //  writer.WriteLine( "QueryString:" );
      //  foreach ( string k in request.QueryString.Keys ) {
      //    writer.WriteLine( k + " : " + request.QueryString[ k ] );
      //  }
      //  writer.Flush();
      //}

      string errorMessage = string.Empty;

      string orderId = request.QueryString[ "orderid" ];
      string currency = request.QueryString[ "currency" ];
      string amount = request.QueryString[ "amount" ];
      string cardType = settings.ContainsKey( "cardtype" ) ? settings[ "cardtype" ] : string.Empty;

      string md5CheckValue = GetMD5Hash( orderId + currency + cardType + amount + settings[ "md5CallbackSecret" ] ); ;

      if ( md5CheckValue.Equals( request.QueryString[ "checkmd5callback" ] ) ) {

        string transaction = request.QueryString[ "transacknum" ];
        string cardtype = request.QueryString[ "cardtype" ];
        string cardnomask = request.QueryString[ "cardnomask" ];

        decimal totalAmount = decimal.Parse( amount, CultureInfo.InvariantCulture );

        //Wannafind cant give us info about auto capturing
        return new CallbackInfo( order.Name, totalAmount / 100M, transaction, PaymentStatus.Authorized, cardtype, cardnomask );
      } else
        errorMessage = "Tea Commerce - Wannafind - MD5Sum security check failed";

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new CallbackInfo( errorMessage );
    }

    public override APIInfo GetStatus( Order order, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      try {
        returnArray returnData = GetWannafindServiceClient( settings ).checkTransaction( int.Parse( order.TransactionPaymentTransactionId ), string.Empty, order.Id.ToString(), string.Empty, string.Empty );

        PaymentStatus paymentStatus = PaymentStatus.Initial;

        switch ( returnData.returncode ) {
          case 5:
            paymentStatus = PaymentStatus.Authorized;
            break;
          case 6:
            paymentStatus = PaymentStatus.Captured;
            break;
          case 7:
            paymentStatus = PaymentStatus.Cancelled;
            break;
          case 8:
            paymentStatus = PaymentStatus.Refunded;
            break;
        }

        return new APIInfo( order.TransactionPaymentTransactionId, paymentStatus );
      } catch ( WebException ) {
        errorMessage = "Tea Commerce - Wannafind - " + umbraco.ui.Text( "teaCommerce", "paymentProvider_Wannafind_wrongCredentials" );
      }

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    public override APIInfo CapturePayment( Order order, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      try {
        //When capturing of the complete amount - send 0 as parameter for amount
        int returnCode = GetWannafindServiceClient( settings ).captureTransaction( int.Parse( order.TransactionPaymentTransactionId ), 0 );
        if ( returnCode == 0 )
          return new APIInfo( order.TransactionPaymentTransactionId, PaymentStatus.Captured );
        else
          errorMessage = "Tea Commerce - Wannafind - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_Wannafind_error" ), returnCode );
      } catch ( WebException ) {
        errorMessage = "Tea Commerce - Wannafind - " + umbraco.ui.Text( "teaCommerce", "paymentProvider_Wannafind_wrongCredentials" );
      }

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    public override APIInfo RefundPayment( Order order, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      try {
        int returnCode = GetWannafindServiceClient( settings ).creditTransaction( int.Parse( order.TransactionPaymentTransactionId ), (int)( order.TotalPrice * 100M ) );
        if ( returnCode == 0 )
          return new APIInfo( order.TransactionPaymentTransactionId, PaymentStatus.Refunded );
        else
          errorMessage = "Tea Commerce - Wannafind - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_Wannafind_error" ), returnCode );
      } catch ( WebException ) {
        errorMessage = "Tea Commerce - Wannafind - " + umbraco.ui.Text( "teaCommerce", "paymentProvider_Wannafind_wrongCredentials" );
      }

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    public override APIInfo CancelPayment( Order order, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      try {
        int returnCode = GetWannafindServiceClient( settings ).cancelTransaction( int.Parse( order.TransactionPaymentTransactionId ) );
        if ( returnCode == 0 )
          return new APIInfo( order.TransactionPaymentTransactionId, PaymentStatus.Cancelled );
        else
          errorMessage = "Tea Commerce - Wannafind - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_Wannafind_error" ), returnCode );
      } catch ( WebException ) {
        errorMessage = "Tea Commerce - Wannafind - " + umbraco.ui.Text( "teaCommerce", "paymentProvider_Wannafind_wrongCredentials" );
      }

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    protected pgwapi GetWannafindServiceClient( Dictionary<string, string> settings ) {
      pgwapi paymentGateWayApi = new pgwapi();
      paymentGateWayApi.Credentials = new NetworkCredential( settings[ "apiUser" ], settings[ "apiPassword" ] );
      return paymentGateWayApi;
    }

  }
}
