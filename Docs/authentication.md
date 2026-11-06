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
- PKCE は S256 が必須。`plain` は拒否される(`code_challenge_method=plain` は `invalid_request`、HTTP 400)。discovery の `code_challenge_methods_supported` も `S256` だけ。
- リダイレクト URI: `http://localhost:{port}/`。ポートは設定 `Authentication:ClientRedirectPorts`(既定は 51180〜51184)。
- scope: `offline_access` だけ。`openid` は宣言(discovery の `scopes_supported`)にも受付にも含めない。`scope=openid offline_access` で authorize すると `invalid_scope`(HTTP 400。リダイレクトされず、サーバーのエラー応答になる)。OpenIddict.Client は `scopes_supported` に `openid` があると自動で付けて id_token を必須にするので、宣言から外してある。identity token は発行しない。
- エンドポイント: `{issuer}connect/authorize`、`{issuer}connect/token`、`{issuer}connect/revoke`。
- refresh token の寿命は7日で、使うたびに新しいものが返る(rolling、有効期限も延びる)。**必ず保存し直すこと。**
- 使用済みの refresh token の再使用は、OpenIddict 既定の猶予(30秒)までしか許されない。猶予を過ぎて再使用すると、不正な再利用とみなされ、同じ authorization のトークンが連鎖して失効しうる(OpenIddict の挙動。この実装では変更していない)。更新結果を保存する前にクラッシュした場合などに備え、更新と保存は1か所で直列に行うこと。
- **refresh は出勤台クライアントだけが行う。** 他のアプリ(ATS など)には、アクセストークンだけを渡すこと(refresh token は渡さない)。

## エラー

| 場面 | error | 意味 | クライアントの動作 |
|---|---|---|---|
| authorize | `access_denied` | Discord サーバーのメンバーではない(未参加・退会済み) | ログインできないことを表示する |
| authorize | `temporarily_unavailable` | Bot の準備ができていない | しばらくしてから再試行を案内する |
| authorize | `access_denied` | Discord の同意画面で、利用者が拒否またはキャンセルした。description は「Discord での認可が拒否されました。」 | ログインが拒否されたことを表示する |
| authorize | `temporarily_unavailable` | Discord 側が `temporarily_unavailable` を返した | しばらくしてから再試行を案内する |
| authorize | `server_error` | Discord 側のそれ以外のエラー(`server_error`、`invalid_request`、不明なもの)。description は「Discord でのログインに失敗しました。」 | エラーを表示し、再試行できるようにする |
| token(refresh) | `invalid_grant` | refresh token が無効・失効、またはメンバーでなくなった | 保存したトークンを捨て、再ログインさせる |
| token(refresh) | `temporarily_unavailable` | Bot の準備ができていない | トークンは捨てず、後で再試行する |

token エンドポイントのエラーは、`invalid_grant` も `temporarily_unavailable` も HTTP 400 で、body は `{"error":"...","error_description":"...","error_uri":"..."}`(JSON)。ステータスコードでは区別できないので、`error` を見ること。authorize のエラーは、クライアントのリダイレクト URI に `error` と `error_description` のクエリで返る。

## Discord の同意画面で拒否・失敗したとき

利用者が Discord の同意画面で拒否(またはキャンセル)すると、Discord は出勤台サーバーの `auth/callback` に `error=access_denied` 付きで戻る。出勤台サーバーはこれを、クライアントの `redirect_uri` への **`error=access_denied` と、クライアントが送った元の `state`** に変換して返す(`http://localhost:{port}/?error=access_denied&error_description=...&state=...&iss=...`)。クライアントは `state` を照合したうえで、`error` を見て表示すること。

