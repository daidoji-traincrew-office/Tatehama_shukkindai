using Microsoft.Extensions.Logging;
using Tatehama.Shukkindai.Server.Services.Certificate;

namespace Tatehama.Shukkindai.Server.UT.Services.Certificate;

public class CertificateLogTest
{
    [Theory]
    // 使用中の証明書の残り日数, 次の鍵がある(再起動すれば済む), 期待するレベル
    [InlineData(91, false, LogLevel.Information)]
    [InlineData(91, true, LogLevel.Information)]
    [InlineData(90, false, LogLevel.Warning)]   // 残り 90 日で、次の鍵が無い → 自動化が追いついていない
    [InlineData(90, true, LogLevel.Information)] // 次の鍵がある → 上げすぎない
    [InlineData(31, false, LogLevel.Warning)]
    [InlineData(30, false, LogLevel.Critical)]  // 残り 30 日で、次の鍵が無い → 人が対応する
    [InlineData(30, true, LogLevel.Warning)]
    [InlineData(1, true, LogLevel.Warning)]
    [InlineData(0, false, LogLevel.Critical)]   // 期限切れで、次の鍵が無い
    [InlineData(-1, false, LogLevel.Critical)]
    [InlineData(0, true, LogLevel.Error)]       // 期限切れだが、次の鍵があり、再起動すれば直る
    public void SetStatusLevel_RemainingDaysAndSuccessor_ChoosesLevel(
        double remainingDays, bool successorExists, LogLevel expected)
    {
        var actual = CertificateLog.SetStatusLevel(TimeSpan.FromDays(remainingDays), successorExists);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(60, LogLevel.Warning)]
    [InlineData(31, LogLevel.Warning)]
    [InlineData(30, LogLevel.Error)]
    [InlineData(-5, LogLevel.Error)]
    public void RestartRequiredLevel_RemainingDays_RaisesAsExpiryApproaches(double remainingDays, LogLevel expected)
    {
        Assert.Equal(expected, CertificateLog.RestartRequiredLevel(TimeSpan.FromDays(remainingDays)));
    }

    [Theory]
    [InlineData(90, LogLevel.Information)]
    [InlineData(14, LogLevel.Warning)]
    [InlineData(1, LogLevel.Warning)]
    public void NextSigningLevel_DaysUntilActivation_WarnsWhenClose(double days, LogLevel expected)
    {
        Assert.Equal(expected, CertificateLog.NextSigningLevel(TimeSpan.FromDays(days)));
    }
}
