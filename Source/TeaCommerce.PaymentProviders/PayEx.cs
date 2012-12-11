using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web;
using System.Xml.Linq;
using System.Xml.XPath;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.PaymentProviders;
using TeaCommerce.PaymentProviders.Extensions;
using TeaCommerce.PaymentProviders.PayExService;
using TeaCommerce.Api.Infrastructure.Logging;

namespace TeaCommerce.PaymentProviders {

  [PaymentProvider( "PayEx" )]
  public class PayEx : APaymentProvider {

    protected const string apiErrorFormatString = "Error making API request - Error code: {0} - Description: {1}";

    protected bool isTesting;
    protected string formPostUrl;

    public override IDictionary<string, string> DefaultSettings {
      get {
        if ( defaultSettings == null ) {
          defaultSettings = new Dictionary<string, string>();
          defaultSettings[ "accountNumber" ] = string.Empty;
          defaultSettings[ "clientLanguage" ] = "en-US";
          defaultSettings[ "returnURL" ] = string.Empty;
          defaultSettings[ "cancelUrl" ] = string.Empty;
          defaultSettings[ "purchaseOperation" ] = "AUTHORIZATION";
          defaultSettings[ "encryptionKey" ] = string.Empty;
          defaultSettings[ "productNumberPropertyAlias" ] = "productNumber";//TODO: disse skal rettes til vores nye best practice
          defaultSettings[ "productNamePropertyAlias" ] = "productName";
          defaultSettings[ "testing" ] = "0";
        }
        return defaultSettings;
      }
    }

    public override string FormPostUrl { get { return formPostUrl; } }
    public override bool FinalizeAtContinueUrl { get { return true; } }
    public override string DocumentationLink { get { return "http://anders.burla.dk/umbraco/tea-commerce/using-payex-with-tea-commerce/"; } }

    public override IDictionary<string, string> GenerateForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, IDictionary<string, string> settings ) {
      long accountNumber = long.Parse( settings[ "accountNumber" ] );
      string purchaseOperation = settings[ "purchaseOperation" ];
      int price = (int)Math.Round( order.TotalPrice.WithVat * 100M, 0 );
      string priceArgList = string.Empty;
      string currency = order.CurrencyISOCode;
      int vat = (int)Math.Round( order.VatRate * 100M * 100M, 0 );
      string orderId = order.CartNumber;
      string productNumber = order.OrderLines.Select( ol => ol.Properties.First( p => p.Alias.Equals( settings[ "productNumberPropertyAlias" ] ) ).Value ).Join( ", " );
      string description = order.OrderLines.Select( ol => ol.Properties.First( p => p.Alias.Equals( settings[ "productNamePropertyAlias" ] ) ).Value ).Join( ", " );
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
        order.Properties.AddOrUpdate( new CustomProperty( "orderRef", xmlDoc.XPathSelectElement( "//orderRef" ).Value ) { ServerSideOnly = true } );
        formPostUrl = xmlDoc.XPathSelectElement( "//redirectUrl" ).Value;
      } else {
        formPostUrl = teaCommerceCancelUrl;
        string errorDescription = xmlDoc.XPathSelectElement( "//status/description" ).Value;
        LoggingService.Instance.Log( "Tea Commerce - PayEx - Error in GenerateForm - Error code: " + errorCode + " - Description: " + errorDescription );
      }

