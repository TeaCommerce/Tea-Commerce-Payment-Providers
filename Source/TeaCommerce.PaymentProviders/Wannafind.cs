using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Web;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.PaymentProviders;
using TeaCommerce.PaymentProviders.wannafindService;
using TeaCommerce.Api.Infrastructure.Logging;

namespace TeaCommerce.PaymentProviders {

  [PaymentProvider( "Wannafind" )]
  public class Wannafind : APaymentProvider {

    protected const string apiErrorFormatString = "Error making API request - Error code: {0} - see http://www.wannafind.dk/support/downloads/19/ (page 27) for a description of these";
    protected const string wrongCredentialsError = "Error making API request - Wrong credentials or IP address not allowed";

    public override IDictionary<string, string> DefaultSettings {
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

    public override IDictionary<string, string> GenerateForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, IDictionary<string, string> settings ) {
      List<string> settingsToExclude = new string[] { "md5AuthSecret", "md5CallbackSecret", "apiUser", "apiPassword" }.ToList();
      Dictionary<string, string> inputFields = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      //orderid
      inputFields[ "orderid" ] = order.Id.ToString();

      //currency
      string currency = ISO4217CurrencyCodes[ order.CurrencyISOCode ];
      inputFields[ "currency" ] = currency;

      //amount
      string amount = ( order.TotalPrice.WithVat * 100M ).ToString( "0", CultureInfo.InvariantCulture );
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

    public override string SubmitJavascriptFunction( IDictionary<string, string> inputFields, IDictionary<string, string> settings ) {
      return @"openPaymenWindow();";
    }

    public override string GetContinueUrl( IDictionary<string, string> settings ) {
      return settings[ "accepturl" ];
    }

    public override string GetCancelUrl( IDictionary<string, string> settings ) {
      return settings[ "declineurl" ];
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, IDictionary<string, string> settings ) {
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
        return new CallbackInfo( totalAmount / 100M, transaction, PaymentState.Authorized, cardtype, cardnomask );
      } else
        errorMessage = "Tea Commerce - Wannafind - MD5Sum security check failed";

      LoggingService.Instance.Log( errorMessage );
      return new CallbackInfo( errorMessage );
    }

    public override ApiInfo GetStatus( Order order, IDictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      try {
        returnArray returnData = GetWannafindServiceClient( settings ).checkTransaction( int.Parse( order.TransactionInformation.TransactionId ), string.Empty, order.Id.ToString(), string.Empty, string.Empty );

        PaymentState paymentState = PaymentState.Initiated;

        switch ( returnData.returncode ) {
          case 5:
            paymentState = PaymentState.Authorized;
            break;
          case 6:
            paymentState = PaymentState.Captured;
            break;
          case 7:
            paymentState = PaymentState.Cancelled;
            break;
          case 8:
            paymentState = PaymentState.Refunded;
            break;
        }

        return new ApiInfo( order.TransactionInformation.TransactionId, paymentState );
      } catch ( WebException ) {
        errorMessage = "Tea Commerce - Wannafind - " + wrongCredentialsError;
      }

      LoggingService.Instance.Log( errorMessage );
      return new ApiInfo( errorMessage );
    }

    public override ApiInfo CapturePayment( Order order, IDictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      try {
        //When capturing of the complete amount - send 0 as parameter for amount
        int returnCode = GetWannafindServiceClient( settings ).captureTransaction( int.Parse( order.TransactionInformation.TransactionId ), 0 );
        if ( returnCode == 0 )
          return new ApiInfo( order.TransactionInformation.TransactionId, PaymentState.Captured );
        else
          errorMessage = "Tea Commerce - Wannafind - " + string.Format( apiErrorFormatString, returnCode );
      } catch ( WebException ) {
        errorMessage = "Tea Commerce - Wannafind - " + wrongCredentialsError;
      }

      LoggingService.Instance.Log( errorMessage );
      return new ApiInfo( errorMessage );
    }

    public override ApiInfo RefundPayment( Order order, IDictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      try {
        int returnCode = GetWannafindServiceClient( settings ).creditTransaction( int.Parse( order.TransactionInformation.TransactionId ), (int)( order.TotalPrice.WithVat * 100M ) );
        if ( returnCode == 0 )
          return new ApiInfo( order.TransactionInformation.TransactionId, PaymentState.Refunded );
        else
          errorMessage = "Tea Commerce - Wannafind - " + string.Format( apiErrorFormatString, returnCode );
      } catch ( WebException ) {
        errorMessage = "Tea Commerce - Wannafind - " + wrongCredentialsError;
      }

      LoggingService.Instance.Log( errorMessage );
      return new ApiInfo( errorMessage );
    }

    public override ApiInfo CancelPayment( Order order, IDictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      try {
        int returnCode = GetWannafindServiceClient( settings ).cancelTransaction( int.Parse( order.TransactionInformation.TransactionId ) );
        if ( returnCode == 0 )
          return new ApiInfo( order.TransactionInformation.TransactionId, PaymentState.Cancelled );
        else
          errorMessage = "Tea Commerce - Wannafind - " + string.Format( apiErrorFormatString, returnCode );
      } catch ( WebException ) {
        errorMessage = "Tea Commerce - Wannafind - " + wrongCredentialsError;
      }

      LoggingService.Instance.Log( errorMessage );
      return new ApiInfo( errorMessage );
    }

    public override string GetLocalizedSettingsKey( string settingsKey, CultureInfo culture ) {
      switch ( settingsKey ) {
        case "accepturl":
          return settingsKey + "<br/><small>e.g. /continue/</small>";
        case "declineurl":
          return settingsKey + "<br/><small>e.g. /cancel/</small>";
        case "cardtype":
          return settingsKey + "<br/><small>e.g. VISA,MC</small>";
        default:
          return base.GetLocalizedSettingsKey( settingsKey, culture );
      }
    }

    protected pgwapi GetWannafindServiceClient( IDictionary<string, string> settings ) {
      pgwapi paymentGateWayApi = new pgwapi();
      paymentGateWayApi.Credentials = new NetworkCredential( settings[ "apiUser" ], settings[ "apiPassword" ] );
      return paymentGateWayApi;
    }

  }
}
