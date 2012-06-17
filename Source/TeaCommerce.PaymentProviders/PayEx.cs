using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;
using System.Xml.Linq;
using System.Xml.XPath;
using TeaCommerce.Data.Payment;
using TeaCommerce.PaymentProviders.PayExService;
using TeaCommerce.Data;
using umbraco.BusinessLogic;
using System.Globalization;
using System.IO;


namespace TeaCommerce.PaymentProviders {
  public class PayEx : APaymentProvider {

    protected bool isTesting;
    protected string formPostUrl;

    public override Dictionary<string, string> DefaultSettings {
      get {
        if ( defaultSettings == null ) {
          defaultSettings = new Dictionary<string, string>();
          defaultSettings[ "accountNumber" ] = string.Empty;
          defaultSettings[ "clientLanguage" ] = "en-US";
          defaultSettings[ "returnURL" ] = string.Empty;
          defaultSettings[ "cancelUrl" ] = string.Empty;
          defaultSettings[ "purchaseOperation" ] = "AUTHORIZATION";
          defaultSettings[ "encryptionKey" ] = string.Empty;
          defaultSettings[ "productNumberPropertyAlias" ] = "productNumber";
          defaultSettings[ "productNamePropertyAlias" ] = "productName";
          defaultSettings[ "testing" ] = "0";
        }
        return defaultSettings;
      }
    }

    public override string FormPostUrl { get { return formPostUrl; } }
    public override bool FinalizeAtContinueUrl { get { return true; } }
    public override string DocumentationLink { get { return "http://anders.burla.dk/umbraco/tea-commerce/using-payex-with-tea-commerce/"; } }

    public override Dictionary<string, string> GenerateForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, Dictionary<string, string> settings ) {
      long accountNumber = settings[ "accountNumber" ].ParseToLong( 0 );
      string purchaseOperation = settings[ "purchaseOperation" ];
      int price = (int)Math.Round( order.TotalPrice * 100M, 0 );
      string priceArgList = string.Empty;
      string currency = order.CurrencyISOCode;
      int vat = (int)Math.Round( order.VAT * 100M * 100M, 0 );
      string orderId = order.Name;
      string productNumber = order.OrderLines.Select( ol => ol.Properties.First( p => p.Alias.Equals( settings[ "productNumberPropertyAlias" ] ) && ( p.UmbracoLanguageId == order.UmbracoLanguageId || p.UmbracoLanguageId == 0 ) ).Value ).Join( ", " );
      string description = order.OrderLines.Select( ol => ol.Properties.First( p => p.Alias.Equals( settings[ "productNamePropertyAlias" ] ) && ( p.UmbracoLanguageId == order.UmbracoLanguageId || p.UmbracoLanguageId == 0 ) ).Value ).Join( ", " );
      string clientIPAddress = HttpContext.Current.Request.UserHostAddress;
      string clientIdentifier = string.Empty;
      string additionalValues = string.Empty;
      string externalID = string.Empty;
      string returnUrl = teaCommerceContinueUrl;
      string view = "CREDITCARD";
      string agreementRef = string.Empty;
      string cancelUrl = teaCommerceCancelUrl;
      string clientLanguage = string.Empty;

      string md5Hash = GetMD5Hash( accountNumber.ToString() + purchaseOperation + price.ToString() + priceArgList + currency + vat.ToString() + orderId + productNumber + description + clientIPAddress + clientIdentifier + additionalValues + externalID + returnUrl + view + agreementRef + cancelUrl + clientLanguage + settings[ "encryptionKey" ] );

      string xmlReturn = GetPayExServiceClient( settings ).Initialize7( accountNumber, purchaseOperation, price, priceArgList, currency, vat, orderId, productNumber, description, clientIPAddress, clientIdentifier, additionalValues, externalID, returnUrl, view, agreementRef, cancelUrl, clientLanguage, md5Hash );
      //using ( StreamWriter writer = new StreamWriter( File.Create( HttpContext.Current.Server.MapPath( "~/payExTestGenerateForm.txt" ) ) ) ) {
      //  writer.WriteLine( "Xml return:" );
      //  writer.WriteLine( xmlReturn );
      //  writer.Flush();
      //}

      XDocument xmlDoc = XDocument.Parse( xmlReturn, LoadOptions.PreserveWhitespace );
      string errorCode = xmlDoc.XPathSelectElement( "//status/errorCode" ).Value;

      if ( errorCode.Equals( "OK" ) ) {
        order.AddProperty( new OrderProperty( "orderRef", xmlDoc.XPathSelectElement( "//orderRef" ).Value, true ) );
        formPostUrl = xmlDoc.XPathSelectElement( "//redirectUrl" ).Value;
      } else {
        formPostUrl = teaCommerceCancelUrl;
        string errorDescription = xmlDoc.XPathSelectElement( "//status/description" ).Value;
        Log.Add( LogTypes.Error, -1, "Tea Commerce - PayEx - Error in GenerateForm - Error code: " + errorCode + " - Description: " + errorDescription );
      }

      return new Dictionary<string, string>();
    }

