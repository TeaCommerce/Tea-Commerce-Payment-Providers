using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.PaymentProviders;
using TeaCommerce.Api.Infrastructure.Logging;

namespace TeaCommerce.PaymentProviders {
  public class WorldPay : APaymentProvider {

    protected bool isTesting;

    protected const string defaultParameterValue = "No value";

    public override bool SupportsRetrievalOfPaymentStatus { get { return false; } }
    public override bool SupportsCancellationOfPayment { get { return false; } }
    public override bool SupportsCapturingOfPayment { get { return false; } }
    public override bool SupportsRefundOfPayment { get { return false; } }

    public override IDictionary<string, string> DefaultSettings {
      get {
        if ( defaultSettings == null ) {
          defaultSettings = new Dictionary<string, string>();
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
        }
        return defaultSettings;
      }
    }

    public override string FormPostUrl { get { return !isTesting ? "https://secure.worldpay.com/wcc/purchase" : "https://secure-test.worldpay.com/wcc/purchase"; } }
    public override string DocumentationLink { get { return "http://anders.burla.dk/umbraco/tea-commerce/using-worldpay-with-tea-commerce/"; } }
    public override bool AllowsCallbackWithoutOrderId { get { return true; } }

    public override IDictionary<string, string> GenerateForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, IDictionary<string, string> settings ) {
      List<string> settingsToExclude = new string[] { "md5Secret", "callbackPW", "streetAddressPropertyAlias", "cityPropertyAlias", "zipCodePropertyAlias" }.ToList();
      Dictionary<string, string> inputFields = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      isTesting = !settings[ "testMode" ].Equals( "0" );

      //cartId
      inputFields[ "cartId" ] = order.CartNumber;

      //currency
      inputFields[ "currency" ] = order.CurrencyISOCode;

      //amount
      string amount = order.TotalPrice.WithVat.ToString( "0.00", CultureInfo.InvariantCulture );
      inputFields[ "amount" ] = amount;

      inputFields[ "successURL" ] = teaCommerceContinueUrl;
      inputFields[ "cancelURL" ] = teaCommerceCancelUrl;

      //name
      inputFields[ "name" ] = order.PaymentInformation.FirstName + " " + order.PaymentInformation.LastName;

      //email
      inputFields[ "email" ] = order.PaymentInformation.Email;

      //country
      inputFields[ "country" ] = order.Country.CountryCode;

      //address1
      OrderProperty streetAddressProperty = settings.ContainsKey( "streetAddressPropertyAlias" ) ? order.Properties.FirstOrDefault( i => i.Alias.Equals( settings[ "streetAddressPropertyAlias" ] ) ) : null;
      inputFields[ "address1" ] = streetAddressProperty != null && !string.IsNullOrEmpty( streetAddressProperty.Value ) ? streetAddressProperty.Value : defaultParameterValue;

      //town
      OrderProperty cityProperty = settings.ContainsKey( "cityPropertyAlias" ) ? order.Properties.FirstOrDefault( i => i.Alias.Equals( settings[ "cityPropertyAlias" ] ) ) : null;
      inputFields[ "town" ] = cityProperty != null && !string.IsNullOrEmpty( cityProperty.Value ) ? cityProperty.Value : defaultParameterValue;

      //postcode
      OrderProperty zipCodeProperty = settings.ContainsKey( "zipCodePropertyAlias" ) ? order.Properties.FirstOrDefault( i => i.Alias.Equals( settings[ "zipCodePropertyAlias" ] ) ) : null;
      inputFields[ "postcode" ] = zipCodeProperty != null && !string.IsNullOrEmpty( zipCodeProperty.Value ) ? zipCodeProperty.Value : defaultParameterValue;

      //noLanguageMenu
      inputFields[ "noLanguageMenu" ] = string.Empty;

      //hideCurrency
      inputFields[ "hideCurrency" ] = string.Empty;

      //fixContact
      inputFields[ "fixContact" ] = string.Empty;

      //hideContact
      inputFields[ "hideContact" ] = string.Empty;

      //MD5 check
      inputFields[ "signatureFields" ] = "amount:currency:instId:cartId";
      inputFields[ "signature" ] = GetMD5Hash( settings[ "md5Secret" ] + ":" + amount + ":" + order.CurrencyISOCode + ":" + settings[ "instId" ] + ":" + order.CartNumber );

      //WorldPay dont support to show order line information to the shopper

      return inputFields;
    }

