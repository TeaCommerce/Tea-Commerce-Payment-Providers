using System;
using System.Collections.Generic;
using TeaCommerce.PaymentProviders.Web.Extensions;
using System.Collections.Specialized;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web;
using System.Web.Hosting;
using System.Web.Script.Serialization;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Serialization;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;

namespace TeaCommerce.PaymentProviders.Web.Inline {
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
          LogRequestToFile( request, HostingEnvironment.MapPath( "~/cyber-source-callback-data.txt" ), logPostData: true );
        }

      //  //if ( settings[ "mode" ] != "live" )
      //  //  LogCallback( order, request, settings, "cybersource-callback-receipt.txt" );

      //  bool error = false;
      //  string errorMessage = null;

      //  // Check the signature
      //  var signatureTimestamp = request.Form[ "signed_date_time" ];
      //  var signature = Sign( request.Form, signatureTimestamp, settings[ "secret_key" ] );
      //  if ( signature != request.Form[ "signature" ] ) {
      //    error = true;
      //    errorMessage = "Payment signature cannot be verified.";
      //  }

      //  // Check for any negative decisions
      //  if ( !error ) {
      //    //TODO: Maybe show more friendlier errors
      //    switch ( request.Form[ "decision" ] ) {
      //      case "ERROR":
      //      case "DECLINE":
      //      case "CANCEL":
      //      case "REVIEW":
      //        error = true;
      //        errorMessage = request.Form[ "message" ];
      //        break;
      //    }
      //  }

      //  // Check to see if any errors were recorded
      //  if ( error ) {
      //    // Because both errors and success messages all come through
      //    // this callback handler, we need a way to get back to the 
      //    // payment page. As this page requires coming from the payment
      //    // form, we redraw the payment form but with an auto submit
      //    // script to force it to jump straight to the payment page.
      //    // It's not the nicest solution, but short of introducing
      //    // an interim page, unfortunately we don't have much option.

      //    // Pass through request fields
      //    var additionalFields = new StringBuilder( "<input type=\"hidden\" name=\"error_message\" value=\"" + errorMessage + "\" />" );
      //    foreach ( var formFieldKey in request.Form.AllKeys ) {
      //      additionalFields.AppendFormat( "<input type=\"hidden\" name=\"{0}\" value=\"{1}\" />",
      //          formFieldKey,
      //          request.Form[ formFieldKey ] );
      //    }

      //    // Regenerate the payment form appending additional fields
      //    var form = PaymentMethodService.Instance.Get( order.StoreId, order.PaymentInformation.PaymentMethodId.Value )
      //        .GeneratePaymentForm( order, additionalFields.ToString() );

      //    // Force the form to auto submit
      //    form += "<script type=\"text/javascript\">document.forms[0].submit();</script>";

      //    // Write out the form
      //    HttpContext.Current.Response.Clear();
      //    HttpContext.Current.Response.Write( form );

      //    //TODO: Maybe show a processing graphic incase postback lags?

      //    return null;
      //  }

      //  // Redirect to continue URL (but don't end processing just yet)
      //  HttpContext.Current.Response.Redirect( GetContinueUrl( order, settings ), false );

      //  // If we've got this far, we assume the payment was successfull
      //  return new CallbackInfo( decimal.Parse( request.Form[ "auth_amount" ] ),
      //      request.Form[ "req_transaction_uuid" ],
      //      PaymentState.Captured );
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "CyberSource(" + order.CartNumber + ") - ProcessCallback" );
      }

      return callbackInfo;
    }

    public override string ProcessRequest( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      string response = "";
      //TODO: tjek for forskellige settings osv
      try {
        order.MustNotBeNull( "order" );
        request.MustNotBeNull( "request" );
        settings.MustNotBeNull( "settings" );


        // If in test mode, write out the form data to a text file
        if ( settings.ContainsKey( "mode" ) && settings[ "mode" ] == "test" ) {
          LogRequestToFile( request, HostingEnvironment.MapPath( "~/cyber-source-request-data.txt" ), logPostData: true );
        }

        //Generate input fields for the JavaScript post of the inline form
        IDictionary<string, string> inputFields = new Dictionary<string, string>();
        inputFields[ "access_key" ] = settings[ "access_key" ];
        inputFields[ "amount" ] = order.TotalPrice.WithVat.ToString( "0.00", CultureInfo.InvariantCulture );
        inputFields[ "override_custom_receipt_page" ] = order.Properties[ "teaCommerceCallBackUrl" ];

        inputFields[ "bill_to_address_city" ] = order.Properties[ settings[ "cityPropertyAlias" ] ];

        Country country = CountryService.Instance.Get( order.StoreId, order.PaymentInformation.CountryId );
        if ( !Iso3166CountryCodes.ContainsKey( country.RegionCode ) ) {
          throw new Exception( "You must specify an ISO 3166 country code for the " + country.Name + " country" );
        }
        inputFields[ "bill_to_address_country" ] = country.RegionCode;

        inputFields[ "bill_to_address_line1" ] = order.Properties[ settings[ "streetAddressPropertyAlias" ] ];
        inputFields[ "bill_to_address_state" ] = order.PaymentInformation.CountryRegionId != null ? CountryRegionService.Instance.Get( order.StoreId, order.PaymentInformation.CountryRegionId.Value ).RegionCode : "";
        inputFields[ "bill_to_email" ] = order.PaymentInformation.Email;
        inputFields[ "bill_to_forename" ] = order.PaymentInformation.FirstName;
        inputFields[ "bill_to_phone" ] = order.Properties[ settings[ "phonePropertyAlias" ] ];
        inputFields[ "bill_to_surname" ] = order.PaymentInformation.LastName;
        inputFields[ "card_expiry_date" ] = request.Form[ "card_expiry_month" ] + "-" + request.Form[ "card_expiry_year" ];

        inputFields[ "card_type" ] = request.Form[ "card_type" ];
        inputFields[ "card_cvn" ] = request.Form[ "card_cvn" ];

        Currency currency = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId );
        if ( !Iso4217CurrencyCodes.ContainsKey( currency.IsoCode ) ) {
          throw new Exception( "You must specify an ISO 4217 currency code for the " + currency.Name + " currency" );
        }
        inputFields[ "currency" ] = currency.IsoCode;

        inputFields[ "locale" ] = settings[ "locale" ];
        inputFields[ "payment_method" ] = "card";
        inputFields[ "profile_id" ] = settings[ "profile_id" ];
        inputFields[ "reference_number" ] = order.CartNumber;
        inputFields[ "signed_date_time" ] = DateTime.UtcNow.ToString( "yyyy-MM-dd'T'HH:mm:ss'Z'" );
        inputFields[ "transaction_type" ] = settings[ "transaction_type" ];
        inputFields[ "transaction_uuid" ] = Guid.NewGuid().ToString();
        inputFields[ "unsigned_field_names" ] = "card_number,signature";
        inputFields[ "signed_field_names" ] = string.Join( ",", inputFields.Select( kvp => kvp.Key ) ) + ",signed_field_names";

        //Signature and card number should not be signed
        inputFields[ "signature" ] = CreateSignature( inputFields, settings );

        //Some input fields are already on the payment form page and don't need to be added again
        IList<string> inputFieldsNotToReturn = new List<string>() { "card_type", "card_cvn" };

        foreach ( KeyValuePair<string, string> kvp in inputFields ) {
          if ( inputFieldsNotToReturn.All( i => i != kvp.Key ) ) {
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

      return GenerateHMACSHA256Hash( settings[ "secret_key" ], string.Join( ",", fields.Select( kvp => kvp.Key + "=" + kvp.Value ) ) ).Base64Encode();
    }

    protected void OutputJson( object obj ) {
      HttpContext.Current.Response.Clear();
      HttpContext.Current.Response.ContentType = "application/json";
      HttpContext.Current.Response.Write( new JavaScriptSerializer().Serialize( obj ) );
    }
  }
}
