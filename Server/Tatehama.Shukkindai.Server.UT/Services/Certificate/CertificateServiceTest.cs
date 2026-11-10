using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Tatehama.Shukkindai.Server.Configuration;
using Tatehama.Shukkindai.Server.Models;
using Tatehama.Shukkindai.Server.Repositories.Certificate;
using Tatehama.Shukkindai.Server.Services.Certificate;
using Tatehama.Shukkindai.Server.UT.TestUtilities;

namespace Tatehama.Shukkindai.Server.UT.Services.Certificate;

public sealed class CertificateServiceTest : IDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 11, 7, 0, 0, 0, TimeSpan.Zero);

    private readonly TemporaryDirectory _directory = new();
    private readonly FakeTimeProvider _time = new(Start);
    private readonly FakeLogger<CertificateService> _logger = new();
    private readonly CertificateRepository _repository;

    public CertificateServiceTest()
    {
        _repository = new(_directory.Path);
    }

    public void Dispose() => _directory.Dispose();

    private IOptions<AuthenticationSettings> Settings(bool allowBootstrap = true) => Options.Create(new AuthenticationSettings
    {
        CertificateDirectory = _directory.Path,
        AllowCertificateBootstrap = allowBootstrap,
    });

    private CertificateService CreateService(ICertificateRepository? repository = null) =>
        new(repository ?? _repository, _time, Settings(), _logger);

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
        // 初回の生成は、意図したものか確かめられるよう Warning
        Assert.All(Records(CertificateLog.CertificateCreatedId), r => Assert.Equal(LogLevel.Warning, r.Level));
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
        // 期限切れから 10 日だが、起動直後なので消さない(直前まで使っていたかもしれない)。pfx も .cer も残る
        Assert.Contains(PublicCers(), n => n.Contains(expired.Thumbprint));
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
            Task.Run(() => new CertificateService(new CertificateRepository(_directory.Path), _time, Settings(),
                new FakeLogger<CertificateService>()).GetStartupCertificates(), TestContext.Current.CancellationToken),
            Task.Run(() => new CertificateService(new CertificateRepository(_directory.Path), _time, Settings(),
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
        var status = Assert.Single(Records(CertificateLog.CertificateSetStatusId), r => r.Message.Contains("server-signing"));
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

        // 再起動した(新しいプロセス)直後は、直前まで使っていた鍵を消さない。起動から保持期間がたつと消え、.cer も一緒に消える
        var restarted = CreateService();
        restarted.GetStartupCertificates();
        Assert.Equal(2, PfxCount("server-signing"));
        _time.Advance(TimeSpan.FromDays(7.5));
        restarted.Maintain();

        Assert.Equal(1, PfxCount("server-signing"));
        Assert.Equal([$"server-signing-{next.Thumbprint}.cer"], PublicCers());
        Assert.Contains(Records(CertificateLog.CertificateDeletedId), r => r.Message.Contains(old.Thumbprint));
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
        var failingService = new CertificateService(failing, _time, Settings(), _logger);
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
    public void Maintain_DirectoryBecomesEmptyWhileRunning_LogsErrorAndCreatesNothing()
    {
        SeedValidSetExceptSigning();
        Seed(CertificatePurpose.ServerSigning, -10, 700);
        var service = new CertificateService(_repository, _time, Settings(allowBootstrap: false), _logger);
        service.GetStartupCertificates();
        Directory.GetFiles(Path.Combine(_directory.Path, "client-encryption"), "*.pfx").ToList().ForEach(File.Delete); // 動作中に消えた
        _logger.Collector.Clear();
        _time.Advance(TimeSpan.FromHours(24));

        service.Maintain();

        var record = Assert.Single(Records(CertificateLog.CertificateDirectoryEmptyId));
        Assert.Equal(LogLevel.Error, record.Level);
        Assert.Contains("client-encryption", record.Message);
        Assert.Contains(Path.Combine(Path.GetFullPath(_directory.Path), "client-encryption"), record.Message);
        Assert.Equal(0, PfxCount("client-encryption"));
        Assert.Empty(Records(CertificateLog.CertificateCreatedId));
        // ローテーションの失敗として扱うので、最終成功時刻は進まない
        Assert.Equal(Start, service.GetStatus().LastRotationSuccess);
    }

    // --- 動作中に使用中の署名 pfx が消えたとき ---

    private string CerPath(CertificateInfo info) =>
        Path.Combine(_directory.Path, "public", $"server-signing-{info.Thumbprint}.cer");

    [Fact]
    public void Maintain_OnlySigningPfxDeletedWhileRunning_KeepsItsCerAndLogsError()
    {
        SeedValidSetExceptSigning();
        var signing = Seed(CertificatePurpose.ServerSigning, -10, 700);
        var service = new CertificateService(_repository, _time, Settings(allowBootstrap: false), _logger);
        service.GetStartupCertificates();
        var cerBefore = File.ReadAllBytes(CerPath(signing));
        File.Delete(signing.Path);
        _logger.Collector.Clear();
        _time.Advance(TimeSpan.FromHours(24));

        service.Maintain();

        Assert.Equal(cerBefore, File.ReadAllBytes(CerPath(signing)));
        Assert.Empty(Records(CertificateLog.PublicCertificatesChangedId));
        var record = Assert.Single(Records(CertificateLog.InUseSigningCertificateMissingId));
        Assert.Equal(LogLevel.Error, record.Level);
        Assert.Contains(signing.Thumbprint, record.Message);
        Assert.Single(Records(CertificateLog.CertificateDirectoryEmptyId));
    }

    [Fact]
    public void Maintain_OneOfTwoSigningPfxsInUseDeleted_KeepsItsCerAndLogsError()
    {
        SeedValidSetExceptSigning();
        var first = Seed(CertificatePurpose.ServerSigning, -10, 700);
        var second = Seed(CertificatePurpose.ServerSigning, -5, 710);
        var service = new CertificateService(_repository, _time, Settings(allowBootstrap: false), _logger);
        service.GetStartupCertificates();
        File.Delete(first.Path);
        _logger.Collector.Clear();
        _time.Advance(TimeSpan.FromHours(24));

        service.Maintain();

        Assert.Equal(new[] { $"server-signing-{first.Thumbprint}.cer", $"server-signing-{second.Thumbprint}.cer" }.Order(),
            PublicCers());
        Assert.Contains(first.Thumbprint, Assert.Single(Records(CertificateLog.InUseSigningCertificateMissingId)).Message);
        // 残っている pfx があるので、7114 は出ない
        Assert.Empty(Records(CertificateLog.CertificateDirectoryEmptyId));
    }

    [Fact]
    public void Maintain_SigningPfxNotInUseDeleted_DeletesItsCerAsBefore()
    {
        SeedValidSetExceptSigning();
        var inUse = Seed(CertificatePurpose.ServerSigning, -10, 700);
        var service = new CertificateService(_repository, _time, Settings(allowBootstrap: false), _logger);
        service.GetStartupCertificates();
        // 起動後に増えた鍵(このプロセスは使っていない)
        var later = Seed(CertificatePurpose.ServerSigning, 5, 730);
        service.Maintain();
        Assert.Contains($"server-signing-{later.Thumbprint}.cer", PublicCers());
        File.Delete(later.Path);
        _logger.Collector.Clear();

        service.Maintain();

        Assert.Equal([$"server-signing-{inUse.Thumbprint}.cer"], PublicCers());
        Assert.Empty(Records(CertificateLog.InUseSigningCertificateMissingId));
    }

    [Fact]
    public void Maintain_InUseSigningPfxAndCerBothDeleted_WritesCerBackFromMemory()
    {
        SeedValidSetExceptSigning();
        var signing = Seed(CertificatePurpose.ServerSigning, -10, 700);
        var service = new CertificateService(_repository, _time, Settings(allowBootstrap: false), _logger);
        service.GetStartupCertificates();
        var cerBefore = File.ReadAllBytes(CerPath(signing));
        File.Delete(signing.Path);
        File.Delete(CerPath(signing));
        _logger.Collector.Clear();
        _time.Advance(TimeSpan.FromHours(24));

        service.Maintain();

        Assert.Equal(cerBefore, File.ReadAllBytes(CerPath(signing)));
        Assert.Single(Records(CertificateLog.InUseSigningCertificateMissingId));
        Assert.Contains($"+server-signing-{signing.Thumbprint}.cer", Assert.Single(Records(CertificateLog.PublicCertificatesChangedId)).Message);
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
        var signing = sets.Single(r => r.Message.Contains("server-signing"));
        Assert.Equal(729, Convert.ToInt32(signing.StructuredState!.Single(p => p.Key == "RemainingDays").Value));
        Assert.Equal("server-signing", signing.StructuredState!.Single(p => p.Key == "CertificatePurpose").Value);
        // 日時は culture 依存にならない文字列
        var usableUntil = signing.StructuredState!.Single(p => p.Key == "UsableUntil").Value!;
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
            r => r.Message.Contains("server-signing")).Level);
    }

    // --- 初回の生成の許可(AllowCertificateBootstrap) ---

    [Fact]
    public void GetStartupCertificates_EmptyDirectoryAndBootstrapNotAllowed_ThrowsAndCreatesNothing()
    {
        var service = new CertificateService(_repository, _time, Settings(allowBootstrap: false), _logger);

        var exception = Assert.Throws<InvalidOperationException>(service.GetStartupCertificates);

        Assert.Contains("AllowCertificateBootstrap", exception.Message);
        Assert.Contains("マウント漏れ", exception.Message);
        Assert.Contains(_directory.Path, exception.Message);
        Assert.Contains("certificate-rotation.md", exception.Message);
        Assert.False(Directory.Exists(Path.Combine(_directory.Path, "server-signing")));
    }

    [Fact]
    public void GetStartupCertificates_OnePurposeDirectoryEmptyAndBootstrapNotAllowed_ThrowsWithoutDeletingAnything()
    {
        var signing = Seed(CertificatePurpose.ServerSigning, -10, 700);

        var service = new CertificateService(_repository, _time, Settings(allowBootstrap: false), _logger);

        Assert.Throws<InvalidOperationException>(service.GetStartupCertificates);
        Assert.Equal(1, PfxCount("server-signing"));
        Assert.True(File.Exists(signing.Path));
    }

    [Fact]
    public void GetStartupCertificates_ExistingCertificatesAndBootstrapNotAllowed_StartsAndStillCreatesNextKey()
    {
        SeedValidSetExceptSigning();
        Seed(CertificatePurpose.ServerSigning, -580, 150);

        var service = new CertificateService(_repository, _time, Settings(allowBootstrap: false), _logger);
        service.GetStartupCertificates();

        Assert.Equal(2, PfxCount("server-signing"));
    }

    [Fact]
    public void GetStartupCertificates_BootstrapAllowed_LogsWarningForCreation()
    {
        CreateService().GetStartupCertificates();

        Assert.Equal(4, Records(CertificateLog.CertificateCreatedId).Count(r => r.Level == LogLevel.Warning));
    }

    [Fact]
    public void GetStartupCertificates_RelativeDirectory_LogsWarningWithResolvedPath()
    {
        var relative = new AuthenticationSettings { CertificateDirectory = "cert-relative-ut", AllowCertificateBootstrap = true };
        var service = new CertificateService(new CertificateRepository(_directory.Path), _time, Options.Create(relative), _logger);

        service.GetStartupCertificates();

        var warning = Assert.Single(Records(CertificateLog.CertificateDirectoryIsRelativeId));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(Path.GetFullPath("cert-relative-ut"), warning.Message);
    }

    // --- public/ の不変条件 ---

    [Fact]
    public void GetStartupCertificates_PublicDirectoryNotWritable_ThrowsAndDoesNotStart()
    {
        SeedValidSetExceptSigning();
        Seed(CertificatePurpose.ServerSigning, -10, 700);
        var failing = new FailingRepository(_repository, CertificatePurpose.ClientSigning, failSyncPublic: true);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new CertificateService(failing, _time, Settings(), _logger).GetStartupCertificates());

        Assert.Contains("起動を中止", exception.Message);
        Assert.Contains("read-only file system", exception.Message);
        Assert.DoesNotContain("すべて期限切れ", exception.Message);
    }

    [Fact]
    public void GetStartupCertificates_SigningKeyNotPublished_IsNotUsedForSigning()
    {
        SeedValidSetExceptSigning();
        var published = Seed(CertificatePurpose.ServerSigning, -600, 100);
        var unpublished = Seed(CertificatePurpose.ServerSigning, -10, 700);
        // .cer を書けない状態を再現する(SyncPublic が何もしない)
        var noPublish = new NoPublishRepository(_repository, published);

        var set = new CertificateService(noPublish, _time, Settings(), _logger).GetStartupCertificates();

        Assert.Equal([published.Thumbprint], set.Get(CertificatePurpose.ServerSigning).Select(c => c.Thumbprint));
        Assert.DoesNotContain(unpublished.Thumbprint, set.Get(CertificatePurpose.ServerSigning).Select(c => c.Thumbprint));
    }

    [Fact]
    public void GetStartupCertificates_NoSigningKeyIsPublished_ThrowsWithPublicMessage()
    {
        SeedValidSetExceptSigning();
        Seed(CertificatePurpose.ServerSigning, -10, 700);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new CertificateService(new NoPublishRepository(_repository, null), _time, Settings(), _logger)
                .GetStartupCertificates());

        Assert.Contains("cert/public/", exception.Message);
        Assert.DoesNotContain("すべて期限切れ", exception.Message);
    }

    // --- 再起動を怠ったあとの起動 ---

    [Fact]
    public void GetStartupCertificates_RestartedEightDaysAfterSigningKeyExpired_KeepsOldKeyAndItsCer()
    {
        SeedValidSetExceptSigning();
        var old = Seed(CertificatePurpose.ServerSigning, -600, -8);
        var current = Seed(CertificatePurpose.ServerSigning, -100, 630);

        var service = CreateService();
        service.GetStartupCertificates();

        // 直前まで使っていた鍵は、すぐには消さない(直前に発行したトークンを、Crew/TTC が検証できなくなるため)
        Assert.Equal(2, PfxCount("server-signing"));
        Assert.Contains($"server-signing-{old.Thumbprint}.cer", PublicCers());
        Assert.Contains($"server-signing-{current.Thumbprint}.cer", PublicCers());

        // 起動から保持期間(7 日)がたってから、定期実行で消える
        _time.Advance(TimeSpan.FromDays(7.5));
        service.Maintain();

        Assert.Equal(1, PfxCount("server-signing"));
        Assert.Equal([$"server-signing-{current.Thumbprint}.cer"], PublicCers());
    }

    [Fact]
    public void GetStartupCertificates_RestartedAfterEncryptionKeyExpiredBeyondRetention_KeepsOldEncryptionKey()
    {
        Seed(CertificatePurpose.ServerSigning, -10, 700);
        Seed(CertificatePurpose.ClientSigning, -10, 700);
        Seed(CertificatePurpose.ClientEncryption, -10, 700);
        var old = Seed(CertificatePurpose.ServerEncryption, -740, -15); // NotAfter + 14 日を過ぎている
        var current = Seed(CertificatePurpose.ServerEncryption, -20, 710);

        var set = CreateService().GetStartupCertificates();

        Assert.Equal(new[] { old.Thumbprint, current.Thumbprint }.Order(),
            set.Get(CertificatePurpose.ServerEncryption).Select(c => c.Thumbprint).Order());
    }

    // --- 時計 ---

    [Fact]
    public void GetStartupCertificates_ClockFarAhead_StopsWithoutDeletingOrCreatingAnything()
    {
        var service = CreateService();
        service.GetStartupCertificates(); // 4 用途を作る
        var before = Directory.GetFiles(_directory.Path, "*.pfx", SearchOption.AllDirectories).Order().ToList();

        _time.Advance(TimeSpan.FromDays(2000));
        var exception = Assert.Throws<InvalidOperationException>(() => CreateService().GetStartupCertificates());

        Assert.Contains("有効なサーバー署名証明書がありません", exception.Message);
        Assert.Contains("何も作らず、何も消していません", exception.Message);
        Assert.Equal(before, Directory.GetFiles(_directory.Path, "*.pfx", SearchOption.AllDirectories).Order().ToList());
    }

    [Fact]
    public void GetStartupCertificates_ClockBehindExistingCertificates_StopsWithoutChangingAnything()
    {
        CreateService().GetStartupCertificates();
        var before = Directory.GetFiles(_directory.Path, "*.pfx", SearchOption.AllDirectories).Order().ToList();

        var past = new FakeTimeProvider(Start.AddDays(-30));
        var exception = Assert.Throws<InvalidOperationException>(() =>
            new CertificateService(_repository, past, Settings(), _logger).GetStartupCertificates());

        Assert.Contains("時計", exception.Message);
        Assert.Equal(before, Directory.GetFiles(_directory.Path, "*.pfx", SearchOption.AllDirectories).Order().ToList());
    }

    private sealed class FailingRepository(ICertificateRepository inner, CertificatePurpose failingPurpose,
        bool failSyncPublic = false)
        : ICertificateRepository
    {
        public IReadOnlyList<CertificateInfo> GetAll(CertificatePurpose purpose) => inner.GetAll(purpose);
        public System.Security.Cryptography.X509Certificates.X509Certificate2 Load(CertificateInfo certificate) =>
            inner.Load(certificate);

        public CertificateInfo Create(CertificatePurpose purpose, DateTimeOffset notBefore, DateTimeOffset notAfter) =>
            purpose == failingPurpose ? throw new IOException("disk full") : inner.Create(purpose, notBefore, notAfter);

        public void Delete(CertificateInfo certificate) => inner.Delete(certificate);
        public IReadOnlyList<string> SyncPublic(IReadOnlyList<CertificateInfo> signing, IReadOnlyDictionary<string, byte[]> retained) =>
            failSyncPublic ? throw new IOException("read-only file system") : inner.SyncPublic(signing, retained);
        public bool IsPublished(CertificateInfo c) => inner.IsPublished(c);
        public IReadOnlyList<string> FindUnexpectedPublicFiles() => inner.FindUnexpectedPublicFiles();
        public T WithLock<T>(Func<T> action) => inner.WithLock(action);
        public IReadOnlyList<string> FindLoosePermissions() => inner.FindLoosePermissions();
    }

    /// <summary>public/ に何も書かない(書けない)状態を再現する。<paramref name="published"/> だけが公開済み。</summary>
    private sealed class NoPublishRepository(ICertificateRepository inner, CertificateInfo? published) : ICertificateRepository
    {
        public IReadOnlyList<CertificateInfo> GetAll(CertificatePurpose purpose) => inner.GetAll(purpose);
        public System.Security.Cryptography.X509Certificates.X509Certificate2 Load(CertificateInfo certificate) => inner.Load(certificate);

        public CertificateInfo Create(CertificatePurpose purpose, DateTimeOffset notBefore, DateTimeOffset notAfter) =>
            inner.Create(purpose, notBefore, notAfter);

        public void Delete(CertificateInfo certificate) => inner.Delete(certificate);
        public IReadOnlyList<string> SyncPublic(IReadOnlyList<CertificateInfo> signing, IReadOnlyDictionary<string, byte[]> retained) => [];
        public bool IsPublished(CertificateInfo c) => published is not null && c.Thumbprint == published.Thumbprint;
        public IReadOnlyList<string> FindUnexpectedPublicFiles() => [];
        public T WithLock<T>(Func<T> action) => inner.WithLock(action);
        public IReadOnlyList<string> FindLoosePermissions() => [];
    }
}
