using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web;
using System.Web.Hosting;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;

namespace TeaCommerce.PaymentProviders.Classic {

  [PaymentProvider( "Axcess" )]
  public class Axcess : APaymentProvider {

    public override bool SupportsCapturingOfPayment { get { return true; } }
    public override bool SupportsRefundOfPayment { get { return true; } }
    public override bool SupportsCancellationOfPayment { get { return true; } }

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
        defaultSettings[ "streetAddressPropertyAlias" ] = "streetAddress";
        defaultSettings[ "cityPropertyAlias" ] = "city";
        defaultSettings[ "zipCodePropertyAlias" ] = "zipCode";
        defaultSettings[ "TRANSACTION.MODE" ] = "INTEGRATOR_TEST";
        defaultSettings[ "SYSTEM" ] = "TEST";

        return defaultSettings;
      }
    }

    public override PaymentHtmlForm GenerateHtmlForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, string teaCommerceCommunicationUrl, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "SECURITY.SENDER", "settings" );
      settings.MustContainKey( "USER.LOGIN", "settings" );
      settings.MustContainKey( "USER.PWD", "settings" );
      settings.MustContainKey( "TRANSACTION.CHANNEL", "settings" );
      settings.MustContainKey( "PAYMENT.CODE", "settings" );
      settings.MustContainKey( "streetAddressPropertyAlias", "settings" );
      settings.MustContainKey( "cityPropertyAlias", "settings" );
      settings.MustContainKey( "zipCodePropertyAlias", "settings" );
      settings.MustContainKey( "TRANSACTION.MODE", "settings" );
      settings.MustContainKey( "SYSTEM", "settings" );
      order.Properties[ settings[ "streetAddressPropertyAlias" ] ].MustNotBeNullOrEmpty( "street address" );
      order.Properties[ settings[ "cityPropertyAlias" ] ].MustNotBeNullOrEmpty( "city" );
      order.Properties[ settings[ "zipCodePropertyAlias" ] ].MustNotBeNullOrEmpty( "zip code" );

      PaymentHtmlForm htmlForm = new PaymentHtmlForm();

      string[] settingsToExclude = new[] { "FRONTEND.RESPONSE_URL", "FRONTEND.CANCEL_URL", "streetAddressPropertyAlias", "cityPropertyAlias", "zipCodePropertyAlias", "SYSTEM" };
      Dictionary<string, string> inputFields = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      inputFields[ "REQUEST.VERSION" ] = "1.0";
      inputFields[ "FRONTEND.ENABLED" ] = "true";
      inputFields[ "FRONTEND.POPUP" ] = "false";

      inputFields[ "IDENTIFICATION.TRANSACTIONID" ] = order.CartNumber;

      Currency currency = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId );
      if ( !Iso4217CurrencyCodes.ContainsKey( currency.IsoCode ) ) {
        throw new Exception( "You must specify an ISO 4217 currency code for the " + currency.Name + " currency" );
      }
      inputFields[ "PRESENTATION.CURRENCY" ] = currency.IsoCode;
      inputFields[ "PRESENTATION.AMOUNT" ] = ( order.TotalPrice.Value.WithVat ).ToString( "0.00", CultureInfo.InvariantCulture );

      inputFields[ "FRONTEND.RESPONSE_URL" ] = teaCommerceCallBackUrl;

      inputFields[ "NAME.GIVEN" ] = order.PaymentInformation.FirstName;
      inputFields[ "NAME.FAMILY" ] = order.PaymentInformation.LastName;

      inputFields[ "CONTACT.EMAIL" ] = order.PaymentInformation.Email;
      inputFields[ "CONTACT.IP" ] = HttpContext.Current.Request.UserHostAddress;

      inputFields[ "ADDRESS.STREET" ] = order.Properties[ settings[ "streetAddressPropertyAlias" ] ];
      inputFields[ "ADDRESS.CITY" ] = order.Properties[ settings[ "cityPropertyAlias" ] ];
      inputFields[ "ADDRESS.ZIP" ] = order.Properties[ settings[ "zipCodePropertyAlias" ] ];

      inputFields[ "ADDRESS.COUNTRY" ] = CountryService.Instance.Get( order.StoreId, order.PaymentInformation.CountryId ).RegionCode;
      if ( order.PaymentInformation.CountryRegionId != null ) {
        inputFields[ "ADDRESS.STATE" ] = CountryRegionService.Instance.Get( order.StoreId, order.PaymentInformation.CountryRegionId.Value ).RegionCode;
      }

      IDictionary<string, string> responseKvps = MakePostRequest( settings, inputFields );
      if ( responseKvps[ "POST.VALIDATION" ].Equals( "ACK" ) ) {
        htmlForm.Action = HttpContext.Current.Server.UrlDecode( responseKvps[ "FRONTEND.REDIRECT_URL" ] );
      } else {
        throw new Exception( "Generate html failed - error code: " + responseKvps[ "POST.VALIDATION" ] );
      }

      return htmlForm;
    }

    public override string GetContinueUrl( Order order, IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "FRONTEND.RESPONSE_URL", "settings" );

      return settings[ "FRONTEND.RESPONSE_URL" ];
    }

    public override string GetCancelUrl( Order order, IDictionary<string, string> settings ) {
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
          LogRequestToFile( request, HostingEnvironment.MapPath( "~/axcess-callback-data.txt" ), logPostData: true );
        }

        HttpContext.Current.Response.Clear();
        if ( request[ "PROCESSING.RESULT" ] == "ACK" ) {
          callbackInfo = new CallbackInfo( decimal.Parse( request.Form[ "PRESENTATION.AMOUNT" ], CultureInfo.InvariantCulture ), request.Form[ "IDENTIFICATION.UNIQUEID" ], request.Form[ "PAYMENT.CODE" ] != "CC.DB" ? PaymentState.Authorized : PaymentState.Captured );

          string continueUrl = GetContinueUrl( order, settings );
          if ( !continueUrl.StartsWith( "http" ) ) {
            Uri baseUrl = new UriBuilder( request.Url.Scheme, request.Url.Host, request.Url.Port ).Uri;
            continueUrl = new Uri( baseUrl, continueUrl ).AbsoluteUri;
          }

          HttpContext.Current.Response.Write( continueUrl );
        } else {
          LoggingService.Instance.Log( "Axcess(" + order.CartNumber + ") - Process callback - PROCESSING.CODE: " + request[ "PROCESSING.CODE" ] );

          string cancelUrl = GetCancelUrl( order, settings );
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

    public override ApiInfo CapturePayment( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "SECURITY.SENDER", "settings" );
        settings.MustContainKey( "USER.LOGIN", "settings" );
        settings.MustContainKey( "USER.PWD", "settings" );
        settings.MustContainKey( "TRANSACTION.CHANNEL", "settings" );
        settings.MustContainKey( "TRANSACTION.MODE", "settings" );

        Dictionary<string, string> inputFields = new Dictionary<string, string>();
        inputFields[ "REQUEST.VERSION" ] = "1.0";

        inputFields[ "SECURITY.SENDER" ] = settings[ "SECURITY.SENDER" ];
        inputFields[ "USER.LOGIN" ] = settings[ "USER.LOGIN" ];
        inputFields[ "USER.PWD" ] = settings[ "USER.PWD" ];
        inputFields[ "TRANSACTION.CHANNEL" ] = settings[ "TRANSACTION.CHANNEL" ];

        inputFields[ "PRESENTATION.CURRENCY" ] = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId ).IsoCode;
        inputFields[ "PRESENTATION.AMOUNT" ] = ( order.TransactionInformation.AmountAuthorized.Value ).ToString( "0.00", CultureInfo.InvariantCulture );

        inputFields[ "PAYMENT.CODE" ] = "CC.CP";
        inputFields[ "IDENTIFICATION.REFERENCEID" ] = order.TransactionInformation.TransactionId;
        inputFields[ "TRANSACTION.MODE" ] = settings[ "TRANSACTION.MODE" ];

        IDictionary<string, string> responseKvps = MakePostRequest( settings, inputFields );
        if ( responseKvps[ "PROCESSING.RESULT" ] == "ACK" ) {
          apiInfo = new ApiInfo( responseKvps[ "IDENTIFICATION.UNIQUEID" ], PaymentState.Captured );
        } else {
          LoggingService.Instance.Log( "Axcess(" + order.OrderNumber + ") - Error making API request - PROCESSING.CODE: " + responseKvps[ "PROCESSING.CODE" ] );
        }

      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "Axcess(" + order.OrderNumber + ") - Capture payment" );
      }

      return apiInfo;
    }

    public override ApiInfo RefundPayment( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "SECURITY.SENDER", "settings" );
        settings.MustContainKey( "USER.LOGIN", "settings" );
        settings.MustContainKey( "USER.PWD", "settings" );
        settings.MustContainKey( "TRANSACTION.CHANNEL", "settings" );
        settings.MustContainKey( "TRANSACTION.MODE", "settings" );

        Dictionary<string, string> inputFields = new Dictionary<string, string>();
        inputFields[ "REQUEST.VERSION" ] = "1.0";

        inputFields[ "SECURITY.SENDER" ] = settings[ "SECURITY.SENDER" ];
        inputFields[ "USER.LOGIN" ] = settings[ "USER.LOGIN" ];
        inputFields[ "USER.PWD" ] = settings[ "USER.PWD" ];
        inputFields[ "TRANSACTION.CHANNEL" ] = settings[ "TRANSACTION.CHANNEL" ];

        inputFields[ "PRESENTATION.CURRENCY" ] = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId ).IsoCode;
        inputFields[ "PRESENTATION.AMOUNT" ] = ( order.TransactionInformation.AmountAuthorized.Value ).ToString( "0.00", CultureInfo.InvariantCulture );

        inputFields[ "PAYMENT.CODE" ] = "CC.RF";
        inputFields[ "IDENTIFICATION.REFERENCEID" ] = order.TransactionInformation.TransactionId;
        inputFields[ "TRANSACTION.MODE" ] = settings[ "TRANSACTION.MODE" ];

        IDictionary<string, string> responseKvps = MakePostRequest( settings, inputFields );
        if ( responseKvps[ "PROCESSING.RESULT" ] == "ACK" ) {
          apiInfo = new ApiInfo( responseKvps[ "IDENTIFICATION.UNIQUEID" ], PaymentState.Refunded );
        } else {
          LoggingService.Instance.Log( "Axcess(" + order.OrderNumber + ") - Error making API request - PROCESSING.CODE: " + responseKvps[ "PROCESSING.CODE" ] );
        }

      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "Axcess(" + order.OrderNumber + ") - Refund payment" );
      }

      return apiInfo;
    }

    public override ApiInfo CancelPayment( Order order, IDictionary<string, string> settings ) {
      ApiInfo apiInfo = null;

      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "SECURITY.SENDER", "settings" );
        settings.MustContainKey( "USER.LOGIN", "settings" );
        settings.MustContainKey( "USER.PWD", "settings" );
        settings.MustContainKey( "TRANSACTION.CHANNEL", "settings" );
        settings.MustContainKey( "TRANSACTION.MODE", "settings" );

        Dictionary<string, string> inputFields = new Dictionary<string, string>();
        inputFields[ "REQUEST.VERSION" ] = "1.0";

        inputFields[ "SECURITY.SENDER" ] = settings[ "SECURITY.SENDER" ];
        inputFields[ "USER.LOGIN" ] = settings[ "USER.LOGIN" ];
        inputFields[ "USER.PWD" ] = settings[ "USER.PWD" ];
        inputFields[ "TRANSACTION.CHANNEL" ] = settings[ "TRANSACTION.CHANNEL" ];

        inputFields[ "PAYMENT.CODE" ] = "CC.RV";
        inputFields[ "IDENTIFICATION.REFERENCEID" ] = order.TransactionInformation.TransactionId;
        inputFields[ "TRANSACTION.MODE" ] = settings[ "TRANSACTION.MODE" ];

        IDictionary<string, string> responseKvps = MakePostRequest( settings, inputFields );
        if ( responseKvps[ "PROCESSING.RESULT" ] == "ACK" ) {
          apiInfo = new ApiInfo( responseKvps[ "IDENTIFICATION.UNIQUEID" ], PaymentState.Cancelled );
        } else {
          LoggingService.Instance.Log( "Axcess(" + order.OrderNumber + ") - Error making API request - PROCESSING.CODE: " + responseKvps[ "PROCESSING.CODE" ] );
        }

      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "Axcess(" + order.OrderNumber + ") - Cancel payment" );
      }

      return apiInfo;
    }

    public override string GetLocalizedSettingsKey( string settingsKey, CultureInfo culture ) {
      switch ( settingsKey ) {
        case "FRONTEND.RESPONSE_URL":
          return settingsKey + "<br/><small>e.g. /continue/</small>";
        case "FRONTEND.CANCEL_URL":
          return settingsKey + "<br/><small>e.g. /cancel/</small>";
        case "PAYMENT.CODE":
          return settingsKey + "<br/><small>CC.PA = Authorize, CC.DB = Instant capture</small>";
        case "TRANSACTION.MODE":
          return settingsKey + "<br/><small>INTEGRATOR_TEST, CONNECTOR_TEST, LIVE</small>";
        case "SYSTEM":
          return settingsKey + "<br/><small>TEST = test.ctpe.net, LIVE = ctpe.net</small>";
        default:
          return base.GetLocalizedSettingsKey( settingsKey, culture );
      }
    }

    #region Helper methods

    protected IDictionary<string, string> MakePostRequest( IDictionary<string, string> settings, IDictionary<string, string> inputFields ) {
      settings.MustNotBeNull( "settings" );
      inputFields.MustNotBeNull( "inputFields" );
      settings.MustContainKey( "SYSTEM", "settings" );

      string response = MakePostRequest( settings[ "SYSTEM" ] == "LIVE" ? "https://ctpe.net/frontend/payment.prc" : "https://test.ctpe.net/frontend/payment.prc", inputFields );
      Dictionary<string, string> responseKvps = new Dictionary<string, string>();
      foreach ( string[] kvpTokens in response.Split( '&' ).Select( kvp => kvp.Split( '=' ) ) ) {
        responseKvps[ kvpTokens[ 0 ] ] = kvpTokens[ 1 ];
      }
      return responseKvps;
    }

    #endregion

  }
}
