# 出勤台(Tatehama_shukkindai)

運転会の勤怠管理システム。現在の実装は IdP(OIDC のトークン発行)の最小構成(MVP)のみ。仕様は `Docs/plans/2026-11-06-idp-mvp.md`、他サービス向けの仕様は `Docs/authentication.md`。

## ビルドとテスト
```
dotnet build Tatehama_shukkindai.slnx
dotnet test --project Server/Tatehama.Shukkindai.Server.UT
docker compose -f Server/compose.yml up -d --wait   # IT の前に DB(ポート 5434)を起動
dotnet test --project Server/Tatehama.Shukkindai.Server.IT
```
- `dotnet test` は `--project <path>` の形で指定する(global.json で Microsoft.Testing.Platform を使うため、位置引数はエラーになる)。
- TreatWarningsAsErrors を有効にしている。警告ゼロを保つこと。
- ローカルの restore で `NETSDK1226`(prune package data が無い)が出たら、`AllowMissingPrunePackageData=true` を環境変数かプロパティで付ける(SDK に ASP.NET Core の targeting pack が無い環境の回避策。CI では不要)。

### CI(GitHub Actions)
Client も置くので、Server 用の workflow とイメージには `server` を付けている。
- `serverUnitTest.yml` / `serverIntegrationTest.yml`: push と PR で UT / IT を走らせる(IT は `Server/compose.yml` の DB を使う)。open な PR があるブランチの push はスキップし、PR 側で走る。
- `serverBuild.yml`: push ごとに `Server/Dockerfile` をビルドし、`ghcr.io/daidoji-traincrew-office/tatehama_shukkindai_server:<ブランチ名の / を _ に>` へ amd64/arm64 で push する(dev は今もホストでビルドしている)。

## コードスタイル(Rider 検査)
C# は JetBrains Rider(ReSharper)の検査で指摘が出ない形で書く。特に次の点。
- ローカル変数は、型が明らかでなくても `var` で宣言する(`var sb = new StringBuilder();`、`var n = list.Count;`)。
- フィールド・プロパティ・引数のオブジェクト生成は target-typed new にする(`private readonly Foo _x = new();`)。`var x = new Foo()` はそのままでよい。
- コレクションの初期化は、使えるなら collection expression(`[]`)にする。
- 要素を詰め替えるだけの `foreach` / `for` は書かず、LINQ にする。
- 使っていないメンバー・`using`・変数を残さない。

設定は `.editorconfig` と `Tatehama_shukkindai.slnx.DotSettings`(cleanupcode の `ClaudeCleanup` プロファイル)にある。

### 自動検査(PostToolUse / Stop / SubagentStop フック)
`.claude/settings.json` のフック(`.github/scripts/rider_lint.py hook`。Traincrew_MultiATS_Server と同じもの)が、Claude が `Edit` / `Write` / `MultiEdit` で触った `.cs` を記録し、応答の終わりにそのファイルの変更行へ `jb inspectcode` をかける。
- 対象は PostToolUse で記録したファイルだけ。何も触っていなければ `jb` は動かない。
- 機械的に直せる指摘(`var` 化、`new()` 化など)は `jb cleanupcode` で自動修正される。修正は**ファイル単位**で、触ったファイルの既存行にもかかる。ファイルが書き換わるので、差し戻されたら編集の前に読み直すこと。
- 変更した行に残った指摘は exit 2 で差し戻される。直してから終えること(差し戻しは 1 回まで。2 回目と、`stop_hook_active` が既に true のときは通知だけで終わる)。
- フックの想定外のエラーでは何もしない(fail-open)。python3 が要る。
- 初回は `dotnet tool restore` と `dotnet restore` が要る(`--no-build` で動くので、ビルド済みであること)。
- 手動実行: `python3 .github/scripts/rider_lint.py check --sln Tatehama_shukkindai.slnx --base HEAD --fix`
- CI(`.github/workflows/riderLint.yml`)は PR で変更行を検査する(`ubuntu-latest`)。

