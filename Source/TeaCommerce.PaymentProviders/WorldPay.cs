using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Web;
using System.Web.Hosting;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;

namespace TeaCommerce.PaymentProviders {

  [PaymentProvider( "WorldPay" )]
  public class WorldPay : APaymentProvider {

    public override string DocumentationLink { get { return "http://anders.burla.dk/umbraco/tea-commerce/using-worldpay-with-tea-commerce/"; } }

    public override bool AllowsCallbackWithoutOrderId { get { return true; } }

    public override IDictionary<string, string> DefaultSettings {
      get {
        Dictionary<string, string> defaultSettings = new Dictionary<string, string>();
        defaultSettings[ "instId" ] = string.Empty;
        defaultSettings[ "lang" ] = "en";
        defaultSettings[ "successURL" ] = string.Empty;
        defaultSettings[ "cancelURL" ] = string.Empty;
        defaultSettings[ "authMode" ] = "A";
        defaultSettings[ "md5Secret" ] = string.Empty;
        defaultSettings[ "paymentResponsePassword" ] = string.Empty;
        defaultSettings[ "streetAddressPropertyAlias" ] = "streetAddress";
        defaultSettings[ "cityPropertyAlias" ] = "city";
        defaultSettings[ "zipCodePropertyAlias" ] = "zipCode";
        defaultSettings[ "testMode" ] = "0";
        return defaultSettings;
      }
    }

    public override PaymentHtmlForm GenerateHtmlForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "md5Secret", "settings" );
      settings.MustContainKey( "instId", "settings" );
      settings.MustContainKey( "streetAddressPropertyAlias", "settings" );
      settings.MustContainKey( "cityPropertyAlias", "settings" );
      settings.MustContainKey( "zipCodePropertyAlias", "settings" );
      order.Properties[ settings[ "streetAddressPropertyAlias" ] ].MustNotBeNullOrEmpty( "street address" );
      order.Properties[ settings[ "cityPropertyAlias" ] ].MustNotBeNullOrEmpty( "city" );
      order.Properties[ settings[ "zipCodePropertyAlias" ] ].MustNotBeNullOrEmpty( "zip code" );

      PaymentHtmlForm htmlForm = new PaymentHtmlForm {
        Action = settings.ContainsKey( "testMode" ) && settings[ "testMode" ] == "1" ? "https://secure-test.worldpay.com/wcc/purchase" : "https://secure.worldpay.com/wcc/purchase"
      };

      string[] settingsToExclude = new[] { "md5Secret", "callbackPW", "streetAddressPropertyAlias", "cityPropertyAlias", "zipCodePropertyAlias" };
      htmlForm.InputFields = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      //cartId
      htmlForm.InputFields[ "cartId" ] = order.CartNumber;

