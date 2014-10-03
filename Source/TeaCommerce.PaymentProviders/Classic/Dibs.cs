using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Hosting;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;

namespace TeaCommerce.PaymentProviders.Classic {

  [PaymentProvider( "DIBS" )]
  public class Dibs : APaymentProvider {

    public override string DocumentationLink { get { return "http://anders.burla.dk/umbraco/tea-commerce/using-dibs-with-tea-commerce/"; } }

    public override bool SupportsRetrievalOfPaymentStatus { get { return true; } }
    public override bool SupportsCapturingOfPayment { get { return true; } }
    public override bool SupportsRefundOfPayment { get { return true; } }
    public override bool SupportsCancellationOfPayment { get { return true; } }

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
        defaultSettings[ "test" ] = "1";
        return defaultSettings;
      }
    }

    public override PaymentHtmlForm GenerateHtmlForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, string teaCommerceCommunicationUrl, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "merchant", "settings" );
      settings.MustContainKey( "md5k1", "settings" );
      settings.MustContainKey( "md5k2", "settings" );

      PaymentHtmlForm htmlForm = new PaymentHtmlForm {
        Action = "https://payment.architrade.com/paymentweb/start.action"
      };

      string[] settingsToExclude = new[] { "md5k1", "md5k2", "apiusername", "apipassword" };
      htmlForm.InputFields = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      htmlForm.InputFields[ "orderid" ] = order.CartNumber;

      string strAmount = ( order.TotalPrice.Value.WithVat * 100M ).ToString( "0", CultureInfo.InvariantCulture );
      htmlForm.InputFields[ "amount" ] = strAmount;

      Currency currency = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId );
      if ( !Iso4217CurrencyCodes.ContainsKey( currency.IsoCode ) ) {
        throw new Exception( "You must specify an ISO 4217 currency code for the " + currency.Name + " currency" );
      }
      string currencyStr = Iso4217CurrencyCodes[ currency.IsoCode ];
      htmlForm.InputFields[ "currency" ] = currencyStr;

      htmlForm.InputFields[ "accepturl" ] = teaCommerceContinueUrl;
      htmlForm.InputFields[ "cancelurl" ] = teaCommerceCancelUrl;
      htmlForm.InputFields[ "callbackurl" ] = teaCommerceCallBackUrl;

      if ( htmlForm.InputFields.ContainsKey( "capturenow" ) && htmlForm.InputFields[ "capturenow" ] != "1" )
        htmlForm.InputFields.Remove( "capturenow" );

      if ( htmlForm.InputFields.ContainsKey( "calcfee" ) && htmlForm.InputFields[ "calcfee" ] != "1" )
        htmlForm.InputFields.Remove( "calcfee" );

      htmlForm.InputFields[ "uniqueoid" ] = string.Empty;

      if ( htmlForm.InputFields.ContainsKey( "test" ) && htmlForm.InputFields[ "test" ] != "1" )
        htmlForm.InputFields.Remove( "test" );

      //DIBS dont support to show order line information to the shopper

      //MD5(key2 + MD5(key1 + “merchant=<merchant>&orderid=<orderid> &currency=<cur>&amount=<amount>)) 
      string md5CheckValue = string.Empty;
      md5CheckValue += settings[ "md5k1" ];
      md5CheckValue += "merchant=" + settings[ "merchant" ];
      md5CheckValue += "&orderid=" + order.CartNumber;
      md5CheckValue += "&currency=" + currencyStr;
      md5CheckValue += "&amount=" + strAmount;

      htmlForm.InputFields[ "md5key" ] = GenerateMD5Hash( settings[ "md5k2" ] + GenerateMD5Hash( md5CheckValue ) );

      return htmlForm;
    }

    public override string GetContinueUrl( Order order, IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "accepturl", "settings" );

      return settings[ "accepturl" ];
    }

    public override string GetCancelUrl( Order order, IDictionary<string, string> settings ) {
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

        //Write data when testing
        if ( settings.ContainsKey( "test" ) && settings[ "test" ] == "1" ) {
          LogRequestToFile( request, HostingEnvironment.MapPath( "~/dibs-callback-data.txt" ), logPostData: true );
        }

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
        if ( GenerateMD5Hash( settings[ "md5k2" ] + GenerateMD5Hash( md5CheckValue ) ) == authkey ) {
          callbackInfo = new CallbackInfo( totalAmount / 100M, transaction, !autoCaptured ? PaymentState.Authorized : PaymentState.Captured, paytype, cardnomask );
        } else {
          LoggingService.Instance.Log( "DIBS(" + order.CartNumber + ") - MD5Sum security check failed" );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "DIBS(" + order.CartNumber + ") - Process callback" );
      }

      return callbackInfo;
    }

    public override ApiInfo GetStatus( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "apiusername", "settings" );
        settings.MustContainKey( "apipassword", "settings" );

        Dictionary<string, string> inputFields = new Dictionary<string, string>();

        try {
          string response = MakePostRequest( "https://@payment.architrade.com/cgi-adm/payinfo.cgi?transact=" + order.TransactionInformation.TransactionId, inputFields, new NetworkCredential( settings[ "apiusername" ], settings[ "apipassword" ] ) );

          Regex regex = new Regex( @"status=(\d+)" );
          string status = regex.Match( response ).Groups[ 1 ].Value;

          PaymentState paymentState = PaymentState.Initialized;

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
          LoggingService.Instance.Log( "DIBS(" + order.OrderNumber + ") - Error making API request - wrong credentials" );
        }

      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "DIBS(" + order.OrderNumber + ") - Get status" );
      }

      return apiInfo;
    }

    public override ApiInfo CapturePayment( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "merchant", "settings" );
        settings.MustContainKey( "md5k1", "settings" );
        settings.MustContainKey( "md5k2", "settings" );

        Dictionary<string, string> inputFields = new Dictionary<string, string>();

        string merchant = settings[ "merchant" ];
        inputFields[ "merchant" ] = merchant;

        string strAmount = ( order.TransactionInformation.AmountAuthorized.Value * 100M ).ToString( "0" );
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

        inputFields[ "md5key" ] = GenerateMD5Hash( settings[ "md5k2" ] + GenerateMD5Hash( md5CheckValue ) );

        try {
          string response = MakePostRequest( "https://payment.architrade.com/cgi-bin/capture.cgi", inputFields );

          Regex reg = new Regex( @"result=(\d*)" );
          string result = reg.Match( response ).Groups[ 1 ].Value;

          if ( result == "0" ) {
            apiInfo = new ApiInfo( order.TransactionInformation.TransactionId, PaymentState.Captured );
          } else {
            LoggingService.Instance.Log( "DIBS(" + order.OrderNumber + ") - Error making API request - error message: " + result );
          }
        } catch ( WebException ) {
          LoggingService.Instance.Log( "DIBS(" + order.OrderNumber + ") - Error making API request - wrong credentials" );
        }

      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "DIBS(" + order.OrderNumber + ") - Capture payment" );
      }

      return apiInfo;
    }

    public override ApiInfo RefundPayment( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "merchant", "settings" );
        settings.MustContainKey( "md5k1", "settings" );
        settings.MustContainKey( "md5k2", "settings" );
        settings.MustContainKey( "apiusername", "settings" );
        settings.MustContainKey( "apipassword", "settings" );

        Dictionary<string, string> inputFields = new Dictionary<string, string>();

        string merchant = settings[ "merchant" ];
        inputFields[ "merchant" ] = merchant;

        string strAmount = ( order.TransactionInformation.AmountAuthorized.Value * 100M ).ToString( "0" );
        inputFields[ "amount" ] = strAmount;

        inputFields[ "orderid" ] = order.CartNumber;
        inputFields[ "transact" ] = order.TransactionInformation.TransactionId;
        inputFields[ "textreply" ] = "yes";

        Currency currency = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId );
        if ( !Iso4217CurrencyCodes.ContainsKey( currency.IsoCode ) ) {
          throw new Exception( "You must specify an ISO 4217 currency code for the " + currency.Name + " currency" );
        }
        inputFields[ "currency" ] = Iso4217CurrencyCodes[ currency.IsoCode ];

        //MD5(key2 + MD5(key1 + “merchant=<merchant>&orderid=<orderid> &transact=<transact>&amount=<amount>"))
        string md5CheckValue = string.Empty;
        md5CheckValue += settings[ "md5k1" ];
        md5CheckValue += "merchant=" + merchant;
        md5CheckValue += "&orderid=" + order.CartNumber;
        md5CheckValue += "&transact=" + order.TransactionInformation.TransactionId;
        md5CheckValue += "&amount=" + strAmount;

        inputFields[ "md5key" ] = GenerateMD5Hash( settings[ "md5k2" ] + GenerateMD5Hash( md5CheckValue ) );

        try {
          string response = MakePostRequest( "https://payment.architrade.com/cgi-adm/refund.cgi", inputFields, new NetworkCredential( settings[ "apiusername" ], settings[ "apipassword" ] ) );

          Regex reg = new Regex( @"result=(\d*)" );
          string result = reg.Match( response ).Groups[ 1 ].Value;

          if ( result == "0" ) {
            apiInfo = new ApiInfo( order.TransactionInformation.TransactionId, PaymentState.Refunded );
          } else {
            LoggingService.Instance.Log( "DIBS(" + order.OrderNumber + ") - Error making API request - error message: " + result );
          }
        } catch ( WebException ) {
          LoggingService.Instance.Log( "DIBS(" + order.OrderNumber + ") - Error making API request - wrong credentials" );
        }

      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "DIBS(" + order.OrderNumber + ") - Refund payment" );
      }

      return apiInfo;
    }

    public override ApiInfo CancelPayment( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "merchant", "settings" );
        settings.MustContainKey( "md5k1", "settings" );
        settings.MustContainKey( "md5k2", "settings" );
        settings.MustContainKey( "apiusername", "settings" );
        settings.MustContainKey( "apipassword", "settings" );

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

        inputFields[ "md5key" ] = GenerateMD5Hash( settings[ "md5k2" ] + GenerateMD5Hash( md5CheckValue ) );

        try {
          string response = MakePostRequest( "https://payment.architrade.com/cgi-adm/cancel.cgi", inputFields, new NetworkCredential( settings[ "apiusername" ], settings[ "apipassword" ] ) );

          Regex reg = new Regex( @"result=(\d*)" );
          string result = reg.Match( response ).Groups[ 1 ].Value;

          if ( result == "0" ) {
            apiInfo = new ApiInfo( order.TransactionInformation.TransactionId, PaymentState.Cancelled );
          } else {
            LoggingService.Instance.Log( "DIBS(" + order.OrderNumber + ") - Error making API request - error message: " + result );
          }
        } catch ( WebException ) {
          LoggingService.Instance.Log( "DIBS(" + order.OrderNumber + ") - Error making API request - wrong credentials" );
        }

      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "DIBS(" + order.OrderNumber + ") - Refund payment" );
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
