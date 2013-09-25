using System;
using System.Text;

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

    public static string Base64Encode( this string str ) {
      try {
        byte[] byteData = Encoding.UTF8.GetBytes( str );
        string encodedData = Convert.ToBase64String( byteData );
        return encodedData;
      } catch ( Exception e ) {
        throw new Exception( e.Message );
      }
    }

    public static string Base64Decode( this string str ) {
      try {
        UTF8Encoding encoder = new UTF8Encoding();
        Decoder utf8Decode = encoder.GetDecoder();
        byte[] byteData = Convert.FromBase64String( str );
        int charCount = utf8Decode.GetCharCount( byteData, 0, byteData.Length );
        char[] decodedChar = new char[ charCount ];
        utf8Decode.GetChars( byteData, 0, byteData.Length, decodedChar, 0 );
        string result = new String( decodedChar );
        return result;
      } catch ( Exception e ) {
        throw new Exception( e.Message );
      }
    }

  }
}
