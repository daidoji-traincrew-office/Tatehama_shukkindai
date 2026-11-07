using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Tatehama.Shukkindai.Server.Models;
using Tatehama.Shukkindai.Server.Repositories.Certificate;
using Tatehama.Shukkindai.Server.Services;
using Tatehama.Shukkindai.Server.UT.TestUtilities;

namespace Tatehama.Shukkindai.Server.UT.Services;

public sealed class CertificateServiceTest : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);

    private readonly TemporaryDirectory _directory = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly FakeLogger<CertificateService> _logger = new();
    private readonly CertificateRepository _repository;

    public CertificateServiceTest()
    {
        _repository = new CertificateRepository(_directory.Path);
    }

    public void Dispose() => _directory.Dispose();

    private CertificateService CreateService(ICertificateRepository? repository = null) =>
        new(repository ?? _repository, _time, _logger);

    private CertificateInfo Seed(CertificatePurpose purpose, double notBeforeDays, double notAfterDays) =>
        _repository.Create(purpose, Start.AddDays(notBeforeDays), Start.AddDays(notAfterDays));

    private void SeedValidSetExceptSigning()
    {
        Seed(CertificatePurpose.ServerEncryption, -10, 700);
        Seed(CertificatePurpose.ClientSigning, -10, 700);
        Seed(CertificatePurpose.ClientEncryption, -10, 700);
    }

    private int PfxCount(string directoryName) =>
        Directory.GetFiles(Path.Combine(_directory.Path, directoryName), "*.pfx").Length;

    private string[] PublicCers() =>
        Directory.GetFiles(Path.Combine(_directory.Path, "public"), "*.cer").Select(Path.GetFileName).Order().ToArray()!;

    private IEnumerable<FakeLogRecord> Records(int eventId) =>
        _logger.Collector.GetSnapshot().Where(r => r.Id.Id == eventId);

    // --- 起動 ---

    [Fact]
    public void GetStartupCertificates_FreshDirectory_CreatesOneCertificatePerPurposeAndPublishesSigningCer()
    {
        var service = CreateService();

        var set = service.GetStartupCertificates();

        foreach (var purpose in Enum.GetValues<CertificatePurpose>())
        {
            Assert.Single(set.Get(purpose));
            Assert.All(set.Get(purpose), c => Assert.True(c.HasPrivateKey));
        }

        Assert.Equal(["server-signing-" + set.Get(CertificatePurpose.ServerSigning)[0].Thumbprint + ".cer"], PublicCers());
        Assert.Equal(4, Records(CertificateLog.CertificateCreatedId).Count());
        Assert.All(Records(CertificateLog.CertificateCreatedId), r => Assert.Equal(LogLevel.Information, r.Level));
    }

    [Fact]
    public void GetStartupCertificates_CalledTwice_ReturnsTheSameSetWithoutRotatingAgain()
    {
        var service = CreateService();

        var first = service.GetStartupCertificates();
        _time.Advance(TimeSpan.FromDays(600)); // 期限が近づいても、起動済みのプロセスでは入れ替えない
        var second = service.GetStartupCertificates();

        Assert.Same(first, second);
        Assert.Equal(1, PfxCount("server-signing"));
    }

    [Fact]
    public void GetStartupCertificates_SigningWithPendingNextKey_RegistersBothAndUsesOldOne()
    {
        SeedValidSetExceptSigning();
        var old = Seed(CertificatePurpose.ServerSigning, -600, 100);
        var next = Seed(CertificatePurpose.ServerSigning, 40, 770);

        var set = CreateService().GetStartupCertificates();

        Assert.Equal(new[] { old.Thumbprint, next.Thumbprint }.Order(),
            set.Get(CertificatePurpose.ServerSigning).Select(c => c.Thumbprint).Order());
        Assert.Equal(2, PublicCers().Length);
    }

    [Fact]
    public void GetStatus_AfterStartup_ReportsTheActiveCertificateByOpenIddictRule()
    {
        SeedValidSetExceptSigning();
        Seed(CertificatePurpose.ServerSigning, -600, 100);
        var current = Seed(CertificatePurpose.ServerSigning, -10, 700);
        Seed(CertificatePurpose.ServerSigning, 40, 1400);
        var service = CreateService();
        service.GetStartupCertificates();

        var signing = service.GetStatus().Purposes.Single(p => p.Purpose == CertificatePurpose.ServerSigning);

        Assert.Equal(current.Thumbprint, signing.InUseActiveThumbprint);
        Assert.Equal(3, signing.InUseThumbprints.Count);
        Assert.False(CertificateRotationPolicy.IsRestartRequired(signing, _time.GetUtcNow()));
    }

    [Fact]
    public void GetStartupCertificates_AllSigningExpired_ThrowsAndLogsCritical()
    {
        SeedValidSetExceptSigning();
        Seed(CertificatePurpose.ServerSigning, -900, -10);

        var exception = Assert.Throws<InvalidOperationException>(() => CreateService().GetStartupCertificates());

        Assert.Contains("有効なサーバー署名証明書がありません", exception.Message);
        Assert.Contains("certificate-rotation.md", exception.Message);
        Assert.Equal(LogLevel.Critical, Assert.Single(Records(CertificateLog.NoValidSigningCertificateId)).Level);
        // 黙って新しい鍵は作らない
        Assert.Equal(1, PfxCount("server-signing"));
    }

    [Fact]
    public void GetStartupCertificates_AllSigningNotYetValid_Throws()
    {
        SeedValidSetExceptSigning();
        Seed(CertificatePurpose.ServerSigning, 5, 735);

        Assert.Throws<InvalidOperationException>(() => CreateService().GetStartupCertificates());
    }

    [Fact]
    public void GetStartupCertificates_ExpiredAndValidSigning_Starts()
    {
        SeedValidSetExceptSigning();
        var expired = Seed(CertificatePurpose.ServerSigning, -900, -10);
        var valid = Seed(CertificatePurpose.ServerSigning, -100, 630);

        var set = CreateService().GetStartupCertificates();

        // 期限切れは登録しない
        Assert.Equal([valid.Thumbprint], set.Get(CertificatePurpose.ServerSigning).Select(c => c.Thumbprint));
        Assert.DoesNotContain(PublicCers(), n => n.Contains(expired.Thumbprint)); // 保持期間内(7日)ではないので消える
    }

    [Fact]
    public void GetStartupCertificates_OldEncryptionCertificatesWithinRetention_AreAllRegistered()
    {
        Seed(CertificatePurpose.ServerSigning, -10, 700);
        Seed(CertificatePurpose.ClientSigning, -10, 700);
        Seed(CertificatePurpose.ClientEncryption, -10, 700);
        var expiredButRetained = Seed(CertificatePurpose.ServerEncryption, -740, -5); // NotAfter + 14日 以内
        var current = Seed(CertificatePurpose.ServerEncryption, -10, 720);

        var set = CreateService().GetStartupCertificates();

        Assert.Equal(new[] { expiredButRetained.Thumbprint, current.Thumbprint }.Order(),
            set.Get(CertificatePurpose.ServerEncryption).Select(c => c.Thumbprint).Order());
    }

    [Fact]
    public async Task GetStartupCertificates_TwoInstancesSharingTheDirectory_CreateOnlyOneCertificate()
    {
        // 別々のプロセスのつもり(別の Service と Repository)で同時に起動する
        var results = await Task.WhenAll(
            Task.Run(() => new CertificateService(new CertificateRepository(_directory.Path), _time,
                new FakeLogger<CertificateService>()).GetStartupCertificates(), TestContext.Current.CancellationToken),
            Task.Run(() => new CertificateService(new CertificateRepository(_directory.Path), _time,
                new FakeLogger<CertificateService>()).GetStartupCertificates(), TestContext.Current.CancellationToken));

        foreach (var directory in new[] { "server-signing", "server-encryption", "client-signing", "client-encryption" })
        {
            Assert.Equal(1, PfxCount(directory));
        }

        Assert.Equal(
            results[0].Get(CertificatePurpose.ServerSigning)[0].Thumbprint,
            results[1].Get(CertificatePurpose.ServerSigning)[0].Thumbprint);
        Assert.Single(PublicCers());
    }

    // --- 定期実行 ---

    [Fact]
    public void Maintain_NextKeyAlreadyCreatedAtStartup_DoesNotCreateAnotherAndLogsPendingKey()
    {
        SeedValidSetExceptSigning();
        var old = Seed(CertificatePurpose.ServerSigning, -580, 150);
        var service = CreateService();
        // 起動時のローテーションで次の鍵が作られる
        service.GetStartupCertificates();
        Assert.Equal(2, PfxCount("server-signing"));
        _logger.Collector.Clear();

        _time.Advance(TimeSpan.FromDays(1));
        service.Maintain();

        // 2 回目の Maintain では、既に次の鍵があるので作らない
        Assert.Equal(2, PfxCount("server-signing"));
        Assert.Empty(Records(CertificateLog.CertificateCreatedId));
        var pending = Assert.Single(Records(CertificateLog.NextSigningCertificatePendingId));
        Assert.Equal(LogLevel.Information, pending.Level); // 切り替えまで 59 日ある
        Assert.Single(PublicCers(), n => n.Contains(old.Thumbprint));
        Assert.Equal(2, PublicCers().Length);
    }

    [Fact]
    public void GetStartupCertificates_SigningRemaining150Days_CreatesNextKeyWithWarningAndActivationInSixtyDaysBeforeExpiry()
    {
        SeedValidSetExceptSigning();
        var old = Seed(CertificatePurpose.ServerSigning, -580, 150);

        var service = CreateService();
        service.GetStartupCertificates();

        var created = Assert.Single(Records(CertificateLog.CertificateCreatedId));
        Assert.Equal(LogLevel.Warning, created.Level);
        var next = service.GetStatus().Purposes.Single(p => p.Purpose == CertificatePurpose.ServerSigning)
            .Certificates.Single(c => c.Thumbprint != old.Thumbprint);
        Assert.Equal(old.NotAfter.AddDays(-60), next.NotBefore);
        Assert.Equal(next.NotBefore.AddDays(730), next.NotAfter);
    }

    [Fact]
    public void Maintain_TimeReachesNextKeyNotBefore_RequiresRestartButKeepsTheKeyInUse()
    {
        SeedValidSetExceptSigning();
        var old = Seed(CertificatePurpose.ServerSigning, -600, 100);
        var next = Seed(CertificatePurpose.ServerSigning, 40, 770);
        var service = CreateService();
        service.GetStartupCertificates();
        _logger.Collector.Clear();

        _time.Advance(TimeSpan.FromDays(41));
        service.Maintain();

        var signing = service.GetStatus().Purposes.Single(p => p.Purpose == CertificatePurpose.ServerSigning);
        Assert.Equal(old.Thumbprint, signing.InUseActiveThumbprint); // 動いているプロセスは切り替えない
        Assert.True(CertificateRotationPolicy.IsRestartRequired(signing, _time.GetUtcNow()));
        var restart = Assert.Single(Records(CertificateLog.RestartRequiredId));
        Assert.Equal(LogLevel.Warning, restart.Level); // 使用中の鍵は 59 日残っている
        Assert.Contains(next.Thumbprint, restart.Message);
        Assert.Contains(old.Thumbprint, restart.Message);
        // 他の用途は再起動不要
        Assert.Single(Records(CertificateLog.RestartRequiredId));
    }

    [Fact]
    public void Maintain_RestartRequiredAndInUseKeyAlmostExpired_LogsError()
    {
        SeedValidSetExceptSigning();
        Seed(CertificatePurpose.ServerSigning, -600, 100);
        Seed(CertificatePurpose.ServerSigning, 40, 770);
        var service = CreateService();
        service.GetStartupCertificates();
        _logger.Collector.Clear();

        _time.Advance(TimeSpan.FromDays(75)); // 使用中の鍵は残り 25 日
        service.Maintain();

        Assert.Equal(LogLevel.Error, Assert.Single(Records(CertificateLog.RestartRequiredId)).Level);
        // 次の鍵があるので、状態のログは上げすぎない(Warning まで)
        var status = Assert.Single(Records(CertificateLog.CertificateSetStatusId), r => r.Message!.Contains("server-signing"));
        Assert.Equal(LogLevel.Warning, status.Level);
    }

    [Fact]
    public void Maintain_ExpiredKeyInUseByThisProcess_IsNotDeletedUntilRestart()
    {
        SeedValidSetExceptSigning();
        var old = Seed(CertificatePurpose.ServerSigning, -600, 1);
        var next = Seed(CertificatePurpose.ServerSigning, -10, 700);
        var service = CreateService();
        service.GetStartupCertificates();

        _time.Advance(TimeSpan.FromDays(10)); // old は期限から 9 日(保持期間の 7 日を超えた)
        service.Maintain();

        Assert.Equal(2, PfxCount("server-signing"));
        Assert.Contains($"server-signing-{old.Thumbprint}.cer", PublicCers());

        // 再起動すると(新しいプロセス)、消える。.cer も一緒に消える
        var restarted = CreateService();
        restarted.GetStartupCertificates();

        Assert.Equal(1, PfxCount("server-signing"));
        Assert.Equal([$"server-signing-{next.Thumbprint}.cer"], PublicCers());
        Assert.Contains(Records(CertificateLog.CertificateDeletedId), r => r.Message!.Contains(old.Thumbprint));
    }

    [Fact]
    public void Maintain_EncryptionRemaining60Days_CreatesImmediatelyValidCertificateAndKeepsOld()
    {
        Seed(CertificatePurpose.ServerSigning, -10, 700);
        Seed(CertificatePurpose.ClientSigning, -10, 700);
        Seed(CertificatePurpose.ClientEncryption, -10, 700);
        var old = Seed(CertificatePurpose.ServerEncryption, -670, 70);
        var service = CreateService();
        service.GetStartupCertificates();
        Assert.Equal(1, PfxCount("server-encryption"));

        _time.Advance(TimeSpan.FromDays(10)); // 残り 60 日
        service.Maintain();

        var status = service.GetStatus().Purposes.Single(p => p.Purpose == CertificatePurpose.ServerEncryption);
        Assert.Equal(2, status.Certificates.Count);
        var created = status.Certificates.Single(c => c.Thumbprint != old.Thumbprint);
        Assert.True(created.NotBefore <= _time.GetUtcNow()); // すぐ使える
        Assert.Equal(old.Thumbprint, status.InUseActiveThumbprint); // 使うのは再起動のとき
        Assert.True(CertificateRotationPolicy.IsRestartRequired(status, _time.GetUtcNow()));
    }

    [Fact]
    public void Maintain_PurposeFails_LogsErrorAndStillRotatesOthers()
    {
        SeedValidSetExceptSigning();
        Seed(CertificatePurpose.ServerSigning, -10, 700);
        var failing = new FailingRepository(_repository, CertificatePurpose.ClientSigning);
        var failingService = new CertificateService(failing, _time, _logger);
        failingService.GetStartupCertificates(); // 起動時の失敗は、用途のログを出して続ける(証明書は有効なものがある)
        _logger.Collector.Clear();
        _time.Advance(TimeSpan.FromDays(650)); // 全部の用途が更新時期に入る(署名の鍵は残り 50 日)

        failingService.Maintain();

        var failed = Assert.Single(Records(CertificateLog.CertificateRotationFailedId));
        Assert.Equal(LogLevel.Error, failed.Level);
        Assert.Contains("client-signing", failed.Message);
        Assert.NotNull(failed.Exception);
        // 他の用途は作られている
        Assert.Equal(2, PfxCount("server-signing"));
        Assert.Equal(2, PfxCount("server-encryption"));
        Assert.Equal(2, PfxCount("client-encryption"));
        Assert.Equal(1, PfxCount("client-signing"));
        // 全部が成功した回ではないので、最終成功時刻は進まない
        Assert.Equal(Start, failingService.GetStatus().LastRotationSuccess);
    }

    [Fact]
    public void Maintain_AllSucceed_UpdatesLastRotationSuccess()
    {
        var service = CreateService();
        service.GetStartupCertificates();
        _time.Advance(TimeSpan.FromHours(24));

        service.Maintain();

        Assert.Equal(_time.GetUtcNow(), service.GetStatus().LastRotationSuccess);
    }

    [Fact]
    public void Maintain_Daily_LogsStatusPerCertificateAndPerPurposeWithFixedEventIds()
    {
        var service = CreateService();
        service.GetStartupCertificates();
        _logger.Collector.Clear();

        service.Maintain();

        Assert.Equal(4, Records(CertificateLog.CertificateStatusId).Count());
        var sets = Records(CertificateLog.CertificateSetStatusId).ToList();
        Assert.Equal(4, sets.Count);
        Assert.All(sets, r => Assert.Equal(LogLevel.Information, r.Level));
        var signing = sets.Single(r => r.Message!.Contains("server-signing"));
        Assert.Equal(729, Convert.ToInt32(signing.StructuredState!.Single(p => p.Key == "RemainingDays").Value));
        Assert.Equal("server-signing", signing.StructuredState!.Single(p => p.Key == "CertificatePurpose").Value);
        // 日時は culture 依存にならない文字列
        var usableUntil = (string)signing.StructuredState!.Single(p => p.Key == "UsableUntil").Value!;
        Assert.True(DateTimeOffset.TryParse(usableUntil, null, System.Globalization.DateTimeStyles.RoundtripKind, out _));
        Assert.EndsWith("Z", usableUntil);
    }

    [Fact]
    public void Maintain_ShortNoticeNextKey_LogsErrorOnCreation()
    {
        SeedValidSetExceptSigning();
        Seed(CertificatePurpose.ServerSigning, -700, 30);

        CreateService().GetStartupCertificates();

        Assert.Equal(LogLevel.Error, Assert.Single(Records(CertificateLog.CertificateCreatedId),
            r => r.Message!.Contains("server-signing")).Level);
    }

    private sealed class FailingRepository(ICertificateRepository inner, CertificatePurpose failingPurpose)
        : ICertificateRepository
    {
        public IReadOnlyList<CertificateInfo> GetAll(CertificatePurpose purpose) => inner.GetAll(purpose);
        public System.Security.Cryptography.X509Certificates.X509Certificate2 Load(CertificateInfo certificate) =>
            inner.Load(certificate);

        public CertificateInfo Create(CertificatePurpose purpose, DateTimeOffset notBefore, DateTimeOffset notAfter) =>
            purpose == failingPurpose ? throw new IOException("disk full") : inner.Create(purpose, notBefore, notAfter);

        public void Delete(CertificateInfo certificate) => inner.Delete(certificate);
        public IReadOnlyList<string> SyncPublic(IReadOnlyList<CertificateInfo> signing) => inner.SyncPublic(signing);
        public T WithLock<T>(Func<T> action) => inner.WithLock(action);
        public IReadOnlyList<string> FindLoosePermissions() => inner.FindLoosePermissions();
    }
}
