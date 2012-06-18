using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web;
using TeaCommerce.Data;
using TeaCommerce.Data.Payment;
using umbraco.BusinessLogic;

namespace TeaCommerce.PaymentProviders {
  public class WorldPay : APaymentProvider {

    protected bool isTesting;

    protected const string defaultParameterValue = "No value";

    public override bool AllowsGetStatus { get { return false; } }
    public override bool AllowsCancelPayment { get { return false; } }
    public override bool AllowsCapturePayment { get { return false; } }
    public override bool AllowsRefundPayment { get { return false; } }

    public override Dictionary<string, string> DefaultSettings {
      get {
        if ( defaultSettings == null ) {
          defaultSettings = new Dictionary<string, string>();
          defaultSettings[ "instId" ] = string.Empty;
          defaultSettings[ "lang" ] = "en";
          defaultSettings[ "C_continueUrl" ] = string.Empty;
          defaultSettings[ "C_cancelUrl" ] = string.Empty;
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
    public override bool AllowCallbackWithoutOrderId { get { return true; } }

    public override Dictionary<string, string> GenerateForm( Data.Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, Dictionary<string, string> settings ) {
      List<string> settingsToExclude = new string[] { "md5Secret", "callbackPW", "streetAddressPropertyAlias", "cityPropertyAlias", "zipCodePropertyAlias" }.ToList();
      Dictionary<string, string> inputFields = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      isTesting = !settings[ "testMode" ].Equals( "0" );

      //cartId
      inputFields[ "cartId" ] = order.Name;

      //currency
      inputFields[ "currency" ] = order.CurrencyISOCode;

      //amount
      string amount = order.TotalPrice.ToString( "0.00", CultureInfo.InvariantCulture );
      inputFields[ "amount" ] = amount;

      inputFields[ "C_continueUrl" ] = teaCommerceContinueUrl;
      inputFields[ "C_cancelUrl" ] = teaCommerceCancelUrl;

      //name
      inputFields[ "name" ] = order.FirstName + " " + order.LastName;

      //email
      inputFields[ "email" ] = order.Email;

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
      inputFields[ "signature" ] = GetMD5Hash( settings[ "md5Secret" ] + ":" + amount + ":" + order.CurrencyISOCode + ":" + settings[ "instId" ] + ":" + order.Name );

      //WorldPay dont support to show order line information to the shopper

      return inputFields;
    }

    public override string GetContinueUrl( Dictionary<string, string> settings ) {
      return settings[ "C_continueUrl" ];
    }

    public override string GetCancelUrl( Dictionary<string, string> settings ) {
      return settings[ "C_cancelUrl" ];
    }

    public override long? GetOrderId( HttpRequest request, Dictionary<string, string> settings ) {
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

      Log.Add( LogTypes.Error, -1, errorMessage );
      return null;
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, Dictionary<string, string> settings ) {
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
          string orderName = request.Form[ "cartId" ];
          decimal totalAmount = decimal.Parse( request.Form[ "authAmount" ], CultureInfo.InvariantCulture );
          string transaction = request.Form[ "transId" ];
          PaymentStatus paymentStatus = request.Form[ "authMode" ].Equals( "E" ) ? PaymentStatus.Authorized : PaymentStatus.Captured;

          string cardtype = request.Form[ "cardtype" ];

          return new CallbackInfo( orderName, totalAmount, transaction, paymentStatus, cardtype, string.Empty );
        } else
          errorMessage = "Tea Commerce - WorldPay - Cancelled transaction";
      } else
        errorMessage = "Tea Commerce - WorldPay - Payment response password check failed - callbackPW: " + callbackPW + " - paymentResponsePassword: " + paymentResponsePassword;

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new CallbackInfo( errorMessage );
    }

    public override APIInfo GetStatus( Order order, Dictionary<string, string> settings ) {
      throw new NotImplementedException();
    }

    public override APIInfo CapturePayment( Order order, Dictionary<string, string> settings ) {
      throw new NotImplementedException();
    }

    public override APIInfo RefundPayment( Order order, Dictionary<string, string> settings ) {
      throw new NotImplementedException();
    }

    public override APIInfo CancelPayment( Order order, Dictionary<string, string> settings ) {
      throw new NotImplementedException();
    }

  }
}
