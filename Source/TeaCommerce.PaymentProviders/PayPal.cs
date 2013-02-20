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

namespace TeaCommerce.PaymentProviders {

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
        defaultSettings[ "isSandbox" ] = "0";
        return defaultSettings;
      }
    }

    public override PaymentHtmlForm GenerateHtmlForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );

      PaymentHtmlForm htmlForm = new PaymentHtmlForm {
        Action = settings.ContainsKey( "isSandbox" ) && settings[ "isSandbox" ] == "1" ? "https://www.sandbox.paypal.com/cgi-bin/webscr" : "https://www.paypal.com/cgi-bin/webscr"
      };

      string[] settingsToExclude = new[] { "USER", "PWD", "SIGNATURE", "isSandbox" };
      htmlForm.InputFields = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      htmlForm.InputFields[ "cmd" ] = "_cart";
      htmlForm.InputFields[ "upload" ] = "1";

      //Check that the Iso code exists
      Currency currency = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId );
      if ( !Iso4217CurrencyCodes.ContainsKey( currency.IsoCode ) ) {
        throw new Exception( "You must specify an ISO 4217 currency code for the " + currency.Name + " currency" );
      }
      htmlForm.InputFields[ "currency_code" ] = currency.IsoCode;

      htmlForm.InputFields[ "invoice" ] = order.CartNumber;
      htmlForm.InputFields[ "no_shipping" ] = "1";

      htmlForm.InputFields[ "return" ] = teaCommerceContinueUrl;
      htmlForm.InputFields[ "rm" ] = "2";
      htmlForm.InputFields[ "cancel_return" ] = teaCommerceCancelUrl;
      htmlForm.InputFields[ "notify_url" ] = teaCommerceCallBackUrl;

      #region Order line information

      int itemIndex = 1;

      foreach ( OrderLine orderLine in order.OrderLines ) {
        htmlForm.InputFields[ "item_name_" + itemIndex ] = orderLine.Name;
        htmlForm.InputFields[ "item_number_" + itemIndex ] = orderLine.Sku;
        htmlForm.InputFields[ "amount_" + itemIndex ] = orderLine.UnitPrice.Value.ToString( "0.00", CultureInfo.InvariantCulture );
        htmlForm.InputFields[ "tax_" + itemIndex ] = orderLine.UnitPrice.Vat.ToString( "0.00", CultureInfo.InvariantCulture );
        htmlForm.InputFields[ "quantity_" + itemIndex ] = orderLine.Quantity.ToString( CultureInfo.InvariantCulture );

        itemIndex++;
      }

      if ( order.ShipmentInformation.ShippingMethodId != null ) {
        ShippingMethod shippingMethod = ShippingMethodService.Instance.Get( order.StoreId, order.ShipmentInformation.ShippingMethodId.Value );
        htmlForm.InputFields[ "item_name_" + itemIndex ] = shippingMethod.Name;
        htmlForm.InputFields[ "amount_" + itemIndex ] = order.ShipmentInformation.TotalPrice.Value.ToString( "0.00", CultureInfo.InvariantCulture );
        htmlForm.InputFields[ "tax_" + itemIndex ] = order.ShipmentInformation.TotalPrice.Vat.ToString( "0.00", CultureInfo.InvariantCulture );
        itemIndex++;
      }

      if ( order.PaymentInformation.PaymentMethodId != null ) {
        PaymentMethod paymentMethod = PaymentMethodService.Instance.Get( order.StoreId, order.PaymentInformation.PaymentMethodId.Value );
        htmlForm.InputFields[ "item_name_" + itemIndex ] = paymentMethod.Name;
        htmlForm.InputFields[ "amount_" + itemIndex ] = order.PaymentInformation.TotalPrice.Value.ToString( "0.00", CultureInfo.InvariantCulture );
        htmlForm.InputFields[ "tax_" + itemIndex ] = order.PaymentInformation.TotalPrice.Vat.ToString( "0.00", CultureInfo.InvariantCulture );
      }

      #endregion

      return htmlForm;
    }

    public override string GetContinueUrl( IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "return", "settings" );

      return settings[ "return" ];
    }

    public override string GetCancelUrl( IDictionary<string, string> settings ) {
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
          using ( StreamWriter writer = new StreamWriter( File.Create( HostingEnvironment.MapPath( "~/paypal-callback-data.txt" ) ) ) ) {
            writer.WriteLine( "Form:" );
            foreach ( string k in request.Form.Keys ) {
              writer.WriteLine( k + " : " + request.Form[ k ] );
            }
            writer.Flush();
          }
        }

        //Verify callback
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
            LoggingService.Instance.Log( "PayPal(" + order.CartNumber + ") - Business isn't identical - settings: " + businessSetting + " | request-receiverId: " + receiverId + " | request-receiverEmail: " + receiverEmail );
          }
        } else {
          LoggingService.Instance.Log( "PayPal(" + order.CartNumber + ") - Couldn't verify response: " + response );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "PayPal(" + order.CartNumber + ") - Process callback" );
      }

      return callbackInfo;
    }

    public override ApiInfo GetStatus( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        apiInfo = InternalGetStatus( order.OrderNumber, order.TransactionInformation.TransactionId, settings );
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "PayPal(" + order.OrderNumber + ") - Get status" );
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
        inputFields.Add( "AMT", order.TotalPrice.WithVat.ToString( "0.00", CultureInfo.InvariantCulture ) );
        Currency currency = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId );
        if ( !Iso4217CurrencyCodes.ContainsKey( currency.IsoCode ) ) {
          throw new Exception( "You must specify an ISO 4217 currency code for the " + currency.Name + " currency" );
        }
        inputFields.Add( "CURRENCYCODE", currency.IsoCode );
        inputFields.Add( "COMPLETETYPE", "Complete" );

        IDictionary<string, string> responseKvp = GetApiResponseKvp( MakePostRequest( settings.ContainsKey( "isSandbox" ) && settings[ "isSandbox" ] == "1" ? "https://api-3t.sandbox.paypal.com/nvp" : "https://api-3t.paypal.com/nvp", inputFields ) );
        if ( responseKvp[ "ACK" ] == "Success" || responseKvp[ "ACK" ] == "SuccessWithWarning" ) {
          apiInfo = InternalGetStatus( order.OrderNumber, responseKvp[ "TRANSACTIONID" ], settings );
        } else {
          LoggingService.Instance.Log( "PayPal(" + order.OrderNumber + ") - Error making API request - error code: " + responseKvp[ "L_ERRORCODE0" ] );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "PayPal(" + order.OrderNumber + ") - Refund payment" );
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

        IDictionary<string, string> responseKvp = GetApiResponseKvp( MakePostRequest( settings.ContainsKey( "isSandbox" ) && settings[ "isSandbox" ] == "1" ? "https://api-3t.sandbox.paypal.com/nvp" : "https://api-3t.paypal.com/nvp", inputFields ) );
        if ( responseKvp[ "ACK" ] == "Success" || responseKvp[ "ACK" ] == "SuccessWithWarning" ) {
          apiInfo = InternalGetStatus( order.OrderNumber, responseKvp[ "REFUNDTRANSACTIONID" ], settings );
        } else {
          LoggingService.Instance.Log( "PayPal(" + order.OrderNumber + ") - Error making API request - error code: " + responseKvp[ "L_ERRORCODE0" ] );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "PayPal(" + order.OrderNumber + ") - Refund payment" );
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

        IDictionary<string, string> responseKvp = GetApiResponseKvp( MakePostRequest( settings.ContainsKey( "isSandbox" ) && settings[ "isSandbox" ] == "1" ? "https://api-3t.sandbox.paypal.com/nvp" : "https://api-3t.paypal.com/nvp", inputFields ) );
        if ( responseKvp[ "ACK" ] == "Success" || responseKvp[ "ACK" ] == "SuccessWithWarning" ) {
          apiInfo = InternalGetStatus( order.OrderNumber, responseKvp[ "AUTHORIZATIONID" ], settings );
        } else {
          LoggingService.Instance.Log( "PayPal(" + order.OrderNumber + ") - Error making API request - error code: " + responseKvp[ "L_ERRORCODE0" ] );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "PayPal(" + order.OrderNumber + ") - Cancel payment" );
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
        LoggingService.Instance.Log( "PayPal(" + orderNumber + ") - Error making API request - error code: " + responseKvp[ "L_ERRORCODE0" ] );
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
