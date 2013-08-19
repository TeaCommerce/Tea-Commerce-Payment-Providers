using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Hosting;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Web.PaymentProviders;
using TeaCommerce.PaymentProviders.AuthorizeNetService;

namespace TeaCommerce.PaymentProviders {

  [PaymentProvider( "AuthorizeNet" )]
  public class AuthorizeNet : APaymentProvider {

    public override string DocumentationLink { get { return "http://anders.burla.dk/umbraco/tea-commerce/using-authorize-net-with-tea-commerce/"; } }

    public override bool SupportsRetrievalOfPaymentStatus { get { return true; } }

    public override bool AllowsCallbackWithoutOrderId { get { return true; } }

    public override IDictionary<string, string> DefaultSettings {
      get {
        Dictionary<string, string> defaultSettings = new Dictionary<string, string>();
        defaultSettings[ "x_login" ] = string.Empty;
        defaultSettings[ "x_receipt_link_url" ] = string.Empty;
        defaultSettings[ "x_cancel_url" ] = string.Empty;
        defaultSettings[ "x_type" ] = "AUTH_ONLY";
        defaultSettings[ "transactionKey" ] = string.Empty;
        defaultSettings[ "md5HashKey" ] = string.Empty;
        defaultSettings[ "testing" ] = "0";
        return defaultSettings;
      }
    }

    public override PaymentHtmlForm GenerateHtmlForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "x_login", "settings" );
      settings.MustContainKey( "x_type", "settings" );
      settings.MustContainKey( "transactionKey", "settings" );

      PaymentHtmlForm htmlForm = new PaymentHtmlForm {
        Action = settings.ContainsKey( "testing" ) && settings[ "testing" ] == "1" ? "https://test.authorize.net/gateway/transact.dll" : "https://secure.authorize.net/gateway/transact.dll"
      };

      string[] settingsToExclude = new[] { "transactionKey", "md5HashKey", "testing" };
      htmlForm.InputFields = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      //Future: Would be cool to support item lines for this one - you have to return a List<Tuple<string, string>> for it to work with this provider
      htmlForm.InputFields[ "x_version" ] = "3.1";
      htmlForm.InputFields[ "x_show_form" ] = "PAYMENT_FORM";
      htmlForm.InputFields[ "x_relay_always" ] = "false";
      htmlForm.InputFields[ "x_relay_response" ] = "TRUE";
      htmlForm.InputFields[ "x_receipt_link_method" ] = "LINK";

      htmlForm.InputFields[ "x_invoice_num" ] = order.CartNumber;

      string amount = order.TotalPrice.WithVat.ToString( "0.00", CultureInfo.InvariantCulture );
      htmlForm.InputFields[ "x_amount" ] = amount;

      htmlForm.InputFields[ "x_receipt_link_url" ] = teaCommerceContinueUrl;
      htmlForm.InputFields[ "x_cancel_url" ] = teaCommerceCancelUrl;

      string sequenceNumber = new Random().Next( 0, 1000 ).ToString( CultureInfo.InvariantCulture );
      htmlForm.InputFields[ "x_fp_sequence" ] = sequenceNumber;

      string timestamp = ( DateTime.UtcNow - new DateTime( 1970, 1, 1 ) ).TotalSeconds.ToString( "0", CultureInfo.InvariantCulture );
      htmlForm.InputFields[ "x_fp_timestamp" ] = timestamp;

      htmlForm.InputFields[ "x_fp_hash" ] = GenerateHMACMD5Hash( settings[ "transactionKey" ], settings[ "x_login" ] + "^" + sequenceNumber + "^" + timestamp + "^" + amount + "^" );

