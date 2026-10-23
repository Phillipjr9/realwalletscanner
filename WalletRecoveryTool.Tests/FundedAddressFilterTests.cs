using WalletRecoveryTool.Core;
using Xunit;

namespace WalletRecoveryTool.Tests;

public class FundedAddressFilterTests
{
    [Fact]
    public void GetStatusSummary_ShouldReportFundedFilterDisabledWhenNoDatasetExists()
    {
        var status = FundedAddressFilter.GetStatusSummary();

        Assert.Contains("BTC funded filter: disabled", status, System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ETH funded filter: disabled", status, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BloomFilterNegativeResult_ShouldNotSkipLiveVerification()
    {
        var settings = AppSettings.Current;
        var original = settings.EnableOfflineFilter;

        try
        {
            settings.EnableOfflineFilter = true;

            var options = new RecoveryOptions
            {
                ScanBitcoin = true,
                ScanEthereum = false,
                ScanSolana = false,
                ScanLitecoin = false,
                ScanDogecoin = false,
                ScanUSDT = false,
                ScanPolygon = false,
                ScanBnb = false,
                CheckBalancesRealTime = true
            };

            Assert.False(WalletRecoveryEngine.ShouldSkipApiCheckForBloomFilter(options, false, false, true));
        }
        finally
        {
            settings.EnableOfflineFilter = original;
        }
    }
}
