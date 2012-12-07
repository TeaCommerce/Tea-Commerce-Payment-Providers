using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.PaymentProviders;
using TeaCommerce.PaymentProviders.AuthorizeNetService;
using TeaCommerce.Api.Infrastructure.Logging;


namespace TeaCommerce.PaymentProviders {
  public class AuthorizeNet : APaymentProvider {

    protected bool isTesting;

    public override bool SupportsCancellationOfPayment { get { return false; } }
    public override bool SupportsCapturingOfPayment { get { return false; } }
    public override bool SupportsRefundOfPayment { get { return false; } }

    public override IDictionary<string, string> DefaultSettings {
      get {
        if ( defaultSettings == null ) {
          defaultSettings = new Dictionary<string, string>();
          defaultSettings[ "x_login" ] = string.Empty;
          defaultSettings[ "x_receipt_link_url" ] = string.Empty;
          defaultSettings[ "x_cancel_url" ] = string.Empty;
          defaultSettings[ "x_type" ] = "AUTH_ONLY";
          defaultSettings[ "transactionKey" ] = string.Empty;
          defaultSettings[ "md5HashKey" ] = string.Empty;
          defaultSettings[ "testing" ] = "0";
        }
        return defaultSettings;
      }
    }

    public override string FormPostUrl { get { return !isTesting ? "https://secure.authorize.net/gateway/transact.dll" : "https://test.authorize.net/gateway/transact.dll"; } }
    public override string DocumentationLink { get { return "http://anders.burla.dk/umbraco/tea-commerce/using-authorize-net-with-tea-commerce/"; } }
    public override bool AllowsCallbackWithoutOrderId { get { return true; } }

    public override IDictionary<string, string> GenerateForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, IDictionary<string, string> settings ) {
      List<string> settingsToExclude = new string[] { "transactionKey", "md5HashKey", "testing" }.ToList();
      Dictionary<string, string> inputFields = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      isTesting = settings[ "testing" ] == "1";

      //Future: Would be cool to support item lines for this one - you have to return a List<Tuple<string, string>> for it to work with this provider
      inputFields[ "x_version" ] = "3.1";
      inputFields[ "x_show_form" ] = "PAYMENT_FORM";
      inputFields[ "x_relay_always" ] = "false";
      inputFields[ "x_relay_response" ] = "TRUE";
      inputFields[ "x_receipt_link_method" ] = "LINK";

      inputFields[ "x_invoice_num" ] = order.CartNumber;

      string amount = order.TotalPrice.WithVat.ToString( "0.00", CultureInfo.InvariantCulture );
      inputFields[ "x_amount" ] = amount;

      inputFields[ "x_receipt_link_url" ] = teaCommerceContinueUrl;
      inputFields[ "x_cancel_url" ] = teaCommerceCancelUrl;

      string sequenceNumber = order.Id.ToString();
      inputFields[ "x_fp_sequence" ] = sequenceNumber;

      string timestamp = ( DateTime.UtcNow - new DateTime( 1970, 1, 1 ) ).TotalSeconds.ToString( "0", CultureInfo.InvariantCulture );
      inputFields[ "x_fp_timestamp" ] = timestamp;

      inputFields[ "x_fp_hash" ] = EncryptHMAC( settings[ "transactionKey" ], settings[ "x_login" ] + "^" + sequenceNumber + "^" + timestamp + "^" + amount + "^" );

      return inputFields;
    }

    public override string GetContinueUrl( IDictionary<string, string> settings ) {
      return settings[ "x_receipt_link_url" ];
    }

    public override string GetCancelUrl( IDictionary<string, string> settings ) {
      return settings[ "x_cancel_url" ];
    }

