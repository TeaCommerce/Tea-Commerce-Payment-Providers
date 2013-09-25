/*
- * FileName: Stripe.cs
- * Description: A payment provider for handling payments via Stripe
- * Author: Matt Brailsford (@mattbrailsford)
- * Create Date: 2013-09-12
- */
using Stripe;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Web;
using System.Web.Hosting;
using TeaCommerce.Api.Common;
using TeaCommerce.Api.Infrastructure.Logging;
using TeaCommerce.Api.Models;
using TeaCommerce.Api.Services;
using TeaCommerce.Api.Web.PaymentProviders;

namespace TeaCommerce.PaymentProviders {
  [PaymentProvider( "Stripe" )]
  public class Stripe : APaymentProvider {

    public override string DocumentationLink { get { return "https://stripe.com/docs"; } }

    public override bool SupportsRetrievalOfPaymentStatus { get { return true; } }
    public override bool SupportsCapturingOfPayment { get { return true; } }
    public override bool SupportsRefundOfPayment { get { return true; } }
    public override bool SupportsCancellationOfPayment { get { return true; } }

    public override bool FinalizeAtContinueUrl { get { return true; } }
    public override bool AllowsCallbackWithoutOrderId { get { return true; } }

    public override IDictionary<string, string> DefaultSettings {
      get {
        return new Dictionary<string, string> {
          { "form_url", "" },
          { "continue_url", "" },
          { "cancel_url", "" },
          { "capture", "false" },
          { "test_secret_key", "" },
          { "test_public_key", "" },
          { "live_secret_key", "" },
          { "live_public_key", "" },
          { "mode", "test" },
        };
      }
    }

    public override PaymentHtmlForm GenerateHtmlForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, IDictionary<string, string> settings ) {
      order.MustNotBeNull( "order" );
      settings.MustNotBeNull( "settings" );
      settings.MustContainKey( "merchantnumber", "settings" );
      settings.MustContainKey( "form_url", "settings" );
      settings.MustContainKey( "mode", "settings" );
      settings.MustContainKey( settings[ "mode" ] + "_public_key", "settings" );

      PaymentHtmlForm htmlForm = new PaymentHtmlForm {
        Action = settings[ "form_url" ]
      };

      htmlForm.InputFields[ "api_key" ] = settings[ settings[ "mode" ] + "_public_key" ];
      htmlForm.InputFields[ "continue_url" ] = teaCommerceContinueUrl;
      htmlForm.InputFields[ "cancel_url" ] = teaCommerceCancelUrl;

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

      StripeEvent stripeEvent = GetStripeEvent( request );

      // We are only interested in charge events
      if ( stripeEvent != null && stripeEvent.Type.StartsWith( "charge." ) ) {
        StripeCharge stripeCharge = Mapper<StripeCharge>.MapFromJson( stripeEvent.Data.Object.ToString() );
        cartNumber = stripeCharge.Description;
      } else {
        HttpContext.Current.Response.StatusCode = (int)HttpStatusCode.BadRequest;
      }

      return cartNumber;
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, IDictionary<string, string> settings ) {
      try {
        order.MustNotBeNull( "order" );
        request.MustNotBeNull( "request" );
        settings.MustNotBeNull( "settings" );

        // If in test mode, write out the form data to a text file
        if ( settings.ContainsKey( "mode" ) && settings[ "mode" ] == "test" ) {
          using ( StreamWriter writer = new StreamWriter( File.Create( HostingEnvironment.MapPath( "~/stripe-callback-data.txt" ) ) ) ) {
            writer.WriteLine( "QueryString:" );
            foreach ( string k in request.QueryString.Keys ) {
              writer.WriteLine( k + " : " + request.QueryString[ k ] );
            }
            writer.WriteLine( "" );
            writer.WriteLine( "-----------------------------------------------------" );
            writer.WriteLine( "" );
            writer.WriteLine( "Form:" );
            foreach ( string k in request.Form.Keys ) {
              writer.WriteLine( k + " : " + request.Form[ k ] );
            }
            writer.Flush();
          }
        }

        // Check to see if being called as a result of a
        // stripe web hook request or not
        StripeEvent stripeEvent = GetStripeEvent( request );
        if ( stripeEvent == null ) {
          // Not a web hook request so process new order
          settings.MustContainKey( "mode", "settings" );
          settings.MustContainKey( settings[ "mode" ] + "_secret_key", "settings" );

          StripeChargeCreateOptions chargeOptions = new StripeChargeCreateOptions {
            AmountInCents = (int)( order.TotalPrice.WithVat * 100 ),
            Currency = CurrencyService.Instance.Get( order.StoreId, order.CurrencyId ).IsoCode,
            TokenId = request.Form[ "stripeToken" ],
            Description = order.CartNumber,
            Capture = settings.ContainsKey( "capture" ) && ( settings[ "capture" ].TryParse<bool>() ?? false )
          };

          StripeChargeService chargeService = new StripeChargeService( settings[ settings[ "mode" ] + "_secret_key" ] );
          StripeCharge charge = chargeService.Create( chargeOptions );

          if ( charge.AmountInCents != null && charge.Paid != null && charge.Paid != null ) {
            return new CallbackInfo( (decimal)charge.AmountInCents.Value / 100, charge.Id, charge.Captured != null && charge.Captured.Value ? PaymentState.Captured : PaymentState.Authorized );
          }
        } else {
          // A web hook request so update existing order
          StripeCharge charge = Mapper<StripeCharge>.MapFromJson( stripeEvent.Data.Object.ToString() );

          if (stripeEvent.Type.StartsWith("charge."))
          {
              var state = GetPaymentState(charge);
              if (order.TransactionInformation.PaymentState != state)
              {
                  order.TransactionInformation.TransactionId = charge.Id;
                  order.TransactionInformation.PaymentState = state;
                  order.Save();
              }
          }
        }
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "Stripe(" + order.CartNumber + ") - ProcessCallback" );
      }

      return null;
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
        LoggingService.Instance.Log( exp, "Stripe(" + order.OrderNumber + ") - GetStatus" );
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
        LoggingService.Instance.Log( exp, "Stripe(" + order.OrderNumber + ") - GetStatus" );
      }

      return null;
    }

    public override ApiInfo RefundPayment( Order order, IDictionary<string, string> settings ) {
      try {
        order.MustNotBeNull( "order" );
        settings.MustNotBeNull( "settings" );
        settings.MustContainKey( "mode", "settings" );
        settings.MustContainKey( settings[ "mode" ] + "_secret_key", "settings" );

        StripeChargeService chargeService = new StripeChargeService( settings[ settings[ "mode" ] + "_secret_key" ] );
        StripeCharge charge = chargeService.Refund( order.TransactionInformation.TransactionId );

        return new ApiInfo( charge.Id, GetPaymentState( charge ) );
      } catch ( Exception exp ) {
        LoggingService.Instance.Log( exp, "Stripe(" + order.OrderNumber + ") - RefundPayment" );
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

      if ( charge.Paid != null && charge.Paid.Value ) {
        paymentState = PaymentState.Authorized;

        if ( charge.Captured != null && charge.Captured.Value ) {
          paymentState = PaymentState.Captured;

          if ( charge.Refunded != null && charge.Refunded.Value ) {
            paymentState = PaymentState.Refunded;
          }
        } else {
          if ( charge.Refunded != null && charge.Refunded.Value ) {
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
