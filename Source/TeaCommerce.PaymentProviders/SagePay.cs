using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.PaymentProviders;
using TeaCommerce.PaymentProviders.Extensions;
using TeaCommerce.Api.Infrastructure.Logging;

namespace TeaCommerce.PaymentProviders {

  public class SagePay : APaymentProvider {

    protected const string apiErrorFormatString = "Error making API request: {0}";

    protected const string defaultParameterValue = "No value";
    protected string formPostUrl;

    public override string FormPostUrl { get { return formPostUrl; } }
    public override bool SupportsRetrievalOfPaymentStatus { get { return false; } }

    public override IDictionary<string, string> DefaultSettings {
      get {
        if ( defaultSettings == null ) {
          defaultSettings = new Dictionary<string, string>();
          defaultSettings[ "Vendor" ] = string.Empty;
          defaultSettings[ "SuccessURL" ] = string.Empty;
          defaultSettings[ "FailureURL" ] = string.Empty;
          defaultSettings[ "TxType" ] = "AUTHENTICATE";
          defaultSettings[ "streetAddressPropertyAlias" ] = "streetAddress";
          defaultSettings[ "cityPropertyAlias" ] = "city";
          defaultSettings[ "zipCodePropertyAlias" ] = "zipCode";
          defaultSettings[ "statePropertyAlias" ] = "state";
          defaultSettings[ "Description" ] = "A description";
          defaultSettings[ "testMode" ] = "SIMULATOR";
        }
        return defaultSettings;
      }
    }

    public override string DocumentationLink { get { return "http://anders.burla.dk/umbraco/tea-commerce/using-sage-pay-with-tea-commerce/"; } }

