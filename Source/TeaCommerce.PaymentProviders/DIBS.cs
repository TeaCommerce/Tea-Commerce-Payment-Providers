using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using TeaCommerce.Data;
using TeaCommerce.Data.Payment;
using umbraco.BusinessLogic;

namespace TeaCommerce.PaymentProviders {

  public class DIBS : APaymentProvider {

    public override Dictionary<string, string> DefaultSettings {
      get {
        if ( defaultSettings == null ) {
          defaultSettings = new Dictionary<string, string>();
          defaultSettings[ "merchant" ] = string.Empty;
          defaultSettings[ "lang" ] = "en";
          defaultSettings[ "accepturl" ] = string.Empty;
          defaultSettings[ "cancelurl" ] = string.Empty;
          defaultSettings[ "capturenow" ] = "0";
          defaultSettings[ "calcfee" ] = "0";
          defaultSettings[ "paytype" ] = string.Empty;
          defaultSettings[ "md5k1" ] = string.Empty;
          defaultSettings[ "md5k2" ] = string.Empty;
          defaultSettings[ "apiusername" ] = string.Empty;
          defaultSettings[ "apipassword" ] = string.Empty;
          defaultSettings[ "test" ] = "0";
        }
        return defaultSettings;
      }
    }

    public override string FormPostUrl { get { return "https://payment.architrade.com/paymentweb/start.action"; } }
    public override string DocumentationLink { get { return "http://anders.burla.dk/umbraco/tea-commerce/using-dibs-with-tea-commerce/"; } }

    public override Dictionary<string, string> GenerateForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, Dictionary<string, string> settings ) {
      List<string> settingsToExclude = new string[] { "md5k1", "md5k2", "apiusername", "apipassword" }.ToList();
      Dictionary<string, string> inputFields = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      inputFields[ "orderid" ] = order.Name;

      string strAmount = ( order.TotalPrice * 100M ).ToString( "0", CultureInfo.InvariantCulture );
      inputFields[ "amount" ] = strAmount;

      string currency = ISO4217CurrencyCodes[ order.CurrencyISOCode ];
      inputFields[ "currency" ] = currency;

      inputFields[ "accepturl" ] = teaCommerceContinueUrl;
      inputFields[ "cancelurl" ] = teaCommerceCancelUrl;
      inputFields[ "callbackurl" ] = teaCommerceCallBackUrl;

      if ( inputFields.ContainsKey( "capturenow" ) && !inputFields[ "capturenow" ].Equals( "1" ) )
        inputFields.Remove( "capturenow" );

      if ( inputFields.ContainsKey( "calcfee" ) && !inputFields[ "calcfee" ].Equals( "1" ) )
        inputFields.Remove( "calcfee" );

      inputFields[ "uniqueoid" ] = string.Empty;

      if ( inputFields.ContainsKey( "test" ) && !inputFields[ "test" ].Equals( "1" ) )
        inputFields.Remove( "test" );

      //DIBS dont support to show order line information to the shopper

      //MD5(key2 + MD5(key1 + “merchant=<merchant>&orderid=<orderid> &currency=<cur>&amount=<amount>)) 
      string md5CheckValue = string.Empty;
      md5CheckValue += settings[ "md5k1" ];
      md5CheckValue += "merchant=" + settings[ "merchant" ];
      md5CheckValue += "&orderid=" + order.Name;
      md5CheckValue += "&currency=" + currency;
      md5CheckValue += "&amount=" + strAmount;

      inputFields[ "md5key" ] = GetMD5Hash( settings[ "md5k2" ] + GetMD5Hash( md5CheckValue ) );

      return inputFields;
    }

    public override string GetContinueUrl( Dictionary<string, string> settings ) {
      return settings[ "accepturl" ];
    }

