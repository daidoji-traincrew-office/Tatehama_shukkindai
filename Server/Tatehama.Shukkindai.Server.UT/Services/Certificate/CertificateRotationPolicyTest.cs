using Tatehama.Shukkindai.Server.Models;
using Tatehama.Shukkindai.Server.Services.Certificate;

namespace Tatehama.Shukkindai.Server.UT.Services.Certificate;

public class CertificateRotationPolicyTest
{
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);
    /// <summary>プロセスは十分前から動いている(起動直後の削除猶予に引っかからない)。</summary>
    private static readonly DateTimeOffset LongAgo = Now.AddDays(-365);
    private static readonly IReadOnlySet<string> NothingInUse = new HashSet<string>();

    private static CertificateInfo Cert(CertificatePurpose purpose, string thumbprint, double notBeforeDays,
        double notAfterDays) =>
        new(purpose, thumbprint, Now.AddDays(notBeforeDays), Now.AddDays(notAfterDays), "/cert/" + thumbprint + ".pfx");

    private static CertificateInfo Signing(string thumbprint, double notBeforeDays, double notAfterDays) =>
        Cert(CertificatePurpose.ServerSigning, thumbprint, notBeforeDays, notAfterDays);

    // --- 作る証明書(署名) ---

    [Fact]
    public void Plan_SigningDirectoryEmpty_CreatesImmediatelyValidCertificate()
    {
        var plan = CertificateRotationPolicy.Plan(CertificatePurpose.ServerSigning, [], NothingInUse, Now, LongAgo);

        var create = Assert.IsType<CertificateToCreate>(plan.Create);
        Assert.True(create.Initial);
        Assert.Equal(Now.AddMinutes(-5), create.NotBefore);
        Assert.Equal(create.NotBefore.AddDays(730), create.NotAfter);
    }

    [Theory]
    // 残り日数(最も遠い NotAfter まで), 期待する NotBefore(今からの日数。null なら作らない), 猶予不足か
    [InlineData(151, null, false)] // 残り 150 日を超える → 何もしない
    [InlineData(150, 90, false)]   // 残り 150 日 → 次の鍵。NotBefore = E-60日
    [InlineData(100, 40, false)]
    [InlineData(74, 14, false)]    // E-60日 = 14日後。ちょうど最小の猶予
    [InlineData(70, 14, true)]     // 生成が遅れた → 公開から 14 日空ける
    [InlineData(10, 9, true)]      // E-1日 より後にはしない
    [InlineData(-1, null, false)]  // 全部期限切れ → 黙って作らない
    public void Plan_SigningNearExpiry_CreatesNextKeyWithDeferredNotBefore(
        int remainingDays, int? expectedNotBeforeDays, bool shortNotice)
    {
        var plan = CertificateRotationPolicy.Plan(CertificatePurpose.ServerSigning,
            [Signing("OLD", -600, remainingDays)], NothingInUse, Now, LongAgo);

        if (expectedNotBeforeDays is null)
        {
            Assert.Null(plan.Create);
            return;
        }

        var create = Assert.IsType<CertificateToCreate>(plan.Create);
        Assert.False(create.Initial);
        Assert.Equal(Now.AddDays(expectedNotBeforeDays.Value), create.NotBefore);
        Assert.Equal(create.NotBefore.AddDays(730), create.NotAfter);
        Assert.Equal(shortNotice, create.ShortNotice);
    }

    [Fact]
    public void Plan_SigningLessThanOneDayLeft_CreatesImmediatelyValidKey()
    {
        var plan = CertificateRotationPolicy.Plan(CertificatePurpose.ServerSigning,
            [Signing("OLD", -600, 0.5)], NothingInUse, Now, LongAgo);

        // E-1日 が過去になる。両方が無効な隙間を作らないよう、すぐ有効にする
        Assert.Equal(Now.AddMinutes(-5), Assert.IsType<CertificateToCreate>(plan.Create).NotBefore);
    }

    [Fact]
    public void Plan_SigningNextKeyAlreadyExists_DoesNothing()
    {
        var plan = CertificateRotationPolicy.Plan(CertificatePurpose.ServerSigning,
            [Signing("OLD", -600, 100), Signing("NEXT", 40, 770)], NothingInUse, Now, LongAgo);

        Assert.Null(plan.Create);
        Assert.Empty(plan.Delete);
    }

    // --- 作る証明書(暗号化・Client) ---

    [Theory]
    [InlineData(CertificatePurpose.ServerEncryption)]
    [InlineData(CertificatePurpose.ClientSigning)]
    [InlineData(CertificatePurpose.ClientEncryption)]
    public void Plan_NonSigningDirectoryEmpty_CreatesImmediatelyValidCertificate(CertificatePurpose purpose)
    {
        var plan = CertificateRotationPolicy.Plan(purpose, [], NothingInUse, Now, LongAgo);

        var create = Assert.IsType<CertificateToCreate>(plan.Create);
        Assert.True(create.Initial);
        Assert.Equal(Now.AddMinutes(-5), create.NotBefore);
    }

    [Theory]
    [InlineData(61, false)]
    [InlineData(60, true)]
    [InlineData(1, true)]
    [InlineData(-30, true)] // 期限切れでも、これらは作り直せる(署名と違って、起動を止める理由にしない)
    public void Plan_NonSigning_CreatesImmediatelyValidCertificateWhenSixtyDaysLeft(double remainingDays, bool creates)
    {
        var plan = CertificateRotationPolicy.Plan(CertificatePurpose.ServerEncryption,
            [Cert(CertificatePurpose.ServerEncryption, "OLD", -600, remainingDays)], NothingInUse, Now, LongAgo);

        if (!creates)
        {
            Assert.Null(plan.Create);
            return;
        }

        var create = Assert.IsType<CertificateToCreate>(plan.Create);
        Assert.Equal(Now.AddMinutes(-5), create.NotBefore);
        Assert.False(create.ShortNotice);
    }

    // --- 消す証明書 ---

    [Theory]
    // 用途, 期限切れからの日数, 消すか
    [InlineData(CertificatePurpose.ServerSigning, 6.9, false)]
    [InlineData(CertificatePurpose.ServerSigning, 7.1, true)]
    [InlineData(CertificatePurpose.ServerEncryption, 13.9, false)]
    [InlineData(CertificatePurpose.ServerEncryption, 14.1, true)]
    [InlineData(CertificatePurpose.ClientSigning, 0.9, false)]
    [InlineData(CertificatePurpose.ClientSigning, 1.1, true)]
    [InlineData(CertificatePurpose.ClientEncryption, 1.1, true)]
    public void Plan_ExpiredCertificate_DeletedOnlyAfterRetentionPeriod(
        CertificatePurpose purpose, double expiredDaysAgo, bool deleted)
    {
        var certificates = new[]
        {
            Cert(purpose, "OLD", -900, -expiredDaysAgo),
            Cert(purpose, "NEW", -100, 630),
        };

        var plan = CertificateRotationPolicy.Plan(purpose, certificates, NothingInUse, Now, LongAgo);

        Assert.Equal(deleted ? ["OLD"] : [], plan.Delete.Select(c => c.Thumbprint));
    }

    [Fact]
    public void Plan_ExpiredCertificateInUseByThisProcess_IsNotDeleted()
    {
        var certificates = new[]
        {
            Signing("OLD", -900, -30),
            Signing("NEW", -100, 630),
        };

        var plan = CertificateRotationPolicy.Plan(CertificatePurpose.ServerSigning, certificates,
            new HashSet<string> { "OLD" }, Now, LongAgo);

        Assert.Empty(plan.Delete);
    }

    [Fact]
    public void Plan_NothingElseValidWouldRemain_DoesNotDeleteTheLastCertificates()
    {
        // 全部が期限切れで保持期間も過ぎている。消すと空になり、次の起動が「初回」扱いになってしまう
        var plan = CertificateRotationPolicy.Plan(CertificatePurpose.ServerSigning,
            [Signing("A", -900, -100), Signing("B", -800, -50)], NothingInUse, Now, LongAgo);

        Assert.Empty(plan.Delete);
        Assert.Null(plan.Create);
    }

    [Fact]
    public void Plan_NonSigningAllExpiredAndRecreated_DeletesOldOnes()
    {
        var plan = CertificateRotationPolicy.Plan(CertificatePurpose.ServerEncryption,
            [Cert(CertificatePurpose.ServerEncryption, "A", -900, -100)], NothingInUse, Now, LongAgo);

        Assert.NotNull(plan.Create);
        Assert.Equal(["A"], plan.Delete.Select(c => c.Thumbprint));
    }

    // --- 使うべき証明書 ---

    [Fact]
    public void SelectActive_ValidAndPending_ChoosesValidOneWithFarthestNotAfter()
    {
        var active = CertificateRotationPolicy.SelectActive(
            [Signing("EXPIRED", -900, -1), Signing("OLD", -600, 100), Signing("NEW", -10, 700), Signing("NEXT", 5, 900)],
            Now);

        Assert.Equal("NEW", active!.Thumbprint);
    }

    [Fact]
    public void SelectActive_AllExpiredOrPending_ReturnsNull()
    {
        Assert.Null(CertificateRotationPolicy.SelectActive([Signing("E", -900, -1), Signing("P", 1, 700)], Now));
    }

    [Fact]
    public void StateOf_Certificates_ClassifiedByTime()
    {
        var active = Signing("A", -10, 700);
        Assert.Equal(CertificateState.Active, CertificateRotationPolicy.StateOf(active, active, Now));
        Assert.Equal(CertificateState.Standby, CertificateRotationPolicy.StateOf(Signing("S", -10, 100), active, Now));
        Assert.Equal(CertificateState.Pending, CertificateRotationPolicy.StateOf(Signing("P", 1, 900), active, Now));
        Assert.Equal(CertificateState.Expired, CertificateRotationPolicy.StateOf(Signing("E", -10, -1), active, Now));
    }

    [Theory]
    // プロセスが使っているもの, 今の時刻で使うべきもの(old/next の NotBefore が 40 日後)
    [InlineData(0, "OLD", false)]
    [InlineData(39, "OLD", false)]
    [InlineData(41, "OLD", true)]
    [InlineData(41, "NEXT", false)]
    public void IsRestartRequired_InUseDiffersFromShouldUse_RequiresRestart(
        double daysLater, string inUse, bool expected)
    {
        var status = new PurposeStatus(CertificatePurpose.ServerSigning,
            [Signing("OLD", -600, 100), Signing("NEXT", 40, 770)], new HashSet<string> { inUse }, inUse);

        Assert.Equal(expected, CertificateRotationPolicy.IsRestartRequired(status, Now.AddDays(daysLater)));
    }

    [Fact]
    public void IsRestartRequired_InUseExpiredAndNothingValid_DoesNotRequireRestart()
    {
        // 有効なものが無いなら、再起動しても直らない(別の対応が要る)
        var status = new PurposeStatus(CertificatePurpose.ServerSigning, [Signing("OLD", -600, 100)],
            new HashSet<string> { "OLD" }, "OLD");

        Assert.False(CertificateRotationPolicy.IsRestartRequired(status, Now.AddDays(101)));
    }

    [Fact]
    public void Plan_DirectoryEmptyAndBootstrapNotAllowed_DoesNotCreate()
    {
        foreach (var purpose in Enum.GetValues<CertificatePurpose>())
        {
            var plan = CertificateRotationPolicy.Plan(purpose, [], NothingInUse, Now, Now, allowBootstrap: false);

            Assert.True(plan.BootstrapDenied);
            Assert.Null(plan.Create);
        }
    }

    [Fact]
    public void Plan_NextKeyNeededAndBootstrapNotAllowed_StillCreatesNextKey()
    {
        // 初回の生成の許可は、次の鍵の自動生成には関係しない
        var plan = CertificateRotationPolicy.Plan(CertificatePurpose.ServerSigning,
            [Signing("OLD", -580, 150)], NothingInUse, Now, LongAgo, allowBootstrap: false);

        Assert.False(plan.BootstrapDenied);
        Assert.NotNull(plan.Create);
    }

    [Theory]
    // 期限切れから 8 日(保持期間 7 日を超えている)。プロセスの起動からの経過日数 → 消すか
    [InlineData(0.0, false)]  // 再起動した直後は、直前まで使っていた鍵を消さない
    [InlineData(6.9, false)]
    [InlineData(7.1, true)]
    public void Plan_ExpiredKeyUsedUntilJustBeforeRestart_IsKeptUntilRetentionPassesSinceProcessStart(
        double processRunningDays, bool deleted)
    {
        var certificates = new[] { Signing("OLD", -900, -8), Signing("NEW", -100, 630) };

        var plan = CertificateRotationPolicy.Plan(CertificatePurpose.ServerSigning, certificates, NothingInUse, Now,
            processStart: Now.AddDays(-processRunningDays));

        Assert.Equal(deleted ? ["OLD"] : [], plan.Delete.Select(c => c.Thumbprint));
    }
}
