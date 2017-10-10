using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web;
using System.Web.Hosting;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;
using System.Net;
using System.Xml;

namespace TeaCommerce.PaymentProviders.Classic {

  [PaymentProvider( "PayPal" )]
  public class PayPal : APaymentProvider {

    public override bool SupportsRetrievalOfPaymentStatus { get { return true; } }
    public override bool SupportsCapturingOfPayment { get { return true; } }
    public override bool SupportsRefundOfPayment { get { return true; } }
    public override bool SupportsCancellationOfPayment { get { return true; } }

    public override IDictionary<string, string> DefaultSettings {
      get {
        Dictionary<string, string> defaultSettings = new Dictionary<string, string>();
        defaultSettings[ "business" ] = string.Empty;
        defaultSettings[ "lc" ] = "US";
        defaultSettings[ "return" ] = string.Empty;
        defaultSettings[ "cancel_return" ] = string.Empty;
        defaultSettings[ "paymentaction" ] = "authorization";
        defaultSettings[ "USER" ] = string.Empty;
        defaultSettings[ "PWD" ] = string.Empty;
        defaultSettings[ "SIGNATURE" ] = string.Empty;
        defaultSettings[ "totalSku" ] = "0001";
        defaultSettings[ "totalName" ] = "Total";
        defaultSettings[ "isSandbox" ] = "1";
        return defaultSettings;
      }
    }

    public override PaymentHtmlForm GenerateHtmlForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, string teaCommerceCommunicationUrl, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      string action = string.Empty;
      string encrypted = string.Empty;

      try {
        int bvCount = 0;

        IDictionary<string, string> inputFields = PrepareApiPostRequest( "BMCreateButton", settings );

        inputFields.Add( "BUTTONCODE", "ENCRYPTED" );
        inputFields.Add( "BUTTONTYPE", "BUYNOW" );
        inputFields.Add( "BUTTONSUBTYPE", "PRODUCTS" );

        bvCount++;
        inputFields.Add( $"L_BUTTONVAR{bvCount}", "business=" + ( settings.ContainsKey( "business" ) ? settings[ "business" ] : string.Empty ) );
        bvCount++;
        inputFields.Add( $"L_BUTTONVAR{bvCount}", "lc=" + ( settings.ContainsKey( "lc" ) ? settings[ "lc" ] : string.Empty ) );
        bvCount++;
        inputFields.Add( $"L_BUTTONVAR{bvCount}", "button_subtype=PRODUCTS" );

        // Check that the Iso code exists
        Currency currency = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId );
        if ( !Iso4217CurrencyCodes.ContainsKey( currency.IsoCode ) ) {
          throw new Exception( "You must specify an ISO 4217 currency code for the " + currency.Name + " currency" );
        }

        bvCount++;
        inputFields.Add( $"L_BUTTONVAR{bvCount}", "currency_code=" + currency.IsoCode );

        bvCount++;
        inputFields.Add( $"L_BUTTONVAR{bvCount}", "invoice=" + order.CartNumber );
        bvCount++;
        inputFields.Add( $"L_BUTTONVAR{bvCount}", "item_name=" + ( settings.ContainsKey( "totalName" ) ? settings[ "totalName" ] : "Total" ) );
        bvCount++;
        inputFields.Add( $"L_BUTTONVAR{bvCount}", "item_number=" + ( settings.ContainsKey( "totalSku" ) ? settings[ "totalSku" ] : "0001" ) );
        bvCount++;
        inputFields.Add( $"L_BUTTONVAR{bvCount}", "amount=" + order.TotalPrice.Value.WithVat.ToString( "0.00", CultureInfo.InvariantCulture ) );
        bvCount++;
        inputFields.Add( $"L_BUTTONVAR{bvCount}", "quantity=" + 1M.ToString( "0", CultureInfo.InvariantCulture ) );

        bvCount++;
        inputFields.Add( $"L_BUTTONVAR{bvCount}", "return=" + teaCommerceContinueUrl );
        bvCount++;
        inputFields.Add( $"L_BUTTONVAR{bvCount}", "rm=2" );
        bvCount++;
        inputFields.Add( $"L_BUTTONVAR{bvCount}", "cancel_return=" + teaCommerceCancelUrl );
        bvCount++;
        inputFields.Add( $"L_BUTTONVAR{bvCount}", "notify_url=" + teaCommerceCallBackUrl );

