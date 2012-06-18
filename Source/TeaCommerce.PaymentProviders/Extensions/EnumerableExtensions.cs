using System.Collections;
using System.Text;

namespace TeaCommerce.PaymentProviders.Extensions {
  public static class EnumerableExtensions {

    /// <summary>
    /// Joins a collection of elements with a seperator text for each element
    /// </summary>
    /// <param name="source">Collection of elements to join</param>
    /// <param name="seperator">Seperator text between each element</param>
    /// <returns>String containing each element seperated by the <paramref name="seperator"/></returns>
    public static string Join( this IEnumerable source, string seperator ) {
      "".Truncate( 100, "" );
      string returnStr = string.Empty;

      StringBuilder sb = new StringBuilder();
      IEnumerator enumerator = source.GetEnumerator();
      while ( enumerator.MoveNext() ) {
        sb.Append( enumerator.Current );
        sb.Append( seperator );
      }
      if ( sb.Length != 0 )
        returnStr = sb.ToString( 0, sb.Length - seperator.Length );
      return returnStr;
    }

  }
}
