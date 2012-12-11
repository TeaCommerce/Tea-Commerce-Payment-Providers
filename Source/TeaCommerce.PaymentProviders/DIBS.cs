using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.PaymentProviders;
using TeaCommerce.Api.Services;
using TeaCommerce.PaymentProviders.Extensions;

namespace TeaCommerce.PaymentProviders {

  [PaymentProvider( "DIBS" )]
  public class DIBS : APaymentProvider {

    protected const string apiErrorFormatString = "Error making API request - Error message: {0}";

    public override IDictionary<string, string> DefaultSettings {
      get {
        Dictionary<string, string> defaultSettings = new Dictionary<string, string>();
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
        return defaultSettings;
      }
    }
    public override string DocumentationLink { get { return "http://anders.burla.dk/umbraco/tea-commerce/using-dibs-with-tea-commerce/"; } }

    public override string FormPostUrl { get { return "https://payment.architrade.com/paymentweb/start.action"; } }

    public override bool SupportsRetrievalOfPaymentStatus { get { return true; } }
    public override bool SupportsCapturingOfPayment { get { return true; } }
    public override bool SupportsRefundOfPayment { get { return true; } }
    public override bool SupportsCancellationOfPayment { get { return true; } }

    public override IDictionary<string, string> GenerateForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "merchant", "settings" );
      settings.MustContainKey( "md5k1", "settings" );
      settings.MustContainKey( "md5k2", "settings" );

      List<string> settingsToExclude = new string[] { "md5k1", "md5k2", "apiusername", "apipassword" }.ToList();
      Dictionary<string, string> inputFields = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      inputFields[ "orderid" ] = order.CartNumber;

      string strAmount = ( order.TotalPrice.WithVat * 100M ).ToString( "0", CultureInfo.InvariantCulture );
      inputFields[ "amount" ] = strAmount;