      return htmlForm;
    }

    public override string GetContinueUrl( Order order, IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "x_receipt_link_url", "settings" );

      return settings[ "x_receipt_link_url" ];
    }

    public override string GetCancelUrl( Order order, IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "x_cancel_url", "settings" );

      return settings[ "x_cancel_url" ];
    }

    public override string GetCartNumber( HttpRequest request, IDictionary<string, string> settings ) {
      string cartNumber = "";

      try {
        request.MustNotBeNull( "request" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "md5HashKey", "settings" );
        settings.MustContainKey( "x_login", "settings" );

        //Write data when testing
        if ( settings.ContainsKey( "testing" ) && settings[ "testing" ] == "1" ) {
          using ( StreamWriter writer = new StreamWriter( File.Create( HostingEnvironment.MapPath( "~/authorize-net-get-cart-number-data.txt" ) ) ) ) {
            writer.WriteLine( "Form:" );
            foreach ( string k in request.Form.Keys ) {
              writer.WriteLine( k + " : " + request.Form[ k ] );
            }
            writer.Flush();
          }
        }

        string responseCode = request.Form[ "x_response_code" ];
        if ( responseCode == "1" ) {

          string amount = request.Form[ "x_amount" ];
          string transaction = request.Form[ "x_trans_id" ];

          string gatewayMd5Hash = request.Form[ "x_MD5_Hash" ];

          MD5CryptoServiceProvider x = new MD5CryptoServiceProvider();
          string calculatedMd5Hash = Regex.Replace( BitConverter.ToString( x.ComputeHash( Encoding.ASCII.GetBytes( settings[ "md5HashKey" ] + settings[ "x_login" ] + transaction + amount ) ) ), "-", string.Empty );

          if ( gatewayMd5Hash == calculatedMd5Hash ) {
            cartNumber = request.Form[ "x_invoice_num" ];
          } else {
            LoggingService.Instance.Log( "Authorize.net - MD5Sum security check failed - " + gatewayMd5Hash + " - " + calculatedMd5Hash + " - " + settings[ "md5HashKey" ] );
          }
        } else {
          LoggingService.Instance.Log( "Authorize.net - Payment not approved: " + responseCode );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "Authorize.net - Get cart number" );
      }

      return cartNumber;
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      CallbackInfo callbackInfo = null;

      try {
        order.MustNotBeNull( "order" );
        request.MustNotBeNull( "request" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "md5HashKey", "settings" );
        settings.MustContainKey( "x_login", "settings" );

        //Write data when testing
        if ( settings.ContainsKey( "testing" ) && settings[ "testing" ] == "1" ) {
          using ( StreamWriter writer = new StreamWriter( File.Create( HttpContext.Current.Server.MapPath( "~/authorize-net-callback-data.txt" ) ) ) ) {
            writer.WriteLine( "Form:" );
            foreach ( string k in request.Form.Keys ) {
              writer.WriteLine( k + " : " + request.Form[ k ] );
            }
            writer.Flush();
          }
        }

        string responseCode = request.Form[ "x_response_code" ];
        if ( responseCode == "1" ) {

          string amount = request.Form[ "x_amount" ];
          string transaction = request.Form[ "x_trans_id" ];

          string gatewayMd5Hash = request.Form[ "x_MD5_Hash" ];

          MD5CryptoServiceProvider x = new MD5CryptoServiceProvider();
          string calculatedMd5Hash = Regex.Replace( BitConverter.ToString( x.ComputeHash( Encoding.ASCII.GetBytes( settings[ "md5HashKey" ] + settings[ "x_login" ] + transaction + amount ) ) ), "-", string.Empty );

          if ( gatewayMd5Hash == calculatedMd5Hash ) {
            PaymentState paymentState = PaymentState.Authorized;
            if ( request.Form[ "x_type" ] == "auth_capture" ) {
              paymentState = PaymentState.Captured;
            }
            string cardType = request.Form[ "x_card_type" ];
            string cardNumber = request.Form[ "x_account_number" ];

            callbackInfo = new CallbackInfo( decimal.Parse( amount, CultureInfo.InvariantCulture ), transaction, paymentState, cardType, cardNumber );
          } else {
            LoggingService.Instance.Log( "Authorize.net(" + order.CartNumber + ") - MD5Sum security check failed - " + gatewayMd5Hash + " - " + calculatedMd5Hash + " - " + settings[ "md5HashKey" ] );
          }
        } else {
          LoggingService.Instance.Log( "Authorize.net(" + order.CartNumber + ") - Payment not approved: " + responseCode );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "Authorize.net(" + order.CartNumber + ") - Process callback" );
      }

      return callbackInfo;
    }

    public override ApiInfo GetStatus( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "x_login", "settings" );
        settings.MustContainKey( "transactionKey", "settings" );

        GetTransactionDetailsResponseType result = GetAuthorizeNetServiceClient( settings ).GetTransactionDetails( new MerchantAuthenticationType { name = settings[ "x_login" ], transactionKey = settings[ "transactionKey" ] }, order.TransactionInformation.TransactionId );

        if ( result.resultCode == MessageTypeEnum.Ok ) {

          PaymentState paymentState = PaymentState.Initialized;
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

          apiInfo = new ApiInfo( result.transaction.transId, paymentState );
        } else {
          LoggingService.Instance.Log( "Authorize.net(" + order.OrderNumber + ") - Error making API request - error code: " + result.messages[ 0 ].code + " | description: " + result.messages[ 0 ].text );
        }

      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "Authorize.net(" + order.OrderNumber + ") - Get status" );
      }

      return apiInfo;
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

    #region Helper methods

    protected Service GetAuthorizeNetServiceClient( IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );

      Service service = new Service {
        Url = settings.ContainsKey( "testing" ) && settings[ "testing" ] == "1" ? "https://apitest.authorize.net/soap/v1/Service.asmx" : "https://api.authorize.net/soap/v1/Service.asmx"
      };
      return service;
    }

    #endregion

  }
}