    public override string GetContinueUrl( Dictionary<string, string> settings ) {
      return settings[ "returnURL" ];
    }

    public override string GetCancelUrl( Dictionary<string, string> settings ) {
      return settings[ "cancelUrl" ];
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      long accountNumber = settings[ "accountNumber" ].ParseToLong( 0 );
      string orderRef = order.Properties.First( p => p.Alias.Equals( "orderRef" ) ).Value;
      string md5Hash = GetMD5Hash( accountNumber + orderRef + settings[ "encryptionKey" ] );

      string xmlReturn = GetPayExServiceClient( settings ).Complete( accountNumber, orderRef, md5Hash );

      //using ( StreamWriter writer = new StreamWriter( File.Create( HttpContext.Current.Server.MapPath( "~/payExTestCallback.txt" ) ) ) ) {
      //  writer.WriteLine( "Xml return:" );
      //  writer.WriteLine( xmlReturn );
      //  writer.Flush();
      //}

      XDocument xmlDoc = XDocument.Parse( xmlReturn, LoadOptions.PreserveWhitespace );
      string transactionStatus = xmlDoc.XPathSelectElement( "//transactionStatus" ).Value;
      string errorCode = xmlDoc.XPathSelectElement( "//status/errorCode" ).Value;

      //0 = Sale | 3 = Authorize
      if ( errorCode.Equals( "OK" ) && ( transactionStatus.Equals( "0" ) || transactionStatus.Equals( "3" ) ) && !xmlDoc.XPathSelectElement( "//alreadyCompleted" ).Value.ParseToBool( true ) ) {
        string orderName = xmlDoc.XPathSelectElement( "//orderId" ).Value;
        decimal amount = xmlDoc.XPathSelectElement( "//amount" ).Value.ParseToDecimal( CultureInfo.InvariantCulture, 0 );
        string transactionNumber = xmlDoc.XPathSelectElement( "//transactionNumber" ).Value;
        PaymentStatus paymentStatus = transactionStatus.Equals( "3" ) ? PaymentStatus.Authorized : PaymentStatus.Captured;
        string paymentMethod = xmlDoc.XPathSelectElement( "//paymentMethod" ).Value;
        string maskedNumber = xmlDoc.XPathSelectElement( "//maskedNumber" ).Value;

        return new CallbackInfo( orderName, amount / 100M, transactionNumber, paymentStatus, paymentMethod, maskedNumber );
      } else {
        string errorDescription = xmlDoc.XPathSelectElement( "//status/description" ).Value;
        errorMessage = "Tea Commerce - PayEx - Callback failed - Error code: " + errorCode + " - Description: " + errorDescription;
      }

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new CallbackInfo( errorMessage );
    }

