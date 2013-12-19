using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using System.Xml.Linq;
using System.Xml.XPath;
using TeaCommerce.Data;
using TeaCommerce.Data.Payment;
using TeaCommerce.Data.Tools;
using TeaCommerce.PaymentProviders.Extensions;
using umbraco.BusinessLogic;

namespace TeaCommerce.PaymentProviders {

  public class Ogone : APaymentProvider {

    protected string formPostUrl;
    public override string FormPostUrl { get { return formPostUrl; } }
    public override bool FinalizeAtContinueUrl { get { return true; } }

    public override Dictionary<string, string> DefaultSettings {
      get {
        if ( defaultSettings == null ) {
          defaultSettings = new Dictionary<string, string>();
          defaultSettings[ "PSPID" ] = string.Empty;
          defaultSettings[ "LANGUAGE" ] = "en_US";
          defaultSettings[ "ACCEPTURL" ] = string.Empty;
          defaultSettings[ "CANCELURL" ] = string.Empty;
          defaultSettings[ "BACKURL" ] = string.Empty;
          defaultSettings[ "PMLIST" ] = string.Empty;
          defaultSettings[ "SHAINPASSPHRASE" ] = string.Empty;
          defaultSettings[ "SHAOUTPASSPHRASE" ] = string.Empty;
          defaultSettings[ "APIUSERID" ] = string.Empty;
          defaultSettings[ "APIPASSWORD" ] = string.Empty;
          defaultSettings[ "TESTMODE" ] = "0";
        }
        return defaultSettings;
      }
    }

    public override string DocumentationLink { get { return "http://anders.burla.dk/umbraco/tea-commerce/using-ogone-with-tea-commerce/"; } }

    public override Dictionary<string, string> GenerateForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallbackUrl, string teaCommerceCommunicationUrl, Dictionary<string, string> settings ) {
      List<string> settingsToExclude = new string[] { "SHAINPASSPHRASE", "SHAOUTPASSPHRASE", "APIUSERID", "APIPASSWORD", "TESTMODE" }.ToList();
      Dictionary<string, string> inputFields = settings.Where( i => !string.IsNullOrEmpty( i.Value ) && !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key.ToUpperInvariant(), i => i.Value );

      inputFields[ "ORDERID" ] = order.Name;
      inputFields[ "AMOUNT" ] = ( order.TotalPrice * 100M ).ToString( "0", CultureInfo.InvariantCulture );
      inputFields[ "CURRENCY" ] = order.CurrencyISOCode;
      inputFields[ "CN" ] = order.FirstName + " " + order.LastName;
      inputFields[ "EMAIL" ] = order.Email;
      inputFields[ "ACCEPTURL" ] = teaCommerceContinueUrl;
      inputFields[ "DECLINEURL" ] = teaCommerceCancelUrl;
      inputFields[ "EXCEPTIONURL" ] = teaCommerceCancelUrl;
      inputFields[ "CANCELURL" ] = teaCommerceCancelUrl;

      //Ogone dont support to show order line information to the shopper

      string strToHash = inputFields.OrderBy( i => i.Key ).Select( i => i.Key + "=" + i.Value + settings[ "SHAINPASSPHRASE" ] ).Join( "" );
      inputFields[ "SHASIGN" ] = CryptoProvider.ConvertToHexString( new SHA512Managed().ComputeHash( Encoding.UTF8.GetBytes( strToHash ) ) );

      formPostUrl = GetMethodUrl( "GENERATEFORM", settings );

      return inputFields;
    }

    public override string GetContinueUrl( Dictionary<string, string> settings ) {
      return settings[ "ACCEPTURL" ];
    }

