
using System.Text;
using System;
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
        byte[] byte_data = new byte[ str.Length ];
        byte_data = Encoding.UTF8.GetBytes( str );
        string encodedData = Convert.ToBase64String( byte_data );
        return encodedData;
      } catch ( Exception e ) {
        throw new Exception( e.Message );
      }
    }

    public static string Base64Decode( this string str ) {
      try {
        UTF8Encoding encoder = new UTF8Encoding();
        Decoder utf8Decode = encoder.GetDecoder();
        byte[] byte_data = Convert.FromBase64String( str );
        int charCount = utf8Decode.GetCharCount( byte_data, 0, byte_data.Length );
        char[] decoded_char = new char[ charCount ];
        utf8Decode.GetChars( byte_data, 0, byte_data.Length, decoded_char, 0 );
        string result = new String( decoded_char );
        return result;
      } catch ( Exception e ) {
        throw new Exception( e.Message );
      }
    }

  }
}