    public override string GetCancelUrl( Dictionary<string, string> settings ) {
      return settings[ "cancelurl" ];
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, Dictionary<string, string> settings ) {
      //using ( StreamWriter writer = new StreamWriter( File.Create( HttpContext.Current.Server.MapPath( "~/DIBSTestCallback.txt" ) ) ) ) {
      //  writer.WriteLine( "Form:" );
      //  foreach ( string k in request.Form.Keys ) {
      //    writer.WriteLine( k + " : " + request.Form[ k ] );
      //  }
      //  writer.Flush();
      //}

      string errorMessage = string.Empty;

      string transaction = request.Form[ "transact" ];
      string currencyCode = request.Form[ "currency" ];
      string strAmount = request.Form[ "amount" ];
      string orderName = request.Form[ "orderid" ];
      string authkey = request.Form[ "authkey" ];
      string capturenow = request.Form[ "capturenow" ];
      string fee = request.Form[ "fee" ] ?? "0"; //Is not always in the return data
      string paytype = request.Form[ "paytype" ];
      string cardnomask = request.Form[ "cardnomask" ];

      decimal totalAmount = ( decimal.Parse( strAmount, CultureInfo.InvariantCulture ) + decimal.Parse( fee, CultureInfo.InvariantCulture ) );
      bool autoCaptured = capturenow == "1";

      string md5CheckValue = string.Empty;
      md5CheckValue += settings[ "md5k1" ];
      md5CheckValue += "transact=" + transaction;
      md5CheckValue += "&amount=" + totalAmount.ToString( "0", CultureInfo.InvariantCulture );
      md5CheckValue += "&currency=" + currencyCode;

      //authkey = MD5(k2 + MD5(k1 + "transact=tt&amount=aa&currency=cc"))
      if ( GetMD5Hash( settings[ "md5k2" ] + GetMD5Hash( md5CheckValue ) ).Equals( authkey ) )
        return new CallbackInfo( orderName, totalAmount / 100M, transaction, !autoCaptured ? PaymentStatus.Authorized : PaymentStatus.Captured, paytype, cardnomask );
      else
        errorMessage = "Tea Commerce - DIBS - MD5Sum security check failed";

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new CallbackInfo( errorMessage );
    }

