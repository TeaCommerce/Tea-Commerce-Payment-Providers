using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Web;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.PaymentProviders;
using TeaCommerce.Api.Infrastructure.Logging;

namespace TeaCommerce.PaymentProviders {

  public class PayPal : APaymentProvider {

    protected const string apiErrorFormatString = "Error making API request - Error code: {0} - see https://cms.paypal.com/us/cgi-bin/?cmd=_render-content&content_ID=developer/e_howto_api_nvp_errorcodes for a description of these";

    public override IDictionary<string, string> DefaultSettings {
      get {
        if ( defaultSettings == null ) {
          defaultSettings = new Dictionary<string, string>();
          defaultSettings[ "business" ] = string.Empty;
          defaultSettings[ "lc" ] = "US";
          defaultSettings[ "return" ] = string.Empty;
          defaultSettings[ "cancel_return" ] = string.Empty;
          defaultSettings[ "paymentaction" ] = "authorization";
          defaultSettings[ "USER" ] = string.Empty;
          defaultSettings[ "PWD" ] = string.Empty;
          defaultSettings[ "SIGNATURE" ] = string.Empty;
          defaultSettings[ "isSandbox" ] = "0";
          defaultSettings[ "productNumberPropertyAlias" ] = "productNumber";
          defaultSettings[ "productNamePropertyAlias" ] = "productName";
          defaultSettings[ "shippingMethodFormatString" ] = "Shipping fee ({0})";
          defaultSettings[ "paymentMethodFormatString" ] = "Payment fee ({0})";
        }
        return defaultSettings;
      }
    }

    protected bool isSandbox;

    public override string FormPostUrl { get { return !isSandbox ? "https://www.paypal.com/cgi-bin/webscr" : "https://www.sandbox.paypal.com/cgi-bin/webscr"; } }
    protected string APIPostUrl { get { return !isSandbox ? "https://api-3t.paypal.com/nvp" : "https://api-3t.sandbox.paypal.com/nvp"; } }

    public override IDictionary<string, string> GenerateForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, IDictionary<string, string> settings ) {
      isSandbox = settings[ "isSandbox" ] == "1";
      List<string> settingsToExclude = new string[] { "USER", "PWD", "SIGNATURE", "isSandbox", "productNumberPropertyAlias", "productNamePropertyAlias", "shippingMethodFormatString", "paymentMethodFormatString" }.ToList();
      Dictionary<string, string> inputFields = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      inputFields[ "cmd" ] = "_cart";
      inputFields[ "upload" ] = "1";
      inputFields[ "currency_code" ] = order.CurrencyISOCode;

      inputFields[ "invoice" ] = order.CartNumber;
      inputFields[ "no_shipping" ] = "1";

      inputFields[ "return" ] = teaCommerceContinueUrl;
      inputFields[ "rm" ] = "2";
      inputFields[ "cancel_return" ] = teaCommerceCancelUrl;
      inputFields[ "notify_url" ] = teaCommerceCallBackUrl;

      #region Order line information

      if ( !string.IsNullOrEmpty( settings[ "productNamePropertyAlias" ] ) ) {

        List<OrderLine> orderLines = order.OrderLines.ToList();
        OrderLine orderLine;
        int itemIndex = 1;

        for ( int i = 0; i < orderLines.Count; i++ ) {
          orderLine = orderLines[ i ];
          OrderLineProperty productNameProp = orderLine.Properties.SingleOrDefault( op => op.Alias.Equals( settings[ "productNamePropertyAlias" ] ) );
          OrderLineProperty productNumberProp = orderLine.Properties.SingleOrDefault( op => op.Alias.Equals( settings[ "productNumberPropertyAlias" ] ) );

          inputFields[ "item_name_" + itemIndex ] = productNameProp != null ? productNameProp.Value : string.Empty;
          if ( productNumberProp != null )
            inputFields[ "item_number_" + itemIndex ] = productNumberProp.Value;
          inputFields[ "amount_" + itemIndex ] = orderLine.UnitPrice.Value.ToString( "0.00", CultureInfo.InvariantCulture );
          inputFields[ "tax_" + itemIndex ] = orderLine.UnitPrice.Vat.ToString( "0.00", CultureInfo.InvariantCulture );
          inputFields[ "quantity_" + itemIndex ] = orderLine.Quantity.ToString();

          itemIndex++;
        }

        if ( order.ShipmentInformation.TotalPrice.WithVat != 0 ) {
          inputFields[ "item_name_" + itemIndex ] = string.Format( settings[ "shippingMethodFormatString" ], order.ShippingMethod.Name );
          inputFields[ "amount_" + itemIndex ] = order.ShipmentInformation.TotalPrice.Value.ToString( "0.00", CultureInfo.InvariantCulture );
          inputFields[ "tax_" + itemIndex ] = order.ShipmentInformation.TotalPrice.Vat.ToString( "0.00", CultureInfo.InvariantCulture );
          itemIndex++;
        }

        if ( order.PaymentInformation.TotalPrice.WithVat != 0 ) {
          inputFields[ "item_name_" + itemIndex ] = string.Format( settings[ "paymentMethodFormatString" ], order.PaymentMethod.Name );
          inputFields[ "amount_" + itemIndex ] = order.PaymentInformation.TotalPrice.Value.ToString( "0.00", CultureInfo.InvariantCulture );
          inputFields[ "tax_" + itemIndex ] = order.PaymentInformation.TotalPrice.Vat.ToString( "0.00", CultureInfo.InvariantCulture );
        }
      }

      #endregion

      return inputFields;
    }

