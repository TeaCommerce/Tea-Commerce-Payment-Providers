using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Web;
using TeaCommerce.Data;
using TeaCommerce.Data.Payment;
using umbraco.BusinessLogic;

namespace TeaCommerce.PaymentProviders {

  public class PayPal : APaymentProvider {

    public override Dictionary<string, string> DefaultSettings {
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

    public override Dictionary<string, string> GenerateForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, Dictionary<string, string> settings ) {
      isSandbox = settings[ "isSandbox" ] == "1";
      List<string> settingsToExclude = new string[] { "USER", "PWD", "SIGNATURE", "isSandbox", "productNumberPropertyAlias", "productNamePropertyAlias", "shippingMethodFormatString", "paymentMethodFormatString" }.ToList();
      Dictionary<string, string> inputFields = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      inputFields[ "cmd" ] = "_cart";
      inputFields[ "upload" ] = "1";
      inputFields[ "currency_code" ] = order.CurrencyISOCode;

      inputFields[ "invoice" ] = order.Name;
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
          inputFields[ "amount_" + itemIndex ] = orderLine.UnitPriceWithoutVAT.ToString( "0.00", CultureInfo.InvariantCulture );
          inputFields[ "tax_" + itemIndex ] = orderLine.UnitVAT.ToString( "0.00", CultureInfo.InvariantCulture );
          inputFields[ "quantity_" + itemIndex ] = orderLine.Quantity.ToString();

          itemIndex++;
        }

        if ( order.ShippingFee != 0 ) {
          inputFields[ "item_name_" + itemIndex ] = string.Format( settings[ "shippingMethodFormatString" ], order.ShippingMethod.Name );
          inputFields[ "amount_" + itemIndex ] = order.ShippingFeeWithoutVAT.ToString( "0.00", CultureInfo.InvariantCulture );
          inputFields[ "tax_" + itemIndex ] = order.ShippingFeeVAT.ToString( "0.00", CultureInfo.InvariantCulture );
          itemIndex++;
        }

        if ( order.PaymentFee != 0 ) {
          inputFields[ "item_name_" + itemIndex ] = string.Format( settings[ "paymentMethodFormatString" ], order.PaymentMethod.Name );
          inputFields[ "amount_" + itemIndex ] = order.PaymentFeeWithoutVAT.ToString( "0.00", CultureInfo.InvariantCulture );
          inputFields[ "tax_" + itemIndex ] = order.PaymentFeeVAT.ToString( "0.00", CultureInfo.InvariantCulture );
        }
      }

      #endregion

