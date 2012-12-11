using System;
using System.Collections.Generic;
using TeaCommerce.Api.Common;

namespace TeaCommerce.PaymentProviders.Extensions {
  public static class AssertionsExtensions {

    public static void MustContainKey( this IDictionary<string, string> dictionary, string key, string paramName ) {
      dictionary.MustNotBeNull( "dictionary" );
      bool containsKey = dictionary.ContainsKey( key );
      if ( !containsKey || ( containsKey && string.IsNullOrEmpty( dictionary[ key ] ) ) ) {
        throw new ArgumentException( "Argument " + paramName + " must have a non empty value for the key " + key );
      }
    }

  }
}