## ディレクトリ
- `Docs/`: 要件定義・ユースケース・計画・`authentication.md`
- `Server/Tatehama.Shukkindai.Server`: ASP.NET Core(IdP + 将来の出勤台 API)。構造は Traincrew_MultiATS_Server に揃えている
  - `Controllers/`(HTTP の糊)→ `Services/`(判断。interface は同じファイル)→ `Repositories/`(外部とのやり取り。interface は別ファイル)の順に呼ぶ。Controller から Repository は呼ばない
  - `HostedService/`(薄く保つ)、`Models/`(結果型など)、`Authentication/`(ASP.NET と OpenIddict の糊)、`Configuration/`、`Data/`
  - OpenIddict のマネージャーは Repository に相当するものとして Service から使う(包まない)
  - 新しい Service・Repository・HostedService のクラスは `sealed` にする(差し替えるのは interface なので Moq に支障は無い)。結果型は `record`
  - 自分で書くメソッドには `Async` を付けない(override とフレームワークの呼び出しは除く)
- `Server/Tatehama.Shukkindai.Server.UT`: xUnit v3 + Moq(DB なし)。クラスは `XxxTest`、メソッドは `Method_Condition_Result`
- `Server/Tatehama.Shukkindai.Server.IT`: WebApplicationFactory + 実 DB(localhost:5434)。`Fixture/`・`Collection/`(`[Collection("WebApplication")]` で fixture を 1 つにまとめる)・`TestUtilities/`
- `Server/Database/`: Atlas(`schema.sql` があるべき状態、`migrations/` に diff)
- `Client/`, `Common/`: MVP では空

## 崩さない不変条件
- アクセストークンは JWS(暗号化しない)、RS256、`aud` を付けない。
- issuer は `Authentication:Issuer` で固定(末尾スラッシュ込みで完全一致)。未設定・不正なら起動時に例外。
- `sub` = Discord ユーザー ID(10進数文字列)、`name` = Discord のユーザー名(ニックネームや GlobalName ではない)。
- ユーザー由来の claim は `sub` と `name` だけ。ロール・社員番号・`oi_reg_id`・`oi_prvd_name` は入れない。
- openid scope は使わず(宣言も受付もしない)、PKCE は S256 だけ。
- 発行時と refresh のたびに、現在の Discord サーバーのメンバーかを確かめる(入鋏ロールは見ない)。
- Discord の判定は Bot のギルドキャッシュだけで行う(REST にフォールバックしない)。Gateway 接続中・ギルドあり・`guild.IsConnected`・`guild.HasAllMembers` の 4 条件がそろわなければ `temporarily_unavailable`(Discord と同期し直している最中だけ起きる)。
- 起動時に Discord に接続できない、またはギルドが見つからないときは、起動に失敗する(待つ上限は 30 秒)。

## ローカル起動
Discord の設定は全環境で必須(未設定だと起動時に例外)。User Secrets に入れる。
```
cd Server/Tatehama.Shukkindai.Server
dotnet user-secrets set Discord:ClientId <値>
dotnet user-secrets set Discord:ClientSecret <値>
dotnet user-secrets set Discord:BotToken <値>
dotnet user-secrets set Discord:GuildId <ギルドID>
```
DB は `docker compose -f Server/compose.yml up -d`。Development の issuer は `https://shukkindai-dev.example.invalid/`。

## 将来の API を足すとき
- 既定の認証スキームは Cookie(ログイン途中用)なので、`[Authorize]` だけだと Cookie で判定される。出勤台 API は OpenIddict Validation のスキーム(`OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme`)を明示して認可すること。
- アクセストークンに載せる claim の方針は `TokenDestinations` に集約している。claim を足すのは仕様変更なので、`Docs/authentication.md` と IT の許可リストも直す。

