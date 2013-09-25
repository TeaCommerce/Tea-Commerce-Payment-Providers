using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web;
using System.Web.Hosting;
using System.Xml.Linq;
using System.Xml.XPath;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;
using TeaCommerce.PaymentProviders.PayExService;
using TeaCommerce.Api.Infrastructure.Logging;

namespace TeaCommerce.PaymentProviders {

  [PaymentProvider( "PayEx" )]
  public class PayEx : APaymentProvider {

    public override string DocumentationLink { get { return "http://anders.burla.dk/umbraco/tea-commerce/using-payex-with-tea-commerce/"; } }

    public override bool SupportsRetrievalOfPaymentStatus { get { return true; } }
    public override bool SupportsCapturingOfPayment { get { return true; } }
    public override bool SupportsRefundOfPayment { get { return true; } }
    public override bool SupportsCancellationOfPayment { get { return true; } }

    public override bool FinalizeAtContinueUrl { get { return true; } }

    public override IDictionary<string, string> DefaultSettings {
      get {
        Dictionary<string, string> defaultSettings = new Dictionary<string, string>();
        defaultSettings[ "accountNumber" ] = string.Empty;
        defaultSettings[ "clientLanguage" ] = "en-US";
        defaultSettings[ "returnURL" ] = string.Empty;
        defaultSettings[ "cancelUrl" ] = string.Empty;
        defaultSettings[ "purchaseOperation" ] = "AUTHORIZATION";
        defaultSettings[ "encryptionKey" ] = string.Empty;
        defaultSettings[ "testing" ] = "1";
        return defaultSettings;
      }
    }

    public override PaymentHtmlForm GenerateHtmlForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "accountNumber", "settings" );
      settings.MustContainKey( "purchaseOperation", "settings" );
      settings.MustContainKey( "encryptionKey", "settings" );

      PaymentHtmlForm htmlForm = new PaymentHtmlForm();

      long accountNumber = long.Parse( settings[ "accountNumber" ] );
      string purchaseOperation = settings[ "purchaseOperation" ];
      int price = (int)Math.Round( order.TotalPrice.WithVat * 100M, 0 );
      string priceArgList = string.Empty;