    public override Guid GetOrderId( HttpRequest request, IDictionary<string, string> settings ) {
      //using ( StreamWriter writer = new StreamWriter( File.Create( HttpContext.Current.Server.MapPath( "~/AuthorizeNetTestGetOrderId.txt" ) ) ) ) {
      //  writer.WriteLine( "Form:" );
      //  foreach ( string k in request.Form.Keys ) {
      //    writer.WriteLine( k + " : " + request.Form[ k ] );
      //  }
      //  writer.Flush();
      //}

      string errorMessage = string.Empty;

      string responseCode = request.Form[ "x_response_code" ];
      if ( responseCode.Equals( "1" ) ) {

        string amount = request.Form[ "x_amount" ];
        string transaction = request.Form[ "x_trans_id" ];

        string gatewayMd5Hash = request.Form[ "x_MD5_Hash" ];

        MD5CryptoServiceProvider x = new MD5CryptoServiceProvider();
        string calculatedMd5Hash = Regex.Replace( BitConverter.ToString( x.ComputeHash( Encoding.ASCII.GetBytes( settings[ "md5HashKey" ] + settings[ "x_login" ] + transaction + amount ) ) ), "-", string.Empty );

        if ( gatewayMd5Hash.Equals( calculatedMd5Hash ) ) {
          string orderName = request.Form[ "x_invoice_num" ];

          return long.Parse( orderName.Remove( 0, TeaCommerceSettings.OrderNamePrefix.Length ) );
        } else
          errorMessage = "Tea Commerce - Authorize.net - MD5Sum security check failed - " + gatewayMd5Hash + " - " + calculatedMd5Hash + " - " + settings[ "md5HashKey" ];
      } else
        errorMessage = "Tea Commerce - Authorize.net - Payment not approved: " + responseCode;

      LoggingService.Instance.Log( errorMessage );
      return null;
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      //using ( StreamWriter writer = new StreamWriter( File.Create( HttpContext.Current.Server.MapPath( "~/AuthorizeNetTestCallback.txt" ) ) ) ) {
      //  writer.WriteLine( "Form:" );
      //  foreach ( string k in request.Form.Keys ) {
      //    writer.WriteLine( k + " : " + request.Form[ k ] );
      //  }
      //  writer.Flush();
      //}

      string errorMessage = string.Empty;

      string responseCode = request.Form[ "x_response_code" ];
      if ( responseCode.Equals( "1" ) ) {

        string amount = request.Form[ "x_amount" ];
        string transaction = request.Form[ "x_trans_id" ];

        string gatewayMd5Hash = request.Form[ "x_MD5_Hash" ];

        MD5CryptoServiceProvider x = new MD5CryptoServiceProvider();
        string calculatedMd5Hash = Regex.Replace( BitConverter.ToString( x.ComputeHash( Encoding.ASCII.GetBytes( settings[ "md5HashKey" ] + settings[ "x_login" ] + transaction + amount ) ) ), "-", string.Empty );

        if ( gatewayMd5Hash.Equals( calculatedMd5Hash ) ) {
          PaymentState paymentState = PaymentState.Authorized;
          if ( request.Form[ "x_type" ].Equals( "auth_capture" ) )
            paymentState = PaymentState.Captured;
          string cardType = request.Form[ "x_card_type" ];
          string cardNumber = request.Form[ "x_account_number" ];

          return new CallbackInfo( decimal.Parse( amount, CultureInfo.InvariantCulture ), transaction, paymentState, cardType, cardNumber );
        } else
          errorMessage = "Tea Commerce - Authorize.net - MD5Sum security check failed - " + gatewayMd5Hash + " - " + calculatedMd5Hash + " - " + settings[ "md5HashKey" ];
      } else
        errorMessage = "Tea Commerce - Authorize.net - Payment not approved: " + responseCode;

      LoggingService.Instance.Log( errorMessage );
      return new CallbackInfo( errorMessage );
    }

    public override ApiInfo GetStatus( Order order, IDictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      GetTransactionDetailsResponseType result = GetAuthorizeNetServiceClient( settings ).GetTransactionDetails( new MerchantAuthenticationType() { name = settings[ "x_login" ], transactionKey = settings[ "transactionKey" ] }, order.TransactionInformation.TransactionId );

      if ( result.resultCode == MessageTypeEnum.Ok ) {

        PaymentState paymentState = PaymentState.Initiated;
        switch ( result.transaction.transactionStatus ) {
          case "authorizedPendingCapture":
            paymentState = PaymentState.Authorized;
            break;
          case "capturedPendingSettlement":
          case "settledSuccessfully":
            paymentState = PaymentState.Captured;
            break;
          case "voided":
            paymentState = PaymentState.Cancelled;
            break;
          case "refundSettledSuccessfully":
          case "refundPendingSettlement":
            paymentState = PaymentState.Refunded;
            break;
        }

        return new ApiInfo( result.transaction.transId, paymentState );
      } else {
        errorMessage = "Tea Commerce - Authorize.net - " + string.Format( "Error making API request - Error code: {0} - Description: {1}", result.messages[ 0 ].code, result.messages[ 0 ].text );
      }

      LoggingService.Instance.Log( errorMessage );
      return new ApiInfo( errorMessage );
    }

    public override ApiInfo CapturePayment( Order order, IDictionary<string, string> settings ) {
      throw new NotImplementedException();
    }

    public override ApiInfo RefundPayment( Order order, IDictionary<string, string> settings ) {
      throw new NotImplementedException();
    }

    public override ApiInfo CancelPayment( Order order, IDictionary<string, string> settings ) {
      throw new NotImplementedException();
    }

    public override string GetLocalizedSettingsKey( string settingsKey, CultureInfo culture ) {
      switch ( settingsKey ) {
        case "x_receipt_link_url":
          return settingsKey + "<br/><small>e.g. /continue/</small>";
        case "x_cancel_url":
          return settingsKey + "<br/><small>e.g. /cancel/</small>";
        case "x_type":
          return settingsKey + "<br/><small>e.g. AUTH_ONLY or AUTH_CAPTURE</small>";
        case "testing":
          return settingsKey + "<br/><small>1 = true; 0 = false</small>";
        default:
          return base.GetLocalizedSettingsKey( settingsKey, culture );
      }
    }

    protected Service GetAuthorizeNetServiceClient( IDictionary<string, string> settings ) {
      Service service = new Service();
      bool isTesting = settings[ "testing" ] == "1";
      service.Url = !isTesting ? "https://api.authorize.net/soap/v1/Service.asmx" : "https://apitest.authorize.net/soap/v1/Service.asmx";
      return service;
    }

  }
}
