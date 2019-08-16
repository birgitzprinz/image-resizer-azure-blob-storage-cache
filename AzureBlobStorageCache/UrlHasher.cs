using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ImageResizer.Plugins.AzureBlobStorageCache
{
    public class UrlHasher
    {
        /// <summary>
        /// Builds a key for the cached version, using the hashcode of the normalized URL.
        /// </summary>
		/// <param name="url"></param>
        /// <returns></returns>
        public string Hash(string url)
        {
            SHA256 h = SHA256.Create();
            byte[] hash = h.ComputeHash(new UTF8Encoding().GetBytes(url));

            // Simple base16 encoding is enough
            return Base16Encode(hash);
        }

        protected string Base16Encode(byte[] bytes)
        {
            StringBuilder sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
                sb.Append(b.ToString("x", NumberFormatInfo.InvariantInfo).PadLeft(2, '0'));
            return sb.ToString();
        }
    }
}