        ServicePointManager.SecurityProtocol = (SecurityProtocolType) 3072;
        string response = MakePostRequest( settings.ContainsKey( "isSandbox" ) && settings[ "isSandbox" ] == "1" ? "https://api-3t.sandbox.paypal.com/nvp" : "https://api-3t.paypal.com/nvp", inputFields );
        IDictionary<string, string> responseKvp = GetApiResponseKvp( response );
        if ( responseKvp[ "ACK" ] == "Success" || responseKvp[ "ACK" ] == "SuccessWithWarning" ) {
          string input = responseKvp["WEBSITECODE"];
          using ( XmlReader reader = XmlReader.Create( new StringReader( input ) ) ) {
            reader.ReadToFollowing( "form" );
            reader.MoveToAttribute( "action" );
            action = reader.Value.Trim();

            while ( reader.ReadToFollowing( "input" ) ) {
              reader.MoveToAttribute( "name" );
              string name = reader.Value.Trim();
              if ( name != "encrypted" ) {
                continue;
              }

              reader.MoveToAttribute( "value" );
              encrypted = reader.Value.Trim();
              break;
            }
          }
        }
        else {
          LoggingService.Instance.Warn<PayPal>( "PayPal(" + order.OrderNumber + ") - Error making API request - error code: " + responseKvp[ "L_ERRORCODE0" ] );
        }
      }
      catch ( Exception exp ) {
        LoggingService.Instance.Error<PayPal>( "PayPal(" + order.OrderNumber + ") - GenerateHtmlForm payment", exp );
      }

