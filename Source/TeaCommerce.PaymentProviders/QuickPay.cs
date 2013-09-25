using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web;
using System.Web.Hosting;
using System.Xml.Linq;
using System.Xml.XPath;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;
using TeaCommerce.PaymentProviders.Extensions;

namespace TeaCommerce.PaymentProviders {
  [PaymentProvider( "QuickPay" )]
  public class QuickPay : APaymentProvider {

    public override string DocumentationLink { get { return "http://anders.burla.dk/umbraco/tea-commerce/using-quickpay-wit-tea-commerce/"; } }

    public override bool SupportsRetrievalOfPaymentStatus { get { return true; } }
    public override bool SupportsCapturingOfPayment { get { return true; } }
    public override bool SupportsRefundOfPayment { get { return true; } }
    public override bool SupportsCancellationOfPayment { get { return true; } }

    public override IDictionary<string, string> DefaultSettings {
      get {
        Dictionary<string, string> defaultSettings = new Dictionary<string, string>();
        defaultSettings[ "merchant" ] = string.Empty;
        defaultSettings[ "language" ] = "en";
        defaultSettings[ "continueurl" ] = string.Empty;
        defaultSettings[ "cancelurl" ] = string.Empty;
        defaultSettings[ "autocapture" ] = "0";
        defaultSettings[ "cardtypelock" ] = string.Empty;
        defaultSettings[ "md5secret" ] = string.Empty;
        defaultSettings[ "testmode" ] = "1";
        return defaultSettings;
      }
    }

    public override PaymentHtmlForm GenerateHtmlForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "merchant", "settings" );
      settings.MustContainKey( "language", "settings" );
      settings.MustContainKey( "md5secret", "settings" );

      PaymentHtmlForm htmlForm = new PaymentHtmlForm {
        Action = "https://secure.quickpay.dk/form/"
      };

      string[] settingsToExclude = new[] { "md5secret" };
      htmlForm.InputFields = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      htmlForm.InputFields[ "protocol" ] = "7";
      htmlForm.InputFields[ "msgtype" ] = "authorize";

      //Order name must be between 4 or 20 chars  
      string orderName = order.CartNumber;
      while ( orderName.Length < 4 )
        orderName = "0" + orderName;
      htmlForm.InputFields[ "ordernumber" ] = orderName.Truncate( 20 );
      htmlForm.InputFields[ "amount" ] = ( order.TotalPrice.WithVat * 100M ).ToString( "0", CultureInfo.InvariantCulture );