## 証明書(自動ローテーション)
Development 以外では、署名・暗号化の証明書を出勤台が自動で作り、切り替え、古いものを消す。運用と手順書は `Docs/certificate-rotation.md`、Crew/TTC 向けの設計は `Docs/authentication.md`。
- レイアウト(`Authentication:CertificateDirectory`、Docker では `/app/cert`。永続化する): `cert/{server-signing,server-encryption,client-signing,client-encryption}/*.pfx`(0600、ディレクトリは `cert/` ごと 0700)、`cert/public/server-signing-{Thumbprint}.cer`(0644、ディレクトリ 0755)、`cert/.rotation.lock`。
- `cert/public/` は `.cer` だけ。Crew と TTC が同じホストで、このディレクトリを**読み取り専用でマウント**して `*.cer` を全部検証鍵にする。pfx は決して置かない。
- 寿命 2 年。署名は残り 150 日で次の鍵を作って公開し、期限の 60 日前に切り替える(古い鍵は NotAfter + 7 日で削除)。暗号化(Server)は残り 60 日で次を作り NotAfter + 14 日まで残す。Client 用は残り 60 日、NotAfter + 1 日。
- **切り替えは再起動のときだけ**(動いているプロセスの OpenIddict の options は作り直さない)。再起動が必要なことは、ログ `RestartRequired`(EventId 7105)とメトリクス `shukkindai.certificate.restart_required` で知らせる。運転会のない日に人が再起動する。
- 有効な署名証明書が 1 つも無ければ、起動を止める。用途のディレクトリに pfx が 1 つも無いときも起動を止め、鍵を作って起動するのは `Authentication:AllowCertificateBootstrap=true` のときだけ(既定 false。初回のデプロイでだけ付け、鍵ができたら外す。`cert/` のマウント漏れで黙って新しい署名鍵で起動するのを防ぐ)。次の鍵の自動生成は、この設定に関係なく行う。
- **不変条件**: `cert/public/` に同じ内容の `.cer` が無い署名鍵では署名しない(起動時の候補を公開済みの鍵に限る。`public/` を同期できなければ起動を止める)。古い鍵は NotAfter + 保持期間と、プロセスの起動 + 保持期間の両方を過ぎるまで消さない。このプロセスが署名に使っている鍵の .cer は、pfx が消えても再起動まで `public/` から消さない(消えていたら、メモリ上の証明書から書き戻し、7115 で知らせる)。起動時の確認(署名の有効性・時計)は、削除や生成より先に行い、止めるときは何も変えない。
- 自動化のコードは `Services/Certificate/CertificateRotationPolicy`(純粋関数)・`Services/Certificate/CertificateService`・`Repositories/Certificate/`・`HostedService/CertificateRotationHostedService`。ログの EventId(7101〜7115)は固定なので変えない(アラートが使う)。
- 手動の作業用に `Server/tools/gen-cert.cs` と `inspect-cert.cs`(`dotnet run Server/tools/gen-cert.cs -- ...`)がある。

## OpenTelemetry
`OTEL_EXPORTER_OTLP_ENDPOINT` が設定されているときだけ有効(Crew と同じ)。ASP.NET Core と HttpClient の metrics・tracing、ログの OTLP 出力、自前の Meter(`Tatehama.Shukkindai.Server.Certificates`)を送る。環境変数 `OTEL_EXPORTER_OTLP_ENDPOINT`、`OTEL_SERVICE_NAME=tatehama-shukkindai` などで設定する。メトリクスの一覧は `Docs/certificate-rotation.md`。

## Atlas
```
cd Server/Database
atlas migrate diff <名前> --env local    # schema.sql を変えたら diff を出す(docker が要る)
```
`schema.sql` を直したら、必ず migrations を再生成してコミットする。
本番(と dev)の DB へは、デプロイ時に `atlas migrate apply` で `migrations/` を適用する(アプリは起動時にスキーマを作らない)。
命名方針: OpenIddict のテーブル名(`OpenIddictApplications` など)はそのまま、列名は snake_case(`ApplicationDbContext` で変換)。

## 秘密情報
Discord の ClientSecret / BotToken はリポジトリに置かない。User Secrets(`dotnet user-secrets`)か環境変数(`Discord__ClientSecret` など)で渡す。appsettings*.json には書かない。証明書(`cert/`。pfx は秘密鍵を含む)もコミットしない。