    public override string GetContinueUrl( IDictionary<string, string> settings ) {
      return settings[ "successURL" ];
    }

    public override string GetCancelUrl( IDictionary<string, string> settings ) {
      return settings[ "cancelURL" ];
    }

    public override Guid GetOrderId( HttpRequest request, IDictionary<string, string> settings ) {
      //using ( StreamWriter writer = new StreamWriter( File.Create( HttpContext.Current.Server.MapPath( "~/worldPayTestGetOrderId.txt" ) ) ) ) {
      //  writer.WriteLine( "Form:" );
      //  foreach ( string k in request.Form.Keys ) {
      //    writer.WriteLine( k + " : " + request.Form[ k ] );
      //  }
      //  writer.Flush();
      //}

      string errorMessage = string.Empty;

      string paymentResponsePassword = settings[ "paymentResponsePassword" ];
      string callbackPW = request.Form[ "callbackPW" ];
      if ( callbackPW.Equals( paymentResponsePassword ) ) {

        string orderName = request.Form[ "cartId" ];

        return long.Parse( orderName.Remove( 0, TeaCommerceSettings.OrderNamePrefix.Length ) );
      } else
        errorMessage = "Tea Commerce - WorldPay - Payment response password check failed - callbackPW: " + callbackPW + " - paymentResponsePassword: " + paymentResponsePassword;

      LoggingService.Instance.Log( errorMessage );
      return null;
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      //using ( StreamWriter writer = new StreamWriter( File.Create( HttpContext.Current.Server.MapPath( "~/worldPayTestCallback.txt" ) ) ) ) {
      //  writer.WriteLine( "Form:" );
      //  foreach ( string k in request.Form.Keys ) {
      //    writer.WriteLine( k + " : " + request.Form[ k ] );
      //  }
      //  writer.Flush();
      //}

      string errorMessage = string.Empty;

      string paymentResponsePassword = settings[ "paymentResponsePassword" ];
      string callbackPW = request.Form[ "callbackPW" ];
      if ( callbackPW.Equals( paymentResponsePassword ) ) {

        if ( request.Form[ "transStatus" ].Equals( "Y" ) ) {
          decimal totalAmount = decimal.Parse( request.Form[ "authAmount" ], CultureInfo.InvariantCulture );
          string transaction = request.Form[ "transId" ];
          PaymentState paymentState = request.Form[ "authMode" ].Equals( "E" ) ? PaymentState.Authorized : PaymentState.Captured;
          string cardtype = request.Form[ "cardtype" ];

          return new CallbackInfo( totalAmount, transaction, paymentState, cardtype );
        } else
          errorMessage = "Tea Commerce - WorldPay - Cancelled transaction";
      } else
        errorMessage = "Tea Commerce - WorldPay - Payment response password check failed - callbackPW: " + callbackPW + " - paymentResponsePassword: " + paymentResponsePassword;

      LoggingService.Instance.Log( errorMessage );
      return new CallbackInfo( errorMessage );
    }

    public override ApiInfo GetStatus( Order order, IDictionary<string, string> settings ) {
      throw new NotImplementedException();
    }

    public override ApiInfo CapturePayment( Order order, IDictionary<string, string> settings ) {
      throw new NotImplementedException();
    }

    public override ApiInfo RefundPayment( Order order, IDictionary<string, string> settings ) {
      throw new NotImplementedException();
    }

    public override ApiInfo CancelPayment( Order order, IDictionary<string, string> settings ) {
      throw new NotImplementedException();
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
