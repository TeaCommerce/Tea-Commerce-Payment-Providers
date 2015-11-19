using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using Newtonsoft.Json;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;

namespace TeaCommerce.PaymentProviders.Classic {
  [PaymentProvider( "QuickPay" )]
  public class QuickPay : APaymentProvider {

    public override string DocumentationLink { get { return "https://documentation.teacommerce.net/guides/payment-providers/quickpay/"; } }

    public override bool SupportsRetrievalOfPaymentStatus { get { return true; } }
    public override bool SupportsCapturingOfPayment { get { return true; } }
    public override bool SupportsRefundOfPayment { get { return true; } }
    public override bool SupportsCancellationOfPayment { get { return true; } }

    public override IDictionary<string, string> DefaultSettings {
      get {
        Dictionary<string, string> defaultSettings = new Dictionary<string, string>();
        defaultSettings[ "merchant_id" ] = string.Empty;
        defaultSettings[ "apiKey" ] = string.Empty;
        defaultSettings[ "windowApiKey" ] = string.Empty;
        defaultSettings[ "privateKey" ] = string.Empty;
        defaultSettings[ "agreement_id" ] = string.Empty;
        defaultSettings[ "language" ] = "en";
        defaultSettings[ "continueurl" ] = string.Empty;
        defaultSettings[ "cancelurl" ] = string.Empty;
        defaultSettings[ "autocapture" ] = "0";
        defaultSettings[ "payment_methods" ] = string.Empty;
        defaultSettings[ "testmode" ] = "1";

        return defaultSettings;
      }
    }

    public override PaymentHtmlForm GenerateHtmlForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, string teaCommerceCommunicationUrl, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "merchant_id", "settings" );
      settings.MustContainKey( "agreement_id", "settings" );
      settings.MustContainKey( "autocapture", "settings" );
      settings.MustContainKey( "language", "settings" );
      settings.MustContainKey( "windowApiKey", "settings" );

      PaymentHtmlForm htmlForm = new PaymentHtmlForm {
        Action = "https://payment.quickpay.net"
      };

      htmlForm.InputFields[ "version" ] = "v10";
      htmlForm.InputFields[ "merchant_id" ] = settings[ "merchant_id" ];
      htmlForm.InputFields[ "agreement_id" ] = settings[ "agreement_id" ];
      htmlForm.InputFields[ "autocapture" ] = settings[ "autocapture" ];
      htmlForm.InputFields[ "payment_methods" ] = settings[ "payment_methods" ];

      //Order name must be between 4 or 20 chars  
      string orderName = order.CartNumber;
      while ( orderName.Length < 4 )
        orderName = "0" + orderName;
      if ( orderName.Length > 20 ) {
        throw new Exception( "Cart number of the order can not exceed 20 characters." );
      }
      htmlForm.InputFields[ "order_id" ] = orderName;

      htmlForm.InputFields[ "amount" ] = ( order.TotalPrice.Value.WithVat * 100M ).ToString( "0", CultureInfo.InvariantCulture );

