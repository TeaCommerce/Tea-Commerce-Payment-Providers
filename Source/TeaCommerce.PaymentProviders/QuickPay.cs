using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web;
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
        defaultSettings[ "testmode" ] = "0";
        return defaultSettings;
      }
    }

    public override PaymentHtmlForm GenerateHtmlForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "merchant", "settings" );
      settings.MustContainKey( "language", "settings" );
      settings.MustContainKey( "md5secret", "settings" );

      PaymentHtmlForm htmlForm = new PaymentHtmlForm();
      htmlForm.Action = "https://secure.quickpay.dk/form/";
      string[] settingsToExclude = new[] { "md5secret" };
      htmlForm.InputFields = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      Dictionary<string, string> inputFields = new Dictionary<string, string>();

      inputFields[ "protocol" ] = "4";
      inputFields[ "msgtype" ] = "authorize";

      //Order name must be between 4 or 20 chars  
      string orderName = order.CartNumber;
      while ( orderName.Length < 4 )
        orderName = "0" + orderName;
      inputFields[ "ordernumber" ] = orderName.Truncate( 20 );
      inputFields[ "amount" ] = ( order.TotalPrice.WithVat * 100M ).ToString( "0", CultureInfo.InvariantCulture );

      //Check that the Iso code exists
      Currency currency = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId );
      if ( !Iso4217CurrencyCodes.ContainsKey( currency.IsoCode ) ) {
        throw new Exception( "You must specify an ISO 4217 currency code for the " + currency.Name + " currency" );
      }
      inputFields[ "currency" ] = currency.IsoCode;

      inputFields[ "continueurl" ] = teaCommerceContinueUrl;
      inputFields[ "cancelurl" ] = teaCommerceCancelUrl;
      inputFields[ "callbackurl" ] = teaCommerceCallBackUrl;

      //Quickpay dont support to show order line information to the shopper

      inputFields[ "md5check" ] = GetMd5Hash( string.Join( "", inputFields.Values ) + settings[ "md5secret" ] );

      return htmlForm;
    }

    public override string GetContinueUrl( IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "continueurl", "settings" );

      return settings[ "continueurl" ];
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
        settings.MustContainKey( "md5secret", "settings" );

        //using ( StreamWriter writer = new StreamWriter( File.Create( HostingEnvironment.MapPath( "~/quick-pay-callback-data.txt" ) ) ) ) {
        //  writer.WriteLine( "FORM:" );
        //  foreach ( string k in request.Form.Keys ) {
        //    writer.WriteLine( k + " : " + request.Form[ k ] );
        //  }
        //  writer.Flush();
        //}

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
        md5CheckValue += request.Form[ "splitpayment" ];
        md5CheckValue += request.Form[ "fraudprobability" ];
        md5CheckValue += request.Form[ "fraudremarks" ];
        md5CheckValue += request.Form[ "fraudreport" ];
        md5CheckValue += request.Form[ "fee" ];
        md5CheckValue += settings[ "md5secret" ];

        if ( GetMd5Hash( md5CheckValue ).Equals( request.Form[ "md5check" ] ) ) {
          string qpstat = request.Form[ "qpstat" ];

          if ( qpstat.Equals( "000" ) ) {
            decimal amount = decimal.Parse( request.Form[ "amount" ], CultureInfo.InvariantCulture ) / 100M;
            string state = request.Form[ "state" ];
            string transaction = request.Form[ "transaction" ];

            callbackInfo = new CallbackInfo( amount, transaction, state.Equals( "1" ) ? PaymentState.Authorized : PaymentState.Captured, request.Form[ "cardtype" ], request.Form[ "cardnumber" ] );
          } else {
            string qpstatmsg = request.Form[ "qpstatmsg" ];
            LoggingService.Instance.Log( "Quickpay - Error making API request - error code: " + qpstat + " | error message: " + qpstatmsg );
          }

        } else {
          LoggingService.Instance.Log( "QuickPay - MD5Sum security check failed" );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "QuickPay - Process callback" );
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

        inputFields[ "protocol" ] = "4";
        inputFields[ "msgtype" ] = "status";
        inputFields[ "merchant" ] = settings[ "merchant" ];
        inputFields[ "transaction" ] = order.TransactionInformation.TransactionId;

        string md5Secret = settings[ "md5secret" ];
        inputFields[ "md5check" ] = GetMd5Hash( string.Join( "", inputFields.Values ) + md5Secret );

        apiInfo = MakeApiPostRequest( inputFields, md5Secret );
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "QuickPay - Get status" );
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

        inputFields[ "protocol" ] = "4";
        inputFields[ "msgtype" ] = "capture";
        inputFields[ "merchant" ] = settings[ "merchant" ];
        inputFields[ "amount" ] = ( order.TotalPrice.WithVat * 100M ).ToString( "0" );
        inputFields[ "finalize" ] = "1";
        inputFields[ "transaction" ] = order.TransactionInformation.TransactionId;

        string md5Secret = settings[ "md5secret" ];
        inputFields[ "md5check" ] = GetMd5Hash( string.Join( "", inputFields.Values ) + md5Secret );

        apiInfo = MakeApiPostRequest( inputFields, md5Secret );
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "QuickPay - Capture payment" );
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

        inputFields[ "protocol" ] = "4";
        inputFields[ "msgtype" ] = "refund";
        inputFields[ "merchant" ] = settings[ "merchant" ];
        inputFields[ "amount" ] = ( order.TotalPrice.WithVat * 100M ).ToString( "0" );
        inputFields[ "transaction" ] = order.TransactionInformation.TransactionId;

        string md5Secret = settings[ "md5secret" ];
        inputFields[ "md5check" ] = GetMd5Hash( string.Join( "", inputFields.Values ) + md5Secret );

        apiInfo = MakeApiPostRequest( inputFields, md5Secret );
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "QuickPay - Refund payment" );
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

        inputFields[ "protocol" ] = "4";
        inputFields[ "msgtype" ] = "cancel";
        inputFields[ "merchant" ] = settings[ "merchant" ];
        inputFields[ "transaction" ] = order.TransactionInformation.TransactionId;

        string md5Secret = settings[ "md5secret" ];
        inputFields[ "md5check" ] = GetMd5Hash( string.Join( "", inputFields.Values ) + md5Secret );

        apiInfo = MakeApiPostRequest( inputFields, md5Secret );
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "QuickPay - Cancel payment" );
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

    protected ApiInfo MakeApiPostRequest( IDictionary<string, string> inputFields, string md5Secret ) {
      ApiInfo apiInfo = null;

      try {
        inputFields.MustNotBeNull( "inputFields" );

        XDocument doc = XDocument.Parse( MakePostRequest( "https://secure.quickpay.dk/api/", inputFields ), LoadOptions.PreserveWhitespace );

        string state = doc.XPathSelectElement( "//state" ).Value;
        string qpstat = doc.XPathSelectElement( "//qpstat" ).Value;
        string qpstatmsg = doc.XPathSelectElement( "//qpstatmsg" ).Value;
        string transaction = doc.XPathSelectElement( "//transaction" ).Value;

        if ( qpstat.Equals( "000" ) ) {
          if ( CheckMd5Sum( doc, md5Secret ) ) {

            PaymentState paymentState = PaymentState.Initialized;
            if ( state.Equals( "1" ) )
              paymentState = PaymentState.Authorized;
            else if ( state.Equals( "3" ) )
              paymentState = PaymentState.Captured;
            else if ( state.Equals( "5" ) )
              paymentState = PaymentState.Cancelled;
            else if ( state.Equals( "7" ) )
              paymentState = PaymentState.Refunded;

            apiInfo = new ApiInfo( transaction, paymentState );
          } else {
            LoggingService.Instance.Log( "Quickpay - MD5Sum security check failed" );
          }
        } else {
          LoggingService.Instance.Log( "Quickpay - Error making API request - error code: " + qpstat + " | error message: " + qpstatmsg );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "QuickPay - MakeApiPostRequest" );
      }

      return apiInfo;
    }

    private bool CheckMd5Sum( XDocument doc, string md5Secret ) {
      doc.MustNotBeNull( "doc" );

      string md5CheckValue = string.Empty;
      md5CheckValue += doc.XPathSelectElement( "//msgtype" ).Value;
      md5CheckValue += doc.XPathSelectElement( "//ordernumber" ).Value;
      md5CheckValue += doc.XPathSelectElement( "//amount" ).Value;
      md5CheckValue += doc.XPathSelectElement( "//currency" ).Value;
      md5CheckValue += doc.XPathSelectElement( "//time" ).Value;
      md5CheckValue += doc.XPathSelectElement( "//state" ).Value;
      md5CheckValue += doc.XPathSelectElement( "//qpstat" ).Value;
      md5CheckValue += doc.XPathSelectElement( "//qpstatmsg" ).Value;
      md5CheckValue += doc.XPathSelectElement( "//chstat" ).Value;
      md5CheckValue += doc.XPathSelectElement( "//chstatmsg" ).Value;
      md5CheckValue += doc.XPathSelectElement( "//merchant" ).Value;
      md5CheckValue += doc.XPathSelectElement( "//merchantemail" ).Value;
      md5CheckValue += doc.XPathSelectElement( "//transaction" ).Value;
      md5CheckValue += doc.XPathSelectElement( "//cardtype" ).Value;
      md5CheckValue += doc.XPathSelectElement( "//cardnumber" ).Value;
      md5CheckValue += doc.XPathSelectElement( "//splitpayment" ).Value;
      md5CheckValue += doc.XPathSelectElement( "//fraudprobability" ).Value;
      md5CheckValue += doc.XPathSelectElement( "//fraudremarks" ).Value;
      md5CheckValue += doc.XPathSelectElement( "//fraudreport" ).Value;
      md5CheckValue += md5Secret;

      return GetMd5Hash( md5CheckValue ).Equals( doc.XPathSelectElement( "//md5check" ).Value );
    }

    #endregion

  }
}