      Currency currency = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId );
      if ( !ISO4217CurrencyCodes.ContainsKey( currency.IsoCode ) ) {
        throw new Exception( "You must specify an ISO 4217 currency code for the " + currency.Name + " currency" );
      }
      string currencyStr = ISO4217CurrencyCodes[ currency.IsoCode ];
      inputFields[ "currency" ] = currencyStr;

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
      md5CheckValue += "&orderid=" + order.CartNumber;
      md5CheckValue += "&currency=" + currencyStr;
      md5CheckValue += "&amount=" + strAmount;

      inputFields[ "md5key" ] = GetMD5Hash( settings[ "md5k2" ] + GetMD5Hash( md5CheckValue ) );

      return inputFields;
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
        settings.MustContainKey( "md5k1", "settings" );
        settings.MustContainKey( "md5k2", "settings" );

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
        if ( GetMD5Hash( settings[ "md5k2" ] + GetMD5Hash( md5CheckValue ) ).Equals( authkey ) ) {
          callbackInfo = new CallbackInfo( totalAmount / 100M, transaction, !autoCaptured ? PaymentState.Authorized : PaymentState.Captured, paytype, cardnomask );
        } else {
          LoggingService.Instance.Log( "DIBS - MD5Sum security check failed" );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp );
      }

      return callbackInfo;
    }

    public override ApiInfo GetStatus( Order order, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "apiusername", "settings" );
      settings.MustContainKey( "apipassword", "settings" );

      ApiInfo apiInfo = null;
      Dictionary<string, string> inputFields = new Dictionary<string, string>();

      try {
        string response = MakePostRequest( "https://@payment.architrade.com/cgi-adm/payinfo.cgi?transact=" + order.TransactionInformation.TransactionId, inputFields, new NetworkCredential( settings[ "apiusername" ], settings[ "apipassword" ] ) );

        Regex regex = new Regex( @"status=(\d+)" );
        string status = regex.Match( response ).Groups[ 1 ].Value;

        PaymentState paymentState = PaymentState.Initiated;

        switch ( status ) {
          case "2":
            paymentState = PaymentState.Authorized;
            break;
          case "5":
            paymentState = PaymentState.Captured;
            break;
          case "6":
            paymentState = PaymentState.Cancelled;
            break;
          case "11":
            paymentState = PaymentState.Refunded;
            break;
        }

        apiInfo = new ApiInfo( order.TransactionInformation.TransactionId, paymentState );
      } catch ( WebException ) {
        apiInfo = new ApiInfo( "DIBS - Error making API request - Wrong credentials" );
      }

      return apiInfo;
    }

    public override ApiInfo CapturePayment( Order order, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "merchant", "settings" );
      settings.MustContainKey( "md5k1", "settings" );
      settings.MustContainKey( "md5k2", "settings" );

      ApiInfo apiInfo = null;
      Dictionary<string, string> inputFields = new Dictionary<string, string>();

      string merchant = settings[ "merchant" ];
      inputFields[ "merchant" ] = merchant;

      string strAmount = ( order.TotalPrice.WithVat * 100M ).ToString( "0" );
      inputFields[ "amount" ] = strAmount;

      inputFields[ "orderid" ] = order.CartNumber;
      inputFields[ "transact" ] = order.TransactionInformation.TransactionId;
      inputFields[ "textreply" ] = "yes";

      //MD5(key2 + MD5(key1 + “merchant=<merchant>&orderid=<orderid> &transact=<transact>&amount=<amount>"))
      string md5CheckValue = string.Empty;
      md5CheckValue += settings[ "md5k1" ];
      md5CheckValue += "merchant=" + merchant;
      md5CheckValue += "&orderid=" + order.CartNumber;
      md5CheckValue += "&transact=" + order.TransactionInformation.TransactionId;
      md5CheckValue += "&amount=" + strAmount;

      inputFields[ "md5key" ] = GetMD5Hash( settings[ "md5k2" ] + GetMD5Hash( md5CheckValue ) );

      try {
        string response = MakePostRequest( "https://payment.architrade.com/cgi-bin/capture.cgi", inputFields );

        Regex reg = new Regex( @"result=(\d*)" );
        string result = reg.Match( response ).Groups[ 1 ].Value;

        if ( result.Equals( "0" ) ) {
          apiInfo = new ApiInfo( order.TransactionInformation.TransactionId, PaymentState.Captured );
        } else {
          apiInfo = new ApiInfo( "DIBS - " + string.Format( apiErrorFormatString, result ) );
        }
      } catch ( WebException ) {
        apiInfo = new ApiInfo( "DIBS - Error making API request - Wrong credentials" );
      }

      return apiInfo;
    }

    public override ApiInfo RefundPayment( Order order, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "merchant", "settings" );
      settings.MustContainKey( "md5k1", "settings" );
      settings.MustContainKey( "md5k2", "settings" );
      settings.MustContainKey( "apiusername", "settings" );
      settings.MustContainKey( "apipassword", "settings" );

      ApiInfo apiInfo = null;
      Dictionary<string, string> inputFields = new Dictionary<string, string>();

      string merchant = settings[ "merchant" ];
      inputFields[ "merchant" ] = merchant;

      string strAmount = ( order.TotalPrice.WithVat * 100M ).ToString( "0" );
      inputFields[ "amount" ] = strAmount;

      inputFields[ "orderid" ] = order.CartNumber;
      inputFields[ "transact" ] = order.TransactionInformation.TransactionId;
      inputFields[ "textreply" ] = "yes";

      Currency currency = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId );
      if ( !ISO4217CurrencyCodes.ContainsKey( currency.IsoCode ) ) {
        throw new Exception( "You must specify an ISO 4217 currency code for the " + currency.Name + " currency" );
      }
      inputFields[ "currency" ] = ISO4217CurrencyCodes[ currency.IsoCode ];

      //MD5(key2 + MD5(key1 + “merchant=<merchant>&orderid=<orderid> &transact=<transact>&amount=<amount>"))
      string md5CheckValue = string.Empty;
      md5CheckValue += settings[ "md5k1" ];
      md5CheckValue += "merchant=" + merchant;
      md5CheckValue += "&orderid=" + order.CartNumber;
      md5CheckValue += "&transact=" + order.TransactionInformation.TransactionId;
      md5CheckValue += "&amount=" + strAmount;

      inputFields[ "md5key" ] = GetMD5Hash( settings[ "md5k2" ] + GetMD5Hash( md5CheckValue ) );

      try {
        string response = MakePostRequest( "https://payment.architrade.com/cgi-adm/refund.cgi", inputFields, new NetworkCredential( settings[ "apiusername" ], settings[ "apipassword" ] ) );

        Regex reg = new Regex( @"result=(\d*)" );
        string result = reg.Match( response ).Groups[ 1 ].Value;

        if ( result.Equals( "0" ) ) {
          apiInfo = new ApiInfo( order.TransactionInformation.TransactionId, PaymentState.Refunded );
        } else {
          apiInfo = new ApiInfo( "DIBS - " + string.Format( apiErrorFormatString, result ) );
        }
      } catch ( WebException ) {
        apiInfo = new ApiInfo( "DIBS - Error making API request - Wrong credentials" );
      }

      return apiInfo;
    }

    public override ApiInfo CancelPayment( Order order, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "merchant", "settings" );
      settings.MustContainKey( "md5k1", "settings" );
      settings.MustContainKey( "md5k2", "settings" );
      settings.MustContainKey( "apiusername", "settings" );
      settings.MustContainKey( "apipassword", "settings" );

      ApiInfo apiInfo = null;
      Dictionary<string, string> inputFields = new Dictionary<string, string>();

      string merchant = settings[ "merchant" ];
      inputFields[ "merchant" ] = merchant;

      inputFields[ "orderid" ] = order.CartNumber;
      inputFields[ "transact" ] = order.TransactionInformation.TransactionId;
      inputFields[ "textreply" ] = "yes";

      //MD5(key2 + MD5(key1 + “merchant=<merchant>&orderid=<orderid>&transact=<transact>)) 
      string md5CheckValue = string.Empty;
      md5CheckValue += settings[ "md5k1" ];
      md5CheckValue += "merchant=" + merchant;
      md5CheckValue += "&orderid=" + order.CartNumber;
      md5CheckValue += "&transact=" + order.TransactionInformation.TransactionId;

      inputFields[ "md5key" ] = GetMD5Hash( settings[ "md5k2" ] + GetMD5Hash( md5CheckValue ) );

      try {
        string response = MakePostRequest( "https://payment.architrade.com/cgi-adm/cancel.cgi", inputFields, new NetworkCredential( settings[ "apiusername" ], settings[ "apipassword" ] ) );

        Regex reg = new Regex( @"result=(\d*)" );
        string result = reg.Match( response ).Groups[ 1 ].Value;

        if ( result.Equals( "0" ) ) {
          apiInfo = new ApiInfo( order.TransactionInformation.TransactionId, PaymentState.Cancelled );
        } else {
          apiInfo = new ApiInfo( "DIBS - " + string.Format( apiErrorFormatString, result ) );
        }
      } catch ( WebException ) {
        apiInfo = new ApiInfo( "DIBS - Error making API request - Wrong credentials" );
      }

      return apiInfo;
    }

    public override string GetLocalizedSettingsKey( string settingsKey, CultureInfo culture ) {
      switch ( settingsKey ) {
        case "accepturl":
          return settingsKey + "<br/><small>e.g. /continue/</small>";
        case "cancelurl":
          return settingsKey + "<br/><small>e.g. /cancel/</small>";
        case "capturenow":
          return settingsKey + "<br/><small>1 = true; 0 = false</small>";
        case "calcfee":
          return settingsKey + "<br/><small>1 = true; 0 = false</small>";
        case "paytype":
          return settingsKey + "<br/><small>e.g. VISA,MC</small>";
        case "test":
          return settingsKey + "<br/><small>1 = true; 0 = false</small>";
        default:
          return base.GetLocalizedSettingsKey( settingsKey, culture );
      }
    }

  }
}