- 流れ: `connect/authorize` → Discord → `auth/callback`(失敗を検知し、ログイン途中の Cookie に失敗の印を付けて authorize へ 302)→ `connect/authorize`(印を見て Cookie を破棄し、OpenIddict がエラー応答を作ってクライアントへ 302)。`auth/callback` からクライアントの `redirect_uri` へ直接は戻らない(`state` を付けられず、open redirect にもなるため)。
- Discord の error の写像は、`access_denied` → `access_denied`、`temporarily_unavailable` → `temporarily_unavailable`、それ以外はすべて `server_error`。
- 戻り先が分からない場合(state が無い・壊れている・期限切れ=ログイン開始から時間が経った、サーバーを再起動した、など)は、クライアントには返せない。この場合はブラウザに「Discord でのログインを完了できませんでした。出勤台クライアントからやり直してください。」(HTTP 400、text/plain)が表示される。クライアントは、一定時間待っても `redirect_uri` に戻らなければ、タイムアウトとして扱うこと。

## Discord ログインについて

- Discord へのログインは、OpenIddict.Client の Discord プロバイダーで行う。OpenIddict 7.8.0 のプロバイダーは静的設定を持たず、Discord 自身の discovery(`https://discord.com/.well-known/openid-configuration`)を読む。Discord は discovery で `authorization_response_iss_parameter_supported: true` を宣言しているので、Crew(OpenIddict 5.x)で必要だった「redirect の `iss` パラメーターの回避ハンドラ」は **7.8.0 では不要になった**ため、この実装には入れていない(`DiscordRegistrationTests` が前提を固定している)。
- 要求される Discord の scope は、実際には `openid identify`(7.8 の WebIntegration が `openid` を足す)。メンバー確認は Bot が行うので、ユーザーの Discord トークンは使わず、Cookie にも持ち回らない。

## 運用上の注意

- `/healthz` は readiness 用(DB 接続と Bot の Ready を見る)。Discord 側の障害で 503 になりうるので、liveness には使わない。
- 証明書の有効期限が切れると、OpenIddict が拒否するため IdP は起動しなくなる。2年の期限の前に更新が要る(30日前から起動時に Warning ログを出す)。
- `cert/` の pfx(秘密鍵を含む)は、Unix では 0600、ディレクトリは 0700 で作る。権限が緩いと起動時に Warning を出す。
- `.cer` は起動のたびに、署名用 pfx と照合して必要なら書き直す。
- 期限切れのトークンと、使われなくなった authorization は、6時間ごとに(14日より古いものを)削除する。
- ログイン途中の Cookie(`__Host-Shukkindai.Login`、Secure・HttpOnly・SameSite=Lax、10分)は、Data Protection の鍵を永続化していないので、再起動すると無効になる(ログインをやり直すだけ)。

## 既知の制約

- `prompt=none`・`prompt=login`・`max_age` には対応していない(毎回、Cookie の有無で動く)。
- Data Protection の鍵は永続化しない(上記のとおり、影響は10分のログイン途中 Cookie が無効になるだけ)。

## デプロイ時の人間の作業

- Discord アプリのリダイレクト URI に `{issuer}auth/callback` を追加する。
- Discord Developer Portal で、Bot の **Server Members Intent** を有効にする。Bot を対象のギルドに招待する(`Discord:GuildId` と一致させる。Bot がギルドにいないと Ready 後に Error ログが出て、認証は常に `temporarily_unavailable` になる)。
- DB に、`Server/Database/migrations/` を `atlas migrate apply` で適用する。
- `cert/` を永続化する(署名・暗号化の証明書と `.cer` が入る。コンテナ作り直しで消えると、全トークンが無効になる)。コンテナのユーザーが書き込めること、権限が 0600 / 0700 であること。
- リバースプロキシの IP を設定 `ProxyIP` に入れる。リバースプロキシは `X-Forwarded-Host` / `X-Forwarded-Proto` を、クライアントから来た値を素通しせず、自分で上書きすること(エンドポイントの URL とリダイレクト URI の組み立てに使われる)。
- `Discord:ClientId`・`Discord:ClientSecret`・`Discord:BotToken`・`Discord:GuildId` を環境変数で渡す(全環境で必須。未設定だと起動時に例外)。
- dev サーバーも `ASPNETCORE_ENVIRONMENT=Development` では動かさない(開発用の証明書になり、ForwardedHeaders も無効になる)。
