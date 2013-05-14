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

        defaultSettings[ "PAYMENT.CODE" ] = "CC.DB";
        defaultSettings[ "FRONTEND.MODE" ] = string.Empty;

        defaultSettings[ "TRANSACTION.MODE" ] = "INTEGRATOR_TEST";

        defaultSettings[ "FRONTEND.JSCRIPT_PATH" ] = string.Empty;

        defaultSettings[ "zipCodePropertyAlias" ] = string.Empty;
        defaultSettings[ "cityPropertyAlias" ] = string.Empty;
        defaultSettings[ "streetAddressPropertyAlias" ] = string.Empty;

        defaultSettings[ "testMode" ] = "1";

        return defaultSettings;
      }
    }

    public override bool FinalizeAtContinueUrl { get { return true; } }
    public override bool RedirectAtContinueUrl { get { return false; } }

    public override PaymentHtmlForm GenerateHtmlForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, IDictionary<string, string> settings ) {

      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "FRONTEND.CANCEL_URL" );
      settings.MustNotBeNull( "FRONTEND.RESPONSE_URL" );

      PaymentHtmlForm htmlForm = new PaymentHtmlForm {
        Action = string.Empty
      };

      string[] settingsToExclude = new[] { "" };
      Dictionary<string, string> initialData = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      // mandatory settings
      initialData[ "REQUEST.VERSION" ] = "1.0";
      initialData[ "FRONTEND.ENABLED" ] = "true";
      initialData[ "FRONTEND.POPUP" ] = "true";

      //orderid
      initialData[ "IDENTIFICATION.TRANSACTIONID" ] = order.CartNumber;
      initialData[ "FRONTEND.MODE" ] = string.IsNullOrEmpty( settings[ "FRONTEND.MODE" ] ) ? "WPF_LIGHT" : settings[ "FRONTEND.MODE" ];

      // currency related settings
      initialData[ "PRESENTATION.CURRENCY" ] = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId ).IsoCode;
      initialData[ "PRESENTATION.AMOUNT" ] = ( order.TotalPrice.WithVat ).ToString( "0", CultureInfo.InvariantCulture );

      initialData[ "FRONTEND.RESPONSE_URL" ] = teaCommerceContinueUrl;

      // fill out fields
      initialData[ "NAME.GIVEN" ] = order.PaymentInformation.FirstName;
      initialData[ "NAME.FAMILY" ] = order.PaymentInformation.LastName;
      initialData[ "CONTACT.EMAIL" ] = order.PaymentInformation.Email;
      initialData[ "ADDRESS.COUNTRY" ] = CountryService.Instance.Get( order.StoreId, order.PaymentInformation.CountryId ).RegionCode;
      initialData[ "ADDRESS.STREET" ] = order.Properties[ settings[ "streetAddressPropertyAlias" ] ];
      initialData[ "ADDRESS.ZIP" ] = order.Properties[ settings[ "zipCodePropertyAlias" ] ];
      initialData[ "ADDRESS.CITY" ] = order.Properties[ settings[ "cityPropertyAlias" ] ];

      Dictionary<string, string> responseKvps = new Dictionary<string, string>();

      foreach ( string kvp in MakePostRequest( settings[ "testMode" ] == "0" ? "https://ctpe.net/frontend/payment.prc" : "https://test.ctpe.net/frontend/payment.prc", initialData ).Split( '&' ) ) {
        string[] kvpTokens = kvp.Split( '=' );
        responseKvps[ kvpTokens[ 0 ] ] = kvpTokens[ 1 ];
      }

      if ( responseKvps[ "POST.VALIDATION" ].Equals( "ACK" ) ) {
        htmlForm.Action = HttpContext.Current.Server.UrlDecode( responseKvps[ "FRONTEND.REDIRECT_URL" ] );
      } else {
        throw new Exception( "Axcess - Generate html failed - check the order data" );
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

        //Write data when testing
        if ( settings.ContainsKey( "testMode" ) && settings[ "testMode" ] == "1" ) {
          using ( StreamWriter writer = new StreamWriter( File.Create( HostingEnvironment.MapPath( "~/axcess-callback-data.txt" ) ) ) ) {
            writer.WriteLine( "FORM:" );
            foreach ( string k in request.Form.Keys ) {
              writer.WriteLine( k + " : " + request.Form[ k ] );
            }
            writer.Flush();
          }
        }

      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "Axcess(" + order.CartNumber + ") - Process callback" );
      }

      HttpContext.Current.Response.Clear();
      if ( request[ "PROCESSING.RESULT" ].Equals( "ACK" ) ) {
        callbackInfo = new CallbackInfo( order.TotalPrice.WithVat, request[ "IDENTIFICATION.UNIQUEID" ], PaymentState.Authorized );
        HttpContext.Current.Response.Write( GetContinueUrl( settings ) );
      } else {
        HttpContext.Current.Response.Write( GetCancelUrl( settings ) );
      }

      return callbackInfo;
    }

    public override string GetLocalizedSettingsKey( string settingsKey, CultureInfo culture ) {
      switch ( settingsKey ) {
        case "accepturl":
          return settingsKey + "<br/><small>e.g. /continue/</small>";
        case "cancelurl":
          return settingsKey + "<br/><small>e.g. /cancel/</small>";
        case "testMode":
          return settingsKey + "<br/><small>If set to 1, use test url. If set to 0, use production url</small>";
        case "FRONTEND.JSCRIPT_PATH":
          return settingsKey + "<br/><small>URL to file that will be included in the payment window. This requires a HTTPS server to work flawlessly.</small>";
        default:
          return base.GetLocalizedSettingsKey( settingsKey, culture );
      }
    }

  }
}
