using System;
using System.Collections.Generic;
using System.Globalization;
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

namespace TeaCommerce.PaymentProviders.Inline {
  [PaymentProvider( "CyberSource - inline" )]
  public class CyberSource : APaymentProvider {

    protected const string TestTransactionEndpoint = "https://testsecureacceptance.cybersource.com/silent/pay";
    protected const string LiveTransactionEndpoint = "https://secureacceptance.cybersource.com/silent/pay";

    public override IDictionary<string, string> DefaultSettings {
      get {
        return new Dictionary<string, string> {
          { "profile_id", "" },
          { "access_key", "" },
          { "locale", "en-us" },
          { "form_url", "" },
          { "continue_url", "" },
          { "cancel_url", "" },
          { "transaction_type", "authorization" },
          { "streetAddressPropertyAlias", "streetAddress" },
          { "cityPropertyAlias", "city" },
          { "zipCodePropertyAlias", "zipCode" },
          { "phonePropertyAlias", "phone" },
          { "secret_key", "" },
          { "mode", "test" }
        };
      }
    }

    public override PaymentHtmlForm GenerateHtmlForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, string teaCommerceCommunicationUrl, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "form_url", "settings" );

      PaymentHtmlForm htmlForm = new PaymentHtmlForm {
        Action = settings[ "form_url" ]
      };

      htmlForm.InputFields[ "form_url" ] = settings.ContainsKey( "mode" ) && settings[ "mode" ] == "live" ? LiveTransactionEndpoint : TestTransactionEndpoint;

      htmlForm.InputFields[ "cancel_url" ] = teaCommerceCancelUrl;
      htmlForm.InputFields[ "communication_url" ] = teaCommerceCommunicationUrl;
      order.Properties.AddOrUpdate( new CustomProperty( "teaCommerceContinueUrl", teaCommerceContinueUrl ) { ServerSideOnly = true } );
      order.Properties.AddOrUpdate( new CustomProperty( "teaCommerceCallBackUrl", teaCommerceCallBackUrl ) { ServerSideOnly = true } );
      order.Save();