    public override IDictionary<string, string> GenerateForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, IDictionary<string, string> settings ) {
      List<string> settingsToExclude = new string[] { "streetAddressPropertyAlias", "cityPropertyAlias", "zipCodePropertyAlias", "statePropertyAlias", "shippingFirstNamePropertyAlias", "shippingLastNamePropertyAlias", "shippingStreetAddressPropertyAlias", "shippingCityPropertyAlias", "shippingZipCodePropertyAlias", "shippingStatePropertyAlias", "testMode" }.ToList();
      Dictionary<string, string> inputFields = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      inputFields[ "VPSProtocol" ] = "2.23";

      Dictionary<string, string> cryptFields = new Dictionary<string, string>();

      #region Address properties

      OrderProperty streetAddressProperty = settings.ContainsKey( "streetAddressPropertyAlias" ) ? order.Properties.FirstOrDefault( i => i.Alias.Equals( settings[ "streetAddressPropertyAlias" ] ) ) : null;
      string streetAddress = streetAddressProperty != null && !string.IsNullOrEmpty( streetAddressProperty.Value ) ? streetAddressProperty.Value : defaultParameterValue;

      OrderProperty cityProperty = settings.ContainsKey( "cityPropertyAlias" ) ? order.Properties.FirstOrDefault( i => i.Alias.Equals( settings[ "cityPropertyAlias" ] ) ) : null;
      string city = cityProperty != null && !string.IsNullOrEmpty( cityProperty.Value ) ? cityProperty.Value : defaultParameterValue;

      OrderProperty zipCodeProperty = settings.ContainsKey( "zipCodePropertyAlias" ) ? order.Properties.FirstOrDefault( i => i.Alias.Equals( settings[ "zipCodePropertyAlias" ] ) ) : null;
      string zipCode = zipCodeProperty != null && !string.IsNullOrEmpty( zipCodeProperty.Value ) ? zipCodeProperty.Value : defaultParameterValue;

      OrderProperty stateProperty = settings.ContainsKey( "statePropertyAlias" ) ? order.Properties.FirstOrDefault( i => i.Alias.Equals( settings[ "statePropertyAlias" ] ) ) : null;
      string state = stateProperty != null && !string.IsNullOrEmpty( stateProperty.Value ) ? stateProperty.Value : string.Empty;

      OrderProperty shippingFirstNameProperty = settings.ContainsKey( "shippingFirstNamePropertyAlias" ) ? order.Properties.FirstOrDefault( i => i.Alias.Equals( settings[ "shippingFirstNamePropertyAlias" ] ) ) : null;
      string shippingFirstName = shippingFirstNameProperty != null && !string.IsNullOrEmpty( shippingFirstNameProperty.Value ) ? shippingFirstNameProperty.Value : order.PaymentInformation.FirstName;

      OrderProperty shippingLastNameProperty = settings.ContainsKey( "shippingLastNamePropertyAlias" ) ? order.Properties.FirstOrDefault( i => i.Alias.Equals( settings[ "shippingLastNamePropertyAlias" ] ) ) : null;
      string shippingLastName = shippingLastNameProperty != null && !string.IsNullOrEmpty( shippingLastNameProperty.Value ) ? shippingLastNameProperty.Value : order.PaymentInformation.LastName;

      OrderProperty shippingStreetAddressProperty = settings.ContainsKey( "shippingStreetAddressPropertyAlias" ) ? order.Properties.FirstOrDefault( i => i.Alias.Equals( settings[ "shippingStreetAddressPropertyAlias" ] ) ) : null;
      string shippingStreetAddress = shippingStreetAddressProperty != null && !string.IsNullOrEmpty( shippingStreetAddressProperty.Value ) ? shippingStreetAddressProperty.Value : streetAddress;

      OrderProperty shippingCityProperty = settings.ContainsKey( "shippingCityPropertyAlias" ) ? order.Properties.FirstOrDefault( i => i.Alias.Equals( settings[ "shippingCityPropertyAlias" ] ) ) : null;
      string shippingCity = shippingCityProperty != null && !string.IsNullOrEmpty( shippingCityProperty.Value ) ? shippingCityProperty.Value : city;

      OrderProperty shippingZipCodeProperty = settings.ContainsKey( "shippingZipCodePropertyAlias" ) ? order.Properties.FirstOrDefault( i => i.Alias.Equals( settings[ "shippingZipCodePropertyAlias" ] ) ) : null;
      string shippingZipCode = shippingZipCodeProperty != null && !string.IsNullOrEmpty( shippingZipCodeProperty.Value ) ? shippingZipCodeProperty.Value : zipCode;

      OrderProperty shippingStateProperty = settings.ContainsKey( "shippingStatePropertyAlias" ) ? order.Properties.FirstOrDefault( i => i.Alias.Equals( settings[ "shippingStatePropertyAlias" ] ) ) : null;
      string shippingState = shippingStateProperty != null && !string.IsNullOrEmpty( shippingStateProperty.Value ) ? shippingStateProperty.Value : state;

      #endregion

      inputFields[ "VendorTxCode" ] = order.CartNumber;
      inputFields[ "Amount" ] = order.TotalPrice.WithVat.ToString( "0.00", CultureInfo.InvariantCulture );
      inputFields[ "Currency" ] = order.CurrencyISOCode;
      if ( inputFields.ContainsKey( "Description" ) )
        inputFields[ "Description" ] = inputFields[ "Description" ].Truncate( 100 );
      inputFields[ "SuccessURL" ] = teaCommerceContinueUrl;
      inputFields[ "FailureURL" ] = teaCommerceCancelUrl;
      inputFields[ "NotificationURL" ] = teaCommerceCallBackUrl;
      inputFields[ "BillingSurname" ] = order.PaymentInformation.LastName.Truncate( 20 );
      inputFields[ "BillingFirstnames" ] = order.PaymentInformation.FirstName.Truncate( 20 );
      inputFields[ "BillingAddress1" ] = streetAddress.Truncate( 100 );
      inputFields[ "BillingCity" ] = city.Truncate( 40 );
      inputFields[ "BillingPostCode" ] = zipCode.Truncate( 10 );
      inputFields[ "BillingCountry" ] = order.Country.CountryCode;
      inputFields[ "DeliverySurname" ] = shippingLastName.Truncate( 20 );
      inputFields[ "DeliveryFirstnames" ] = shippingFirstName.Truncate( 20 );
      inputFields[ "DeliveryAddress1" ] = shippingStreetAddress.Truncate( 100 );
      inputFields[ "DeliveryCity" ] = shippingCity.Truncate( 40 );
      inputFields[ "DeliveryPostCode" ] = shippingZipCode.Truncate( 10 );
      inputFields[ "DeliveryCountry" ] = order.Country.CountryCode;
      inputFields[ "Apply3DSecure" ] = "2";

      if ( order.Country.CountryCode.ToUpperInvariant() == "US" ) {
        inputFields[ "BillingState" ] = state.Truncate( 2 );
        inputFields[ "DeliveryState" ] = shippingState.Truncate( 2 );
      }

      IDictionary<string, string> responseFields = GetFields( MakePostRequest( GetMethodUrl( "PURCHASE", settings ), inputFields ) );
      string status = responseFields[ "Status" ];

      if ( status.Equals( "OK" ) || status.Equals( "OK REPEATED" ) ) {
        lock ( order ) {
          order.AddProperty( new OrderProperty( "SecurityKey", responseFields[ "SecurityKey" ], true ) );
          order.AddProperty( new OrderProperty( "TeaCommerceContinueUrl", teaCommerceContinueUrl, true ) );
          order.AddProperty( new OrderProperty( "TeaCommerceCancelUrl", teaCommerceCancelUrl, true ) );
          order.Save();
        }
        formPostUrl = responseFields[ "NextURL" ];
      } else {
        formPostUrl = teaCommerceCancelUrl;
        LoggingService.Instance.Log( "Tea Commerce - SagePay - Error in GenerateForm - Status: " + status + " - Status details: " + responseFields[ "StatusDetail" ] );
      }

      return new Dictionary<string, string>();
    }

