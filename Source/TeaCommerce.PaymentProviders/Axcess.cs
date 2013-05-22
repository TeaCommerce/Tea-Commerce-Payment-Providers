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

namespace TeaCommerce.PaymentProviders {

  [PaymentProvider( "Axcess" )]
  public class Axcess : APaymentProvider {

    public override IDictionary<string, string> DefaultSettings {
      get {
        Dictionary<string, string> defaultSettings = new Dictionary<string, string>();
        defaultSettings[ "SECURITY.SENDER" ] = string.Empty;
        defaultSettings[ "USER.LOGIN" ] = string.Empty;
        defaultSettings[ "USER.PWD" ] = string.Empty;
        defaultSettings[ "TRANSACTION.CHANNEL" ] = string.Empty;
        defaultSettings[ "FRONTEND.LANGUAGE" ] = "en";
        defaultSettings[ "FRONTEND.RESPONSE_URL" ] = string.Empty;
        defaultSettings[ "FRONTEND.CANCEL_URL" ] = string.Empty;
        defaultSettings[ "PAYMENT.CODE" ] = "CC.PA";
        defaultSettings[ "streetAddressPropertyAlias" ] = string.Empty;
        defaultSettings[ "cityPropertyAlias" ] = string.Empty;
        defaultSettings[ "zipCodePropertyAlias" ] = string.Empty;
        defaultSettings[ "TRANSACTION.MODE" ] = "LIVE";

        return defaultSettings;
      }
    }

    public override PaymentHtmlForm GenerateHtmlForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, IDictionary<string, string> settings ) {

      order.MustNotBeNull( "order" );
      settings.MustContainKey( "SECURITY.SENDER", "settings" );
      settings.MustContainKey( "USER.LOGIN", "settings" );
      settings.MustContainKey( "USER.PWD", "settings" );
      settings.MustContainKey( "TRANSACTION.CHANNEL", "settings" );
      settings.MustContainKey( "PAYMENT.CODE", "settings" );
      settings.MustContainKey( "streetAddressPropertyAlias", "settings" );
      settings.MustContainKey( "cityPropertyAlias", "settings" );
      settings.MustContainKey( "zipCodePropertyAlias", "settings" );
      settings.MustContainKey( "TRANSACTION.MODE", "settings" );

      PaymentHtmlForm htmlForm = new PaymentHtmlForm();

      string[] settingsToExclude = new[] { "FRONTEND.RESPONSE_URL", "FRONTEND.CANCEL_URL", "streetAddressPropertyAlias", "cityPropertyAlias", "zipCodePropertyAlias" };
      Dictionary<string, string> initialData = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      initialData[ "REQUEST.VERSION" ] = "1.0";
      initialData[ "FRONTEND.ENABLED" ] = "true";
      initialData[ "FRONTEND.POPUP" ] = "true";//TODO: undersøge false

      initialData[ "IDENTIFICATION.TRANSACTIONID" ] = order.CartNumber;

      initialData[ "PRESENTATION.CURRENCY" ] = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId ).IsoCode;
      initialData[ "PRESENTATION.AMOUNT" ] = ( order.TotalPrice.WithVat ).ToString( "0.00", CultureInfo.InvariantCulture );

      initialData[ "FRONTEND.RESPONSE_URL" ] = teaCommerceCallBackUrl;

      initialData[ "NAME.GIVEN" ] = order.PaymentInformation.FirstName;
      initialData[ "NAME.FAMILY" ] = order.PaymentInformation.LastName;

      initialData[ "CONTACT.EMAIL" ] = order.PaymentInformation.Email;
      initialData[ "CONTACT.IP" ] = HttpContext.Current.Request.UserHostAddress;

      string streetAddress = order.Properties[ settings[ "streetAddressPropertyAlias" ] ];
      string city = order.Properties[ settings[ "cityPropertyAlias" ] ];
      string zipCode = order.Properties[ settings[ "zipCodePropertyAlias" ] ];

      streetAddress.MustNotBeNullOrEmpty( "streetAddress" );
      city.MustNotBeNullOrEmpty( "city" );
      zipCode.MustNotBeNullOrEmpty( "zipCode" );

      initialData[ "ADDRESS.STREET" ] = streetAddress;
      initialData[ "ADDRESS.CITY" ] = city;
      initialData[ "ADDRESS.ZIP" ] = zipCode;

      Country country = CountryService.Instance.Get( order.StoreId, order.PaymentInformation.CountryId );
      initialData[ "ADDRESS.COUNTRY" ] = country.RegionCode;
      if ( order.PaymentInformation.CountryRegionId != null ) {
        CountryRegion countryRegion = CountryRegionService.Instance.Get( order.StoreId, order.PaymentInformation.CountryRegionId.Value );
        initialData[ "ADDRESS.STATE" ] = country.RegionCode + "." + countryRegion.RegionCode;
      }

