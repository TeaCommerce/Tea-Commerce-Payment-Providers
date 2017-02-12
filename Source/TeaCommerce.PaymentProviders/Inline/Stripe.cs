using Stripe;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Web;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;

namespace TeaCommerce.PaymentProviders.Inline {
  [PaymentProvider( "Stripe - inline" )]
  public class Stripe : APaymentProvider {

    public override string DocumentationLink { get { return "https://stripe.com/docs"; } }

    public override bool SupportsRetrievalOfPaymentStatus { get { return true; } }
    public override bool SupportsCapturingOfPayment { get { return true; } }
    public override bool SupportsRefundOfPayment { get { return true; } }
    public override bool SupportsCancellationOfPayment { get { return true; } }

    public override bool FinalizeAtContinueUrl { get { return true; } }

    public override IDictionary<string, string> DefaultSettings {
      get {
        return new Dictionary<string, string> {
          { "form_url", "" },
          { "continue_url", "" },
          { "cancel_url", "" },
          { "capture", "false" },
          { "validate_cvc", "false" },
          { "validate_address", "false" },
          { "address_property_alias", "streetAddress" },
          { "validate_zipcode", "false" },
          { "zipcode_property_alias", "zipCode" },
          { "validate_country", "false" },
          { "test_secret_key", "" },
          { "test_public_key", "" },
          { "live_secret_key", "" },
          { "live_public_key", "" },
          { "mode", "test" },
        };
      }
    }

    public override PaymentHtmlForm GenerateHtmlForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, string teaCommerceCommunicationUrl, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "form_url", "settings" );
      settings.MustContainKey( "mode", "settings" );
      settings.MustContainKey( settings[ "mode" ] + "_public_key", "settings" );

      PaymentHtmlForm htmlForm = new PaymentHtmlForm {
        Action = settings[ "form_url" ]
      };

      string[] settingsToExclude = new[] { "form_url", "capture", "test_secret_key", "test_public_key", "live_secret_key", "live_public_key", "mode" };
      htmlForm.InputFields = settings.Where( i => !settingsToExclude.Contains( i.Key ) ).ToDictionary( i => i.Key, i => i.Value );

      htmlForm.InputFields[ "api_key" ] = settings[ settings[ "mode" ] + "_public_key" ];
      htmlForm.InputFields[ "continue_url" ] = teaCommerceContinueUrl;
      htmlForm.InputFields[ "cancel_url" ] = teaCommerceCancelUrl;

     if (settings.ContainsKey("address_property_alias") && !string.IsNullOrWhiteSpace(settings["address_property_alias"]))
        htmlForm.InputFields["address"] = order.Properties.First(x => x.Alias == settings["address_property_alias"]).Value;

     if (settings.ContainsKey("zipcode_property_alias") && !string.IsNullOrWhiteSpace(settings["zipcode_property_alias"]))
        htmlForm.InputFields["zipcode"] = order.Properties.First(x => x.Alias == settings["zipcode_property_alias"]).Value;

     if (order.PaymentInformation != null && order.PaymentInformation.CountryId > 0)
     {
        var country = CountryService.Instance.Get(order.StoreId, order.PaymentInformation.CountryId);
        htmlForm.InputFields["country"] = country.RegionCode.ToLowerInvariant();
     }

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

