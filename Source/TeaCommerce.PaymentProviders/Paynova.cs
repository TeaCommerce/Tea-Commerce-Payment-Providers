using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Web;
using Paynova.Api.Client;
using Paynova.Api.Client.Model;
using Paynova.Api.Client.Requests;
using Paynova.Api.Client.Responses;
using Paynova.Api.Client.Security;
using TeaCommerce.Data;
using TeaCommerce.Data.Payment;
using umbraco.BusinessLogic;
using PaynovaPaymentMethod = Paynova.Api.Client.Model.PaymentMethod;

namespace TeaCommerce.PaymentProviders {
  public class Paynova : APaymentProvider {

    public override bool AllowsGetStatus { get { return false; } }

    public override Dictionary<string, string> DefaultSettings {
      get {
        if ( defaultSettings == null ) {
          defaultSettings = new Dictionary<string, string>();
          defaultSettings[ "merchantId" ] = "";
          defaultSettings[ "customerLanguageCode" ] = "ENG";
          defaultSettings[ "urlRedirectSuccess" ] = "";
          defaultSettings[ "urlRedirectCancel" ] = "";
          defaultSettings[ "paymentMethods" ] = "";
          defaultSettings[ "secretKey" ] = "";
          defaultSettings[ "apiPassword" ] = "";
          defaultSettings[ "unitMeasure" ] = "pcs";
          defaultSettings[ "testMode" ] = "1";
          defaultSettings[ "productNumberPropertyAlias" ] = "productNumber";
          defaultSettings[ "productNamePropertyAlias" ] = "productName";
          defaultSettings[ "shippingMethodProductNumber" ] = "1000";
          defaultSettings[ "paymentMethodProductNumber" ] = "2000";
        }
        return defaultSettings;
      }
    }

    private string _formPostUrl;
    public override string FormPostUrl { get { return _formPostUrl; } }
    public override bool FinalizeAtContinueUrl { get { return true; } }