      return inputFields;
    }

    public override string GetContinueUrl( Dictionary<string, string> settings ) {
      return settings[ "return" ];
    }

    public override string GetCancelUrl( Dictionary<string, string> settings ) {
      return settings[ "cancel_return" ];
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, Dictionary<string, string> settings ) {
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
        string orderName = request.Form[ "invoice" ];

        string businessSetting = settings[ "business" ];

        //Check if the business email is the same in the callback
        if ( !string.IsNullOrEmpty( transaction ) && ( ( !string.IsNullOrEmpty( receiverId ) && businessSetting.Equals( receiverId ) ) || ( !string.IsNullOrEmpty( receiverEmail ) && businessSetting.Equals( receiverEmail ) ) ) ) {

          //Pending
          if ( state.Equals( "Pending" ) ) {

            if ( request.Form[ "pending_reason" ].Equals( "authorization" ) ) {
              if ( request.Form[ "transaction_entity" ].Equals( "auth" ) ) {
                return new CallbackInfo( orderName, amount, transaction, PaymentStatus.Authorized, null, null );
              }
            } else if ( request.Form[ "pending_reason" ].Equals( "multi_currency" ) ) {
              return new CallbackInfo( orderName, amount, transaction, PaymentStatus.PendingExternalSystem, null, null );
            }

            //Completed - auto capture
          } else if ( state.Equals( "Completed" ) )
            return new CallbackInfo( orderName, amount, transaction, PaymentStatus.Captured, null, null );

        } else
          errorMessage = "Tea Commerce - Paypal - Business isn't identical - settings: " + businessSetting + " - request-receiverId: " + receiverId + " - request-receiverEmail: " + receiverEmail;
      } else
        errorMessage = "Tea Commerce - Paypal - Couldn't verify response - " + response;

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new CallbackInfo( errorMessage );
    }

    public override APIInfo GetStatus( Order order, Dictionary<string, string> settings ) {
      return InternalGetStatus( order.TransactionPaymentTransactionId, settings );
    }

    public override APIInfo CapturePayment( Order order, Dictionary<string, string> settings ) {
      isSandbox = settings[ "isSandbox" ] == "1";

      string errorMessage = string.Empty;
      Dictionary<string, string> inputFields = PrepareAPIPostRequest( "DoCapture", settings );

      inputFields.Add( "AUTHORIZATIONID", order.TransactionPaymentTransactionId );
      inputFields.Add( "AMT", order.TotalPrice.ToString( "0.00", CultureInfo.InvariantCulture ) );
      inputFields.Add( "CURRENCYCODE", order.CurrencyISOCode );
      inputFields.Add( "COMPLETETYPE", "Complete" );

      Dictionary<string, string> responseKvp = GetApiResponseKvp( MakePostRequest( APIPostUrl, inputFields ) );
      if ( responseKvp[ "ACK" ].Equals( "Success" ) || responseKvp[ "ACK" ].Equals( "SuccessWithWarning" ) ) {
        return InternalGetStatus( responseKvp[ "TRANSACTIONID" ], settings );
      } else
        errorMessage = "Tea Commerce - PayPal - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_PayPal_error" ), responseKvp[ "L_ERRORCODE0" ] );

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    public override APIInfo RefundPayment( Order order, Dictionary<string, string> settings ) {
      isSandbox = settings[ "isSandbox" ] == "1";

      string errorMessage = string.Empty;
      Dictionary<string, string> inputFields = PrepareAPIPostRequest( "RefundTransaction", settings );

      inputFields.Add( "TRANSACTIONID", order.TransactionPaymentTransactionId );

      Dictionary<string, string> responseKvp = GetApiResponseKvp( MakePostRequest( APIPostUrl, inputFields ) );
      if ( responseKvp[ "ACK" ].Equals( "Success" ) || responseKvp[ "ACK" ].Equals( "SuccessWithWarning" ) ) {
        return InternalGetStatus( responseKvp[ "REFUNDTRANSACTIONID" ], settings );
      } else
        errorMessage = "Tea Commerce - PayPal - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_PayPal_error" ), responseKvp[ "L_ERRORCODE0" ] );

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    public override APIInfo CancelPayment( Order order, Dictionary<string, string> settings ) {
      isSandbox = settings[ "isSandbox" ] == "1";

      string errorMessage = string.Empty;
      Dictionary<string, string> inputFields = PrepareAPIPostRequest( "DoVoid", settings );

      inputFields.Add( "AUTHORIZATIONID", order.TransactionPaymentTransactionId );

      Dictionary<string, string> responseKvp = GetApiResponseKvp( MakePostRequest( APIPostUrl, inputFields ) );
      if ( responseKvp[ "ACK" ].Equals( "Success" ) || responseKvp[ "ACK" ].Equals( "SuccessWithWarning" ) ) {
        return InternalGetStatus( responseKvp[ "AUTHORIZATIONID" ], settings );
      } else
        errorMessage = "Tea Commerce - PayPal - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_PayPal_error" ), responseKvp[ "L_ERRORCODE0" ] );

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    protected APIInfo InternalGetStatus( string transactionId, Dictionary<string, string> settings ) {
      isSandbox = settings[ "isSandbox" ] == "1";

      string errorMessage = string.Empty;
      Dictionary<string, string> inputFields = PrepareAPIPostRequest( "GetTransactionDetails", settings );

      inputFields.Add( "TRANSACTIONID", transactionId );

      Dictionary<string, string> responseKvp = GetApiResponseKvp( MakePostRequest( APIPostUrl, inputFields ) );
      if ( responseKvp[ "ACK" ].Equals( "Success" ) || responseKvp[ "ACK" ].Equals( "SuccessWithWarning" ) ) {

        string paymentStatusResponse = responseKvp[ "PAYMENTSTATUS" ];

        //If the transaction is a refund
        if ( responseKvp.ContainsKey( "TRANSACTIONTYPE" ) && responseKvp.ContainsKey( "PARENTTRANSACTIONID" ) && responseKvp[ "TRANSACTIONTYPE" ].Equals( "sendmoney" ) )
          return InternalGetStatus( responseKvp[ "PARENTTRANSACTIONID" ], settings );

        PaymentStatus paymentStatus = PaymentStatus.Initial;
        if ( paymentStatusResponse.Equals( "Pending" ) ) {
          if ( responseKvp[ "PENDINGREASON" ].Equals( "authorization" ) )
            paymentStatus = PaymentStatus.Authorized;
          else
            paymentStatus = PaymentStatus.PendingExternalSystem;
        } else if ( paymentStatusResponse.Equals( "Completed" ) )
          paymentStatus = PaymentStatus.Captured;
        else if ( paymentStatusResponse.Equals( "Voided" ) )
          paymentStatus = PaymentStatus.Cancelled;
        else if ( paymentStatusResponse.Equals( "Refunded" ) )
          paymentStatus = PaymentStatus.Refunded;

        return new APIInfo( transactionId, paymentStatus );
      } else
        errorMessage = "Tea Commerce - PayPal - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_PayPal_error" ), responseKvp[ "L_ERRORCODE0" ] );

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    protected Dictionary<string, string> PrepareAPIPostRequest( string methodName, Dictionary<string, string> settings ) {
      Dictionary<string, string> inputFields = new Dictionary<string, string>();
      inputFields.Add( "USER", settings[ "USER" ] );
      inputFields.Add( "PWD", settings[ "PWD" ] );
      inputFields.Add( "SIGNATURE", settings[ "SIGNATURE" ] );
      inputFields.Add( "VERSION", "98.0" );
      inputFields.Add( "METHOD", methodName );
      return inputFields;
    }

    protected Dictionary<string, string> GetApiResponseKvp( string response ) {
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
