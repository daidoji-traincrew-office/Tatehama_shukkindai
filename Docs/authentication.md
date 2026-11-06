# 認証(出勤台 IdP)

出勤台サーバーは OIDC の IdP として、運転会の全サービス(Crew = MultiATS、TTC = Rendo、出勤台)が使うトークンを発行する。このファイルは、Crew・TTC・各クライアントの実装者向けの仕様。

## issuer と公開エンドポイント

| 環境 | issuer(末尾の `/` まで完全一致) |
|---|---|
| prod | `https://shukkindai.example.invalid/` |
| dev | `https://shukkindai-dev.example.invalid/` |

- discovery: `{issuer}.well-known/openid-configuration`
- JWKS: `{issuer}.well-known/jwks`(discovery の `jwks_uri` を参照)
- discovery の `issuer` は設定値で固定だが、各エンドポイントの URL はリクエストの host から組み立てられる。リバースプロキシが `X-Forwarded-Host` / `X-Forwarded-Proto` を正しく渡す必要がある。
- **検証側の受け入れ範囲**
  - prod が発行したトークン: MultiATS と Rendo の dev・prod の両方で通す。
  - dev が発行したトークン: 出勤台 dev だけで通す。
- `aud` は付かない。検証側は audience の検証を切ること。

## アクセストークン

- JWS(暗号化しない)。署名は RS256。
- header: `alg` = `RS256`、`typ` = `at+jwt`、`kid` と `x5t` = 署名証明書の識別子(JWKS の `kid` と一致する)。
- 寿命は1時間(`exp - iat` = 3600秒)。
- ユーザー由来の claim は次の2つだけ。ロール・社員番号・登録 ID などは入らない。
  - **`sub`: Discord ユーザー ID(10進数の文字列)。ulong に変換して使うこと。**
  - `name`: Discord の**ユーザー名**。サーバー内のニックネームや表示名(グローバル名)ではない。出勤台で表示する名前とも別。
- そのほかに OpenIddict が付けるプロトコル claim: `iss`(issuer)、`iat`、`exp`、`jti`、`scope`(`offline_access`)、`client_id`、`oi_prst`、`oi_au_id`、`oi_tkn_id`。`nbf` と `aud` は付かない。`oi_*` は OpenIddict 内部用なので、検証側は使わないこと。

実際のトークンの例(テストで発行したもの。値はダミー):

```json
// header
{"alg":"RS256","kid":"7FFB4FBA9B318B82E6DDD5FF825090BD05FDF3D6","x5t":"f_tPupsxi4Lm3dX_glCQvQX989Y","typ":"at+jwt"}
// payload
{"iss":"https://tatehama-shukkindai-test.example/","exp":1793956743,"iat":1793953143,"scope":"offline_access","jti":"d2c47065-49bf-4e5a-9189-fe983a611fa8","sub":"900000000000740692","name":"alice","oi_prst":"Tatehama_Shukkindai_Client","oi_au_id":"51dd6aa6-6a8a-4331-8de3-b9b5cc8aec91","client_id":"Tatehama_Shukkindai_Client","oi_tkn_id":"18bad90a-cfaa-430d-a215-ca5826d89bed"}
```

## 署名公開鍵の受け取り方

- サーバーの `cert/server-signing-certificate.cer`(公開鍵だけの X.509、DER)を受け取り、検証側に静的に設定する。ネットワークから JWKS を取りに行く必要はない。
- 証明書の有効期限は2年。ローテーションのときは、新しい `.cer` を Crew と TTC に配る必要がある。ローテーションの手順書は範囲外(別タスク)。

.NET での検証例:

```csharp
var certificate = X509CertificateLoader.LoadCertificateFromFile("server-signing-certificate.cer");
var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, new TokenValidationParameters
{
    ValidIssuer = "https://shukkindai.example.invalid/",
    IssuerSigningKey = new X509SecurityKey(certificate),
    ValidateIssuer = true,
    ValidateAudience = false,   // aud は付かない
    ValidateLifetime = true,
});
```

## クライアント登録

- `client_id`: `Tatehama_Shukkindai_Client`(public client、native)。client secret は無い。
- PKCE(S256)が必須。
- リダイレクト URI: `http://localhost:{port}/`。ポートは設定 `Authentication:ClientRedirectPorts`(既定は 51180〜51184)。
- scope: `offline_access`(これ以外の scope は通らない)。
- エンドポイント: `{issuer}connect/authorize`、`{issuer}connect/token`、`{issuer}connect/revoke`。
- refresh token の寿命は7日で、使うたびに新しいものが返る(rolling)。**必ず保存し直すこと。** 古いものは使えなくなる場合がある。

## エラー

| 場面 | error | 意味 | クライアントの動作 |
|---|---|---|---|
| authorize | `access_denied` | Discord サーバーのメンバーではない(未参加・退会済み) | ログインできないことを表示する |
| authorize | `temporarily_unavailable` | Bot の準備ができていない | しばらくしてから再試行を案内する |
| token(refresh) | `invalid_grant` | refresh token が無効・失効、またはメンバーでなくなった | 保存したトークンを捨て、再ログインさせる |
| token(refresh) | `temporarily_unavailable` | Bot の準備ができていない | トークンは捨てず、後で再試行する |

token エンドポイントのエラーは、`invalid_grant` も `temporarily_unavailable` も HTTP 400 で、body は `{"error":"...","error_description":"...","error_uri":"..."}`(JSON)。ステータスコードでは区別できないので、`error` を見ること。authorize のエラーは、クライアントのリダイレクト URI に `error` と `error_description` のクエリで返る。

## デプロイ時の人間の作業

- Discord アプリのリダイレクト URI に `{issuer}auth/callback` を追加する。
- `cert/` を永続化する(署名・暗号化の証明書と `.cer` が入る。コンテナ作り直しで消えると、全トークンが無効になる)。
- リバースプロキシの IP を設定 `ProxyIP` に入れる。
- `Discord:ClientId`・`Discord:ClientSecret`・`Discord:BotToken`・`Discord:GuildId` を環境変数で渡す。