      //Check that the Iso code exists
      Currency currency = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId );
      if ( !Iso4217CurrencyCodes.ContainsKey( currency.IsoCode ) ) {
        throw new Exception( "You must specify an ISO 4217 currency code for the " + currency.Name + " currency" );
      }
      htmlForm.InputFields[ "currency" ] = currency.IsoCode;

      htmlForm.InputFields[ "continueurl" ] = teaCommerceContinueUrl;
      htmlForm.InputFields[ "cancelurl" ] = teaCommerceCancelUrl;
      htmlForm.InputFields[ "callbackurl" ] = teaCommerceCallBackUrl;

      //Quickpay dont support to show order line information to the shopper

      //Md5 check sum
      string[] md5CheckSumKeys = { "protocol", "msgtype", "merchant", "language", "ordernumber", "amount", "currency", "continueurl", "cancelurl", "callbackurl", "autocapture", "autofee", "cardtypelock", "description", "group", "testmode", "splitpayment", "forcemobile", "deadline", "cardhash" };
      string md5CheckValue = "";
      foreach ( string key in md5CheckSumKeys ) {
        if ( htmlForm.InputFields.ContainsKey( key ) ) {
          md5CheckValue += htmlForm.InputFields[ key ];
        }
      }
      md5CheckValue += settings[ "md5secret" ];

      htmlForm.InputFields[ "md5check" ] = GenerateMD5Hash( md5CheckValue );

      return htmlForm;
    }

    public override string GetContinueUrl( Order order, IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "continueurl", "settings" );

      return settings[ "continueurl" ];
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
        settings.MustContainKey( "md5secret", "settings" );

        //Write data when testing
        if ( settings.ContainsKey( "testmode" ) && settings[ "testmode" ] == "1" ) {
          using ( StreamWriter writer = new StreamWriter( File.Create( HostingEnvironment.MapPath( "~/quick-pay-callback-data.txt" ) ) ) ) {
            writer.WriteLine( "Form:" );
            foreach ( string k in request.Form.Keys ) {
              writer.WriteLine( k + " : " + request.Form[ k ] );
            }
            writer.Flush();
          }
        }

        string md5CheckValue = string.Empty;
        md5CheckValue += request.Form[ "msgtype" ];
        md5CheckValue += request.Form[ "ordernumber" ];
        md5CheckValue += request.Form[ "amount" ];
        md5CheckValue += request.Form[ "currency" ];
        md5CheckValue += request.Form[ "time" ];
        md5CheckValue += request.Form[ "state" ];
        md5CheckValue += request.Form[ "qpstat" ];
        md5CheckValue += request.Form[ "qpstatmsg" ];
        md5CheckValue += request.Form[ "chstat" ];
        md5CheckValue += request.Form[ "chstatmsg" ];
        md5CheckValue += request.Form[ "merchant" ];
        md5CheckValue += request.Form[ "merchantemail" ];
        md5CheckValue += request.Form[ "transaction" ];
        md5CheckValue += request.Form[ "cardtype" ];
        md5CheckValue += request.Form[ "cardnumber" ];
        md5CheckValue += request.Form[ "cardhash" ];
        md5CheckValue += request.Form[ "cardexpire" ];
        md5CheckValue += request.Form[ "acquirer" ];
        md5CheckValue += request.Form[ "splitpayment" ];
        md5CheckValue += request.Form[ "fraudprobability" ];
        md5CheckValue += request.Form[ "fraudremarks" ];
        md5CheckValue += request.Form[ "fraudreport" ];
        md5CheckValue += request.Form[ "fee" ];
        md5CheckValue += settings[ "md5secret" ];

        if ( GenerateMD5Hash( md5CheckValue ) == request.Form[ "md5check" ] ) {
          string qpstat = request.Form[ "qpstat" ];

          if ( qpstat == "000" ) {
            decimal amount = decimal.Parse( request.Form[ "amount" ], CultureInfo.InvariantCulture ) / 100M;
            string state = request.Form[ "state" ];
            string transaction = request.Form[ "transaction" ];

            callbackInfo = new CallbackInfo( amount, transaction, state == "1" ? PaymentState.Authorized : PaymentState.Captured, request.Form[ "cardtype" ], request.Form[ "cardnumber" ] );
          } else {
            LoggingService.Instance.Log( "Quickpay(" + order.CartNumber + ") - Error making API request - error code: " + qpstat + " | error message: " + request.Form[ "qpstatmsg" ] );
          }

        } else {
          LoggingService.Instance.Log( "QuickPay(" + order.CartNumber + ") - MD5Sum security check failed" );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "QuickPay(" + order.CartNumber + ") - Process callback" );
      }

      return callbackInfo;
    }

    public override ApiInfo GetStatus( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "merchant", "settings" );
        settings.MustContainKey( "md5secret", "settings" );

        Dictionary<string, string> inputFields = new Dictionary<string, string>();

        inputFields[ "protocol" ] = "7";
        inputFields[ "msgtype" ] = "status";
        inputFields[ "merchant" ] = settings[ "merchant" ];
        inputFields[ "transaction" ] = order.TransactionInformation.TransactionId;

        string md5Secret = settings[ "md5secret" ];
        inputFields[ "md5check" ] = GenerateMD5Hash( string.Join( "", inputFields.Values ) + md5Secret );

        apiInfo = MakeApiPostRequest( order.OrderNumber, inputFields, md5Secret );
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "QuickPay(" + order.OrderNumber + ") - Get status" );
      }

      return apiInfo;
    }

    public override ApiInfo CapturePayment( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "merchant", "settings" );
        settings.MustContainKey( "md5secret", "settings" );

        Dictionary<string, string> inputFields = new Dictionary<string, string>();

        inputFields[ "protocol" ] = "7";
        inputFields[ "msgtype" ] = "capture";
        inputFields[ "merchant" ] = settings[ "merchant" ];
        inputFields[ "amount" ] = ( order.TransactionInformation.AmountAuthorized.Value * 100M ).ToString( "0" );
        inputFields[ "finalize" ] = "1";
        inputFields[ "transaction" ] = order.TransactionInformation.TransactionId;

        string md5Secret = settings[ "md5secret" ];
        inputFields[ "md5check" ] = GenerateMD5Hash( string.Join( "", inputFields.Values ) + md5Secret );

        apiInfo = MakeApiPostRequest( order.OrderNumber, inputFields, md5Secret );
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "QuickPay(" + order.OrderNumber + ") - Capture payment" );
      }

      return apiInfo;
    }

    public override ApiInfo RefundPayment( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "merchant", "settings" );
        settings.MustContainKey( "md5secret", "settings" );

        Dictionary<string, string> inputFields = new Dictionary<string, string>();

        inputFields[ "protocol" ] = "7";
        inputFields[ "msgtype" ] = "refund";
        inputFields[ "merchant" ] = settings[ "merchant" ];
        inputFields[ "amount" ] = ( order.TransactionInformation.AmountAuthorized.Value * 100M ).ToString( "0" );
        inputFields[ "transaction" ] = order.TransactionInformation.TransactionId;

        string md5Secret = settings[ "md5secret" ];
        inputFields[ "md5check" ] = GenerateMD5Hash( string.Join( "", inputFields.Values ) + md5Secret );

        apiInfo = MakeApiPostRequest( order.OrderNumber, inputFields, md5Secret );
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "QuickPay(" + order.OrderNumber + ") - Refund payment" );
      }

      return apiInfo;
    }

    public override ApiInfo CancelPayment( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "merchant", "settings" );
        settings.MustContainKey( "md5secret", "settings" );

        Dictionary<string, string> inputFields = new Dictionary<string, string>();

        inputFields[ "protocol" ] = "7";
        inputFields[ "msgtype" ] = "cancel";
        inputFields[ "merchant" ] = settings[ "merchant" ];
        inputFields[ "transaction" ] = order.TransactionInformation.TransactionId;

        string md5Secret = settings[ "md5secret" ];
        inputFields[ "md5check" ] = GenerateMD5Hash( string.Join( "", inputFields.Values ) + md5Secret );

        apiInfo = MakeApiPostRequest( order.OrderNumber, inputFields, md5Secret );
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "QuickPay(" + order.OrderNumber + ") - Cancel payment" );
      }

      return apiInfo;
    }

    public override string GetLocalizedSettingsKey( string settingsKey, CultureInfo culture ) {
      switch ( settingsKey ) {
        case "continueurl":
          return settingsKey + "<br/><small>e.g. /continue/</small>";
        case "cancelurl":
          return settingsKey + "<br/><small>e.g. /cancel/</small>";
        case "autocapture":
          return settingsKey + "<br/><small>1 = true; 0 = false</small>";
        case "cardtypelock":
          return settingsKey + "<br/><small>e.g. visa,mastercard</small>";
        case "testmode":
          return settingsKey + "<br/><small>1 = true; 0 = false</small>";
        default:
          return base.GetLocalizedSettingsKey( settingsKey, culture );
      }
    }

    #region Helper methods

    protected ApiInfo MakeApiPostRequest( string orderNumber, IDictionary<string, string> inputFields, string md5Secret ) {
      ApiInfo apiInfo = null;

      try {
        inputFields.MustNotBeNull( "inputFields" );

        XDocument doc = XDocument.Parse( MakePostRequest( "https://secure.quickpay.dk/api/", inputFields ), LoadOptions.PreserveWhitespace );

        string state = doc.XPathSelectElement( "//state" ).Value;
        string qpstat = doc.XPathSelectElement( "//qpstat" ).Value;
        string qpstatmsg = doc.XPathSelectElement( "//qpstatmsg" ).Value;
        string transaction = doc.XPathSelectElement( "//transaction" ).Value;

        if ( qpstat == "000" ) {

          string[] md5CheckSumKeys = { "msgtype", "ordernumber", "amount", "balance", "currency", "time", "state", "qpstat", "qpstatmsg", "chstat", "chstatmsg", "merchant", "merchantemail", "transaction", "cardtype", "cardnumber", "cardhash", "cardexpire", "splitpayment", "acquirer", "fraudprobability", "fraudremarks", "fraudreport" };
          string md5CheckValue = string.Empty;
          foreach ( string key in md5CheckSumKeys ) {
            XElement xElement = doc.XPathSelectElement( "//" + key );
            if ( xElement != null ) {
              md5CheckValue += xElement.Value;
            }
          }
          md5CheckValue += md5Secret;

          if ( GenerateMD5Hash( md5CheckValue ) == doc.XPathSelectElement( "//md5check" ).Value ) {

            PaymentState paymentState = PaymentState.Initialized;
            if ( state == "1" )
              paymentState = PaymentState.Authorized;
            else if ( state == "3" )
              paymentState = PaymentState.Captured;
            else if ( state == "5" )
              paymentState = PaymentState.Cancelled;
            else if ( state == "7" )
              paymentState = PaymentState.Refunded;

            apiInfo = new ApiInfo( transaction, paymentState );
          } else {
            LoggingService.Instance.Log( "Quickpay(" + orderNumber + ") - MD5Sum security check failed" );
          }
        } else {
          LoggingService.Instance.Log( "Quickpay(" + orderNumber + ") - Error making API request - error code: " + qpstat + " | error message: " + qpstatmsg );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "QuickPay(" + orderNumber + ") - Make API post request" );
      }

      return apiInfo;
    }

    #endregion

  }
}