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
        /// Public entrypoint to analyze a decoded QR payload string (BIP-21, EIP-681, BIP39, Electrum, WIF, BIP38, Hex, Address).
        /// </summary>
        public static QrDecodeResult AnalyzeDecodedString(string content) => AnalyzeContent(content);

        /// <summary>
        /// Analyze decoded content to determine type
        /// </summary>
        private static QrDecodeResult AnalyzeContent(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
                return new QrDecodeResult { Success = false, Error = "Empty QR content" };

            var result = new QrDecodeResult { Success = true, RawContent = content };

            // Strip BIP-21 / EIP-681 URI schemes (case-insensitive)
            content = Regex.Replace(content.Trim(), @"^(bitcoin|ethereum|solana|litecoin|dogecoin|bcash):", "", RegexOptions.IgnoreCase).Trim();
            if (content.StartsWith("pay-", StringComparison.OrdinalIgnoreCase))
                content = content[4..].Trim();

            // Strip query parameters (?amount=0.5&label=...) and EIP-681 chainId/method suffixes (@1/transfer)
            int queryIdx = content.IndexOf('?');
            if (queryIdx > 0)
                content = content[..queryIdx].Trim();
            int atIdx = content.IndexOf('@');
            if (atIdx > 0 && content.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                content = content[..atIdx].Trim();
            int slashIdx = content.IndexOf('/');
            if (slashIdx > 0 && content.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                content = content[..slashIdx].Trim();

            // Check for BIP39 or Electrum mnemonic (12, 15, 18, 21, or 24 words)
            var words = content.Split(new[] { ' ', '\t', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length >= 12 && words.Length <= 24 && words.Length % 3 == 0)
            {
                if (Array.TrueForAll(words, w => Regex.IsMatch(w, @"^[a-zA-Z]+$")))
                {
                    var normalizedMnemonic = string.Join(" ", Array.ConvertAll(words, w => w.ToLowerInvariant()));
                    result.ContentType = QrContentType.Mnemonic;
                    result.Mnemonic = normalizedMnemonic;
                    result.WordCount = words.Length;
                    result.IsValidChecksum = CryptoWalletGenerator.IsValidMnemonic(normalizedMnemonic) ||
                                             CryptoWalletGenerator.IsElectrumMnemonic(normalizedMnemonic, out _);
                    result.IsRestrictedTrivial = MnemonicTracker.IsRestrictedTrivialMnemonic(normalizedMnemonic);
                    result.AlreadyScannedEmpty = GlobalMnemonicTracker.Instance.WasScanned(normalizedMnemonic);
                    return result;
                }
            }

            // Check for WIF private key (starts with 5, K, or L for mainnet) or BIP38 encrypted key (starts with 6P)
            if (Regex.IsMatch(content, @"^[5KL][1-9A-HJ-NP-Za-km-z]{50,51}$") ||
                Regex.IsMatch(content, @"^6P[1-9A-HJ-NP-Za-km-z]{56}$"))
            {
                result.ContentType = QrContentType.WifPrivateKey;
                result.PrivateKey = content;
                result.IsValidChecksum = true;
                return result;
            }

            // Check for hex private key (64 hex characters)
            if (Regex.IsMatch(content, @"^(0x)?[0-9a-fA-F]{64}$"))
            {
                var hexKey = content.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                    ? content[2..].ToLowerInvariant()
                    : content.ToLowerInvariant();
                result.ContentType = QrContentType.HexPrivateKey;
                result.PrivateKey = hexKey;
                result.IsRestrictedTrivial = MnemonicTracker.IsRestrictedTrivialPrivateKey(hexKey);
                result.AlreadyScannedEmpty = GlobalMnemonicTracker.Instance.WasScanned("pk:" + hexKey);
                return result;
            }

            // Check for Bitcoin address (Legacy 1..., SegWit 3..., Native SegWit bc1q..., Taproot bc1p...)
            if (Regex.IsMatch(content, @"^(1|3)[1-9A-HJ-NP-Za-km-z]{25,34}$") ||
                Regex.IsMatch(content, @"^bc1[qpzry9x8gf2tvdw0s3jn54khce6mua7l]{39,89}$", RegexOptions.IgnoreCase))
            {
                result.ContentType = QrContentType.BitcoinAddress;
                result.Address = content;
                result.AlreadyScannedEmpty = GlobalMnemonicTracker.Instance.IsAddressOrKeyScannedEmpty(content);
                return result;
            }

            // Check for Ethereum address
            if (Regex.IsMatch(content, @"^0x[0-9a-fA-F]{40}$"))
            {
                result.ContentType = QrContentType.EthereumAddress;
                result.Address = content;
                result.AlreadyScannedEmpty = GlobalMnemonicTracker.Instance.IsAddressOrKeyScannedEmpty(content);
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
        public bool IsValidChecksum { get; set; }
        public bool IsRestrictedTrivial { get; set; }
        public bool AlreadyScannedEmpty { get; set; }
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