    public override string GetCancelUrl( Dictionary<string, string> settings ) {
      return settings[ "CANCELURL" ];
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, Dictionary<string, string> settings ) {
      //using ( StreamWriter writer = new StreamWriter( File.Create( HttpContext.Current.Server.MapPath( "~/OgoneTestCallback.txt" ) ) ) ) {
      //  writer.WriteLine( "QUERYSTRING:" );
      //  foreach ( string k in request.QueryString.Keys ) {
      //    writer.WriteLine( k + " : " + request.QueryString[ k ] );
      //  }
      //  writer.Flush();
      //}

      string errorMessage = string.Empty;
      Dictionary<string, string> inputFields = new Dictionary<string, string>();

      string SHASign = request.QueryString[ "SHASIGN" ];
      string orderName = request.QueryString[ "ORDERID" ];
      string strAmount = request.QueryString[ "AMOUNT" ];
      string transaction = request.QueryString[ "PAYID" ];
      string status = request.QueryString[ "STATUS" ];
      string cardType = request.QueryString[ "BRAND" ];
      string cardNo = request.QueryString[ "CARDNO" ];

      foreach ( string key in request.QueryString.Keys ) {
        if ( !key.Equals( "SHASIGN" ) )
          inputFields[ key ] = request.QueryString[ key ];
      }

      string strToHash = inputFields.OrderBy( i => i.Key ).Select( i => i.Key.ToUpperInvariant() + "=" + i.Value + settings[ "SHAOUTPASSPHRASE" ] ).Join( "" );
      string digest = CryptoProvider.ConvertToHexString( new SHA512Managed().ComputeHash( Encoding.UTF8.GetBytes( strToHash ) ) );

      if ( digest.Equals( SHASign ) ) {
        return new CallbackInfo( orderName, decimal.Parse( strAmount, CultureInfo.InvariantCulture ), transaction, status.Equals( "5" ) || status.Equals( "51" ) ? PaymentStatus.Authorized : PaymentStatus.Captured, cardType, cardNo );
      } else
        errorMessage = string.Format( "Tea Commerce - Ogone - SHASIGN check isn't valid - Calculated digest: {0} - Ogone SHASIGN: {1}", digest, SHASign );

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new CallbackInfo( errorMessage );
    }

