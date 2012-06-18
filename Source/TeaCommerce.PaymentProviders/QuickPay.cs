using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web;
using System.Xml.Linq;
using System.Xml.XPath;
using TeaCommerce.Data;
using TeaCommerce.Data.Payment;
using TeaCommerce.PaymentProviders.Extensions;
using umbraco.BusinessLogic;

namespace TeaCommerce.PaymentProviders {

  public class QuickPay : APaymentProvider {

    protected const string formPostUrl = "https://secure.quickpay.dk/form/";
    protected const string apiPostUrl = "https://secure.quickpay.dk/api/";

    public override Dictionary<string, string> DefaultSettings {
      get {
        if ( defaultSettings == null ) {
          defaultSettings = new Dictionary<string, string>();
          defaultSettings[ "merchant" ] = string.Empty;
          defaultSettings[ "language" ] = "en";
          defaultSettings[ "continueurl" ] = string.Empty;
          defaultSettings[ "cancelurl" ] = string.Empty;
          defaultSettings[ "autocapture" ] = "0";
          defaultSettings[ "cardtypelock" ] = string.Empty;
          defaultSettings[ "md5secret" ] = string.Empty;
          defaultSettings[ "testmode" ] = "0";
        }
        return defaultSettings;
      }
    }

    public override string FormPostUrl { get { return formPostUrl; } }
    public override string DocumentationLink { get { return "http://anders.burla.dk/umbraco/tea-commerce/using-quickpay-wit-tea-commerce/"; } }

    public override Dictionary<string, string> GenerateForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, Dictionary<string, string> settings ) {
      Dictionary<string, string> inputFields = new Dictionary<string, string>();

      inputFields[ "protocol" ] = "4";
      inputFields[ "msgtype" ] = "authorize";

      MoveInputFieldValue( inputFields, settings, "merchant" );
      MoveInputFieldValue( inputFields, settings, "language" );

      string orderName = order.Name;
      while ( orderName.Length < 4 )
        orderName = "0" + orderName;
      inputFields[ "ordernumber" ] = orderName;
      inputFields[ "amount" ] = ( order.TotalPrice * 100M ).ToString( "0", CultureInfo.InvariantCulture );
      inputFields[ "currency" ] = order.CurrencyISOCode;

      inputFields[ "continueurl" ] = teaCommerceContinueUrl;
      inputFields[ "cancelurl" ] = teaCommerceCancelUrl;
      inputFields[ "callbackurl" ] = teaCommerceCallBackUrl;

      MoveInputFieldValue( inputFields, settings, "autocapture" );
      MoveInputFieldValue( inputFields, settings, "autofee" );
      MoveInputFieldValue( inputFields, settings, "cardtypelock" );
      MoveInputFieldValue( inputFields, settings, "description" );
      MoveInputFieldValue( inputFields, settings, "group" );
      MoveInputFieldValue( inputFields, settings, "testmode" );
      MoveInputFieldValue( inputFields, settings, "splitpayment" );

      //Quickpay dont support to show order line information to the shopper

      string md5CheckValue = inputFields.Select( i => i.Value ).Join( string.Empty ) + settings[ "md5secret" ];
      inputFields[ "md5check" ] = GetMD5Hash( md5CheckValue );

      return inputFields;
    }

    public override string GetContinueUrl( Dictionary<string, string> settings ) {
      return settings[ "continueurl" ];
    }

    public override string GetCancelUrl( Dictionary<string, string> settings ) {
      return settings[ "cancelurl" ];
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, Dictionary<string, string> settings ) {
      //using ( StreamWriter writer = new StreamWriter( File.Create( HttpContext.Current.Server.MapPath( "~/QuickPayTestCallback.txt" ) ) ) ) {
      //  writer.WriteLine( "FORM:" );
      //  foreach ( string k in request.Form.Keys ) {
      //    writer.WriteLine( k + " : " + request.Form[ k ] );
      //  }
      //  writer.Flush();
      //}

      string errorMessage = string.Empty;

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

      if ( GetMD5Hash( md5CheckValue ).Equals( request.Form[ "md5check" ] ) ) {
        string qpstat = request.Form[ "qpstat" ];

        if ( qpstat.Equals( "000" ) ) {
          string orderName = request.Form[ "ordernumber" ];
          decimal amount = decimal.Parse( request.Form[ "amount" ], CultureInfo.InvariantCulture ) / 100M;
          string state = request.Form[ "state" ];
          string transaction = request.Form[ "transaction" ];
          decimal fee = decimal.Parse( request.Form[ "fee" ], CultureInfo.InvariantCulture ) / 100M;

          return new CallbackInfo( orderName, amount, transaction, state.Equals( "1" ) ? PaymentStatus.Authorized : PaymentStatus.Captured, request.Form[ "cardtype" ], request.Form[ "cardnumber" ] );
        } else {
          string qpstatmsg = request.Form[ "qpstatmsg" ];
          errorMessage = "Tea Commerce - QuickPay - Error making API request<br/>Error code: " + qpstat + "<br/>Error message: " + qpstatmsg + "<br/>See http://quickpay.net/faq/status-codes/ for a description of these";
        }

      } else
        errorMessage = "Tea Commerce - QuickPay - MD5Sum security check failed";

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new CallbackInfo( errorMessage );
    }

