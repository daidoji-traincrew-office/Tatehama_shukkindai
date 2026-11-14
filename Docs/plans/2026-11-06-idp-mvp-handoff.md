# 引き継ぎ: 出勤台 IdP MVP の実装(2026-11-06)

このファイルは、別のエージェントセッションで実装を始めるための引き継ぎ。
**実装の仕様は `Docs/plans/2026-11-06-idp-mvp.md`(以下「計画」)にすべて書いてある。最初に計画を全部読むこと。** ここには、計画に書いていない前提と状況、注意点だけを書く。

## 今の状態

- リポジトリ: `~/develop/train/Tatehama_shukkindai`(`daidoji-traincrew-office/Tatehama_shukkindai`、private)
  - main ブランチで、コミットは initial commit の1つだけ。中身は要件定義と UC のドキュメントで、コードはまだ無い。
- 計画とこの引き継ぎファイルは **未コミット**(`Docs/plans/` 配下)。最初のコミットに含めてよい。
- コードはまだ1行も書いていない。ブランチもまだ作っていない。
- 人間(ユーザー)は計画を承認済み。

## やること(順番)

1. ブランチ `feat/idp-mvp` を作る。
2. **コミット1(ドキュメント)**: 計画の「ドキュメント」節に従って直す。あわせて計画と引き継ぎファイルもコミットする。
   - `Docs/usecases/UC-01_ログイン.md` と `Docs/usecases/要判断事項.md`(D-9、Q-01-3、手順6)を改訂する。
   - `Docs/authentication.md` を新しく作る。
3. **コミット2以降(実装)**: 計画の「ソリューション構成」「実装」「DB」「テスト」の節に従う。
   - まず雛形を作る。`global.json`、`.slnx`、`Directory.Build.props`、Server・UT・IT の3プロジェクト、`Server/compose.yml`、`Server/Database/`。
   - 次に IdP 本体を作り、UT と IT を書く。
4. UT と IT が通ることを確認したら、人間に報告する。**push と PR の作成は、人間に確認してから行う。**

## 計画に書いていない前提・背景

- **この IdP の位置づけ**: 運転会の全サービス(Crew = `Traincrew_MultiATS_Server`、TTC = `Traincrew_Rendo_Server`、出勤台)が、ここが発行したトークンで認証する。
  - Crew と TTC は、署名公開鍵(`.cer` ファイル)と issuer を静的に設定して、ローカルで検証する予定。だから **JWS であること、issuer を固定すること、`sub` = Discord ID であることは、絶対に崩さない**。
- **認証基盤全体の計画**: `~/.claude/plans/public-private-serialized-rossum.md`(T0〜T5)。リスク分析の経緯は `~/.claude/plans/ats-zazzy-twilight.md`。ただし ats-zazzy-twilight の一部(audience で絞る案、移行期間を設ける案)は、後の決定で覆っている。食い違ったら public-private-serialized-rossum が正。
- **人間が決めたこと(覆さない)**
  - dev と prod は統合しない。出勤台 prod のトークンは、MultiATS と Rendo の dev と prod でも通す。出勤台 dev のトークンは出勤台 dev だけ。aud は使わない。
  - サーバーを抜けた人や BAN された人にはトークンを発行しない(要件定義の D-9 を改訂する理由)。
  - `name` は Discord のユーザー名(ニックネームではない)。
  - 強制切断の仕組みは作らない。管理者による一括失効は MVP の範囲外。
  - 複数アカウントには、MVP では対応しない(出勤台クライアントのトークン共有ファイル側で後から足す。サーバー側は無関係)。
  - .NET 10、Atlas、IdP と API は同じプロセス、トップディレクトリは `Docs/`・`Server/`・`Client/`・`Common/`。

## 移植元のコード(Crew)

- **参照するのは `~/develop/train/Traincrew_Rendo_Server/public/`**(Rendo リポジトリの submodule。commit `8d69e89a`、ブランチ `ttc-initial-foundation`)。
  - `~/develop/train/Traincrew_MultiATS_Server`(単独の clone)と、公開側の origin/main は古い。`DiscordIssuerParameterSupport` が無い。
- 主な移植元:
  - `Traincrew_MultiATS_Server.Crew/Program.cs`: `ConfigureOpeniddictService`、`ConfigureOpeniddictClientService`、`RegisterOpeniddictApplicationAsync`、`EnsureCertificateExists`、`ConfigureForwardedHeaders`
  - `Traincrew_MultiATS_Server.Crew/Controllers/AuthenticationController.cs`
  - `Traincrew_MultiATS_Server.Crew/Authentication/DiscordIssuerParameterSupport.cs`
  - `Traincrew_MultiATS_Server/Repositories/Discord/DiscordRepository.cs`、`Traincrew_MultiATS_Server/HostedService/DiscordBotHostedService.cs`
  - `Database/schema.sql`、`Database/atlas.hcl`、`Database/compose.yml`
- **OpenIddict のバージョン差に注意**: Crew は OpenIddict **5.8.0** のまま。7.x への更新は、公開側の PR #16(ブランチ `chore/update-libraries`、OPEN)にある。
  - 出勤台は最初から **7.x の最新安定版**(2026-11 時点で 7.8.0)を使う。移植するときは、7.x の API に合わせて書き直す。
  - PR #16 のブランチは 7.x 向けの書き方とスキーマ差分の参考になる。たとえば `OpenIddictTokens.type` を varchar(150) にしている。`git -C ~/develop/train/Traincrew_Rendo_Server/public fetch origin chore/update-libraries` で取れる。
- **Discord `iss` の回避ハンドラは必要**: 上流の openiddict-core#2562 はクローズされた。代わりの #2563 は 8.0.0-preview にしか入っていない。7.x を使う間は、`DiscordIssuerParameterSupport` を移植する。
- 移植しないもの:
  - Crew の `EnableAuthorization=false`(認証無効モード): IdP には不要。
  - `IsUserCanAuthenticate` の入鋏ロール判定: IdP ではロールを見ず、メンバーかどうかだけを確かめる。

## 注意点

- **refresh のときのメンバー確認には、token エンドポイントのパススルーが必要**(`EnableTokenEndpointPassthrough()`)。パススルーにすると、authorization_code grant も自前で SignIn しないといけない。計画の「AuthorizationController の処理」節を参照。
- **Bot の起動待ち**: Crew の `DiscordRepository.Initialize` は、Ready を10秒待つ。Ready の前にリクエストが来たら、計画のとおり `temporarily_unavailable` を返す。Ready の前は `guild` が null になるので、NullReferenceException に気をつける。
- **ForwardedHeaders**: リバースプロキシの後ろで動く。issuer は固定するが、リダイレクト URI の組み立てには scheme と host が効くので、Crew と同じ設定を入れる。
- **IT 用の DB**: ポートは Crew(5432)と Rendo(5433)と重ならない番号にする。
- **テスト**: xUnit v3 + Moq + Microsoft.Testing.Platform で、Rendo と同じ構成にする。`dotnet test --project <path>` の形で実行する(位置引数はエラーになる)。
- **秘密情報**: Discord の ClientSecret と BotToken は、リポジトリに置かない。User Secrets か環境変数を使う。
- このリポジトリには `CLAUDE.md` がまだ無い。雛形のコミットで、ビルドとテストのコマンドと上記の不変条件を書いた `CLAUDE.md` を足すとよい。
- gh は `Kesigomon` アカウントが active なら、この org の private リポジトリが見える(`rf-castle` では見えない)。