    public override APIInfo GetStatus( Order order, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      XDocument doc = GetStatusInternal( order, settings );
      string status = doc.XPathSelectElement( "//ncresponse" ).Attribute( "STATUS" ).Value;

      PaymentStatus paymentStatus = PaymentStatus.Error;
      switch ( status ) {
        case "5":
        case "51":
          paymentStatus = PaymentStatus.Authorized;
          break;
        case "9":
        case "91":
          paymentStatus = PaymentStatus.Captured;
          break;
        case "6":
        case "61":
          paymentStatus = PaymentStatus.Cancelled;
          break;
        case "7":
        case "71":
        case "8":
        case "81":
          paymentStatus = PaymentStatus.Refunded;
          break;
      }

      if ( paymentStatus != PaymentStatus.Error )
        return new APIInfo( doc.XPathSelectElement( "//ncresponse" ).Attribute( "PAYID" ).Value, paymentStatus );
      else
        errorMessage = "Tea Commerce - Ogone - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_Ogone_error" ), doc.XPathSelectElement( "//ncresponse" ).Attribute( "NCERROR" ).Value, doc.XPathSelectElement( "//ncresponse" ).Attribute( "NCERRORPLUS" ).Value );

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    public override APIInfo CapturePayment( Order order, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      XDocument doc = MakeApiRequest( "CAPTURE", "SAS", order, settings );
      string status = doc.XPathSelectElement( "//ncresponse" ).Attribute( "STATUS" ).Value;

      if ( status.Equals( "9" ) || status.Equals( "91" ) )
        return new APIInfo( doc.XPathSelectElement( "//ncresponse" ).Attribute( "PAYID" ).Value, PaymentStatus.Captured );
      else
        errorMessage = "Tea Commerce - Ogone - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_Ogone_error" ), doc.XPathSelectElement( "//ncresponse" ).Attribute( "NCERROR" ).Value, doc.XPathSelectElement( "//ncresponse" ).Attribute( "NCERRORPLUS" ).Value );

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    public override APIInfo RefundPayment( Order order, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      XDocument statusDoc = GetStatusInternal( order, settings );
      string statusStatus = statusDoc.XPathSelectElement( "//ncresponse" ).Attribute( "STATUS" ).Value;

      if ( !statusStatus.Equals( "91" ) ) {
        XDocument doc = MakeApiRequest( "REFUND", "RFS", order, settings );
        string status = doc.XPathSelectElement( "//ncresponse" ).Attribute( "STATUS" ).Value;

        if ( status.Equals( "7" ) || status.Equals( "71" ) || status.Equals( "8" ) || status.Equals( "81" ) )
          return new APIInfo( doc.XPathSelectElement( "//ncresponse" ).Attribute( "PAYID" ).Value, PaymentStatus.Refunded );
        else
          errorMessage = "Tea Commerce - Ogone - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_Ogone_error" ), doc.XPathSelectElement( "//ncresponse" ).Attribute( "NCERROR" ).Value, doc.XPathSelectElement( "//ncresponse" ).Attribute( "NCERRORPLUS" ).Value );
      } else
        errorMessage = "Tea Commerce - Ogone - " + umbraco.ui.Text( "teaCommerce", "paymentProvider_Ogone_cantRefundStatus91" );

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    public override APIInfo CancelPayment( Order order, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;
      Dictionary<string, string> inputFields = new Dictionary<string, string>();

      XDocument doc = MakeApiRequest( "CANCEL", "DES", order, settings );
      string status = doc.XPathSelectElement( "//ncresponse" ).Attribute( "STATUS" ).Value;

      if ( status.Equals( "6" ) || status.Equals( "61" ) )
        return new APIInfo( doc.XPathSelectElement( "//ncresponse" ).Attribute( "PAYID" ).Value, PaymentStatus.Cancelled );
      else
        errorMessage = "Tea Commerce - Ogone - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_Ogone_error" ), doc.XPathSelectElement( "//ncresponse" ).Attribute( "NCERROR" ).Value, doc.XPathSelectElement( "//ncresponse" ).Attribute( "NCERRORPLUS" ).Value );

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    protected XDocument GetStatusInternal( Order order, Dictionary<string, string> settings ) {
      return MakeApiRequest( "STATUS", string.Empty, order, settings );
    }

    protected XDocument MakeApiRequest( string methodName, string operation, Order order, Dictionary<string, string> settings ) {
      Dictionary<string, string> inputFields = new Dictionary<string, string>();

      inputFields[ "PSPID" ] = settings[ "PSPID" ];
      inputFields[ "USERID" ] = settings[ "APIUSERID" ];
      inputFields[ "PSWD" ] = settings[ "APIPASSWORD" ];
      inputFields[ "PAYID" ] = order.TransactionPaymentTransactionId;
      if ( !methodName.Equals( "STATUS" ) ) {
        inputFields[ "AMOUNT" ] = ( order.TotalPrice * 100M ).ToString( "0", CultureInfo.InvariantCulture );
        inputFields[ "OPERATION" ] = operation;
      }

      string strToHash = inputFields.OrderBy( i => i.Key ).Select( i => i.Key.ToUpperInvariant() + "=" + i.Value + settings[ "SHAINPASSPHRASE" ] ).Join( "" );
      inputFields[ "SHASIGN" ] = CryptoProvider.ConvertToHexString( new SHA512Managed().ComputeHash( Encoding.UTF8.GetBytes( strToHash ) ) );

      string response = MakePostRequest( GetMethodUrl( methodName, settings ), inputFields );
      return XDocument.Parse( response, LoadOptions.PreserveWhitespace );
    }

    protected string GetMethodUrl( string type, Dictionary<string, string> settings ) {
      string environment = settings[ "TESTMODE" ].Equals( "0" ) ? "prod" : "test";

      switch ( type.ToUpperInvariant() ) {
        case "GENERATEFORM":
          return "https://secure.ogone.com/ncol/" + environment + "/orderstandard_utf8.asp";
        case "STATUS":
          return "https://secure.ogone.com/ncol/" + environment + "/querydirect.asp";
        case "CAPTURE":
        case "CANCEL":
        case "REFUND":
          return "https://secure.ogone.com/ncol/" + environment + "/maintenancedirect.asp";
      }

      return string.Empty;
    }

  }
}
