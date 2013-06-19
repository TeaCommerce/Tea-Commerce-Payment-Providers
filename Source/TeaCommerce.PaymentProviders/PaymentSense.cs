using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web;
using System.Web.Hosting;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;
using TeaCommerce.PaymentProviders.Extensions;

namespace TeaCommerce.PaymentProviders {

  [PaymentProvider( "PaymentSense" )]
  public class PaymentSense : APaymentProvider {

    public override IDictionary<string, string> DefaultSettings {
      get {
        Dictionary<string, string> defaultSettings = new Dictionary<string, string>();
        defaultSettings[ "MerchantID" ] = string.Empty;
        defaultSettings[ "CallbackURL" ] = string.Empty;
        defaultSettings[ "CancelURL" ] = string.Empty;
        defaultSettings[ "TransactionType" ] = "PREAUTH";
        defaultSettings[ "PreSharedKey" ] = "";
        defaultSettings[ "Password" ] = "";
        defaultSettings[ "Testing" ] = "0";

        return defaultSettings;
      }
    }

    public override PaymentHtmlForm GenerateHtmlForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "MerchantID", "settings" );
      settings.MustContainKey( "TransactionType", "settings" );

      PaymentHtmlForm htmlForm = new PaymentHtmlForm() {
        Action = "https://mms.paymentsensegateway.com/Pages/PublicPages/PaymentForm.aspx"
      };

      string[] settingsToExclude = new[] { "CancelURL", "PreSharedKey", "Password", "Testing" };
      htmlForm.InputFields = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      htmlForm.InputFields[ "OrderID" ] = order.CartNumber.Truncate( 50 );

      Currency currency = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId );
      if ( !Iso4217CurrencyCodes.ContainsKey( currency.IsoCode ) ) {
        throw new Exception( "You must specify an ISO 4217 currency code for the " + currency.Name + " currency" );
      }
      htmlForm.InputFields[ "CurrencyCode" ] = Iso4217CurrencyCodes[ currency.IsoCode ];
      htmlForm.InputFields[ "Amount" ] = ( order.TotalPrice.WithVat * 100M ).ToString( "0", CultureInfo.InvariantCulture );

      htmlForm.InputFields[ "CallbackURL" ] = teaCommerceCallBackUrl;
      htmlForm.InputFields[ "ServerResultURL" ] = teaCommerceCallBackUrl;

      htmlForm.InputFields[ "ResultDeliveryMethod" ] = "SERVER";
      htmlForm.InputFields[ "PaymentFormDisplaysResult" ] = bool.FalseString;
      htmlForm.InputFields[ "TransactionDateTime" ] = DateTime.Now.ToString( "yyyy-MM-dd HH:mm:ss zzz" );

      htmlForm.InputFields[ "CV2Mandatory" ] = bool.TrueString;
      htmlForm.InputFields[ "Address1Mandatory" ] = bool.FalseString;
      htmlForm.InputFields[ "CityMandatory" ] = bool.FalseString;
      htmlForm.InputFields[ "PostCodeMandatory" ] = bool.FalseString;
      htmlForm.InputFields[ "StateMandatory" ] = bool.FalseString;
      htmlForm.InputFields[ "CountryMandatory" ] = bool.FalseString;

      htmlForm.InputFields[ "HashDigest" ] = CreateHashDigest( new[] {
        "MerchantID",
        "Password",
        "Amount",
        "CurrencyCode",
        "EchoAVSCheckResult",
        "EchoCV2CheckResult",
        "EchoThreeDSecureAuthenticationCheckResult",
        "EchoCardType",
        "AVSOverridePolicy",
        "CV2OverridePolicy",
        "ThreeDSecureOverridePolicy",
        "OrderID",
        "TransactionType",
        "TransactionDateTime",
        "CallbackURL",
        "OrderDescription",
        "CustomerName",
        "Address1",
        "Address2",
        "Address3",
        "Address4",
        "City",
        "State",
        "PostCode",
        "CountryCode",
        "EmailAddress",
        "PhoneNumber",
        "EmailAddressEditable",
        "PhoneNumberEditable",
        "CV2Mandatory",
        "Address1Mandatory",
        "CityMandatory",
        "PostCodeMandatory",
        "StateMandatory",
        "CountryMandatory",
        "ResultDeliveryMethod",
        "ServerResultURL",
        "PaymentFormDisplaysResult",
        "ServerResultURLCookieVariables",
        "ServerResultURLFormVariables",
        "ServerResultURLQueryStringVariables"
      },
      settings, htmlForm.InputFields );

      return htmlForm;
    }

    public override string GetContinueUrl( IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "CallbackURL", "settings" );

      return settings[ "CallbackURL" ];
    }

    public override string GetCancelUrl( IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "CancelURL", "settings" );

      return settings[ "CancelURL" ];
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      CallbackInfo callbackInfo = null;

      if ( request != null && request.Form[ "StatusCode" ] != "" ) {
        //First callback from PaymentSense - server to server callback        

        HttpContext.Current.Response.Clear();
        try {
          order.MustNotBeNull( "order" );
          settings.MustNotBeNull( "settings" );

          //Write data when testing
          if ( settings.ContainsKey( "testMode" ) && settings[ "testMode" ] == "1" ) {
            using ( StreamWriter writer = new StreamWriter( File.Create( HostingEnvironment.MapPath( "~/payment-sense-callback-data.txt" ) ) ) ) {
              writer.WriteLine( "FORM:" );
              foreach ( string k in request.Form.Keys ) {
                writer.WriteLine( k + " : " + request.Form[ k ] );
              }
              writer.Flush();
            }
          }

          string hashDigest = CreateHashDigest( new[] {
            "MerchantID",
            "Password",
            "StatusCode",
            "Message",
            "PreviousStatusCode",
            "PreviousMessage",
            "CrossReference",
            "Amount",
            "CurrencyCode",
            "OrderID",
            "TransactionType",
            "TransactionDateTime",
            "OrderDescription",
            "CustomerName",
            "Address1",
            "Address2",
            "Address3",
            "Address4",
            "City",
            "State",
            "PostCode",
            "CountryCode"
          }, settings, request.Form.AllKeys.ToDictionary( k => k, k => request.Form[ k ] ) );

          if ( hashDigest == request.Form[ "HashDigest" ] ) {
            if ( request.Form[ "StatusCode" ] == "0" ) {
              callbackInfo = new CallbackInfo( decimal.Parse( request.Form[ "PRESENTATION.AMOUNT" ], CultureInfo.InvariantCulture ) / 100M, request.Form[ "CrossReference" ], request.Form[ "TransactionType" ] != "SALE" ? PaymentState.Authorized : PaymentState.Captured );
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
        HttpContext.Current.Response.Redirect( order != null && order.IsFinalized ? GetContinueUrl( settings ) : GetCancelUrl( settings ) );
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

      string test = string.Join( "&", keys.Select( k => k + "=" + ( inputFields.ContainsKey( k ) ? inputFields[ k ] : ( settings.ContainsKey( k ) ? settings[ k ] : "" ) ) ) );
      string encrypted = EncryptHmac( settings[ "PreSharedKey" ], test );
      using ( StreamWriter writer = new StreamWriter( File.Create( HostingEnvironment.MapPath( "~/payment-sense-generate-form-data.txt" ) ) ) ) {
        writer.WriteLine( test );
        writer.WriteLine( encrypted );
        writer.Flush();
      }

      return encrypted;
    }

    #endregion

  }
}
