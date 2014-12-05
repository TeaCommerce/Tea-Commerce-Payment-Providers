using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using System.Web.Hosting;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;

namespace TeaCommerce.PaymentProviders.Classic {

  [PaymentProvider( "PaymentSense" )]
  public class PaymentSense : APaymentProvider {

    public override IDictionary<string, string> DefaultSettings {
      get {
        Dictionary<string, string> defaultSettings = new Dictionary<string, string>();
        defaultSettings[ "MerchantID" ] = string.Empty;
        defaultSettings[ "CallbackURL" ] = string.Empty;
        defaultSettings[ "CancelURL" ] = string.Empty;
        defaultSettings[ "TransactionType" ] = "PREAUTH";
        defaultSettings[ "streetAddressPropertyAlias" ] = "streetAddress";
        defaultSettings[ "cityPropertyAlias" ] = "city";
        defaultSettings[ "zipCodePropertyAlias" ] = "zipCode";
        defaultSettings[ "PreSharedKey" ] = "";
        defaultSettings[ "Password" ] = "";
        defaultSettings[ "Testing" ] = "1";

        return defaultSettings;
      }
    }

    public override PaymentHtmlForm GenerateHtmlForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, string teaCommerceCommunicationUrl, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "MerchantID", "settings" );
      settings.MustContainKey( "TransactionType", "settings" );

      PaymentHtmlForm htmlForm = new PaymentHtmlForm {
        Action = "https://mms.paymentsensegateway.com/Pages/PublicPages/PaymentForm.aspx"
      };

      string[] settingsToExclude = new[] { "CancelURL", "streetAddressPropertyAlias", "cityPropertyAlias", "zipCodePropertyAlias", "PreSharedKey", "Password", "Testing" };
      htmlForm.InputFields = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      if ( order.CartNumber.Length > 50 ) {
        throw new Exception( "Cart number of the order can not exceed 50 characters." );
      }
      htmlForm.InputFields[ "OrderID" ] = order.CartNumber;