      //Check that the Iso code exists
      Currency currency = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId );
      if ( !Iso4217CurrencyCodes.ContainsKey( currency.IsoCode ) ) {
        throw new Exception( "You must specify an ISO 4217 currency code for the " + currency.Name + " currency" );
      }
      htmlForm.InputFields[ "currency" ] = currency.IsoCode;

      htmlForm.InputFields[ "continueurl" ] = teaCommerceContinueUrl;
      htmlForm.InputFields[ "cancelurl" ] = teaCommerceCancelUrl;
      htmlForm.InputFields[ "callbackurl" ] = teaCommerceCallBackUrl;

      htmlForm.InputFields[ "language" ] = settings[ "language" ];

      htmlForm.InputFields[ "checksum" ] = GetChecksum( htmlForm.InputFields, settings[ "windowApiKey" ] );

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
        settings.MustContainKey( "privateKey", "settings" );

        string orderName = order.CartNumber;
        while ( orderName.Length < 4 )
          orderName = "0" + orderName;

        string checkSum = request.Headers[ "QuickPay-Checksum-Sha256" ];

        byte[] bytes = new byte[ request.InputStream.Length ];
        request.InputStream.Read( bytes, 0, bytes.Length );
        request.InputStream.Position = 0;

        string streamContent = Encoding.ASCII.GetString( bytes );

        Result result = JsonConvert.DeserializeObject<Result>( streamContent );

        if ( orderName == result.OrderId && GetChecksum( streamContent, settings[ "privateKey" ] ) == checkSum ) {
          Operation lastAuthorize = result.Operations.LastOrDefault( o => o.Type == "authorize" );

          if ( lastAuthorize != null ) {
            if ( lastAuthorize.QpStatusCode == "20000" ) {
              decimal amount = decimal.Parse( lastAuthorize.Amount, CultureInfo.InvariantCulture ) / 100M;

              callbackInfo = new CallbackInfo( amount, result.Id, PaymentState.Authorized, result.Metadata.Type, result.Metadata.Last4 );
            } else {
              LoggingService.Instance.Log( "Quickpay10(" + order.CartNumber + ") - Error making API request - error code: " + lastAuthorize.QpStatusCode + " | error message: " + lastAuthorize.QpStatusMsg );
            }
          } else {
            LoggingService.Instance.Log( "QuickPay10(" + order.CartNumber + ") - No authorize found" );
          }
        } else {
          LoggingService.Instance.Log( "QuickPay10(" + order.CartNumber + ") - Checksum security check failed" );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "QuickPay10(" + order.CartNumber + ") - Process callback" );
      }

      return callbackInfo;
    }

    public override ApiInfo GetStatus( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "apiKey", "settings" );

        Dictionary<string, string> parameters = new Dictionary<string, string>();

        apiInfo = MakeApiRequest( order.TransactionInformation.TransactionId, settings[ "apiKey" ], "", parameters, "GET" );
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
        settings.MustContainKey( "apiKey", "settings" );

        Dictionary<string, string> parameters = new Dictionary<string, string> {
          { "amount", (order.TransactionInformation.AmountAuthorized.Value*100M).ToString("0") }
        };

        apiInfo = MakeApiRequest( order.TransactionInformation.TransactionId, settings[ "apiKey" ], "capture", parameters );
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
        settings.MustContainKey( "apiKey", "settings" );

        Dictionary<string, string> parameters = new Dictionary<string, string> {
          { "amount", (order.TransactionInformation.AmountAuthorized.Value*100M).ToString("0") }
        };

        apiInfo = MakeApiRequest( order.TransactionInformation.TransactionId, settings[ "apiKey" ], "refund", parameters );
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
        settings.MustContainKey( "apiKey", "settings" );

        Dictionary<string, string> parameters = new Dictionary<string, string>();

        apiInfo = MakeApiRequest( order.TransactionInformation.TransactionId, settings[ "apiKey" ], "cancel", parameters );
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
        case "testmode":
          return settingsKey + "<br/><small>1 = true; 0 = false</small>";
        case "privateKey":
          return settingsKey + "<br/><small>Private key</small>";
        case "apiKey":
          return settingsKey + "<br/><small>API key for API user</small>";
        case "windowApiKey":
          return settingsKey + "<br/><small>API key for Payment Window</small>";
        case "agreement_id":
          return settingsKey + "<br/><small>Agreement id for Payment Window</small>";
        case "autocapture":
          return settingsKey + "<br/><small>1 = true; 0 = false</small>";
        case "payment_methods":
          return settingsKey + "<br/><small>e.g. visa,mastercard</small>";
        default:
          return base.GetLocalizedSettingsKey( settingsKey, culture );
      }
    }

    #region Helper methods

    protected ApiInfo MakeApiRequest( string transactionId, string apiKey, string operation, Dictionary<string, string> parameters, string method = "POST" ) {
      ApiInfo apiInfo = null;

      string url = string.Format( "https://api.quickpay.net/payments/" + transactionId + ( !string.IsNullOrEmpty( operation ) ? "/" + operation : "" ) + "?synchronized&{0}",
        string.Join( "&", parameters.Select( kvp => string.Format( "{0}={1}", kvp.Key, kvp.Value ) ) ) );

      if ( !string.IsNullOrEmpty( url ) ) {
        HttpWebRequest webRequest = (HttpWebRequest)WebRequest.Create( url );

        webRequest.Method = method;
        webRequest.ContentType = "application/x-www-form-urlencoded";
        webRequest.Headers.Add( HttpRequestHeader.Authorization, string.Format( "Basic {0}", Convert.ToBase64String( Encoding.ASCII.GetBytes( ":" + apiKey ) ) ) );
        webRequest.Headers.Add( "Accept-Version", "v10" );

        using ( Stream responseStream = ( webRequest.GetResponse() ).GetResponseStream() ) {
          if ( responseStream != null ) {
            using ( StreamReader reader = new StreamReader( responseStream, Encoding.UTF8 ) ) {
              string str = reader.ReadToEnd();

              Result result = JsonConvert.DeserializeObject<Result>( str );

              Operation lastCompletedOperation = result.Operations.LastOrDefault( o => !o.Pending && o.QpStatusCode == "20000" );

              PaymentState paymentState = PaymentState.Initialized;

              if ( lastCompletedOperation != null ) {
                switch ( lastCompletedOperation.Type ) {
                  case "capture":
                    paymentState = PaymentState.Captured;
                    break;
                  case "refund":
                    paymentState = PaymentState.Refunded;
                    break;
                  case "cancel":
                    paymentState = PaymentState.Cancelled;
                    break;
                }
              }

              apiInfo = new ApiInfo( transactionId, paymentState );
            }
          }
        }
      }

      return apiInfo;
    }

    protected string GetChecksum( IDictionary<string, string> inputFields, string apiKey ) {
      string result = String.Join( " ", inputFields.OrderBy( c => c.Key ).Select( c => c.Value ).ToArray() );

      return GetChecksum( result, apiKey );
    }

    protected string GetChecksum( string fields, string apiKey ) {
      HMACSHA256 hmac = new HMACSHA256( Encoding.UTF8.GetBytes( apiKey ) );

      byte[] b = hmac.ComputeHash( Encoding.UTF8.GetBytes( fields ) );

      StringBuilder sb = new StringBuilder();
      foreach ( byte hmacByte in b ) {
        sb.Append( hmacByte.ToString( "x2" ) );
      }
      return sb.ToString();
    }

    #endregion

    #region Json deserialization

    protected class Result {
      [JsonProperty( "id" )]
      public string Id { get; set; }

      [JsonProperty( "order_id" )]
      public string OrderId { get; set; }

      [JsonProperty( "operations" )]
      public List<Operation> Operations { get; set; }

      [JsonProperty( "metadata" )]
      public Metadata Metadata { get; set; }
    }

    protected class Operation {
      [JsonProperty( "type" )]
      public string Type { get; set; }

      [JsonProperty( "amount" )]
      public string Amount { get; set; }

      [JsonProperty( "pending" )]
      public bool Pending { get; set; }

      [JsonProperty( "qp_status_code" )]
      public string QpStatusCode { get; set; }

      [JsonProperty( "qp_status_msg" )]
      public string QpStatusMsg { get; set; }
    }

    protected class Metadata {
      [JsonProperty( "type" )]
      public string Type { get; set; }

      [JsonProperty( "last4" )]
      public string Last4 { get; set; }
    }

    #endregion
  }
}