    public override string GetContinueUrl( IDictionary<string, string> settings ) {
      return settings[ "return" ];
    }

    public override string GetCancelUrl( IDictionary<string, string> settings ) {
      return settings[ "cancel_return" ];
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      //using ( StreamWriter writer = new StreamWriter( File.Create( HttpContext.Current.Server.MapPath( "~/PayPalTestCallback.txt" ) ) ) ) {
      //  writer.WriteLine( "FORM:" );
      //  foreach ( string k in request.Form.Keys ) {
      //    writer.WriteLine( k + " : " + request.Form[ k ] );
      //  }
      //  writer.Flush();
      //}

      string errorMessage = string.Empty;
      isSandbox = settings[ "isSandbox" ] == "1";

      //Verify callback
      string response = MakePostRequest( FormPostUrl, Encoding.ASCII.GetString( request.BinaryRead( request.ContentLength ) ) + "&cmd=_notify-validate" );

      //using ( StreamWriter writer = new StreamWriter( File.Create( HttpContext.Current.Server.MapPath( "~/PayPalTestCallback2.txt" ) ) ) ) {
      //  writer.WriteLine( response );
      //  writer.Flush();
      //}

      if ( response.Equals( "VERIFIED" ) ) {

        string receiverId = request.Form[ "receiver_id" ];
        string receiverEmail = request.Form[ "receiver_email" ];
        string transaction = request.Form[ "txn_id" ];
        string transactionEntity = request.Form[ "transaction_entity" ];
        decimal amount = decimal.Parse( request.Form[ "mc_gross" ], CultureInfo.InvariantCulture );
        string state = request.Form[ "payment_status" ];

        string businessSetting = settings[ "business" ];

        //Check if the business email is the same in the callback
        if ( !string.IsNullOrEmpty( transaction ) && ( ( !string.IsNullOrEmpty( receiverId ) && businessSetting.Equals( receiverId ) ) || ( !string.IsNullOrEmpty( receiverEmail ) && businessSetting.Equals( receiverEmail ) ) ) ) {

          //Pending
          if ( state.Equals( "Pending" ) ) {

            if ( request.Form[ "pending_reason" ].Equals( "authorization" ) ) {
              if ( request.Form[ "transaction_entity" ].Equals( "auth" ) ) {
                return new CallbackInfo( amount, transaction, PaymentState.Authorized );
              }
            } else if ( request.Form[ "pending_reason" ].Equals( "multi_currency" ) ) {
              return new CallbackInfo( amount, transaction, PaymentState.PendingExternalSystem );
            }

            //Completed - auto capture
          } else if ( state.Equals( "Completed" ) )
            return new CallbackInfo( amount, transaction, PaymentState.Captured);

        } else
          errorMessage = "Tea Commerce - Paypal - Business isn't identical - settings: " + businessSetting + " - request-receiverId: " + receiverId + " - request-receiverEmail: " + receiverEmail;
      } else
        errorMessage = "Tea Commerce - Paypal - Couldn't verify response - " + response;

      LoggingService.Instance.Log( errorMessage );
      return new CallbackInfo( errorMessage );
    }

    public override ApiInfo GetStatus( Order order, IDictionary<string, string> settings ) {
      return InternalGetStatus( order.TransactionInformation.TransactionId, settings );
    }

    public override ApiInfo CapturePayment( Order order, IDictionary<string, string> settings ) {
      isSandbox = settings[ "isSandbox" ] == "1";

      string errorMessage = string.Empty;
      IDictionary<string, string> inputFields = PrepareAPIPostRequest( "DoCapture", settings );

      inputFields.Add( "AUTHORIZATIONID", order.TransactionInformation.TransactionId );
      inputFields.Add( "AMT", order.TotalPrice.WithVat.ToString( "0.00", CultureInfo.InvariantCulture ) );
      inputFields.Add( "CURRENCYCODE", order.CurrencyISOCode );
      inputFields.Add( "COMPLETETYPE", "Complete" );

      IDictionary<string, string> responseKvp = GetApiResponseKvp( MakePostRequest( APIPostUrl, inputFields ) );
      if ( responseKvp[ "ACK" ].Equals( "Success" ) || responseKvp[ "ACK" ].Equals( "SuccessWithWarning" ) ) {
        return InternalGetStatus( responseKvp[ "TRANSACTIONID" ], settings );
      } else
        errorMessage = "Tea Commerce - PayPal - " + string.Format( apiErrorFormatString, responseKvp[ "L_ERRORCODE0" ] );

      LoggingService.Instance.Log( errorMessage );
      return new ApiInfo( errorMessage );
    }