      Currency currency = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId );
      if ( !Iso4217CurrencyCodes.ContainsKey( currency.IsoCode ) ) {
        throw new Exception( "You must specify an ISO 4217 currency code for the " + currency.Name + " currency" );
      }
      htmlForm.InputFields[ "CurrencyCode" ] = Iso4217CurrencyCodes[ currency.IsoCode ];
      htmlForm.InputFields[ "Amount" ] = ( order.TotalPrice.Value.WithVat * 100M ).ToString( "0", CultureInfo.InvariantCulture );

      htmlForm.InputFields[ "CustomerName" ] = order.PaymentInformation.FirstName + " " + order.PaymentInformation.LastName;

      if ( settings.ContainsKey( "streetAddressPropertyAlias" ) ) {
        htmlForm.InputFields[ "Address1" ] = order.Properties[ settings[ "streetAddressPropertyAlias" ] ];
      }

      if ( settings.ContainsKey( "cityPropertyAlias" ) ) {
        htmlForm.InputFields[ "City" ] = order.Properties[ settings[ "cityPropertyAlias" ] ];
      }

      if ( order.PaymentInformation.CountryRegionId != null ) {
        htmlForm.InputFields[ "State" ] = CountryRegionService.Instance.Get( order.StoreId, order.PaymentInformation.CountryRegionId.Value ).Name;
      }

      if ( settings.ContainsKey( "zipCodePropertyAlias" ) ) {
        htmlForm.InputFields[ "PostCode" ] = order.Properties[ settings[ "zipCodePropertyAlias" ] ];
      }

      Country country = CountryService.Instance.Get( order.StoreId, order.PaymentInformation.CountryId );
      if ( !Iso3166CountryCodes.ContainsKey( country.RegionCode ) ) {
        throw new Exception( "You must specify an ISO 3166 country code for the " + country.Name + " country" );
      }
      htmlForm.InputFields[ "CountryCode" ] = Iso3166CountryCodes[ country.RegionCode ];

      htmlForm.InputFields[ "EmailAddress" ] = order.PaymentInformation.Email;

      htmlForm.InputFields[ "CallbackURL" ] = teaCommerceCallBackUrl;
      htmlForm.InputFields[ "ServerResultURL" ] = teaCommerceCallBackUrl;

      htmlForm.InputFields[ "ResultDeliveryMethod" ] = "SERVER";
      htmlForm.InputFields[ "PaymentFormDisplaysResult" ] = bool.FalseString;
      htmlForm.InputFields[ "TransactionDateTime" ] = DateTime.Now.ToString( "yyyy-MM-dd HH:mm:ss zzz" );

      List<string> keysToHash = new List<string>();
      keysToHash.Add( "PreSharedKey" );
      keysToHash.Add( "MerchantID" );
      keysToHash.Add( "Password" );
      keysToHash.Add( "Amount" );
      keysToHash.Add( "CurrencyCode" );
      if ( htmlForm.InputFields.ContainsKey( "EchoAVSCheckResult" ) ) keysToHash.Add( "EchoAVSCheckResult" );
      if ( htmlForm.InputFields.ContainsKey( "EchoCV2CheckResult" ) ) keysToHash.Add( "EchoCV2CheckResult" );
      if ( htmlForm.InputFields.ContainsKey( "EchoThreeDSecureAuthenticationCheckResult" ) ) keysToHash.Add( "EchoThreeDSecureAuthenticationCheckResult" );
      if ( htmlForm.InputFields.ContainsKey( "EchoCardType" ) ) keysToHash.Add( "EchoCardType" );
      if ( htmlForm.InputFields.ContainsKey( "AVSOverridePolicy" ) ) keysToHash.Add( "AVSOverridePolicy" );
      if ( htmlForm.InputFields.ContainsKey( "CV2OverridePolicy" ) ) keysToHash.Add( "CV2OverridePolicy" );
      if ( htmlForm.InputFields.ContainsKey( "ThreeDSecureOverridePolicy" ) ) keysToHash.Add( "ThreeDSecureOverridePolicy" );
      keysToHash.Add( "OrderID" );
      keysToHash.Add( "TransactionType" );
      keysToHash.Add( "TransactionDateTime" );
      keysToHash.Add( "CallbackURL" );
      keysToHash.Add( "OrderDescription" );
      keysToHash.Add( "CustomerName" );
      keysToHash.Add( "Address1" );
      keysToHash.Add( "Address2" );
      keysToHash.Add( "Address3" );
      keysToHash.Add( "Address4" );
      keysToHash.Add( "City" );
      keysToHash.Add( "State" );
      keysToHash.Add( "PostCode" );
      keysToHash.Add( "CountryCode" );
      if ( htmlForm.InputFields.ContainsKey( "EmailAddress" ) ) keysToHash.Add( "EmailAddress" );
      if ( htmlForm.InputFields.ContainsKey( "PhoneNumber" ) ) keysToHash.Add( "PhoneNumber" );
      if ( htmlForm.InputFields.ContainsKey( "EmailAddressEditable" ) ) keysToHash.Add( "EmailAddressEditable" );
      if ( htmlForm.InputFields.ContainsKey( "PhoneNumberEditable" ) ) keysToHash.Add( "PhoneNumberEditable" );
      if ( htmlForm.InputFields.ContainsKey( "CV2Mandatory" ) ) keysToHash.Add( "CV2Mandatory" );
      if ( htmlForm.InputFields.ContainsKey( "Address1Mandatory" ) ) keysToHash.Add( "Address1Mandatory" );
      if ( htmlForm.InputFields.ContainsKey( "CityMandatory" ) ) keysToHash.Add( "CityMandatory" );
      if ( htmlForm.InputFields.ContainsKey( "PostCodeMandatory" ) ) keysToHash.Add( "PostCodeMandatory" );
      if ( htmlForm.InputFields.ContainsKey( "StateMandatory" ) ) keysToHash.Add( "StateMandatory" );
      if ( htmlForm.InputFields.ContainsKey( "CountryMandatory" ) ) keysToHash.Add( "CountryMandatory" );
      keysToHash.Add( "ResultDeliveryMethod" );
      if ( htmlForm.InputFields.ContainsKey( "ServerResultURL" ) ) keysToHash.Add( "ServerResultURL" );
      if ( htmlForm.InputFields.ContainsKey( "PaymentFormDisplaysResult" ) ) keysToHash.Add( "PaymentFormDisplaysResult" );
      if ( htmlForm.InputFields.ContainsKey( "ServerResultURLCookieVariables" ) ) keysToHash.Add( "ServerResultURLCookieVariables" );
      if ( htmlForm.InputFields.ContainsKey( "ServerResultURLFormVariables" ) ) keysToHash.Add( "ServerResultURLFormVariables" );
      if ( htmlForm.InputFields.ContainsKey( "ServerResultURLQueryStringVariables" ) ) keysToHash.Add( "ServerResultURLQueryStringVariables" );

      htmlForm.InputFields[ "HashDigest" ] = CreateHashDigest( keysToHash, settings, htmlForm.InputFields );

      return htmlForm;
    }

    public override string GetContinueUrl( Order order, IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "CallbackURL", "settings" );

      return settings[ "CallbackURL" ];
    }

    public override string GetCancelUrl( Order order, IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "CancelURL", "settings" );

      return settings[ "CancelURL" ];
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      CallbackInfo callbackInfo = null;

      if ( request != null && !string.IsNullOrEmpty( request.Form[ "StatusCode" ] ) ) {
        //First callback from PaymentSense - server to server callback        

        HttpContext.Current.Response.Clear();
        try {
          order.MustNotBeNull( "order" );
          settings.MustNotBeNull( "settings" );

          //Write data when testing
          if ( settings.ContainsKey( "Testing" ) && settings[ "Testing" ] == "1" ) {
            LogRequest( request, logPostData: true );
          }

          List<string> keysToHash = new List<string>();
          keysToHash.Add( "PreSharedKey" );
          keysToHash.Add( "MerchantID" );
          keysToHash.Add( "Password" );
          keysToHash.Add( "StatusCode" );
          keysToHash.Add( "Message" );
          keysToHash.Add( "PreviousStatusCode" );
          keysToHash.Add( "PreviousMessage" );
          keysToHash.Add( "CrossReference" );
          if ( !string.IsNullOrEmpty( request.Form[ "AddressNumericCheckResult" ] ) ) keysToHash.Add( "AddressNumericCheckResult" );
          if ( !string.IsNullOrEmpty( request.Form[ "PostCodeCheckResult" ] ) ) keysToHash.Add( "PostCodeCheckResult" );
          if ( !string.IsNullOrEmpty( request.Form[ "CV2CheckResult" ] ) ) keysToHash.Add( "CV2CheckResult" );
          if ( !string.IsNullOrEmpty( request.Form[ "ThreeDSecureCheckResult" ] ) ) keysToHash.Add( "ThreeDSecureCheckResult" );
          if ( !string.IsNullOrEmpty( request.Form[ "CardType" ] ) ) keysToHash.Add( "CardType" );
          if ( !string.IsNullOrEmpty( request.Form[ "CardClass" ] ) ) keysToHash.Add( "CardClass" );
          if ( !string.IsNullOrEmpty( request.Form[ "CardIssuer" ] ) ) keysToHash.Add( "CardIssuer" );
          if ( !string.IsNullOrEmpty( request.Form[ "CardIssuerCountryCode" ] ) ) keysToHash.Add( "CardIssuerCountryCode" );
          keysToHash.Add( "Amount" );
          keysToHash.Add( "CurrencyCode" );
          keysToHash.Add( "OrderID" );
          keysToHash.Add( "TransactionType" );
          keysToHash.Add( "TransactionDateTime" );
          keysToHash.Add( "OrderDescription" );
          keysToHash.Add( "CustomerName" );
          keysToHash.Add( "Address1" );
          keysToHash.Add( "Address2" );
          keysToHash.Add( "Address3" );
          keysToHash.Add( "Address4" );
          keysToHash.Add( "City" );
          keysToHash.Add( "State" );
          keysToHash.Add( "PostCode" );
          keysToHash.Add( "CountryCode" );
          if ( !string.IsNullOrEmpty( request.Form[ "EmailAddress" ] ) ) keysToHash.Add( "EmailAddress" );
          if ( !string.IsNullOrEmpty( request.Form[ "PhoneNumber" ] ) ) keysToHash.Add( "PhoneNumber" );

          string hashDigest = CreateHashDigest( keysToHash, settings, request.Form.AllKeys.ToDictionary( k => k, k => request.Form[ k ] ) );

          if ( order.CartNumber == request.Form[ "OrderID" ] && hashDigest == request.Form[ "HashDigest" ] ) {
            if ( request.Form[ "StatusCode" ] == "0" || ( request.Form[ "StatusCode" ] == "20" && request.Form[ "PreviousStatusCode" ] == "0" ) ) {
              callbackInfo = new CallbackInfo( decimal.Parse( request.Form[ "Amount" ], CultureInfo.InvariantCulture ) / 100M, request.Form[ "CrossReference" ], request.Form[ "TransactionType" ] != "SALE" ? PaymentState.Authorized : PaymentState.Captured );
              HttpContext.Current.Response.Write( "StatusCode=0" );
            } else {
              HttpContext.Current.Response.Write( "StatusCode=" + request.Form[ "StatusCode" ] + "&Message=" + request.Form[ "Message" ] );
            }
          } else {
            string message = "PaymentSense(" + order.CartNumber + ") - Digest check failed - calculated: " + hashDigest + " PaymentSense: " + request.Form[ "HashDigest" ];
            LoggingService.Instance.Log( message );
            HttpContext.Current.Response.Write( "StatusCode=30&Message=" + message );
          }
        } catch ( Exception exp ) {
          string message = "PaymentSense(" + order.CartNumber + ") - Process callback failed";
          LoggingService.Instance.Log( exp, message );
          HttpContext.Current.Response.Write( "StatusCode=30&Message=" + message );
        }
      } else {
        //Second callback from PaymentSense - redirect the user to the continue or cancel url
        HttpContext.Current.Response.Redirect( order != null && order.IsFinalized ? GetContinueUrl( order, settings ) : GetCancelUrl( order, settings ) );
      }

      return callbackInfo;
    }

    public override string GetLocalizedSettingsKey( string settingsKey, CultureInfo culture ) {
      switch ( settingsKey ) {
        case "CallbackURL":
          return settingsKey + "<br/><small>e.g. /continue/</small>";
        case "CancelURL":
          return settingsKey + "<br/><small>e.g. /cancel/</small>";
        case "TransactionType":
          return settingsKey + "<br/><small>PREAUTH or SALE</small>";
        case "Testing":
          return settingsKey + "<br/><small>1 = true; 0 = false</small>";
        default:
          return base.GetLocalizedSettingsKey( settingsKey, culture );
      }
    }

    #region Helper methods

    protected string CreateHashDigest( IEnumerable<string> keys, IDictionary<string, string> settings, IDictionary<string, string> inputFields ) {
      settings.MustNotBeNull( "keys" );
      settings.MustNotBeNull( "settings" );
      settings.MustNotBeNull( "inputFields" );
      settings.MustContainKey( "Password", "settings" );
      settings.MustContainKey( "PreSharedKey", "settings" );

      string valueToHash = string.Join( "&", keys.Select( k => k + "=" + ( inputFields.ContainsKey( k ) ? inputFields[ k ] : settings.ContainsKey( k ) ? settings[ k ] : "" ) ) );
      string hashValue = new SHA1CryptoServiceProvider().ComputeHash( Encoding.UTF8.GetBytes( valueToHash ) ).ToHex();

      if ( settings.ContainsKey( "Testing" ) && settings[ "Testing" ] == "1" ) {
        using ( StreamWriter writer = new StreamWriter( File.Create( HostingEnvironment.MapPath( "~/payment-sense-generate-digest.txt" ) ) ) ) {
          writer.WriteLine( "Value to hash: " + valueToHash );
          writer.WriteLine( "Hash: " + hashValue );
          writer.Flush();
        }
      }

      return hashValue;
    }

    #endregion

  }
}
