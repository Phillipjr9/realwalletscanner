using System;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using ZXing;
using ZXing.Common;
using ZXing.Windows.Compatibility;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Import mnemonics and private keys from QR code images
    /// Supports BIP39 mnemonics, WIF private keys, and hex private keys
    /// </summary>
    public static class QrCodeImporter
    {
        /// <summary>
        /// Decode QR code from image file
        /// </summary>
        public static QrDecodeResult DecodeFromFile(string imagePath)
        {
            if (!File.Exists(imagePath))
                throw new FileNotFoundException($"Image file not found: {imagePath}");

            using var bitmap = (Bitmap)Image.FromFile(imagePath);
            return DecodeFromBitmap(bitmap);
        }

        /// <summary>
        /// Decode QR code from bitmap
        /// </summary>
        public static QrDecodeResult DecodeFromBitmap(Bitmap bitmap)
        {
            try
            {
                var reader = new BarcodeReader
                {
                    AutoRotate = true,
                    Options = new DecodingOptions
                    {
                        TryHarder = true,
                        PossibleFormats = new[] { BarcodeFormat.QR_CODE }
                    }
                };

                var result = reader.Decode(bitmap);
                
                if (result == null)
                    return new QrDecodeResult { Success = false, Error = "No QR code found in image" };

                var content = result.Text.Trim();
                return AnalyzeContent(content);
            }
            catch (Exception ex)
            {
                return new QrDecodeResult { Success = false, Error = $"Decode error: {ex.Message}" };
            }
        }

        /// <summary>
        /// Decode QR code from clipboard image
        /// </summary>
        public static QrDecodeResult DecodeFromClipboard()
        {
            try
            {
                if (!System.Windows.Forms.Clipboard.ContainsImage())
                    return new QrDecodeResult { Success = false, Error = "No image in clipboard" };

                var image = System.Windows.Forms.Clipboard.GetImage();
                if (image == null)
                    return new QrDecodeResult { Success = false, Error = "Could not get image from clipboard" };

                using var bitmap = new Bitmap(image);
                return DecodeFromBitmap(bitmap);
            }
            catch (Exception ex)
            {
                return new QrDecodeResult { Success = false, Error = $"Clipboard error: {ex.Message}" };
            }
        }

        /// <summary>
        /// Analyze decoded content to determine type
        /// </summary>
        private static QrDecodeResult AnalyzeContent(string content)
        {
            var result = new QrDecodeResult { Success = true, RawContent = content };

            // Remove common prefixes
            content = content
                .Replace("bitcoin:", "")
                .Replace("ethereum:", "")
                .Replace("solana:", "")
                .Trim();

            // Check for BIP39 mnemonic (12, 15, 18, 21, or 24 words)
            var words = content.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 12 && words.Length <= 24 && words.Length % 3 == 0)
            {
                // All words should be lowercase letters
                if (Array.TrueForAll(words, w => Regex.IsMatch(w, @"^[a-z]+$")))
                {
                    result.ContentType = QrContentType.Mnemonic;
                    result.Mnemonic = string.Join(" ", words);
                    result.WordCount = words.Length;
                    return result;
                }
            }

            // Check for WIF private key (starts with 5, K, or L for mainnet)
            if (Regex.IsMatch(content, @"^[5KL][1-9A-HJ-NP-Za-km-z]{50,51}$"))
            {
                result.ContentType = QrContentType.WifPrivateKey;
                result.PrivateKey = content;
                return result;
            }

            // Check for hex private key (64 hex characters)
            if (Regex.IsMatch(content, @"^(0x)?[0-9a-fA-F]{64}$"))
            {
                result.ContentType = QrContentType.HexPrivateKey;
                result.PrivateKey = content.Replace("0x", "").ToLower();
                return result;
            }

            // Check for Bitcoin address
            if (Regex.IsMatch(content, @"^(1|3)[1-9A-HJ-NP-Za-km-z]{25,34}$") ||
                Regex.IsMatch(content, @"^bc1[qpzry9x8gf2tvdw0s3jn54khce6mua7l]{39,59}$"))
            {
                result.ContentType = QrContentType.BitcoinAddress;
                result.Address = content;
                return result;
            }

            // Check for Ethereum address
            if (Regex.IsMatch(content, @"^0x[0-9a-fA-F]{40}$"))
            {
                result.ContentType = QrContentType.EthereumAddress;
                result.Address = content;
                return result;
            }

            // Unknown content
            result.ContentType = QrContentType.Unknown;
            return result;
        }

        /// <summary>
        /// Generate QR code for a string
        /// </summary>
        public static Bitmap GenerateQrCode(string content, int size = 300)
        {
            var writer = new BarcodeWriter
            {
                Format = BarcodeFormat.QR_CODE,
                Options = new EncodingOptions
                {
                    Width = size,
                    Height = size,
                    Margin = 1
                }
            };

            return writer.Write(content);
        }

        /// <summary>
        /// Save QR code to file
        /// </summary>
        public static void SaveQrCode(string content, string filePath, int size = 300)
        {
            using var bitmap = GenerateQrCode(content, size);
            
            var format = Path.GetExtension(filePath).ToLower() switch
            {
                ".jpg" or ".jpeg" => System.Drawing.Imaging.ImageFormat.Jpeg,
                ".gif" => System.Drawing.Imaging.ImageFormat.Gif,
                ".bmp" => System.Drawing.Imaging.ImageFormat.Bmp,
                _ => System.Drawing.Imaging.ImageFormat.Png
            };
            
            bitmap.Save(filePath, format);
        }
    }

    public class QrDecodeResult
    {
        public bool Success { get; set; }
        public string Error { get; set; }
        public string RawContent { get; set; }
        public QrContentType ContentType { get; set; }
        public string Mnemonic { get; set; }
        public string PrivateKey { get; set; }
        public string Address { get; set; }
        public int WordCount { get; set; }
    }

    public enum QrContentType
    {
        Unknown,
        Mnemonic,
        WifPrivateKey,
        HexPrivateKey,
        BitcoinAddress,
        EthereumAddress
    }
}