    public override APIInfo GetStatus( Order order, Dictionary<string, string> settings ) {
      Dictionary<string, string> inputFields = new Dictionary<string, string>();

      inputFields[ "protocol" ] = "4";
      inputFields[ "msgtype" ] = "status";
      inputFields[ "merchant" ] = settings[ "merchant" ];
      inputFields[ "transaction" ] = order.TransactionPaymentTransactionId;

      string md5secret = settings[ "md5secret" ];
      string md5CheckValue = inputFields.Select( i => i.Value ).Join( string.Empty ) + md5secret;
      inputFields[ "md5check" ] = GetMD5Hash( md5CheckValue );

      return MakeApiPostRequest( inputFields, md5secret );
    }

    public override APIInfo CapturePayment( Order order, Dictionary<string, string> settings ) {
      Dictionary<string, string> inputFields = new Dictionary<string, string>();

      inputFields[ "protocol" ] = "4";
      inputFields[ "msgtype" ] = "capture";
      inputFields[ "merchant" ] = settings[ "merchant" ];
      inputFields[ "amount" ] = ( order.TotalPrice * 100M ).ToString( "0" );
      inputFields[ "finalize" ] = "1";
      inputFields[ "transaction" ] = order.TransactionPaymentTransactionId;

      string md5secret = settings[ "md5secret" ];
      string md5CheckValue = inputFields.Select( i => i.Value ).Join( string.Empty ) + md5secret;
      inputFields[ "md5check" ] = GetMD5Hash( md5CheckValue );

      return MakeApiPostRequest( inputFields, md5secret );
    }

    public override APIInfo RefundPayment( Order order, Dictionary<string, string> settings ) {
      Dictionary<string, string> inputFields = new Dictionary<string, string>();

      inputFields[ "protocol" ] = "4";
      inputFields[ "msgtype" ] = "refund";
      inputFields[ "merchant" ] = settings[ "merchant" ];
      inputFields[ "amount" ] = ( order.TotalPrice * 100M ).ToString( "0" );
      inputFields[ "transaction" ] = order.TransactionPaymentTransactionId;

      string md5secret = settings[ "md5secret" ];
      string md5CheckValue = inputFields.Select( i => i.Value ).Join( string.Empty ) + md5secret;
      inputFields[ "md5check" ] = GetMD5Hash( md5CheckValue );

      return MakeApiPostRequest( inputFields, md5secret );
    }

    public override APIInfo CancelPayment( Order order, Dictionary<string, string> settings ) {
      Dictionary<string, string> inputFields = new Dictionary<string, string>();

      inputFields[ "protocol" ] = "4";
      inputFields[ "msgtype" ] = "cancel";
      inputFields[ "merchant" ] = settings[ "merchant" ];
      inputFields[ "transaction" ] = order.TransactionPaymentTransactionId;

      string md5secret = settings[ "md5secret" ];
      string md5CheckValue = inputFields.Select( i => i.Value ).Join( string.Empty ) + md5secret;
      inputFields[ "md5check" ] = GetMD5Hash( md5CheckValue );

      return MakeApiPostRequest( inputFields, md5secret );
    }

    protected APIInfo MakeApiPostRequest( Dictionary<string, string> inputFields, string MD5Secret ) {
      string response = MakePostRequest( apiPostUrl, inputFields );
      string errorMessage = string.Empty;

      #region Status response

      XDocument doc = XDocument.Parse( response, LoadOptions.PreserveWhitespace );

      string state = doc.XPathSelectElement( "//state" ).Value;
      string qpstat = doc.XPathSelectElement( "//qpstat" ).Value;
      string qpstatmsg = doc.XPathSelectElement( "//qpstatmsg" ).Value;
      string transaction = doc.XPathSelectElement( "//transaction" ).Value;

      if ( qpstat.Equals( "000" ) ) {
        if ( CheckMD5Sum( doc, MD5Secret ) ) {

          PaymentStatus paymentStatus = PaymentStatus.Initial;
          if ( state.Equals( "1" ) )
            paymentStatus = PaymentStatus.Authorized;
          else if ( state.Equals( "3" ) )
            paymentStatus = PaymentStatus.Captured;
          else if ( state.Equals( "5" ) )
            paymentStatus = PaymentStatus.Cancelled;
          else if ( state.Equals( "7" ) )
            paymentStatus = PaymentStatus.Refunded;

          return new APIInfo( transaction, paymentStatus );
        } else
          errorMessage = "Tea Commerce - Quickpay - " + umbraco.ui.Text( "teaCommerce", "paymentProvider_Quickpay_md5SumFailed" );
      } else
        errorMessage = "Tea Commerce - Quickpay - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_Quickpay_error" ), qpstat, qpstatmsg );


      #endregion

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    protected bool CheckMD5Sum( XDocument doc, string MD5Secret ) {
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
      md5CheckValue += MD5Secret;

      return GetMD5Hash( md5CheckValue ).Equals( doc.XPathSelectElement( "//md5check" ).Value );
    }

    protected void MoveInputFieldValue( Dictionary<string, string> inputFields, Dictionary<string, string> settings, string key ) {
      if ( settings.ContainsKey( key ) && !string.IsNullOrEmpty( settings[ key ] ) )
        inputFields[ key ] = settings[ key ];
    }

  }
}
