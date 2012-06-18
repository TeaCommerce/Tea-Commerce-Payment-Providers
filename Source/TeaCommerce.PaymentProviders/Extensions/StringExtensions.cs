
namespace TeaCommerce.PaymentProviders.Extensions {
  public static class StringExtensions {

    /// <summary>
    /// Truncates a string to the specified length
    /// </summary>
    /// <param name="str">String to truncate</param>
    /// <param name="maxLength">Max length of the string</param>
    /// <returns>Truncated string</returns>
    public static string Truncate( this string str, int maxLength ) {
      return str.Truncate( maxLength, string.Empty );
    }

    /// <summary>
    /// Truncates the specified original text and appends the <paramref name="append"/> string. The resulting text may therefor be longer than the specified <paramref name="maxLength"/>
    /// </summary>
    /// <param name="str">String to truncate</param>
    /// <param name="maxLength">Max length of the string</param>
    /// <param name="append">The string to append if <paramref name="maxLength"/> is reached</param>
    /// <returns>Truncated string</returns>
    public static string Truncate( this string str, int maxLength, string append ) {
      if ( str == null || maxLength > str.Length )
        return str;

      return str.Substring( 0, maxLength ) + append;
    }

  }
}