      //Check that the Iso code exists
      Currency currency = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId );
      if ( !Iso4217CurrencyCodes.ContainsKey( currency.IsoCode ) ) {
        throw new Exception( "You must specify an ISO 4217 currency code for the " + currency.Name + " currency" );
      }

      int vat = (int)Math.Round( order.VatRate * 100M * 100M, 0 );
      string orderId = order.CartNumber;
      string productNumber = string.Join( ",", order.OrderLines.Select( ol => ol.Sku ) );
      string description = string.Join( ",", order.OrderLines.Select( ol => ol.Name ) );
      string clientIpAddress = HttpContext.Current.Request.UserHostAddress;
      string clientIdentifier = string.Empty;
      string additionalValues = string.Empty;
      string externalId = string.Empty;
      string returnUrl = teaCommerceContinueUrl;
      const string view = "CREDITCARD";
      string agreementRef = string.Empty;
      string cancelUrl = teaCommerceCancelUrl;
      string clientLanguage = string.Empty;

      string md5Hash = GenerateMD5Hash( accountNumber.ToString( CultureInfo.InvariantCulture ) + purchaseOperation + price.ToString( CultureInfo.InvariantCulture ) + priceArgList + currency.IsoCode + vat.ToString( CultureInfo.InvariantCulture ) + orderId + productNumber + description + clientIpAddress + clientIdentifier + additionalValues + externalId + returnUrl + view + agreementRef + cancelUrl + clientLanguage + settings[ "encryptionKey" ] );

      string xmlReturn = GetPayExServiceClient( settings ).Initialize7( accountNumber, purchaseOperation, price, priceArgList, currency.IsoCode, vat, orderId, productNumber, description, clientIpAddress, clientIdentifier, additionalValues, externalId, returnUrl, view, agreementRef, cancelUrl, clientLanguage, md5Hash );

      XDocument xmlDoc = XDocument.Parse( xmlReturn, LoadOptions.PreserveWhitespace );
      string errorCode = xmlDoc.XPathSelectElement( "//status/errorCode" ).Value;

      if ( errorCode.Equals( "OK" ) ) {
        order.Properties.AddOrUpdate( new CustomProperty( "orderRef", xmlDoc.XPathSelectElement( "//orderRef" ).Value ) { ServerSideOnly = true } );
        order.Save();
        htmlForm.Action = xmlDoc.XPathSelectElement( "//redirectUrl" ).Value;
      } else {
        htmlForm.Action = teaCommerceCancelUrl;
        LoggingService.Instance.Log( "PayEx(" + order.CartNumber + ") - Generate html form error - error code: " + xmlDoc.XPathSelectElement( "//status/description" ).Value );
      }

      return htmlForm;
    }

    public override string GetContinueUrl( Order order, IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "returnURL", "settings" );

      return settings[ "returnURL" ];
    }

    public override string GetCancelUrl( Order order, IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "cancelUrl", "settings" );

      return settings[ "cancelUrl" ];
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      CallbackInfo callbackInfo = null;

      try {
        order.MustNotBeNull( "order" );
        request.MustNotBeNull( "request" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "accountNumber", "settings" );
        settings.MustContainKey( "encryptionKey", "settings" );

        long accountNumber = long.Parse( settings[ "accountNumber" ] );
        string orderRef = order.Properties.First( p => p.Alias.Equals( "orderRef" ) ).Value;
        string md5Hash = GenerateMD5Hash( accountNumber + orderRef + settings[ "encryptionKey" ] );

        string xmlReturn = GetPayExServiceClient( settings ).Complete( accountNumber, orderRef, md5Hash );

        //Write data when testing
        if ( settings.ContainsKey( "testing" ) && settings[ "testing" ] == "1" ) {
          using ( StreamWriter writer = new StreamWriter( File.Create( HostingEnvironment.MapPath( "~/payex-callback-data.txt" ) ) ) ) {
            writer.WriteLine( "Xml return:" );
            writer.WriteLine( xmlReturn );
            writer.Flush();
          }
        }

        XDocument xmlDoc = XDocument.Parse( xmlReturn, LoadOptions.PreserveWhitespace );
        string transactionStatus = xmlDoc.XPathSelectElement( "//transactionStatus" ).Value;
        string errorCode = xmlDoc.XPathSelectElement( "//status/errorCode" ).Value;

        //0 = Sale | 3 = Authorize
        if ( errorCode == "OK" && ( transactionStatus == "0" || transactionStatus == "3" ) && !bool.Parse( xmlDoc.XPathSelectElement( "//alreadyCompleted" ).Value ) ) {
          decimal amount = decimal.Parse( xmlDoc.XPathSelectElement( "//amount" ).Value, CultureInfo.InvariantCulture );
          string transactionNumber = xmlDoc.XPathSelectElement( "//transactionNumber" ).Value;
          PaymentState paymentState = transactionStatus.Equals( "3" ) ? PaymentState.Authorized : PaymentState.Captured;
          string paymentMethod = xmlDoc.XPathSelectElement( "//paymentMethod" ).Value;
          string maskedNumber = xmlDoc.XPathSelectElement( "//maskedNumber" ).Value;

          callbackInfo = new CallbackInfo( amount / 100M, transactionNumber, paymentState, paymentMethod, maskedNumber );
        } else {
          LoggingService.Instance.Log( "PayEx(" + order.CartNumber + ") - Callback failed - error code: " + errorCode + " - Description: " + xmlDoc.XPathSelectElement( "//status/description" ).Value );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "PayEx(" + order.CartNumber + ") - Process callback" );
      }

      return callbackInfo;
    }

    public override ApiInfo GetStatus( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "accountNumber", "settings" );
        settings.MustContainKey( "encryptionKey", "settings" );

        long accountNumber = long.Parse( settings[ "accountNumber" ] );
        int transactionNumber = int.Parse( order.TransactionInformation.TransactionId );

        string md5Hash = GenerateMD5Hash( accountNumber.ToString( CultureInfo.InvariantCulture ) + transactionNumber.ToString( CultureInfo.InvariantCulture ) + settings[ "encryptionKey" ] );

        XDocument xmlDoc = XDocument.Parse( GetPayExServiceClient( settings ).GetTransactionDetails2( accountNumber, transactionNumber, md5Hash ), LoadOptions.PreserveWhitespace );
        string errorCode = xmlDoc.XPathSelectElement( "//status/errorCode" ).Value;

        if ( errorCode == "OK" ) {
          PaymentState paymentState = PaymentState.Initialized;
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

          apiInfo = new ApiInfo( xmlDoc.XPathSelectElement( "//transactionNumber" ).Value, paymentState );
        } else {
          LoggingService.Instance.Log( "PayEx(" + order.OrderNumber + ") - Error making API request - Error code: " + errorCode + " - Description: " + xmlDoc.XPathSelectElement( "//status/description" ).Value );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "PayEx(" + order.OrderNumber + ") - Get status" );
      }

      return apiInfo;
    }

    public override ApiInfo CapturePayment( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "accountNumber", "settings" );
        settings.MustContainKey( "encryptionKey", "settings" );

        long accountNumber = long.Parse( settings[ "accountNumber" ] );
        int transactionNumber = int.Parse( order.TransactionInformation.TransactionId );
        int amount = (int)Math.Round( order.TransactionInformation.AmountAuthorized.Value * 100M, 0 );
        string orderId = order.CartNumber;
        int vatAmount = (int)Math.Round( order.VatRate * 100M * 100M, 0 );
        string additionalValues = string.Empty;

        string md5Hash = GenerateMD5Hash( accountNumber.ToString( CultureInfo.InvariantCulture ) + transactionNumber.ToString( CultureInfo.InvariantCulture ) + amount.ToString( CultureInfo.InvariantCulture ) + orderId + vatAmount.ToString( CultureInfo.InvariantCulture ) + additionalValues + settings[ "encryptionKey" ] );

        XDocument xmlDoc = XDocument.Parse( GetPayExServiceClient( settings ).Capture4( accountNumber, transactionNumber, amount, orderId, vatAmount, additionalValues, md5Hash ), LoadOptions.PreserveWhitespace );
        string errorCode = xmlDoc.XPathSelectElement( "//status/errorCode" ).Value;

        if ( errorCode == "OK" && xmlDoc.XPathSelectElement( "//transactionStatus" ).Value == "6" ) {
          apiInfo = new ApiInfo( xmlDoc.XPathSelectElement( "//transactionNumber" ).Value, PaymentState.Captured );
        } else {
          LoggingService.Instance.Log( "PayEx(" + order.OrderNumber + ") - Error making API request - Error code: " + errorCode + " - Description: " + xmlDoc.XPathSelectElement( "//status/description" ).Value );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "PayEx(" + order.OrderNumber + ") - Capture payment" );
      }

      return apiInfo;
    }

    public override ApiInfo RefundPayment( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "accountNumber", "settings" );
        settings.MustContainKey( "encryptionKey", "settings" );

        long accountNumber = long.Parse( settings[ "accountNumber" ] );
        int transactionNumber = int.Parse( order.TransactionInformation.TransactionId );
        int amount = (int)Math.Round( order.TransactionInformation.AmountAuthorized.Value * 100M, 0 );
        string orderId = order.CartNumber;
        int vatAmount = (int)Math.Round( order.VatRate * 100M, 0 );
        string additionalValues = string.Empty;

        string md5Hash = GenerateMD5Hash( accountNumber.ToString( CultureInfo.InvariantCulture ) + transactionNumber.ToString( CultureInfo.InvariantCulture ) + amount.ToString( CultureInfo.InvariantCulture ) + orderId + vatAmount.ToString( CultureInfo.InvariantCulture ) + additionalValues + settings[ "encryptionKey" ] );

        XDocument xmlDoc = XDocument.Parse( GetPayExServiceClient( settings ).Credit4( accountNumber, transactionNumber, amount, orderId, vatAmount, additionalValues, md5Hash ), LoadOptions.PreserveWhitespace );
        string errorCode = xmlDoc.XPathSelectElement( "//status/errorCode" ).Value;

        if ( errorCode == "OK" && xmlDoc.XPathSelectElement( "//transactionStatus" ).Value == "2" ) {
          apiInfo = new ApiInfo( xmlDoc.XPathSelectElement( "//transactionNumber" ).Value, PaymentState.Refunded );
        } else {
          LoggingService.Instance.Log( "PayEx(" + order.OrderNumber + ") - Error making API request - Error code: " + errorCode + " - Description: " + xmlDoc.XPathSelectElement( "//status/description" ).Value );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "PayEx(" + order.OrderNumber + ") - Refund payment" );
      }

      return apiInfo;
    }

    public override ApiInfo CancelPayment( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "accountNumber", "settings" );
        settings.MustContainKey( "encryptionKey", "settings" );

        long accountNumber = long.Parse( settings[ "accountNumber" ] );
        int transactionNumber = int.Parse( order.TransactionInformation.TransactionId );

        string md5Hash = GenerateMD5Hash( accountNumber.ToString( CultureInfo.InvariantCulture ) + transactionNumber.ToString( CultureInfo.InvariantCulture ) + settings[ "encryptionKey" ] );

        XDocument xmlDoc = XDocument.Parse( GetPayExServiceClient( settings ).Cancel2( accountNumber, transactionNumber, md5Hash ), LoadOptions.PreserveWhitespace );
        string errorCode = xmlDoc.XPathSelectElement( "//status/errorCode" ).Value;

        if ( errorCode == "OK" && xmlDoc.XPathSelectElement( "//transactionStatus" ).Value == "4" ) {
          apiInfo = new ApiInfo( xmlDoc.XPathSelectElement( "//transactionNumber" ).Value, PaymentState.Cancelled );
        } else {
          LoggingService.Instance.Log( "PayEx(" + order.OrderNumber + ") - Error making API request - Error code: " + errorCode + " - Description: " + xmlDoc.XPathSelectElement( "//status/description" ).Value );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "PayEx(" + order.OrderNumber + ") - Cancel payment" );
      }

      return apiInfo;
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

    #region Helper methods

    protected PxOrder GetPayExServiceClient( IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );

      PxOrder pxOrder = new PxOrder {
        Url = settings.ContainsKey( "testing" ) && settings[ "testing" ] == "1" ? "https://test-external.payex.com/pxorder/pxorder.asmx" : "https://external.payex.com/pxorder/pxorder.asmx"
      };
      return pxOrder;
    }

    #endregion

  }
}