    public override Dictionary<string, string> GenerateForm( Order order, string teaCommerceContinueUrl, string teaCommerceCancelUrl, string teaCommerceCallBackUrl, string teaCommerceCommunicationUrl, Dictionary<string, string> settings ) {
      Dictionary<string, string> inputFields = new Dictionary<string, string>();

      IPaynovaClient client = GetClient( settings );

      //Create order request
      CreateOrderRequest createOrderRequest = new CreateOrderRequest( order.Name, order.CurrencyISOCode, order.TotalPrice );

      //Add line items
      string unitMeasure = settings[ "unitMeasure" ];
      foreach ( OrderLine orderLine in order.OrderLines ) {
        createOrderRequest.AddLineItem( new LineItem( orderLine.Id.ToString( CultureInfo.InvariantCulture ), orderLine.Properties.Single( p => p.Alias == settings[ "productNumberPropertyAlias" ] ).Value, orderLine.Properties.Single( p => p.Alias == settings[ "productNamePropertyAlias" ] ).Value, unitMeasure, orderLine.VAT, orderLine.Quantity, orderLine.UnitPriceWithoutVAT, orderLine.TotalPrice, orderLine.TotalVAT ) );
      }

      if ( order.ShippingFee > 0M ) {
        createOrderRequest.AddLineItem( new LineItem( "shipping_" + order.ShippingMethod.Id, settings[ "shippingMethodProductNumber" ], order.ShippingMethod.Name, unitMeasure, order.ShippingVAT, 1M, order.ShippingFeeWithoutVAT, order.ShippingFee, order.ShippingFeeVAT ) );
      }

      if ( order.PaymentFee > 0M ) {
        createOrderRequest.AddLineItem( new LineItem( "payment_" + order.PaymentMethod.Id, settings[ "paymentMethodProductNumber" ], order.PaymentMethod.Name, unitMeasure, order.PaymentVAT, 1M, order.PaymentFeeWithoutVAT, order.PaymentFee, order.PaymentFeeVAT ) );
      }

      #region Customer information
      createOrderRequest.Customer = new Customer();
      createOrderRequest.BillTo = new NameAndAddress();
      createOrderRequest.ShipTo = new NameAndAddress();
      createOrderRequest.Customer.EmailAddress = order.Email;
      createOrderRequest.Customer.Name.CompanyName = createOrderRequest.BillTo.Name.CompanyName = GetOrderProperty( order, settings, "company" );
      createOrderRequest.Customer.Name.Title = createOrderRequest.BillTo.Name.Title = createOrderRequest.BillTo.Name.CompanyName = GetOrderProperty( order, settings, "title" );
      createOrderRequest.Customer.Name.FirstName = createOrderRequest.BillTo.Name.FirstName = order.FirstName;
      createOrderRequest.Customer.Name.MiddleNames = createOrderRequest.BillTo.Name.MiddleNames = createOrderRequest.BillTo.Name.CompanyName = GetOrderProperty( order, settings, "middleNames" );
      createOrderRequest.Customer.Name.LastName = createOrderRequest.BillTo.Name.LastName = order.LastName;
      createOrderRequest.Customer.Name.Suffix = createOrderRequest.BillTo.Name.Suffix = GetOrderProperty( order, settings, "suffix" );
      createOrderRequest.Customer.HomeTelephone = GetOrderProperty( order, settings, "phone" );
      createOrderRequest.Customer.WorkTelephone = GetOrderProperty( order, settings, "workPhone" );
      createOrderRequest.Customer.MobileTelephone = GetOrderProperty( order, settings, "mobile" );
      createOrderRequest.BillTo.Address.Street1 = GetOrderProperty( order, settings, "streetAddress" );
      createOrderRequest.BillTo.Address.Street2 = GetOrderProperty( order, settings, "streetAddress2" );
      createOrderRequest.BillTo.Address.Street3 = GetOrderProperty( order, settings, "streetAddress3" );
      createOrderRequest.BillTo.Address.Street4 = GetOrderProperty( order, settings, "streetAddress4" );
      createOrderRequest.BillTo.Address.City = GetOrderProperty( order, settings, "city" );
      createOrderRequest.BillTo.Address.PostalCode = GetOrderProperty( order, settings, "zipCode" );
      createOrderRequest.BillTo.Address.CountryCode = order.Country.CountryCode;

      createOrderRequest.ShipTo.Name.CompanyName = GetOrderProperty( order, settings, "shipping_company" );
      createOrderRequest.ShipTo.Name.Title = GetOrderProperty( order, settings, "shipping_title" );
      createOrderRequest.ShipTo.Name.FirstName = GetOrderProperty( order, settings, "shipping_firstName" );
      createOrderRequest.ShipTo.Name.MiddleNames = GetOrderProperty( order, settings, "shipping_middleNames" );
      createOrderRequest.ShipTo.Name.LastName = GetOrderProperty( order, settings, "shipping_lastName" );
      createOrderRequest.ShipTo.Name.Suffix = GetOrderProperty( order, settings, "shipping_suffix" );
      createOrderRequest.ShipTo.Address.Street1 = GetOrderProperty( order, settings, "shipping_streetAddress" );
      createOrderRequest.ShipTo.Address.Street2 = GetOrderProperty( order, settings, "shipping_streetAddress2" );
      createOrderRequest.ShipTo.Address.Street3 = GetOrderProperty( order, settings, "shipping_streetAddress3" );
      createOrderRequest.ShipTo.Address.Street4 = GetOrderProperty( order, settings, "shipping_streetAddress4" );
      createOrderRequest.ShipTo.Address.City = GetOrderProperty( order, settings, "shipping_city" );
      createOrderRequest.ShipTo.Address.PostalCode = GetOrderProperty( order, settings, "shipping_zipCode" );

      #endregion

      CreateOrderResponse createOrderResponse = client.CreateOrder( createOrderRequest );

      //Initialize payment request
      InterfaceOptions interfaceOptions = new InterfaceOptions( InterfaceId.Aero, settings[ "customerLanguageCode" ], new Uri( teaCommerceContinueUrl ), new Uri( teaCommerceCancelUrl ), new Uri( teaCommerceContinueUrl ) );
      InitializePaymentRequest initializePaymentRequest = new InitializePaymentRequest( createOrderResponse.OrderId, order.TotalPrice, PaymentChannelId.Web, interfaceOptions );

      if ( settings.ContainsKey( "paymentMethods" ) && !string.IsNullOrEmpty( settings[ "paymentMethods" ] ) ) {
        initializePaymentRequest.WithPaymentMethods( settings[ "paymentMethods" ].Split( ',' ).Select( i => PaynovaPaymentMethod.Custom( int.Parse( i ) ) ) );
      }

      InitializePaymentResponse initializePaymentResponse = client.InitializePayment( initializePaymentRequest );
      _formPostUrl = initializePaymentResponse.Url;

      return inputFields;
    }