    public override ApiInfo RefundPayment( Order order, IDictionary<string, string> settings ) {
      isSandbox = settings[ "isSandbox" ] == "1";

      string errorMessage = string.Empty;
      IDictionary<string, string> inputFields = PrepareAPIPostRequest( "RefundTransaction", settings );

      inputFields.Add( "TRANSACTIONID", order.TransactionInformation.TransactionId );

      IDictionary<string, string> responseKvp = GetApiResponseKvp( MakePostRequest( APIPostUrl, inputFields ) );
      if ( responseKvp[ "ACK" ].Equals( "Success" ) || responseKvp[ "ACK" ].Equals( "SuccessWithWarning" ) ) {
        return InternalGetStatus( responseKvp[ "REFUNDTRANSACTIONID" ], settings );
      } else
        errorMessage = "Tea Commerce - PayPal - " + string.Format( apiErrorFormatString, responseKvp[ "L_ERRORCODE0" ] );

      LoggingService.Instance.Log( errorMessage );
      return new ApiInfo( errorMessage );
    }

    public override ApiInfo CancelPayment( Order order, IDictionary<string, string> settings ) {
      isSandbox = settings[ "isSandbox" ] == "1";

      string errorMessage = string.Empty;
      IDictionary<string, string> inputFields = PrepareAPIPostRequest( "DoVoid", settings );

      inputFields.Add( "AUTHORIZATIONID", order.TransactionInformation.TransactionId );

      IDictionary<string, string> responseKvp = GetApiResponseKvp( MakePostRequest( APIPostUrl, inputFields ) );
      if ( responseKvp[ "ACK" ].Equals( "Success" ) || responseKvp[ "ACK" ].Equals( "SuccessWithWarning" ) ) {
        return InternalGetStatus( responseKvp[ "AUTHORIZATIONID" ], settings );
      } else
        errorMessage = "Tea Commerce - PayPal - " + string.Format( apiErrorFormatString, responseKvp[ "L_ERRORCODE0" ] );

      LoggingService.Instance.Log( errorMessage );
      return new ApiInfo( errorMessage );
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

    protected ApiInfo InternalGetStatus( string transactionId, IDictionary<string, string> settings ) {
      isSandbox = settings[ "isSandbox" ] == "1";

      string errorMessage = string.Empty;
      IDictionary<string, string> inputFields = PrepareAPIPostRequest( "GetTransactionDetails", settings );

      inputFields.Add( "TRANSACTIONID", transactionId );

      IDictionary<string, string> responseKvp = GetApiResponseKvp( MakePostRequest( APIPostUrl, inputFields ) );
      if ( responseKvp[ "ACK" ].Equals( "Success" ) || responseKvp[ "ACK" ].Equals( "SuccessWithWarning" ) ) {

        string paymentStatusResponse = responseKvp[ "PAYMENTSTATUS" ];

        //If the transaction is a refund
        if ( responseKvp.ContainsKey( "TRANSACTIONTYPE" ) && responseKvp.ContainsKey( "PARENTTRANSACTIONID" ) && responseKvp[ "TRANSACTIONTYPE" ].Equals( "sendmoney" ) )
          return InternalGetStatus( responseKvp[ "PARENTTRANSACTIONID" ], settings );

        PaymentState paymentState = PaymentState.Initiated;
        if ( paymentStatusResponse.Equals( "Pending" ) ) {
          if ( responseKvp[ "PENDINGREASON" ].Equals( "authorization" ) )
            paymentState = PaymentState.Authorized;
          else
            paymentState = PaymentState.PendingExternalSystem;
        } else if ( paymentStatusResponse.Equals( "Completed" ) )
          paymentState = PaymentState.Captured;
        else if ( paymentStatusResponse.Equals( "Voided" ) )
          paymentState = PaymentState.Cancelled;
        else if ( paymentStatusResponse.Equals( "Refunded" ) )
          paymentState = PaymentState.Refunded;

        return new ApiInfo( transactionId, paymentState );
      } else
        errorMessage = "Tea Commerce - PayPal - " + string.Format( apiErrorFormatString, responseKvp[ "L_ERRORCODE0" ] );

      LoggingService.Instance.Log( errorMessage );
      return new ApiInfo( errorMessage );
    }

    protected IDictionary<string, string> PrepareAPIPostRequest( string methodName, IDictionary<string, string> settings ) {
      Dictionary<string, string> inputFields = new Dictionary<string, string>();
      inputFields.Add( "USER", settings[ "USER" ] );
      inputFields.Add( "PWD", settings[ "PWD" ] );
      inputFields.Add( "SIGNATURE", settings[ "SIGNATURE" ] );
      inputFields.Add( "VERSION", "56.0" );
      inputFields.Add( "METHOD", methodName );
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

  }
}