      return htmlForm;
    }

    public override string GetContinueUrl( Order order, IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "continue_url", "settings" );

      return settings[ "continue_url" ];
    }

    public override string GetCancelUrl( Order order, IDictionary<string, string> settings ) {
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "cancel_url", "settings" );

      return settings[ "cancel_url" ];
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      CallbackInfo callbackInfo = null;

      try {
        order.MustNotBeNull( "order" );
        request.MustNotBeNull( "request" );
        settings.MustNotBeNull( "settings" );

        if ( settings.ContainsKey( "mode" ) && settings[ "mode" ] == "test" ) {
          LogRequest( request, logPostData: true );
        }

        string calculatedSignature = CreateSignature( request.Form[ "signed_field_names" ].Split( new[] { ',' }, StringSplitOptions.RemoveEmptyEntries ).ToDictionary( k => k, k => request.Form[ k ] ), settings );

        if ( order.CartNumber == request.Form[ "reference_number" ] && request.Form[ "signature" ] == calculatedSignature ) {
          //Both errors and successful callbacks will go here

          if ( request.Form[ "decision" ] == "ACCEPT" ) {
            callbackInfo = new CallbackInfo( decimal.Parse( request.Form[ "auth_amount" ], CultureInfo.InvariantCulture ), request.Form[ "transaction_id" ], request.Form[ "req_transaction_type" ] == "authorization" ? PaymentState.Authorized : PaymentState.Captured );

            HttpContext.Current.Response.Redirect( order.Properties[ "teaCommerceContinueUrl" ], false );
          } else {
            //Post interim form and auto submit it - act like the customer just clicked to the payment form page
            if ( order.PaymentInformation.PaymentMethodId != null ) {
              // Pass through request fields
              string requestFields = string.Join( "", request.Form.AllKeys.Select( k => "<input type=\"hidden\" name=\"" + k + "\" value=\"" + request.Form[ k ] + "\" />" ) );
              string paymentForm = PaymentMethodService.Instance.Get( order.StoreId, order.PaymentInformation.PaymentMethodId.Value ).GeneratePaymentForm( order, requestFields );

              //Force the form to auto submit
              paymentForm += "<script type=\"text/javascript\">document.forms[0].submit();</script>";

              //Write out the form
              HttpContext.Current.Response.Clear();
              HttpContext.Current.Response.Write( paymentForm );
            }
          }

        } else {
          LoggingService.Instance.Log( "CyberSource(" + order.CartNumber + ") - Signature security check failed" );
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "CyberSource(" + order.CartNumber + ") - ProcessCallback" );
      }

      return callbackInfo;
    }

    public override string ProcessRequest( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      string response = "";

      try {
        order.MustNotBeNull( "order" );
        request.MustNotBeNull( "request" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "profile_id", "settings" );
        settings.MustContainKey( "access_key", "settings" );
        settings.MustContainKey( "locale", "settings" );
        settings.MustContainKey( "transaction_type", "settings" );
        settings.MustContainKey( "phonePropertyAlias", "settings" );
        settings.MustContainKey( "streetAddressPropertyAlias", "settings" );
        settings.MustContainKey( "cityPropertyAlias", "settings" );
        order.Properties[ settings[ "phonePropertyAlias" ] ].MustNotBeNullOrEmpty( "phone" );
        order.Properties[ settings[ "streetAddressPropertyAlias" ] ].MustNotBeNullOrEmpty( "street address" );
        order.Properties[ settings[ "cityPropertyAlias" ] ].MustNotBeNullOrEmpty( "city" );
        order.PaymentInformation.FirstName.MustNotBeNull( "first name" );
        order.PaymentInformation.LastName.MustNotBeNull( "last name" );
        order.PaymentInformation.Email.MustNotBeNull( "email" );

        // If in test mode, write out the form data to a text file
        if ( settings.ContainsKey( "mode" ) && settings[ "mode" ] == "test" ) {
          LogRequest( request, logPostData: true );
        }

        //Generate input fields for the JavaScript post of the inline form
        IDictionary<string, string> inputFields = new Dictionary<string, string>();
        inputFields[ "profile_id" ] = settings[ "profile_id" ];
        inputFields[ "access_key" ] = settings[ "access_key" ];
        inputFields[ "override_custom_receipt_page" ] = order.Properties[ "teaCommerceCallBackUrl" ];
        inputFields[ "locale" ] = settings[ "locale" ];
        inputFields[ "payment_method" ] = "card";

        inputFields[ "reference_number" ] = order.CartNumber;
        inputFields[ "signed_date_time" ] = DateTime.UtcNow.ToString( "yyyy-MM-dd'T'HH:mm:ss'Z'" );
        inputFields[ "transaction_type" ] = settings[ "transaction_type" ];
        inputFields[ "transaction_uuid" ] = Guid.NewGuid().ToString();
        inputFields[ "amount" ] = order.TotalPrice.Value.WithVat.ToString( "0.00", CultureInfo.InvariantCulture );
        Currency currency = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId );
        if ( !Iso4217CurrencyCodes.ContainsKey( currency.IsoCode ) ) {
          throw new Exception( "You must specify an ISO 4217 currency code for the " + currency.Name + " currency" );
        }
        inputFields[ "currency" ] = currency.IsoCode;

        inputFields[ "bill_to_forename" ] = order.PaymentInformation.FirstName;
        inputFields[ "bill_to_surname" ] = order.PaymentInformation.LastName;
        inputFields[ "bill_to_email" ] = order.PaymentInformation.Email;
        inputFields[ "bill_to_phone" ] = order.Properties[ settings[ "phonePropertyAlias" ] ];

        inputFields[ "bill_to_address_line1" ] = order.Properties[ settings[ "streetAddressPropertyAlias" ] ];
        inputFields[ "bill_to_address_city" ] = order.Properties[ settings[ "cityPropertyAlias" ] ];
        if ( settings.ContainsKey( "zipCodePropertyAlias" ) ) {
          inputFields[ "bill_to_address_postal_code" ] = order.Properties[ settings[ "zipCodePropertyAlias" ] ];
        }
        Country country = CountryService.Instance.Get( order.StoreId, order.PaymentInformation.CountryId );
        if ( !Iso3166CountryCodes.ContainsKey( country.RegionCode ) ) {
          throw new Exception( "You must specify an ISO 3166 country code for the " + country.Name + " country" );
        }
        inputFields[ "bill_to_address_country" ] = country.RegionCode;
        inputFields[ "bill_to_address_state" ] = order.PaymentInformation.CountryRegionId != null ? CountryRegionService.Instance.Get( order.StoreId, order.PaymentInformation.CountryRegionId.Value ).RegionCode : "";

        inputFields[ "card_type" ] = request.Form[ "card_type" ];
        inputFields[ "card_expiry_date" ] = request.Form[ "card_expiry_date" ];
        inputFields[ "card_cvn" ] = request.Form[ "card_cvn" ];
        inputFields[ "card_number" ] = request.Form[ "card_number" ];

        inputFields[ "unsigned_field_names" ] = "";
        inputFields[ "signed_field_names" ] = string.Join( ",", inputFields.Select( kvp => kvp.Key ) ) + ",signed_field_names";

        //Signature and card number should not be signed
        inputFields[ "signature" ] = CreateSignature( inputFields, settings );

        foreach ( KeyValuePair<string, string> kvp in inputFields ) {
          if ( request.Form[ kvp.Key ] != "" ) {
            response += "<input type=\"hidden\" name=\"" + kvp.Key + "\" value=\"" + kvp.Value + "\" />";
          }
        }

      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "CyberSource(" + order.CartNumber + ") - ProcessRequest" );
      }

      return response;
    }

    public override string GetLocalizedSettingsKey( string settingsKey, CultureInfo culture ) {
      switch ( settingsKey ) {
        case "profile_id":
          return settingsKey + "<br/><small>The CyberSource profile id.</small>";
        case "access_key":
          return settingsKey + "<br/><small>The CyberSource access key.</small>";
        case "locale":
          return settingsKey + "<br/><small>Language for the CyberSource UI - e.g. en-us.</small>";
        case "form_url":
          return settingsKey + "<br/><small>The url of the page with the CyberSource payment form on - e.g. /payment/</small>";
        case "continue_url":
          return settingsKey + "<br/><small>The url to navigate to after payment is processed - e.g. /confirmation/</small>";
        case "cancel_url":
          return settingsKey + "<br/><small>The url to navigate to if the customer cancels the payment - e.g. /cancel/</small>";
        case "transaction_type":
          return settingsKey + "<br/><small>The type of transactions - authorization/sale</small>";
        case "streetAddressPropertyAlias":
          return settingsKey + "<br/><small>Alias of the property that holds the address</small>";
        case "cityPropertyAlias":
          return settingsKey + "<br/><small>Alias of the property that holds the city</small>";
        case "zipCodePropertyAlias":
          return settingsKey + "<br/><small>Alias of the property that holds the zip code</small>";
        case "phonePropertyAlias":
          return settingsKey + "<br/><small>Alias of the property that holds the phone</small>";
        case "secret_key":
          return settingsKey + "<br/><small>The CyberSource secret key.</small>";
        case "mode":
          return settingsKey + "<br/><small>The mode of the provider - test/live.</small>";
        default:
          return base.GetLocalizedSettingsKey( settingsKey, culture );
      }
    }

    protected string CreateSignature( IDictionary<string, string> fields, IDictionary<string, string> settings ) {
      fields.MustNotBeNull( "fields" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "secret_key", "settings" );

      return new HMACSHA256( Encoding.UTF8.GetBytes( settings[ "secret_key" ] ) ).ComputeHash( Encoding.UTF8.GetBytes( string.Join( ",", fields.Select( kvp => kvp.Key + "=" + kvp.Value ) ) ) ).ToBase64();
    }
  }
}