    public override string GetCartNumber( HttpRequest request, IDictionary<string, string> settings ) {
      string cartNumber = "";

      try {
        request.MustNotBeNull( "request" );
        settings.MustNotBeNull( "settings" );

        // If in test mode, write out the form data to a text file
        if ( settings.ContainsKey( "mode" ) && settings[ "mode" ] == "test" ) {
          LogRequest<Stripe>( request, logPostData: true );
        }

        StripeEvent stripeEvent = GetStripeEvent( request );

        // We are only interested in charge events
        if ( stripeEvent != null && stripeEvent.Type.StartsWith( "charge." ) ) {
          StripeCharge stripeCharge = Mapper<StripeCharge>.MapFromJson( stripeEvent.Data.Object.ToString() );
          cartNumber = stripeCharge.Description;
        } else {
          HttpContext.Current.Response.StatusCode = (int)HttpStatusCode.BadRequest;
        }

      } catch ( Exception exp ) {
        LoggingService.Instance.Error<Stripe>( "Stripe - Get cart number", exp );
      }

      return cartNumber;
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      CallbackInfo callbackInfo = null;

      try {

        order.MustNotBeNull( "order" );
        request.MustNotBeNull( "request" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "mode", "settings" );
        settings.MustContainKey( settings[ "mode" ] + "_secret_key", "settings" );

        // If in test mode, write out the form data to a text file
        if ( settings.ContainsKey( "mode" ) && settings[ "mode" ] == "test" ) {
          LogRequest<Stripe>( request, logPostData: true );
        }

        var validateCvc = settings["validate_cvc"].TryParse<bool>() ?? false;
        var validateAddress = settings["validate_address"].TryParse<bool>() ?? false;
        var validateZipCode = settings["validate_zipcode"].TryParse<bool>() ?? false;
        var validateCountry = settings["validate_country"].TryParse<bool>() ?? false;
        var capture = settings["capture"].TryParse<bool>() ?? false;
    
        StripeChargeService chargeService = new StripeChargeService( settings[ settings[ "mode" ] + "_secret_key" ] );

        StripeChargeCreateOptions chargeOptions = new StripeChargeCreateOptions {
          Amount = (int)Math.Round(order.TotalPrice.Value.WithVat * 100, MidpointRounding.AwayFromZero),
          Currency = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId ).IsoCode,
          SourceTokenOrExistingSourceId = request.Form[ "stripeToken" ],
          Description = order.CartNumber,
          Capture = false
        };

        StripeCharge charge = chargeService.Create( chargeOptions );

        // Check CVC
        if (validateCvc && charge.Source.Card.CvcCheck == "fail")
        {
            throw new StripeException(HttpStatusCode.Unauthorized,
                new StripeError
                {
                    ChargeId = charge.Id,
                    Code = "TEA_ERROR",
                    Message = "Credit card failed CVC checks. Please check your CVC code and try again."
                }, "Credit card failed CVC checks. Please check your CVC code and try again.");
        }

        // Check address
        if (validateAddress)
        {
            var address = order.Properties.FirstOrDefault(x => x.Alias == settings["address_property_alias"]);
            if (address != null && (address.Value != charge.Source.Card.AddressLine1 || charge.Source.Card.AddressLine1Check == "fail"))
            {
                throw new StripeException(HttpStatusCode.Unauthorized,
                    new StripeError
                    {
                        ChargeId = charge.Id,
                        Code = "TEA_COMMERCE",
                        Message = "Address failed security checks. Please check your billing address matches the address your card is registered to."
                    },
                    "Address failed security checks. Please check your billing address matches the address your card is registered to.");
            }
        }

        // Check zipcode
        if (validateZipCode)
        {
            var zipCode = order.Properties.FirstOrDefault(x => x.Alias == settings["zipcode_property_alias"]);
            if (zipCode != null && (zipCode.Value != charge.Source.Card.AddressZip || charge.Source.Card.AddressZipCheck == "fail"))
            {
                throw new StripeException(HttpStatusCode.Unauthorized,
                    new StripeError
                    {
                        ChargeId = charge.Id,
                        Code = "TEA_ERROR",
                        Message = "Address failed security checks. Please check your billing address matches the address your card is registered to."
                    },
                    "Address failed security checks. Please check your billing address matches the address your card is registered to.");
            }
        }

        // Check country
        if (validateCountry)
        {
            var country = CountryService.Instance.Get(order.StoreId, order.PaymentInformation.CountryId);
            if (country != null && !string.IsNullOrEmpty(charge.Source.Card.Country) &&
                charge.Source.Card.Country.ToLowerInvariant() != country.RegionCode.ToLowerInvariant())
            {
                throw new StripeException(HttpStatusCode.Unauthorized,
                    new StripeError
                    {
                        ChargeId = charge.Id,
                        Code = "TEA_ERROR",
                        Message = "Card country of origin does not match billing address. Please check your billing address country matches the country your card is registered to."
                    },
                    "Card country of origin does not match billing address. Please check your billing address country matches the country your card is registered to.");
            }
        }

        // Check payment ammount
        if (charge.Amount != chargeOptions.Amount)
        {
            throw new StripeException(HttpStatusCode.Unauthorized,
                new StripeError
                {
                    ChargeId = charge.Id,
                    Code = "TEA_ERROR",
                    Message = "Payment ammount differs from authorized payment ammount"
                }, "Payment ammount differs from authorized payment ammount");
        }

        // Check paid status
        if (!charge.Paid)
        {
            throw new StripeException(HttpStatusCode.Unauthorized,
                new StripeError
                {
                    ChargeId = charge.Id,
                    Code = "TEA_ERROR",
                    Message = "Payment failed"
                }, "Payment failed");
        }

        if (capture && charge.Captured == false)
        {
            var result = CapturePayment(order, settings);
            if (result == null || result.PaymentState != PaymentState.Captured)
            {
                throw new StripeException(HttpStatusCode.Unauthorized,
                    new StripeError
                    {
                        ChargeId = charge.Id,
                        Code = "TEA_ERROR",
                        Message = "Error capturing payment"
                    }, "Error capturing payment");
            }
        }

        callbackInfo = new CallbackInfo((decimal)charge.Amount / 100, charge.Id, capture ? PaymentState.Captured : PaymentState.Authorized);

      } catch ( StripeException e ) {

        // Pass through request fields
        string requestFields = string.Join( "", request.Form.AllKeys.Select( k => "<input type=\"hidden\" name=\"" + k + "\" value=\"" + request.Form[ k ] + "\" />" ) );

        //Add error details from the exception.
        requestFields = requestFields + "<input type=\"hidden\" name=\"TransactionFailed\" value=\"true\" />";
        requestFields = requestFields + "<input type=\"hidden\" name=\"FailureReason.chargeId\" value=\"" + e.StripeError.ChargeId + "\" />";
        requestFields = requestFields + "<input type=\"hidden\" name=\"FailureReason.Code\" value=\"" + e.StripeError.Code + "\" />";
        requestFields = requestFields + "<input type=\"hidden\" name=\"FailureReason.Error\" value=\"" + e.StripeError.Error + "\" />";
        requestFields = requestFields + "<input type=\"hidden\" name=\"FailureReason.ErrorSubscription\" value=\"" + e.StripeError.ErrorSubscription + "\" />";
        requestFields = requestFields + "<input type=\"hidden\" name=\"FailureReason.ErrorType\" value=\"" + e.StripeError.ErrorType + "\" />";
        requestFields = requestFields + "<input type=\"hidden\" name=\"FailureReason.Message\" value=\"" + e.StripeError.Message + "\" />";
        requestFields = requestFields + "<input type=\"hidden\" name=\"FailureReason.Parameter\" value=\"" + e.StripeError.Parameter + "\" />";

        string paymentForm = PaymentMethodService.Instance.Get( order.StoreId, order.PaymentInformation.PaymentMethodId.Value ).GeneratePaymentForm( order, requestFields );

        //Force the form to auto submit
        paymentForm += "<script type=\"text/javascript\">document.forms[0].submit();</script>";

        //Write out the form
        HttpContext.Current.Response.Clear();
        HttpContext.Current.Response.Write( paymentForm );
        HttpContext.Current.Response.End();
      } catch ( Exception exp ) {
        LoggingService.Instance.Error<Stripe>( "Stripe(" + order.CartNumber + ") - ProcessCallback", exp );
      }