      //currency
      Currency currency = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId );
      if ( !Iso4217CurrencyCodes.ContainsKey( currency.IsoCode ) ) {
        throw new Exception( "You must specify an ISO 4217 currency code for the " + currency.Name + " currency" );
      }
      htmlForm.InputFields[ "currency" ] = currency.IsoCode;

      //amount
      string amount = order.TotalPrice.WithVat.ToString( "0.00", CultureInfo.InvariantCulture );
      htmlForm.InputFields[ "amount" ] = amount;

      htmlForm.InputFields[ "successURL" ] = teaCommerceContinueUrl;
      htmlForm.InputFields[ "cancelURL" ] = teaCommerceCancelUrl;

      //name
      htmlForm.InputFields[ "name" ] = order.PaymentInformation.FirstName + " " + order.PaymentInformation.LastName;

      //email
      htmlForm.InputFields[ "email" ] = order.PaymentInformation.Email;

      //country
      Country country = CountryService.Instance.Get( order.StoreId, order.PaymentInformation.CountryId );
      htmlForm.InputFields[ "country" ] = country.RegionCode;

      //country region
      if ( order.PaymentInformation.CountryRegionId != null ) {
        CountryRegion countryRegion = CountryRegionService.Instance.Get( order.StoreId, order.PaymentInformation.CountryRegionId.Value );
        htmlForm.InputFields[ "region" ] = countryRegion.RegionCode;
      }

      //address1
      htmlForm.InputFields[ "address1" ] = order.Properties[ settings[ "streetAddressPropertyAlias" ] ];

      //town
      htmlForm.InputFields[ "town" ] = order.Properties[ settings[ "cityPropertyAlias" ] ];

      //postcode
      htmlForm.InputFields[ "postcode" ] = order.Properties[ settings[ "zipCodePropertyAlias" ] ];

      //UI settings
      htmlForm.InputFields[ "noLanguageMenu" ] = string.Empty;
      htmlForm.InputFields[ "hideCurrency" ] = string.Empty;
      htmlForm.InputFields[ "fixContact" ] = string.Empty;
      htmlForm.InputFields[ "hideContact" ] = string.Empty;

      htmlForm.InputFields[ "signatureFields" ] = "amount:currency:instId:cartId";
      htmlForm.InputFields[ "signature" ] = GetMd5Hash( settings[ "md5Secret" ] + ":" + amount + ":" + currency.IsoCode + ":" + settings[ "instId" ] + ":" + order.CartNumber );

      //WorldPay dont support to show order line information to the shopper

      return htmlForm;
    }

    public override string GetContinueUrl( IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "successURL", "settings" );

      return settings[ "successURL" ];
    }

    public override string GetCancelUrl( IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "cancelURL", "settings" );

      return settings[ "cancelURL" ];
    }

    public override string GetCartNumber( HttpRequest request, IDictionary<string, string> settings ) {
      string cartNumber = "";

      try {
        request.MustNotBeNull( "request" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "paymentResponsePassword", "settings" );

        //Write data when testing
        if ( settings.ContainsKey( "testMode" ) && settings[ "testMode" ] == "1" ) {
          using ( StreamWriter writer = new StreamWriter( File.Create( HostingEnvironment.MapPath( "~/world-pay-get-cart-number-data.txt" ) ) ) ) {
            writer.WriteLine( "Form:" );
            foreach ( string k in request.Form.Keys ) {
              writer.WriteLine( k + " : " + request.Form[ k ] );
            }
            writer.Flush();
          }
        }

        string paymentResponsePassword = settings[ "paymentResponsePassword" ];
        string callbackPw = request.Form[ "callbackPW" ];

        if ( callbackPw == paymentResponsePassword ) {
          cartNumber = request.Form[ "cartId" ];
        } else {
          LoggingService.Instance.Log( "WorldPay - Payment response password check failed - callbackPW: " + callbackPw + " - paymentResponsePassword: " + paymentResponsePassword );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "WorldPay - Get cart number" );
      }

      return cartNumber;
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      CallbackInfo callbackInfo = null;

      try {
        order.MustNotBeNull( "order" );
        request.MustNotBeNull( "request" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "paymentResponsePassword", "settings" );

        //Write data when testing
        if ( settings.ContainsKey( "testMode" ) && settings[ "testMode" ] == "1" ) {
          using ( StreamWriter writer = new StreamWriter( File.Create( HostingEnvironment.MapPath( "~/world-pay-callback-data.txt" ) ) ) ) {
            writer.WriteLine( "Form:" );
            foreach ( string k in request.Form.Keys ) {
              writer.WriteLine( k + " : " + request.Form[ k ] );
            }
            writer.Flush();
          }
        }

        string paymentResponsePassword = settings[ "paymentResponsePassword" ];
        string callbackPw = request.Form[ "callbackPW" ];

        if ( callbackPw == paymentResponsePassword ) {
          if ( request.Form[ "transStatus" ] == "Y" ) {
            decimal totalAmount = decimal.Parse( request.Form[ "authAmount" ], CultureInfo.InvariantCulture );
            string transaction = request.Form[ "transId" ];
            PaymentState paymentState = request.Form[ "authMode" ] == "E" ? PaymentState.Authorized : PaymentState.Captured;
            string cardtype = request.Form[ "cardtype" ];

            callbackInfo = new CallbackInfo( totalAmount, transaction, paymentState, cardtype );
          } else {
            LoggingService.Instance.Log( "WorldPay(" + order.CartNumber + ") - Cancelled transaction" );
          }
        } else {
          LoggingService.Instance.Log( "WorldPay(" + order.CartNumber + ") - Payment response password check failed - callbackPW: " + callbackPw + " - paymentResponsePassword: " + paymentResponsePassword );
        }

      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "WorldPay(" + order.CartNumber + ") - Process callback" );
      }

      return callbackInfo;
    }

    public override string GetLocalizedSettingsKey( string settingsKey, CultureInfo culture ) {
      switch ( settingsKey ) {
        case "successURL":
          return settingsKey + "<br/><small>e.g. /continue/</small>";
        case "cancelURL":
          return settingsKey + "<br/><small>e.g. /cancel/</small>";
        case "authMode":
          return settingsKey + "<br/><small>A = automatic, E = manual</small>";
        case "testMode":
          return settingsKey + "<br/><small>1 = true; 0 = false</small>";
        default:
          return base.GetLocalizedSettingsKey( settingsKey, culture );
      }
    }

  }
}