    public override string GetContinueUrl( IDictionary<string, string> settings ) {
      return settings[ "SuccessURL" ];
    }

    public override string GetCancelUrl( IDictionary<string, string> settings ) {
      return settings[ "FailureURL" ];
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      //using ( StreamWriter writer = new StreamWriter( File.Create( HttpContext.Current.Server.MapPath( "~/SagePayTestCallback.txt" ) ) ) ) {
      //  writer.WriteLine( "FORM:" );
      //  foreach ( string k in request.Form.Keys ) {
      //    writer.WriteLine( k + " : " + request.Form[ k ] );
      //  }
      //  writer.Flush();
      //}

      string errorMessage = string.Empty;

      string transaction = request.Form[ "VPSTxId" ];
      string status = request.Form[ "Status" ];
      string vendorTxCode = request.Form[ "VendorTxCode" ];
      string txAuthNo = request.Form[ "TxAuthNo" ];
      string cardType = request.Form[ "CardType" ];
      string last4Digits = request.Form[ "Last4Digits" ];

      string md5CheckValue = string.Empty;
      md5CheckValue += transaction;
      md5CheckValue += vendorTxCode;
      md5CheckValue += status;
      md5CheckValue += txAuthNo;
      md5CheckValue += settings[ "Vendor" ].ToLowerInvariant();
      md5CheckValue += HttpUtility.UrlDecode( request.Form[ "AVSCV2" ] );
      md5CheckValue += order.Properties.First( i => i.Alias.Equals( "SecurityKey" ) ).Value;
      md5CheckValue += HttpUtility.UrlDecode( request.Form[ "AddressResult" ] );
      md5CheckValue += HttpUtility.UrlDecode( request.Form[ "PostCodeResult" ] );
      md5CheckValue += HttpUtility.UrlDecode( request.Form[ "CV2Result" ] );
      md5CheckValue += request.Form[ "GiftAid" ];
      md5CheckValue += request.Form[ "3DSecureStatus" ];
      md5CheckValue += request.Form[ "CAVV" ];
      md5CheckValue += HttpUtility.UrlDecode( request.Form[ "AddressStatus" ] );
      md5CheckValue += HttpUtility.UrlDecode( request.Form[ "PayerStatus" ] );
      md5CheckValue += cardType;
      md5CheckValue += last4Digits;

      string calcedMD5Hash = GetMD5Hash( md5CheckValue ).ToUpperInvariant();
      string VPSSignature = request.Form[ "VPSSignature" ];

      if ( calcedMD5Hash.Equals( VPSSignature ) ) {

        Dictionary<string, string> inputFields = new Dictionary<string, string>();
        CallbackInfo callbackInfo = null;

        if ( status.Equals( "OK" ) || status.Equals( "AUTHENTICATED" ) || status.Equals( "REGISTERED" ) ) {
          callbackInfo = new CallbackInfo( order.TotalPrice.WithVat, transaction, !request.Form[ "TxType" ].Equals( "PAYMENT" ) ? PaymentState.Authorized : PaymentState.Captured, cardType, last4Digits );

          lock ( order ) {
            if ( status.Equals( "OK" ) )
              order.AddProperty( new OrderProperty( "TxAuthNo", txAuthNo, true ) );
            order.AddProperty( new OrderProperty( "VendorTxCode", vendorTxCode, true ) );
            order.Save();
          }

          inputFields[ "Status" ] = "OK";
          inputFields[ "RedirectURL" ] = order.Properties.First( i => i.Alias.Equals( "TeaCommerceContinueUrl" ) ).Value;
          inputFields[ "StatusDetail" ] = "OK";

        } else {
          errorMessage = "Tea Commerce - SagePay - Error  in Callback - Status: " + status + " - Status details: " + request.Form[ "StatusDetail" ];

          if ( status.Equals( "ERROR" ) )
            inputFields[ "Status" ] = "INVALID";
          else
            inputFields[ "Status" ] = "OK";

          inputFields[ "RedirectURL" ] = order.Properties.First( i => i.Alias.Equals( "TeaCommerceCancelUrl" ) ).Value;
          inputFields[ "StatusDetail" ] = "Error: " + status;
        }

        HttpContext.Current.Response.Clear();
        HttpContext.Current.Response.Write( string.Join( Environment.NewLine, inputFields.Select( i => string.Format( "{0}={1}", i.Key, i.Value ) ).ToArray() ) );

        if ( callbackInfo != null )
          return callbackInfo;

      } else
        errorMessage = string.Format( "Tea Commerce - SagePay - VPSSignature check isn't valid - Calculated signature: {0} - SagePay VPSSignature: {1}", calcedMD5Hash, VPSSignature );

      LoggingService.Instance.Log( errorMessage );
      return new CallbackInfo( errorMessage );
    }