      Dictionary<string, string> responseKvps = new Dictionary<string, string>();

      string response = MakePostRequest( settings[ "TRANSACTION.MODE" ] == "LIVE" ? "https://ctpe.net/frontend/payment.prc" : "https://test.ctpe.net/frontend/payment.prc", initialData );
      foreach ( string[] kvpTokens in response.Split( '&' ).Select( kvp => kvp.Split( '=' ) ) ) {
        responseKvps[ kvpTokens[ 0 ] ] = kvpTokens[ 1 ];
      }

      if ( responseKvps[ "POST.VALIDATION" ].Equals( "ACK" ) ) {
        htmlForm.Action = HttpContext.Current.Server.UrlDecode( responseKvps[ "FRONTEND.REDIRECT_URL" ] );
      } else {
        throw new Exception( "Generate html failed - error code: " + responseKvps[ "POST.VALIDATION" ] );
      }

      return htmlForm;
    }

    public override string GetContinueUrl( IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "FRONTEND.RESPONSE_URL", "settings" );

      return settings[ "FRONTEND.RESPONSE_URL" ];
    }

    public override string GetCancelUrl( IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "FRONTEND.CANCEL_URL", "settings" );

      return settings[ "FRONTEND.CANCEL_URL" ];
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      CallbackInfo callbackInfo = null;

      try {
        order.MustNotBeNull( "order" );
        request.MustNotBeNull( "request" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "TRANSACTION.MODE", "settings" );

        //Write data when testing
        if ( settings[ "TRANSACTION.MODE" ] != "LIVE" ) {
          using ( StreamWriter writer = new StreamWriter( File.Create( HostingEnvironment.MapPath( "~/axcess-callback-data.txt" ) ) ) ) {
            writer.WriteLine( "FORM:" );
            foreach ( string k in request.Form.Keys ) {
              writer.WriteLine( k + " : " + request.Form[ k ] );
            }
            writer.Flush();
          }
        }

        HttpContext.Current.Response.Clear();
        if ( request[ "PROCESSING.RESULT" ].Equals( "ACK" ) ) {
          callbackInfo = new CallbackInfo( decimal.Parse( request[ "PRESENTATION.AMOUNT" ], CultureInfo.InvariantCulture ), request[ "IDENTIFICATION.UNIQUEID" ], request[ "PAYMENT.CODE" ] != "CC.DB" ? PaymentState.Authorized : PaymentState.Captured );

          string continueUrl = GetContinueUrl( settings );
          if ( !continueUrl.StartsWith( "http" ) ) {
            Uri baseUrl = new UriBuilder( request.Url.Scheme, request.Url.Host, request.Url.Port ).Uri;
            continueUrl = new Uri( baseUrl, continueUrl ).AbsoluteUri;
          }

          HttpContext.Current.Response.Write( continueUrl );
        } else {
          LoggingService.Instance.Log( "Axcess(" + order.CartNumber + ") - Process callback - PROCESSING.CODE: " + request[ "PROCESSING.CODE" ] );

          string cancelUrl = GetCancelUrl( settings );
          if ( !cancelUrl.StartsWith( "http" ) ) {
            Uri baseUrl = new UriBuilder( request.Url.Scheme, request.Url.Host, request.Url.Port ).Uri;
            cancelUrl = new Uri( baseUrl, cancelUrl ).AbsoluteUri;
          }
          HttpContext.Current.Response.Write( cancelUrl );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "Axcess(" + order.CartNumber + ") - Process callback" );
      }

      return callbackInfo;
    }

    public override string GetLocalizedSettingsKey( string settingsKey, CultureInfo culture ) {
      switch ( settingsKey ) {
        case "FRONTEND.RESPONSE_URL":
          return settingsKey + "<br/><small>e.g. /continue/</small>";
        case "FRONTEND.CANCEL_URL":
          return settingsKey + "<br/><small>e.g. /cancel/</small>";
        case "PAYMENT.CODE":
          return settingsKey + "<br/><small>CC.PA = Authorize, CC.DB = Instant capture</small>";
        //case "FRONTEND.JSCRIPT_PATH":
        //  return settingsKey + "<br/><small>URL to file that will be included in the payment window. This requires a HTTPS server to work flawlessly.</small>";
        case "TRANSACTION.MODE":
          return settingsKey + "<br/><small>INTEGRATOR_TEST, CONNECTOR_TEST, LIVE</small>";
        default:
          return base.GetLocalizedSettingsKey( settingsKey, culture );
      }
    }

  }
}