      return new Dictionary<string, string>();
    }

    public override string GetContinueUrl( IDictionary<string, string> settings ) {
      return settings[ "returnURL" ];
    }

    public override string GetCancelUrl( IDictionary<string, string> settings ) {
      return settings[ "cancelUrl" ];
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      long accountNumber = long.Parse( settings[ "accountNumber" ] );
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
      if ( errorCode.Equals( "OK" ) && ( transactionStatus.Equals( "0" ) || transactionStatus.Equals( "3" ) ) && !bool.Parse( xmlDoc.XPathSelectElement( "//alreadyCompleted" ).Value ) ) {
        decimal amount = decimal.Parse( xmlDoc.XPathSelectElement( "//amount" ).Value, CultureInfo.InvariantCulture );
        string transactionNumber = xmlDoc.XPathSelectElement( "//transactionNumber" ).Value;
        PaymentState paymentState = transactionStatus.Equals( "3" ) ? PaymentState.Authorized : PaymentState.Captured;
        string paymentMethod = xmlDoc.XPathSelectElement( "//paymentMethod" ).Value;
        string maskedNumber = xmlDoc.XPathSelectElement( "//maskedNumber" ).Value;

        return new CallbackInfo( amount / 100M, transactionNumber, paymentState, paymentMethod, maskedNumber );
      } else {
        string errorDescription = xmlDoc.XPathSelectElement( "//status/description" ).Value;
        errorMessage = "Tea Commerce - PayEx - Callback failed - Error code: " + errorCode + " - Description: " + errorDescription;
      }

      LoggingService.Instance.Log( errorMessage );
      return new CallbackInfo( errorMessage );
    }

    public override ApiInfo GetStatus( Order order, IDictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      long accountNumber = long.Parse( settings[ "accountNumber" ] );
      int transactionNumber = int.Parse( order.TransactionInformation.TransactionId );

      string md5Hash = GetMD5Hash( accountNumber.ToString() + transactionNumber.ToString() + settings[ "encryptionKey" ] );

      XDocument xmlDoc = XDocument.Parse( GetPayExServiceClient( settings ).GetTransactionDetails2( accountNumber, transactionNumber, md5Hash ), LoadOptions.PreserveWhitespace );
      string errorCode = xmlDoc.XPathSelectElement( "//status/errorCode" ).Value;

      if ( errorCode.Equals( "OK" ) ) {
        PaymentState paymentState = PaymentState.Initiated;
        switch ( xmlDoc.XPathSelectElement( "//transactionStatus" ).Value ) {
          case "3":
            paymentState = PaymentState.Authorized;
            break;
          case "6":
            paymentState = PaymentState.Captured;
            break;
          case "4":
            paymentState = PaymentState.Cancelled;
            break;
          case "2":
            paymentState = PaymentState.Refunded;
            break;
        }

        return new ApiInfo( xmlDoc.XPathSelectElement( "//transactionNumber" ).Value, paymentState );
      } else {
        string errorDescription = xmlDoc.XPathSelectElement( "//status/description" ).Value;
        errorMessage = "Tea Commerce - PayEx - " + string.Format( apiErrorFormatString, errorCode, errorDescription );
      }

      LoggingService.Instance.Log( errorMessage );
      return new ApiInfo( errorMessage );
    }

    public override ApiInfo CapturePayment( Order order, IDictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      long accountNumber = long.Parse( settings[ "accountNumber" ] );
      int transactionNumber = int.Parse( order.TransactionInformation.TransactionId );
      int amount = (int)Math.Round( order.TotalPrice.WithVat * 100M, 0 );
      string orderId = order.CartNumber;
      int vatAmount = (int)Math.Round( order.VatRate * 100M * 100M, 0 );
      string additionalValues = string.Empty;

      string md5Hash = GetMD5Hash( accountNumber.ToString() + transactionNumber.ToString() + amount.ToString() + orderId + vatAmount.ToString() + additionalValues + settings[ "encryptionKey" ] );

      XDocument xmlDoc = XDocument.Parse( GetPayExServiceClient( settings ).Capture4( accountNumber, transactionNumber, amount, orderId, vatAmount, additionalValues, md5Hash ), LoadOptions.PreserveWhitespace );
      string errorCode = xmlDoc.XPathSelectElement( "//status/errorCode" ).Value;

      if ( errorCode.Equals( "OK" ) && xmlDoc.XPathSelectElement( "//transactionStatus" ).Value.Equals( "6" ) ) {
        return new ApiInfo( xmlDoc.XPathSelectElement( "//transactionNumber" ).Value, PaymentState.Captured );
      } else {
        string errorDescription = xmlDoc.XPathSelectElement( "//status/description" ).Value;
        errorMessage = "Tea Commerce - PayEx - " + string.Format( apiErrorFormatString, errorCode, errorDescription );
      }

      LoggingService.Instance.Log( errorMessage );
      return new ApiInfo( errorMessage );
    }

    public override ApiInfo RefundPayment( Order order, IDictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      long accountNumber = long.Parse( settings[ "accountNumber" ] );
      int transactionNumber = int.Parse( order.TransactionInformation.TransactionId );
      int amount = (int)Math.Round( order.TotalPrice.WithVat * 100M, 0 );
      string orderId = order.CartNumber;
      int vatAmount = (int)Math.Round( order.VatRate * 100M, 0 );
      string additionalValues = string.Empty;

      string md5Hash = GetMD5Hash( accountNumber.ToString() + transactionNumber.ToString() + amount.ToString() + orderId + vatAmount.ToString() + additionalValues + settings[ "encryptionKey" ] );

      XDocument xmlDoc = XDocument.Parse( GetPayExServiceClient( settings ).Credit4( accountNumber, transactionNumber, amount, orderId, vatAmount, additionalValues, md5Hash ), LoadOptions.PreserveWhitespace );
      string errorCode = xmlDoc.XPathSelectElement( "//status/errorCode" ).Value;

      if ( errorCode.Equals( "OK" ) && xmlDoc.XPathSelectElement( "//transactionStatus" ).Value.Equals( "2" ) ) {
        return new ApiInfo( xmlDoc.XPathSelectElement( "//transactionNumber" ).Value, PaymentState.Refunded );
      } else {
        string errorDescription = xmlDoc.XPathSelectElement( "//status/description" ).Value;
        errorMessage = "Tea Commerce - PayEx - " + string.Format( apiErrorFormatString, errorCode, errorDescription );
      }

      LoggingService.Instance.Log( errorMessage );
      return new ApiInfo( errorMessage );
    }

    public override ApiInfo CancelPayment( Order order, IDictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      long accountNumber = long.Parse( settings[ "accountNumber" ] );
      int transactionNumber = int.Parse( order.TransactionInformation.TransactionId );

      string md5Hash = GetMD5Hash( accountNumber.ToString() + transactionNumber.ToString() + settings[ "encryptionKey" ] );

      XDocument xmlDoc = XDocument.Parse( GetPayExServiceClient( settings ).Cancel2( accountNumber, transactionNumber, md5Hash ), LoadOptions.PreserveWhitespace );
      string errorCode = xmlDoc.XPathSelectElement( "//status/errorCode" ).Value;

      if ( errorCode.Equals( "OK" ) && xmlDoc.XPathSelectElement( "//transactionStatus" ).Value.Equals( "4" ) ) {
        return new ApiInfo( xmlDoc.XPathSelectElement( "//transactionNumber" ).Value, PaymentState.Cancelled );
      } else {
        string errorDescription = xmlDoc.XPathSelectElement( "//status/description" ).Value;
        errorMessage = "Tea Commerce - PayEx - " + string.Format( apiErrorFormatString, errorCode, errorDescription );
      }

      LoggingService.Instance.Log( errorMessage );
      return new ApiInfo( errorMessage );
    }

    public override string GetLocalizedSettingsKey( string settingsKey, CultureInfo culture ) {
      switch ( settingsKey ) {
        case "clientLanguage":
          return settingsKey + "<br/><small>e.g. en-US or da-DK</small>";
        case "returnURL":
          return settingsKey + "<br/><small>e.g. /continue/</small>";
        case "cancelUrl":
          return settingsKey + "<br/><small>e.g. /cancel/</small>";
        case "purchaseOperation":
          return settingsKey + "<br/><small>e.g. SALE or AUTHORIZATION</small>";
        case "testing":
          return settingsKey + "<br/><small>1 = true; 0 = false</small>";
        default:
          return base.GetLocalizedSettingsKey( settingsKey, culture );
      }
    }

    protected PxOrder GetPayExServiceClient( IDictionary<string, string> settings ) {
      PxOrder pxOrder = new PxOrder();
      pxOrder.Url = settings[ "testing" ] != "1" ? "https://external.payex.com/pxorder/pxorder.asmx" : "https://test-external.payex.com/pxorder/pxorder.asmx";
      return pxOrder;
    }

  }
}
