# 出勤台(Tatehama_shukkindai)

運転会の勤怠管理システム。現在の実装は IdP(OIDC のトークン発行)の最小構成(MVP)のみ。仕様は `Docs/plans/2026-11-06-idp-mvp.md`、他サービス向けの仕様は `Docs/authentication.md`。

## ビルドとテスト
```
dotnet build Tatehama_shukkindai.slnx
dotnet test --project Server/Tatehama.Shukkindai.Server.UT
docker compose -f Server/compose.yml up -d     # IT の前に DB(ポート 5434)を起動
dotnet test --project Server/Tatehama.Shukkindai.Server.IT
```
- `dotnet test` は `--project <path>` の形で指定する(global.json で Microsoft.Testing.Platform を使うため、位置引数はエラーになる)。
- TreatWarningsAsErrors を有効にしている。警告ゼロを保つこと。

## ディレクトリ
- `Docs/`: 要件定義・ユースケース・計画・`authentication.md`
- `Server/Tatehama.Shukkindai.Server`: ASP.NET Core(IdP + 将来の出勤台 API)
- `Server/Tatehama.Shukkindai.Server.UT`: xUnit v3 + Moq(DB なし)
- `Server/Tatehama.Shukkindai.Server.IT`: WebApplicationFactory + 実 DB(localhost:5434)
- `Server/Database/`: Atlas(`schema.sql` があるべき状態、`migrations/` に diff)
- `Client/`, `Common/`: MVP では空

## 崩さない不変条件
- アクセストークンは JWS(暗号化しない)、RS256、`aud` を付けない。
- issuer は `Authentication:Issuer` で固定(末尾スラッシュ込みで完全一致)。未設定・不正なら起動時に例外。
- `sub` = Discord ユーザー ID(10進数文字列)、`name` = Discord のユーザー名(ニックネームや GlobalName ではない)。
- ユーザー由来の claim は `sub` と `name` だけ。ロール・社員番号・`oi_reg_id`・`oi_prvd_name` は入れない。
- 発行時と refresh のたびに、現在の Discord サーバーのメンバーかを確かめる(入鋏ロールは見ない)。

## Atlas
```
cd Server/Database
atlas migrate diff <名前> --env local    # schema.sql を変えたら diff を出す(docker が要る)
```
`schema.sql` を直したら、必ず migrations を再生成してコミットする。

## 秘密情報
Discord の ClientSecret / BotToken はリポジトリに置かない。User Secrets(`dotnet user-secrets`)か環境変数(`Discord__ClientSecret` など)で渡す。appsettings*.json には書かない。証明書(`cert/`)もコミットしない。