    private string GetOrderProperty( Order order, Dictionary<string, string> settings, string propertyAlias ) {
      string value = "";

      if ( settings.ContainsKey( propertyAlias + "PropertyAlias" ) && !string.IsNullOrEmpty( settings[ propertyAlias + "PropertyAlias" ] ) ) {
        propertyAlias = settings[ propertyAlias + "PropertyAlias" ];
      }
      OrderProperty property = order.Properties.SingleOrDefault( p => p.Alias == propertyAlias );
      if ( property != null ) {
        value = property.Value;
      }
      return value;
    }

    public override string GetContinueUrl( Dictionary<string, string> settings ) {
      return settings[ "urlRedirectSuccess" ];
    }

    public override string GetCancelUrl( Dictionary<string, string> settings ) {
      return settings[ "urlRedirectCancel" ];
    }

    public override CallbackInfo ProcessCallback( Order order, HttpRequest request, Dictionary<string, string> settings ) {
      //using ( StreamWriter writer = new StreamWriter( File.Create( HttpContext.Current.Server.MapPath( "~/paynovaTestCallback.txt" ) ) ) ) {
      //  writer.WriteLine( "FORM:" );
      //  foreach ( string k in request.Form.Keys ) {
      //    writer.WriteLine( k + " : " + request.Form[ k ] );
      //  }
      //  writer.Flush();
      //}

      string errorMessage = string.Empty;

      PostbackDigest postbackDigest = new PostbackDigest( settings[ "secretKey" ] );

      if ( postbackDigest.Validate( request.Form ) ) {
        decimal amountAuthorized = decimal.Parse( request.Form[ "PAYMENT_1_AMOUNT" ], CultureInfo.InvariantCulture );
        string transaction = request.Form[ "PAYMENT_1_TRANSACTION_ID" ];
        string paymentType = request.Form[ "PAYMENT_1_PAYMENT_METHOD_NAME" ];
        string paymentIdentifier = request.Form[ "PAYMENT_1_CARD_LAST_FOUR" ];

        PaymentStatus? paymentStatus = null;
        switch ( request.Form[ "PAYMENT_1_STATUS" ] ) {
          case "Pending":
            paymentStatus = PaymentStatus.PendingExternalSystem;
            break;
          case "Completed":
          case "PartiallyCompleted":
            paymentStatus = PaymentStatus.Captured;
            break;
          case "Authorized":
            paymentStatus = PaymentStatus.Authorized;
            break;
        }

        if ( paymentStatus != null ) {
          return new CallbackInfo( order.Name, amountAuthorized, transaction, paymentStatus.Value, paymentType, paymentIdentifier );
        }
      } else
        errorMessage = "Tea Commerce - Paynova - digest check failed";

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new CallbackInfo( errorMessage );
    }

    public override APIInfo GetStatus( Order order, Dictionary<string, string> settings ) {
      throw new NotImplementedException();
    }

    public override APIInfo CapturePayment( Order order, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      try {
        FinalizeAuthorizationRequest request = new FinalizeAuthorizationRequest( order.TransactionPaymentTransactionId, order.TotalPrice );
        FinalizeAuthorizationResponse response = GetClient( settings ).FinalizeAuthorization( request );
        return new APIInfo( response.TransactionId, PaymentStatus.Captured );
      } catch ( Exception exp ) {
        errorMessage = "Paynova - Refund payment: " + exp;
      }

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    public override APIInfo RefundPayment( Order order, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      try {
        RefundPaymentRequest request = new RefundPaymentRequest( order.TransactionPaymentTransactionId, order.TotalPrice );
        RefundPaymentResponse response = GetClient( settings ).RefundPayment( request );
        return new APIInfo( response.TransactionId, PaymentStatus.Refunded );
      } catch ( Exception exp ) {
        errorMessage = "Paynova - Refund payment: " + exp;
      }

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    public override APIInfo CancelPayment( Order order, Dictionary<string, string> settings ) {
      string errorMessage = string.Empty;

      try {
        AnnulAuthorizationRequest request = new AnnulAuthorizationRequest( order.TransactionPaymentTransactionId, order.TotalPrice );
        GetClient( settings ).AnnulAuthorization( request );
        return new APIInfo( order.TransactionPaymentTransactionId, PaymentStatus.Cancelled );
      } catch ( Exception exp ) {
        errorMessage = "Paynova - Cancel payment: " + exp;
      }

      Log.Add( LogTypes.Error, -1, errorMessage );
      return new APIInfo( errorMessage );
    }

    protected IPaynovaClient GetClient( IDictionary<string, string> settings ) {
      return new PaynovaClient( settings.ContainsKey( "testMode" ) && settings[ "testMode" ] == "1" ? "https://testapi.paynova.com/" : "https://api.paynova.com", settings[ "merchantId" ], settings[ "apiPassword" ] );
    }

  }
}