    public override ApiInfo GetStatus( Order order, IDictionary<string, string> settings ) {
      throw new NotSupportedException();
    }

    public override ApiInfo CapturePayment( Order order, IDictionary<string, string> settings ) {
      string errorMessage = string.Empty;
      Dictionary<string, string> inputFields = new Dictionary<string, string>();

      Guid vendorTxCode = Guid.NewGuid();

      inputFields[ "VPSProtocol" ] = "2.23";
      inputFields[ "TxType" ] = "AUTHORISE";
      inputFields[ "Vendor" ] = settings[ "Vendor" ];
      inputFields[ "VendorTxCode" ] = vendorTxCode.ToString();
      inputFields[ "Amount" ] = order.TotalPrice.WithVat.ToString( "0.00", CultureInfo.InvariantCulture );
      inputFields[ "Description" ] = settings[ "Description" ].Truncate( 100 );
      inputFields[ "RelatedVPSTxId" ] = order.TransactionInformation.TransactionId;
      inputFields[ "RelatedVendorTxCode" ] = order.CartNumber;
      inputFields[ "RelatedSecurityKey" ] = order.Properties.First( i => i.Alias.Equals( "SecurityKey" ) ).Value;
      inputFields[ "ApplyAVSCV2" ] = "0";

      IDictionary<string, string> responseFields = GetFields( MakePostRequest( GetMethodUrl( "AUTHORISE", settings ), inputFields ) );

      if ( responseFields[ "Status" ].Equals( "OK" ) ) {
        lock ( order ) {
          order.AddProperty( new OrderProperty( "VendorTxCode", vendorTxCode.ToString(), true ) );
          order.AddProperty( new OrderProperty( "TxAuthNo", responseFields[ "TxAuthNo" ], true ) );
          order.AddProperty( new OrderProperty( "SecurityKey", responseFields[ "SecurityKey" ], true ) );
          order.Save();
        }

        return new ApiInfo( responseFields[ "VPSTxId" ], PaymentState.Captured );
      } else
        errorMessage = "Tea Commerce - SagePay - " + string.Format( apiErrorFormatString, responseFields[ "StatusDetail" ] );

      LoggingService.Instance.Log( errorMessage );
      return new ApiInfo( errorMessage );
    }