      return callbackInfo;
    }

    public override string ProcessRequest( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      string response = "";

      try {
        order.MustNotBeNull( "order" );
        request.MustNotBeNull( "request" );
        settings.MustNotBeNull( "settings" );

        // If in test mode, write out the form data to a text file
        if ( settings.ContainsKey( "mode" ) && settings[ "mode" ] == "test" ) {
          LogRequest<Stripe>( request, logPostData: true );
        }

        //Stripe supports webhooks
        StripeEvent stripeEvent = GetStripeEvent( request );
        StripeCharge charge = Mapper<StripeCharge>.MapFromJson( stripeEvent.Data.Object.ToString() );

        if ( stripeEvent.Type.StartsWith( "charge." ) ) {
          PaymentState paymentState = GetPaymentState( charge );
          if ( order.TransactionInformation.PaymentState != paymentState ) {
            order.TransactionInformation.TransactionId = charge.Id;
            order.TransactionInformation.PaymentState = paymentState;
            order.Save();
          }
        }


      } catch ( Exception exp ) {
        LoggingService.Instance.Error<Stripe>( "Stripe(" + order.CartNumber + ") - ProcessRequest", exp );
      }

      return response;
    }

    public override ApiInfo GetStatus( Order order, IDictionary<string, string> settings ) {
      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "mode", "settings" );
        settings.MustContainKey( settings[ "mode" ] + "_secret_key", "settings" );

        StripeChargeService chargeService = new StripeChargeService( settings[ settings[ "mode" ] + "_secret_key" ] );
        StripeCharge charge = chargeService.Get( order.TransactionInformation.TransactionId );

        return new ApiInfo( charge.Id, GetPaymentState( charge ) );
      } catch ( Exception exp ) {
        LoggingService.Instance.Error<Stripe>( "Stripe(" + order.OrderNumber + ") - GetStatus", exp );
      }

      return null;
    }

    public override ApiInfo CapturePayment( Order order, IDictionary<string, string> settings ) {
      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "mode", "settings" );
        settings.MustContainKey( settings[ "mode" ] + "_secret_key", "settings" );

        StripeChargeService chargeService = new StripeChargeService( settings[ settings[ "mode" ] + "_secret_key" ] );
        StripeCharge charge = chargeService.Capture( order.TransactionInformation.TransactionId, (int)order.TransactionInformation.AmountAuthorized.Value * 100 );

        return new ApiInfo( charge.Id, GetPaymentState( charge ) );
      } catch ( Exception exp ) {
        LoggingService.Instance.Error<Stripe>( "Stripe(" + order.OrderNumber + ") - GetStatus", exp );
      }

      return null;
    }

    public override ApiInfo RefundPayment( Order order, IDictionary<string, string> settings ) {
      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "mode", "settings" );
        settings.MustContainKey( settings[ "mode" ] + "_secret_key", "settings" );

        StripeRefundService refundService = new StripeRefundService(settings[settings["mode"] + "_secret_key"]);
        StripeRefund refund = refundService.Create(order.TransactionInformation.TransactionId);
        StripeCharge charge = refund.Charge;

        if (!charge.Refunded)
        {
            StripeChargeService chargeService = new StripeChargeService(settings[settings["mode"] + "_secret_key"]);
            charge = chargeService.Capture(refund.ChargeId);
        }

        return new ApiInfo( charge.Id, GetPaymentState( charge ) );
      } catch ( Exception exp ) {
        LoggingService.Instance.Error<Stripe>( "Stripe(" + order.OrderNumber + ") - RefundPayment", exp );
      }

      return null;
    }

    public override ApiInfo CancelPayment( Order order, IDictionary<string, string> settings ) {
      return RefundPayment( order, settings );
    }

    public override string GetLocalizedSettingsKey( string settingsKey, CultureInfo culture ) {
      switch ( settingsKey ) {
        case "form_url":
          return settingsKey + "<br/><small>The url of the page with the Stripe payment form on - e.g. /payment/</small>";
        case "continue_url":
          return settingsKey + "<br/><small>The url to navigate to after payment is processed - e.g. /confirmation/</small>";
        case "cancel_url":
          return settingsKey + "<br/><small>The url to navigate to if the customer cancels the payment - e.g. /cancel/</small>";
        case "capture":
          return settingsKey + "<br/><small>Flag indicating if a payment should be captured instantly - true/false.</small>";
        case "validate_cvc":
          return settingsKey + "<br/><small>Flag indicating whether to validate the credit cards cvc number - true/false.</small>";
        case "validate_address":
          return settingsKey + "<br/><small>Flag indicating whether to validate the credit cards address matches the billing address - true/false.</small>";
        case "address_property_alias":
          return settingsKey + "<br/><small>The alias of the field containing the billing address - e.g. billingStreetAddress.</small>";
        case "validate_zipcode":
          return settingsKey + "<br/><small>Flag indicating whether to validate the credit cards zip code matches the billing zip code - true/false.</small>";
        case "zipcode_property_alias":
          return settingsKey + "<br/><small>The alias of the field containing the billing zip code - e.g. billingZipCode.</small>";
        case "validate_country":
          return settingsKey + "<br/><small>Flag indicating whether to validate the credit cards country matches the billing country - true/false.</small>";
        case "test_secret_key":
          return settingsKey + "<br/><small>Your test stripe secret key.</small>";
        case "test_public_key":
          return settingsKey + "<br/><small>Your test stripe public key.</small>";
        case "live_secret_key":
          return settingsKey + "<br/><small>Your live stripe secret key.</small>";
        case "live_public_key":
          return settingsKey + "<br/><small>Your live stripe public key.</small>";
        case "mode":
          return settingsKey + "<br/><small>The mode of the provider - test/live.</small>";
        default:
          return base.GetLocalizedSettingsKey( settingsKey, culture );
      }
    }

    protected PaymentState GetPaymentState( StripeCharge charge ) {
      PaymentState paymentState = PaymentState.Initialized;

      if ( charge.Paid ) {
        paymentState = PaymentState.Authorized;

        if ( charge.Captured != null && charge.Captured.Value ) {
          paymentState = PaymentState.Captured;

          if ( charge.Refunded ) {
            paymentState = PaymentState.Refunded;
          }
        } else {
          if ( charge.Refunded ) {
            paymentState = PaymentState.Cancelled;
          }
        }
      }

      return paymentState;
    }

    protected StripeEvent GetStripeEvent( HttpRequest request ) {
      StripeEvent stripeEvent = null;

      if ( HttpContext.Current.Items[ "TC_StripeEvent" ] != null ) {
        stripeEvent = (StripeEvent)HttpContext.Current.Items[ "TC_StripeEvent" ];
      } else {
        try {
          if ( request.InputStream.CanSeek ) {
            request.InputStream.Seek( 0, SeekOrigin.Begin );
          }

          using ( StreamReader reader = new StreamReader( request.InputStream ) ) {
            stripeEvent = StripeEventUtility.ParseEvent( reader.ReadToEnd() );

            HttpContext.Current.Items[ "TC_StripeEvent" ] = stripeEvent;
          }
        } catch {
        }
      }

      return stripeEvent;
    }
  }
}