    public override APIInfo GetStatus( Order order, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;
      Dictionary<string, string> inputFields = new Dictionary<string, string>();

      try {
        string response = MakePostRequest( "https://@payment.architrade.com/cgi-adm/payinfo.cgi?transact=" + order.TransactionPaymentTransactionId, inputFields, new NetworkCredential( settings[ "apiusername" ], settings[ "apipassword" ] ) );

        Regex regex = new Regex( @"status=(\d+)" );
        string status = regex.Match( response ).Groups[ 1 ].Value;

        PaymentStatus paymentStatus = PaymentStatus.Initial;

        switch ( status ) {
          case "2":
            paymentStatus = PaymentStatus.Authorized;
            break;
          case "5":
            paymentStatus = PaymentStatus.Captured;
            break;
          case "6":
            paymentStatus = PaymentStatus.Cancelled;
            break;
          case "11":
            paymentStatus = PaymentStatus.Refunded;
            break;
        }

        return new APIInfo( order.TransactionPaymentTransactionId, paymentStatus );
      } catch ( WebException ) {
        errorMessage = "Tea Commerce - DIBS - " + umbraco.ui.Text( "teaCommerce", "paymentProvider_DIBS_wrongCredentials" );
      }

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    public override APIInfo CapturePayment( Order order, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;
      Dictionary<string, string> inputFields = new Dictionary<string, string>();

      string merchant = settings[ "merchant" ];
      inputFields[ "merchant" ] = merchant;

      string strAmount = ( order.TotalPrice * 100M ).ToString( "0" );
      inputFields[ "amount" ] = strAmount;

      inputFields[ "orderid" ] = order.Name;
      inputFields[ "transact" ] = order.TransactionPaymentTransactionId;
      inputFields[ "textreply" ] = "yes";

      //MD5(key2 + MD5(key1 + “merchant=<merchant>&orderid=<orderid> &transact=<transact>&amount=<amount>"))
      string md5CheckValue = string.Empty;
      md5CheckValue += settings[ "md5k1" ];
      md5CheckValue += "merchant=" + merchant;
      md5CheckValue += "&orderid=" + order.Name;
      md5CheckValue += "&transact=" + order.TransactionPaymentTransactionId;
      md5CheckValue += "&amount=" + strAmount;

      inputFields[ "md5key" ] = GetMD5Hash( settings[ "md5k2" ] + GetMD5Hash( md5CheckValue ) );

      try {
        string response = MakePostRequest( "https://payment.architrade.com/cgi-bin/capture.cgi", inputFields );

        Regex reg = new Regex( @"result=(\d*)" );
        string result = reg.Match( response ).Groups[ 1 ].Value;

        if ( result.Equals( "0" ) )
          return new APIInfo( order.TransactionPaymentTransactionId, PaymentStatus.Captured );
        else
          errorMessage = "Tea Commerce - DIBS - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_DIBS_error" ), result );
      } catch ( WebException ) {
        errorMessage = "Tea Commerce - DIBS - " + umbraco.ui.Text( "teaCommerce", "paymentProvider_DIBS_wrongCredentials" );
      }

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    public override APIInfo RefundPayment( Order order, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;
      Dictionary<string, string> inputFields = new Dictionary<string, string>();

      string merchant = settings[ "merchant" ];
      inputFields[ "merchant" ] = merchant;

      string strAmount = ( order.TotalPrice * 100M ).ToString( "0" );
      inputFields[ "amount" ] = strAmount;

      inputFields[ "orderid" ] = order.Name;
      inputFields[ "transact" ] = order.TransactionPaymentTransactionId;
      inputFields[ "textreply" ] = "yes";

      inputFields[ "currency" ] = ISO4217CurrencyCodes[ order.CurrencyISOCode ];

      //MD5(key2 + MD5(key1 + “merchant=<merchant>&orderid=<orderid> &transact=<transact>&amount=<amount>"))
      string md5CheckValue = string.Empty;
      md5CheckValue += settings[ "md5k1" ];
      md5CheckValue += "merchant=" + merchant;
      md5CheckValue += "&orderid=" + order.Name;
      md5CheckValue += "&transact=" + order.TransactionPaymentTransactionId;
      md5CheckValue += "&amount=" + strAmount;

      inputFields[ "md5key" ] = GetMD5Hash( settings[ "md5k2" ] + GetMD5Hash( md5CheckValue ) );

      try {
        string response = MakePostRequest( "https://payment.architrade.com/cgi-adm/refund.cgi", inputFields, new NetworkCredential( settings[ "apiusername" ], settings[ "apipassword" ] ) );

        Regex reg = new Regex( @"result=(\d*)" );
        string result = reg.Match( response ).Groups[ 1 ].Value;

        if ( result.Equals( "0" ) )
          return new APIInfo( order.TransactionPaymentTransactionId, PaymentStatus.Refunded );
        else
          errorMessage = "Tea Commerce - DIBS - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_DIBS_error" ), result );
      } catch ( WebException ) {
        errorMessage = "Tea Commerce - DIBS - " + umbraco.ui.Text( "teaCommerce", "paymentProvider_DIBS_wrongCredentials" );
      }

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    public override APIInfo CancelPayment( Order order, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;
      Dictionary<string, string> inputFields = new Dictionary<string, string>();

      string merchant = settings[ "merchant" ];
      inputFields[ "merchant" ] = merchant;

      inputFields[ "orderid" ] = order.Name;
      inputFields[ "transact" ] = order.TransactionPaymentTransactionId;
      inputFields[ "textreply" ] = "yes";

      //MD5(key2 + MD5(key1 + “merchant=<merchant>&orderid=<orderid>&transact=<transact>)) 
      string md5CheckValue = string.Empty;
      md5CheckValue += settings[ "md5k1" ];
      md5CheckValue += "merchant=" + merchant;
      md5CheckValue += "&orderid=" + order.Name;
      md5CheckValue += "&transact=" + order.TransactionPaymentTransactionId;

      inputFields[ "md5key" ] = GetMD5Hash( settings[ "md5k2" ] + GetMD5Hash( md5CheckValue ) );

      try {
        string response = MakePostRequest( "https://payment.architrade.com/cgi-adm/cancel.cgi", inputFields, new NetworkCredential( settings[ "apiusername" ], settings[ "apipassword" ] ) );

        Regex reg = new Regex( @"result=(\d*)" );
        string result = reg.Match( response ).Groups[ 1 ].Value;

        if ( result.Equals( "0" ) )
          return new APIInfo( order.TransactionPaymentTransactionId, PaymentStatus.Cancelled );
        else
          errorMessage = "Tea Commerce - DIBS - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_DIBS_error" ), result );
      } catch ( WebException ) {
        errorMessage = "Tea Commerce - DIBS - " + umbraco.ui.Text( "teaCommerce", "paymentProvider_DIBS_wrongCredentials" );
      }

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

  }
}