    public override ApiInfo RefundPayment( Order order, IDictionary<string, string> settings ) {
      string errorMessage = string.Empty;
      Dictionary<string, string> inputFields = new Dictionary<string, string>();

      Guid vendorTxCode = Guid.NewGuid();

      inputFields[ "VPSProtocol" ] = "2.23";
      inputFields[ "TxType" ] = "REFUND";
      inputFields[ "Vendor" ] = settings[ "Vendor" ];
      inputFields[ "VendorTxCode" ] = vendorTxCode.ToString();
      inputFields[ "Amount" ] = order.TotalPrice.WithVat.ToString( "0.00", CultureInfo.InvariantCulture );
      inputFields[ "Currency" ] = order.CurrencyISOCode;
      inputFields[ "Description" ] = settings[ "Description" ].Truncate( 100 );
      inputFields[ "RelatedVPSTxId" ] = order.TransactionInformation.TransactionId;
      inputFields[ "RelatedVendorTxCode" ] = order.Properties.First( i => i.Alias.Equals( "VendorTxCode" ) ).Value;
      inputFields[ "RelatedSecurityKey" ] = order.Properties.First( i => i.Alias.Equals( "SecurityKey" ) ).Value;
      inputFields[ "RelatedTxAuthNo" ] = order.Properties.First( i => i.Alias.Equals( "TxAuthNo" ) ).Value;
      inputFields[ "ApplyAVSCV2" ] = "0";

      IDictionary<string, string> responseFields = GetFields( MakePostRequest( GetMethodUrl( "REFUND", settings ), inputFields ) );

      if ( responseFields[ "Status" ].Equals( "OK" ) ) {
        lock ( order ) {
          order.AddProperty( new OrderProperty( "VendorTxCode", vendorTxCode.ToString(), true ) );
          order.AddProperty( new OrderProperty( "TxAuthNo", responseFields[ "TxAuthNo" ], true ) );
          order.Save();
        }

        return new ApiInfo( responseFields[ "VPSTxId" ], PaymentState.Refunded );
      } else
        errorMessage = "Tea Commerce - SagePay - " + string.Format( apiErrorFormatString, responseFields[ "StatusDetail" ] );

      LoggingService.Instance.Log( errorMessage );
      return new ApiInfo( errorMessage );
    }