    public override APIInfo GetStatus( Order order, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      long accountNumber = settings[ "accountNumber" ].ParseToLong( 0 );
      int transactionNumber = order.TransactionPaymentTransactionId.ParseToInt( 0 );

      string md5Hash = GetMD5Hash( accountNumber.ToString() + transactionNumber.ToString() + settings[ "encryptionKey" ] );

      XDocument xmlDoc = XDocument.Parse( GetPayExServiceClient( settings ).GetTransactionDetails2( accountNumber, transactionNumber, md5Hash ), LoadOptions.PreserveWhitespace );
      string errorCode = xmlDoc.XPathSelectElement( "//status/errorCode" ).Value;

      if ( errorCode.Equals( "OK" ) ) {
        PaymentStatus paymentStatus = PaymentStatus.Initial;
        switch ( xmlDoc.XPathSelectElement( "//transactionStatus" ).Value ) {
          case "3":
            paymentStatus = PaymentStatus.Authorized;
            break;
          case "6":
            paymentStatus = PaymentStatus.Captured;
            break;
          case "4":
            paymentStatus = PaymentStatus.Cancelled;
            break;
          case "2":
            paymentStatus = PaymentStatus.Refunded;
            break;
        }

        return new APIInfo( xmlDoc.XPathSelectElement( "//transactionNumber" ).Value, paymentStatus );
      } else {
        string errorDescription = xmlDoc.XPathSelectElement( "//status/description" ).Value;
        errorMessage = "Tea Commerce - PayEx - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_PayEx_error" ), errorCode, errorDescription );
      }

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    public override APIInfo CapturePayment( Order order, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      long accountNumber = settings[ "accountNumber" ].ParseToLong( 0 );
      int transactionNumber = order.TransactionPaymentTransactionId.ParseToInt( 0 );
      int amount = (int)Math.Round( order.TotalPrice * 100M, 0 );
      string orderId = order.Name;
      int vatAmount = (int)Math.Round( order.VAT * 100M * 100M, 0 );
      string additionalValues = string.Empty;

      string md5Hash = GetMD5Hash( accountNumber.ToString() + transactionNumber.ToString() + amount.ToString() + orderId + vatAmount.ToString() + additionalValues + settings[ "encryptionKey" ] );

      XDocument xmlDoc = XDocument.Parse( GetPayExServiceClient( settings ).Capture4( accountNumber, transactionNumber, amount, orderId, vatAmount, additionalValues, md5Hash ), LoadOptions.PreserveWhitespace );
      string errorCode = xmlDoc.XPathSelectElement( "//status/errorCode" ).Value;

      if ( errorCode.Equals( "OK" ) && xmlDoc.XPathSelectElement( "//transactionStatus" ).Value.Equals( "6" ) ) {
        return new APIInfo( xmlDoc.XPathSelectElement( "//transactionNumber" ).Value, PaymentStatus.Captured );
      } else {
        string errorDescription = xmlDoc.XPathSelectElement( "//status/description" ).Value;
        errorMessage = "Tea Commerce - PayEx - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_PayEx_error" ), errorCode, errorDescription );
      }

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    public override APIInfo RefundPayment( Order order, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      long accountNumber = settings[ "accountNumber" ].ParseToLong( 0 );
      int transactionNumber = order.TransactionPaymentTransactionId.ParseToInt( 0 );
      int amount = (int)Math.Round( order.TotalPrice * 100M, 0 );
      string orderId = order.Name;
      int vatAmount = (int)Math.Round( order.VAT * 100M, 0 );
      string additionalValues = string.Empty;

      string md5Hash = GetMD5Hash( accountNumber.ToString() + transactionNumber.ToString() + amount.ToString() + orderId + vatAmount.ToString() + additionalValues + settings[ "encryptionKey" ] );

      XDocument xmlDoc = XDocument.Parse( GetPayExServiceClient( settings ).Credit4( accountNumber, transactionNumber, amount, orderId, vatAmount, additionalValues, md5Hash ), LoadOptions.PreserveWhitespace );
      string errorCode = xmlDoc.XPathSelectElement( "//status/errorCode" ).Value;

      if ( errorCode.Equals( "OK" ) && xmlDoc.XPathSelectElement( "//transactionStatus" ).Value.Equals( "2" ) ) {
        return new APIInfo( xmlDoc.XPathSelectElement( "//transactionNumber" ).Value, PaymentStatus.Refunded );
      } else {
        string errorDescription = xmlDoc.XPathSelectElement( "//status/description" ).Value;
        errorMessage = "Tea Commerce - PayEx - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_PayEx_error" ), errorCode, errorDescription );
      }

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    public override APIInfo CancelPayment( Order order, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      long accountNumber = settings[ "accountNumber" ].ParseToLong( 0 );
      int transactionNumber = order.TransactionPaymentTransactionId.ParseToInt( 0 );

      string md5Hash = GetMD5Hash( accountNumber.ToString() + transactionNumber.ToString() + settings[ "encryptionKey" ] );

      XDocument xmlDoc = XDocument.Parse( GetPayExServiceClient( settings ).Cancel2( accountNumber, transactionNumber, md5Hash ), LoadOptions.PreserveWhitespace );
      string errorCode = xmlDoc.XPathSelectElement( "//status/errorCode" ).Value;

      if ( errorCode.Equals( "OK" ) && xmlDoc.XPathSelectElement( "//transactionStatus" ).Value.Equals( "4" ) ) {
        return new APIInfo( xmlDoc.XPathSelectElement( "//transactionNumber" ).Value, PaymentStatus.Cancelled );
      } else {
        string errorDescription = xmlDoc.XPathSelectElement( "//status/description" ).Value;
        errorMessage = "Tea Commerce - PayEx - " + string.Format( umbraco.ui.Text( "teaCommerce", "paymentProvider_PayEx_error" ), errorCode, errorDescription );
      }

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    protected PxOrder GetPayExServiceClient( Dictionary<string, string> settings ) {
      PxOrder pxOrder = new PxOrder();
      pxOrder.Url = !settings[ "testing" ].ParseToBool( false ) ? "https://external.payex.com/pxorder/pxorder.asmx" : "https://test-external.payex.com/pxorder/pxorder.asmx";
      return pxOrder;
    }

  }
}