      PaymentHtmlForm htmlForm = new PaymentHtmlForm {
        Action = action,
        InputFields = new Dictionary<string, string> {
          { "cmd", "_s-xclick" },
          { "encrypted", encrypted }
        }
      };
      return htmlForm;
    }

    public override string GetContinueUrl( Order order, IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "return", "settings" );

      return settings[ "return" ];
    }

    public override string GetCancelUrl( Order order, IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "cancel_return", "settings" );

      return settings[ "cancel_return" ];
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      CallbackInfo callbackInfo = null;

      try {
        order.MustNotBeNull( "order" );
        request.MustNotBeNull( "request" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "business", "settings" );

        //Write data when testing
        if ( settings.ContainsKey( "isSandbox" ) && settings[ "isSandbox" ] == "1" ) {
          LogRequest<PayPal>( request, logPostData: true );
        }

        //Verify callback
        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
        string response = MakePostRequest( settings.ContainsKey( "isSandbox" ) && settings[ "isSandbox" ] == "1" ? "https://www.sandbox.paypal.com/cgi-bin/webscr" : "https://www.paypal.com/cgi-bin/webscr", Encoding.ASCII.GetString( request.BinaryRead( request.ContentLength ) ) + "&cmd=_notify-validate" );

        if ( settings.ContainsKey( "isSandbox" ) && settings[ "isSandbox" ] == "1" ) {
          using ( StreamWriter writer = new StreamWriter( File.Create( HostingEnvironment.MapPath( "~/paypal-callback-data-2.txt" ) ) ) ) {
            writer.WriteLine( response );
            writer.Flush();
          }
        }

        if ( response == "VERIFIED" ) {

          string receiverId = request.Form[ "receiver_id" ];
          string receiverEmail = request.Form[ "receiver_email" ];
          string transaction = request.Form[ "txn_id" ];
          decimal amount = decimal.Parse( request.Form[ "mc_gross" ], CultureInfo.InvariantCulture );
          string state = request.Form[ "payment_status" ];

          string businessSetting = settings[ "business" ];

          //Check if the business email is the same in the callback
          if ( !string.IsNullOrEmpty( transaction ) && ( ( !string.IsNullOrEmpty( receiverId ) && businessSetting == receiverId ) || ( !string.IsNullOrEmpty( receiverEmail ) && businessSetting == receiverEmail ) ) ) {

            //Pending
            if ( state == "Pending" ) {

              if ( request.Form[ "pending_reason" ] == "authorization" ) {
                if ( request.Form[ "transaction_entity" ] == "auth" ) {
                  callbackInfo = new CallbackInfo( amount, transaction, PaymentState.Authorized );
                }
              } else if ( request.Form[ "pending_reason" ] == "multi_currency" ) {
                callbackInfo = new CallbackInfo( amount, transaction, PaymentState.PendingExternalSystem );
              }

              //Completed - auto capture
            } else if ( state == "Completed" ) {
              callbackInfo = new CallbackInfo( amount, transaction, PaymentState.Captured );
            }
          } else {
            LoggingService.Instance.Warn<PayPal>( "PayPal(" + order.CartNumber + ") - Business isn't identical - settings: " + businessSetting + " | request-receiverId: " + receiverId + " | request-receiverEmail: " + receiverEmail );
          }
        } else {
          LoggingService.Instance.Warn<PayPal>( "PayPal(" + order.CartNumber + ") - Couldn't verify response: " + response );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Error<PayPal>( "PayPal(" + order.CartNumber + ") - Process callback", exp );
      }

      return callbackInfo;
    }

    public override ApiInfo GetStatus( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        apiInfo = InternalGetStatus( order.OrderNumber, order.TransactionInformation.TransactionId, settings );
      } catch ( Exception exp ) {
        LoggingService.Instance.Error<PayPal>( "PayPal(" + order.OrderNumber + ") - Get status", exp );
      }

      return apiInfo;
    }

    public override ApiInfo CapturePayment( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );

        IDictionary<string, string> inputFields = PrepareApiPostRequest( "DoCapture", settings );

        inputFields.Add( "AUTHORIZATIONID", order.TransactionInformation.TransactionId );
        inputFields.Add( "AMT", order.TransactionInformation.AmountAuthorized.Value.ToString( "0.00", CultureInfo.InvariantCulture ) );
        Currency currency = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId );
        if ( !Iso4217CurrencyCodes.ContainsKey( currency.IsoCode ) ) {
          throw new Exception( "You must specify an ISO 4217 currency code for the " + currency.Name + " currency" );
        }
        inputFields.Add( "CURRENCYCODE", currency.IsoCode );
        inputFields.Add( "COMPLETETYPE", "Complete" );

        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
        IDictionary<string, string> responseKvp = GetApiResponseKvp( MakePostRequest( settings.ContainsKey( "isSandbox" ) && settings[ "isSandbox" ] == "1" ? "https://api-3t.sandbox.paypal.com/nvp" : "https://api-3t.paypal.com/nvp", inputFields ) );
        if ( responseKvp[ "ACK" ] == "Success" || responseKvp[ "ACK" ] == "SuccessWithWarning" ) {
          apiInfo = InternalGetStatus( order.OrderNumber, responseKvp[ "TRANSACTIONID" ], settings );
        } else {
          LoggingService.Instance.Warn<PayPal>( "PayPal(" + order.OrderNumber + ") - Error making API request - error code: " + responseKvp[ "L_ERRORCODE0" ] );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Error<PayPal>( "PayPal(" + order.OrderNumber + ") - Refund payment", exp );
      }

      return apiInfo;
    }

    public override ApiInfo RefundPayment( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );

        IDictionary<string, string> inputFields = PrepareApiPostRequest( "RefundTransaction", settings );

        inputFields.Add( "TRANSACTIONID", order.TransactionInformation.TransactionId );

        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
        IDictionary<string, string> responseKvp = GetApiResponseKvp( MakePostRequest( settings.ContainsKey( "isSandbox" ) && settings[ "isSandbox" ] == "1" ? "https://api-3t.sandbox.paypal.com/nvp" : "https://api-3t.paypal.com/nvp", inputFields ) );
        if ( responseKvp[ "ACK" ] == "Success" || responseKvp[ "ACK" ] == "SuccessWithWarning" ) {
          apiInfo = InternalGetStatus( order.OrderNumber, responseKvp[ "REFUNDTRANSACTIONID" ], settings );
        } else {
          LoggingService.Instance.Warn<PayPal>( "PayPal(" + order.OrderNumber + ") - Error making API request - error code: " + responseKvp[ "L_ERRORCODE0" ] );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Error<PayPal>( "PayPal(" + order.OrderNumber + ") - Refund payment", exp );
      }

      return apiInfo;
    }

    public override ApiInfo CancelPayment( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );

        IDictionary<string, string> inputFields = PrepareApiPostRequest( "DoVoid", settings );

        inputFields.Add( "AUTHORIZATIONID", order.TransactionInformation.TransactionId );

        ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
        IDictionary<string, string> responseKvp = GetApiResponseKvp( MakePostRequest( settings.ContainsKey( "isSandbox" ) && settings[ "isSandbox" ] == "1" ? "https://api-3t.sandbox.paypal.com/nvp" : "https://api-3t.paypal.com/nvp", inputFields ) );
        if ( responseKvp[ "ACK" ] == "Success" || responseKvp[ "ACK" ] == "SuccessWithWarning" ) {
          apiInfo = InternalGetStatus( order.OrderNumber, responseKvp[ "AUTHORIZATIONID" ], settings );
        } else {
          LoggingService.Instance.Warn<PayPal>( "PayPal(" + order.OrderNumber + ") - Error making API request - error code: " + responseKvp[ "L_ERRORCODE0" ] );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Error<PayPal>( "PayPal(" + order.OrderNumber + ") - Cancel payment", exp );
      }

      return apiInfo;
    }

    public override string GetLocalizedSettingsKey( string settingsKey, CultureInfo culture ) {
      switch ( settingsKey ) {
        case "return":
          return settingsKey + "<br/><small>e.g. /continue/</small>";
        case "cancel_return":
          return settingsKey + "<br/><small>e.g. /cancel/</small>";
        case "paymentaction":
          return settingsKey + "<br/><small>e.g. sale or authorization</small>";
        case "isSandbox":
          return settingsKey + "<br/><small>1 = true; 0 = false</small>";
        default:
          return base.GetLocalizedSettingsKey( settingsKey, culture );
      }
    }

    #region Helper methods

    protected ApiInfo InternalGetStatus( string orderNumber, string transactionId, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      IDictionary<string, string> inputFields = PrepareApiPostRequest( "GetTransactionDetails", settings );

      inputFields.Add( "TRANSACTIONID", transactionId );

      ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;
      IDictionary<string, string> responseKvp = GetApiResponseKvp( MakePostRequest( settings.ContainsKey( "isSandbox" ) && settings[ "isSandbox" ] == "1" ? "https://api-3t.sandbox.paypal.com/nvp" : "https://api-3t.paypal.com/nvp", inputFields ) );
      if ( responseKvp[ "ACK" ] == "Success" || responseKvp[ "ACK" ] == "SuccessWithWarning" ) {

        string paymentStatusResponse = responseKvp[ "PAYMENTSTATUS" ];

        //If the transaction is a refund
        if ( responseKvp.ContainsKey( "TRANSACTIONTYPE" ) && responseKvp.ContainsKey( "PARENTTRANSACTIONID" ) &&
             responseKvp[ "TRANSACTIONTYPE" ] == "sendmoney" ) {
          apiInfo = InternalGetStatus( orderNumber, responseKvp[ "PARENTTRANSACTIONID" ], settings );
        } else {
          PaymentState paymentState = PaymentState.Initialized;
          if ( paymentStatusResponse == "Pending" ) {
            paymentState = responseKvp[ "PENDINGREASON" ] == "authorization" ? PaymentState.Authorized : PaymentState.PendingExternalSystem;
          } else if ( paymentStatusResponse == "Completed" )
            paymentState = PaymentState.Captured;
          else if ( paymentStatusResponse == "Voided" )
            paymentState = PaymentState.Cancelled;
          else if ( paymentStatusResponse == "Refunded" )
            paymentState = PaymentState.Refunded;

          apiInfo = new ApiInfo( transactionId, paymentState );
        }
      } else {
        LoggingService.Instance.Warn<PayPal>( "PayPal(" + orderNumber + ") - Error making API request - error code: " + responseKvp[ "L_ERRORCODE0" ] );
      }

      return apiInfo;
    }

    protected IDictionary<string, string> PrepareApiPostRequest( string methodName, IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "USER", "settings" );
      settings.MustContainKey( "PWD", "settings" );
      settings.MustContainKey( "SIGNATURE", "settings" );

      Dictionary<string, string> inputFields = new Dictionary<string, string> {
        {"USER", settings[ "USER" ]},
        {"PWD", settings[ "PWD" ]},
        {"SIGNATURE", settings[ "SIGNATURE" ]},
        {"VERSION", "98.0"},
        {"METHOD", methodName}
      };
      return inputFields;
    }

    protected IDictionary<string, string> GetApiResponseKvp( string response ) {
      HttpServerUtility server = HttpContext.Current.Server;
      return ( from item in response.Split( '&' )
               let kvp = item.Split( '=' )
               select new {
                 Key = kvp[ 0 ],
                 Value = server.UrlDecode( kvp[ 1 ] )
               } ).ToDictionary( i => i.Key, i => i.Value );
    }

    #endregion

  }
}