    public override ApiInfo CancelPayment( Order order, IDictionary<string, string> settings ) {
      string errorMessage = string.Empty;
      Dictionary<string, string> inputFields = new Dictionary<string, string>();

      inputFields[ "VPSProtocol" ] = "2.23";
      inputFields[ "TxType" ] = "CANCEL";
      inputFields[ "Vendor" ] = settings[ "Vendor" ];
      inputFields[ "VendorTxCode" ] = order.CartNumber;
      inputFields[ "VPSTxId" ] = order.TransactionInformation.TransactionId;
      inputFields[ "SecurityKey" ] = order.Properties.First( i => i.Alias.Equals( "SecurityKey" ) ).Value;

      IDictionary<string, string> responseFields = GetFields( MakePostRequest( GetMethodUrl( "CANCEL", settings ), inputFields ) );

      if ( responseFields[ "Status" ].Equals( "OK" ) ) {
        return new ApiInfo( order.TransactionInformation.TransactionId, PaymentState.Cancelled );
      } else
        errorMessage = "Tea Commerce - SagePay - " + string.Format( apiErrorFormatString, responseFields[ "StatusDetail" ] );

      LoggingService.Instance.Log( errorMessage );
      return new ApiInfo( errorMessage );
    }

    public override string GetLocalizedSettingsKey( string settingsKey, CultureInfo culture ) {
      switch ( settingsKey ) {
        case "SuccessURL":
          return settingsKey + "<br/><small>e.g. /continue/</small>";
        case "FailureURL":
          return settingsKey + "<br/><small>e.g. /cancel/</small>";
        case "TxType":
          return settingsKey + "<br/><small>PAYMENT, AUTHENTICATE</small>";
        case "testMode":
          return settingsKey + "<br/><small>LIVE, TEST, SIMULATOR</small>";
        default:
          return base.GetLocalizedSettingsKey( settingsKey, culture );
      }
    }

    protected IDictionary<string, string> GetFields( string response ) {
      return response.Split( Environment.NewLine.ToCharArray(), StringSplitOptions.RemoveEmptyEntries ).ToDictionary( i => i.Substring( 0, i.IndexOf( "=" ) ), i => i.Substring( i.IndexOf( "=" ) + 1, i.Length - ( i.IndexOf( "=" ) + 1 ) ) );
    }

    protected string GetMethodUrl( string type, IDictionary<string, string> settings ) {
      switch ( settings[ "testMode" ].ToUpperInvariant() ) {
        case "LIVE":
          switch ( type.ToUpperInvariant() ) {
            case "AUTHORISE":
              return "https://live.sagepay.com/gateway/service/authorise.vsp";
            case "PURCHASE":
              return "https://live.sagepay.com/gateway/service/vspserver-register.vsp";
            case "CANCEL":
              return "https://live.sagepay.com/gateway/service/cancel.vsp";
            case "REFUND":
              return "https://live.sagepay.com/gateway/service/refund.vsp";
          }
          break;
        case "TEST":
          switch ( type.ToUpperInvariant() ) {
            case "AUTHORISE":
              return "https://test.sagepay.com/gateway/service/authorise.vsp";
            case "PURCHASE":
              return "https://test.sagepay.com/gateway/service/vspserver-register.vsp";
            case "CANCEL":
              return "https://test.sagepay.com/gateway/service/cancel.vsp";
            case "REFUND":
              return "https://test.sagepay.com/gateway/service/refund.vsp";
          }
          break;
        case "SIMULATOR":
          switch ( type.ToUpperInvariant() ) {
            case "AUTHORISE":
              return "https://test.sagepay.com/simulator/vspserverGateway.asp?Service=VendorAuthoriseTx";
            case "PURCHASE":
              return "https://test.sagepay.com/simulator/VSPServerGateway.asp?Service=VendorRegisterTx";
            case "CANCEL":
              return "https://test.sagepay.com/simulator/vspserverGateway.asp?Service=VendorCancelTx";
            case "REFUND":
              return "https://test.sagepay.com/simulator/vspserverGateway.asp?Service=VendorRefundTx";
          }
          break;
      }

      return string.Empty;
    }

  }
}
