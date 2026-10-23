using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace WalletRecoveryTool.Core
{
    /// <summary>
    /// Minimal compatibility shim for the target-address pre-filter used by the scanner.
    /// It keeps a local set of watched addresses and quickly checks whether a generated wallet
    /// intersects any of those addresses before firing a more expensive balance check.
    /// </summary>
    public sealed class TargetAddressPreFilter
    {
        private readonly ConcurrentDictionary<string, string> _addresses = new(StringComparer.OrdinalIgnoreCase);

        public static TargetAddressPreFilter Instance { get; } = new();

        public int Count => _addresses.Count;

        public void Clear()
        {
            _addresses.Clear();
        }

        public void AddAddress(string address, string label = null)
        {
            if (string.IsNullOrWhiteSpace(address)) return;
            _addresses[address.Trim()] = string.IsNullOrWhiteSpace(label) ? address.Trim() : label.Trim();
        }

        public void LoadFromFile(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

            foreach (var line in File.ReadLines(path))
            {
                var address = line.Trim();
                if (!string.IsNullOrWhiteSpace(address))
                    AddAddress(address);
            }
        }

        public bool ContainsAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address)) return false;
            return _addresses.ContainsKey(address.Trim());
        }

        public bool MatchesAnyWalletAddress(CryptoWalletGenerator.WalletInfo wallet)
        {
            if (wallet == null || _addresses.Count == 0) return false;

            var candidates = new List<string>();
            if (!string.IsNullOrWhiteSpace(wallet.BitcoinAddress)) candidates.Add(wallet.BitcoinAddress);
            if (!string.IsNullOrWhiteSpace(wallet.BitcoinAddressSegWit)) candidates.Add(wallet.BitcoinAddressSegWit);
            if (!string.IsNullOrWhiteSpace(wallet.BitcoinAddressNativeSegWit)) candidates.Add(wallet.BitcoinAddressNativeSegWit);
            if (!string.IsNullOrWhiteSpace(wallet.BitcoinAddressTaproot)) candidates.Add(wallet.BitcoinAddressTaproot);
            if (!string.IsNullOrWhiteSpace(wallet.EthereumAddress)) candidates.Add(wallet.EthereumAddress);
            if (!string.IsNullOrWhiteSpace(wallet.SolanaAddress)) candidates.Add(wallet.SolanaAddress);
            if (!string.IsNullOrWhiteSpace(wallet.LitecoinAddress)) candidates.Add(wallet.LitecoinAddress);
            if (!string.IsNullOrWhiteSpace(wallet.DogecoinAddress)) candidates.Add(wallet.DogecoinAddress);
            if (wallet.AllBitcoinAddresses != null) candidates.AddRange(wallet.AllBitcoinAddresses.Where(a => !string.IsNullOrWhiteSpace(a)));
            if (wallet.AllEthereumAddresses != null) candidates.AddRange(wallet.AllEthereumAddresses.Where(a => !string.IsNullOrWhiteSpace(a)));

            foreach (var candidate in candidates)
            {
                if (_addresses.ContainsKey(candidate.Trim()))
                    return true;
            }

            return false;
        }
    }
}
