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

## 署名公開鍵の受け取り方(Crew・TTC 向け)

Crew と TTC は出勤台と同じホストで動く前提で、出勤台の公開鍵ディレクトリ `cert/public/` を**読み取り専用でマウント**し、中の `*.cer` を**全部**検証鍵にする。人が `.cer` を配る工程は無い。生成・公開・切り替え・撤去は、すべて出勤台が自動で行う(`Docs/certificate-rotation.md`)。ネットワークから JWKS を取りに行く必要は無い。

- `cert/public/` には、署名用の公開鍵だけの X.509(DER)が `server-signing-{Thumbprint}.cer` の名前で入る。**pfx(秘密鍵)は決して置かれない**。
- 通常は 2 つある。現在の鍵と、次の鍵(NotBefore が未来。切り替えの 90 日前に置かれる)。切り替え後は、古い鍵も期限後 7 日まで残る。したがって、3 つ以上になることもある。
- 各 `.cer` の Thumbprint(SHA-1、大文字の 16 進数)が、アクセストークンの header の `kid` と一致する。**`kid` で選んで検証する**(全部を総当たりしない)。
- **実装は Crew と TTC のリポジトリで後日行う**(この文書は設計書。出勤台側の PR には含まれない)。

### 読み込み方

- 起動時に、`*.cer` を全部読む。**加えて、定期的に(例: 1 時間ごと)、または `FileSystemWatcher` で、読み直すことを推奨する**。次の鍵は切り替えの 90 日前に置かれるので、読み直しが無くても、遅くとも切り替えまでに Crew/TTC を再起動すれば間に合う。ただし、出勤台は再起動のときに次の鍵で署名し始めるので、**出勤台の再起動より前に**、Crew/TTC が次の `.cer` を読み込み済みであること。
- ディレクトリを読む間に、書きかけのファイルを読まない。出勤台は、一時ファイル(`.{guid}.tmp`)に書いてから move で置くので、`*.cer` は完成したファイルか、存在しないかのどちらかしか見えない。**`*.cer` だけを対象にする**こと(`*.tmp` や拡張子の無いファイルは読まない)。読み込みに失敗した `.cer` は、スキップしてログを出し、次の読み直しで再試行する。
- 空のディレクトリのとき(出勤台がまだ一度も起動していない、マウントのミス): 検証鍵が 0 個になる。この状態では**どのトークンも通さず**(全部 401)、起動時はエラーログを出すこと。起動を止めるかどうかは Crew/TTC の判断でよい。読み直しをする実装なら、鍵が 1 つ以上見つかるまで再試行する。
- 読み直しで鍵が 0 個になったとき(マウントが外れた、など)は、前回の鍵を使い続ける(0 個で上書きしない)。
- 読み込んだ `kid` の一覧を、起動時と読み直しのたびにログに出すこと(障害時に「どの鍵を信頼しているか」を確かめられる)。

### .NET での検証例

```csharp
// cert/public を読み取り専用でマウントしたパス(例: /idp-public)
IReadOnlyList<SecurityKey> LoadKeys(string directory) =>
    Directory.EnumerateFiles(directory, "*.cer")
        .Select(path => (SecurityKey)new X509SecurityKey(X509CertificateLoader.LoadCertificateFromFile(path)))
        .ToList();   // kid は Thumbprint になる(JWT header の kid と一致する)

var parameters = new TokenValidationParameters
{
    ValidIssuer = "https://shukkindai.example.invalid/",
    IssuerSigningKeys = LoadKeys("/idp-public"),   // 複数の鍵。header の kid が一致する鍵で検証される
    ValidateIssuer = true,
    ValidateAudience = false,           // aud は付かない
    ValidateLifetime = true,
    ValidateIssuerSigningKey = false,   // 鍵(証明書)の期限は見ない。下記
    ValidTypes = ["at+jwt"],
};
var result = await new JsonWebTokenHandler().ValidateTokenAsync(token, parameters);
```

- `ValidateIssuerSigningKey` は **`false` のままにする**(明示しておく)。`true` にすると、X.509 の期限切れの鍵で署名されたトークンを、証明書の期限を理由に拒否する(`IDX10249`)。出勤台は、切り替えが少し遅れても(再起動のタイミングなど)、期限切れの鍵で署名を続けることがあるので、期限の境目で検証が落ちる。トークン自体の期限(`ValidateLifetime`)は `true` のまま。
- kid が一致する鍵が無いと、`IDX10503`(署名の検証に失敗)になる。次の鍵で署名されたトークンが、鍵を読み込む前に届くと起きる。読み直しの仕組みがあれば、この失敗を契機に 1 回だけ読み直して再試行してもよい。
- OpenIddict.Validation を使う場合(Crew)は、`options.SetIssuer(...)` と `options.SetConfiguration(new OpenIddictConfiguration { Issuer = ..., SigningKeys = { ...keys } })` で静的に渡す。`AddSigningKey` / `AddSigningCertificate` は秘密鍵を要求するので、公開鍵だけの `.cer` には使えない。鍵の読み直しをするときは、`OpenIddictConfiguration` を作り直して差し替える必要がある(OpenIddict のバージョンによって API が違うので、実装時に確かめること)。

### Docker compose でのマウントの例

出勤台の `cert/` を永続化する(bind mount)。Crew/TTC は、そのうちの `public/` だけを読み取り専用でマウントする。

```yaml
services:
  idp:
    image: tatehama-shukkindai-server
    volumes:
      - ./idp-cert:/app/cert            # pfx(秘密鍵)を含む。出勤台だけがマウントする
    environment:
      - OTEL_SERVICE_NAME=tatehama-shukkindai
  crew:
    image: traincrew-multiats-crew
    volumes:
      - ./idp-cert/public:/idp-public:ro   # 公開鍵(.cer)だけ。読み取り専用
  ttc:
    image: ttc
    volumes:
      - ./idp-cert/public:/idp-public:ro
```

- `./idp-cert` はホスト側で、コンテナのユーザー(`$APP_UID`、既定 1654)が書き込めるようにしておく(`sudo chown 1654:1654 idp-cert && chmod 700 idp-cert`)。`public/` は出勤台が 0755 で作り、`.cer` は 0644 なので、他のコンテナのユーザーからも読める。
- 出勤台が `public/` を作る前に Crew/TTC が起動すると、マウント元が無い(Docker が root 所有で空のディレクトリを作ってしまう)ことがある。出勤台を先に起動し、`depends_on` で順序を付けるか、`public/` を事前に作っておくこと。
- `./idp-cert` 全体を Crew/TTC にマウントしてはいけない(pfx が見える)。

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
| authorize | `temporarily_unavailable` | Discord と同期し直している最中(接続が切れた直後など) | しばらくしてから再試行を案内する |
| authorize | `access_denied` | Discord の同意画面で、利用者が拒否またはキャンセルした。description は「Discord での認可が拒否されました。」 | ログインが拒否されたことを表示する |
| authorize | `temporarily_unavailable` | Discord 側が `temporarily_unavailable` を返した | しばらくしてから再試行を案内する |
| authorize | `server_error` | Discord 側のそれ以外のエラー(`server_error`、`invalid_request`、不明なもの)。description は「Discord でのログインに失敗しました。」 | エラーを表示し、再試行できるようにする |
| token(refresh) | `invalid_grant` | refresh token が無効・失効、またはメンバーでなくなった | 保存したトークンを捨て、再ログインさせる |
| token(refresh) | `temporarily_unavailable` | Discord と同期し直している最中(接続が切れた直後など) | トークンは捨てず、後で再試行する |

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

- Discord の判定は Bot のギルドキャッシュだけで行い、REST には問い合わせない(`AlwaysDownloadUsers` で全メンバーを持つ)。キャッシュを信用するのは、Gateway に接続中・ギルドが見つかる・ギルドが接続中・全メンバーを受け取り済み、の 4 条件が全部そろったときだけ。1 つでも欠けると `temporarily_unavailable` を返す(メンバーでないとは言い切れないため)。切断後の再接続など、Discord と同期し直している間だけ起きる。
- 起動時に Bot のログイン・ギルドの発見・全メンバーの受信を最大 30 秒待つ。Discord に接続できない(トークン不正を含む)、またはギルドが見つからない(`Discord:GuildId` の誤り、Bot が未参加)場合は、**起動に失敗する**(例外でプロセスが終わる)。ASP.NET Core は hosted service の起動が終わってから listen するので、起動直後に `temporarily_unavailable` が返ることは基本的にない。
- `/healthz` は readiness 用(DB 接続と、Discord との同期を見る)。Discord 側の障害で 503 になりうるので、liveness には使わない。
- 証明書(署名・暗号化)は、出勤台が自動でローテーションする(寿命 2 年。署名は残り 150 日で次の鍵を作って公開し、期限の 60 日前に切り替える)。**切り替えは再起動のときだけ**で、再起動が必要になるとログ・メトリクスで知らせる。有効な署名証明書が 1 つも無いと、IdP は起動しない。運用と手順書は `Docs/certificate-rotation.md`。
- `cert/` は用途別のディレクトリ(`server-signing/` など)に pfx(秘密鍵を含む)を置く。Unix では pfx は 0600、`cert/` と用途別ディレクトリは 0700、`public/` は 0755、`.cer` は 0644。権限が緩いと Warning を出す。
- `cert/public/` の `.cer` は、署名用 pfx と照合して必要なら書き直す。pfx を消すと `.cer` も消える。
- 期限切れのトークンと、使われなくなった authorization は、6時間ごとに(14日より古いものを)削除する。
- ログイン途中の Cookie(`__Host-Shukkindai.Login`、Secure・HttpOnly・SameSite=Lax、10分)は、Data Protection の鍵を永続化していないので、再起動すると無効になる(ログインをやり直すだけ)。

## 既知の制約

- `prompt=none`・`prompt=login`・`max_age` には対応していない(毎回、Cookie の有無で動く)。
- Data Protection の鍵は永続化しない(上記のとおり、影響は10分のログイン途中 Cookie が無効になるだけ)。

## デプロイ時の人間の作業

- Discord アプリのリダイレクト URI に `{issuer}auth/callback` を追加する。
- Discord Developer Portal で、Bot の **Server Members Intent** を有効にする。Bot を対象のギルドに招待する(`Discord:GuildId` と一致させる。Bot がギルドにいない、または `Discord:GuildId` が違うと、起動に失敗する)。
- DB に、`Server/Database/migrations/` を `atlas migrate apply` で適用する。
- `cert/` を永続化する(署名・暗号化の証明書が用途別に入り、公開鍵は `cert/public/` に入る。コンテナ作り直しで消えると、新しい鍵が作られ、全トークンが無効になる)。コンテナのユーザーが書き込めること。Crew と TTC には `cert/public/` だけを読み取り専用でマウントする(上の「Docker compose でのマウントの例」)。
- OpenTelemetry を使うときは、`OTEL_EXPORTER_OTLP_ENDPOINT`(と `OTEL_SERVICE_NAME=tatehama-shukkindai`)を環境変数で渡す。未設定なら無効。メトリクスの内容とアラートは `Docs/certificate-rotation.md`。
- リバースプロキシの IP を設定 `ProxyIP` に入れる。リバースプロキシは `X-Forwarded-Host` / `X-Forwarded-Proto` を、クライアントから来た値を素通しせず、自分で上書きすること(エンドポイントの URL とリダイレクト URI の組み立てに使われる)。
- `Discord:ClientId`・`Discord:ClientSecret`・`Discord:BotToken`・`Discord:GuildId` を環境変数で渡す(全環境で必須。未設定だと起動時に例外)。
- dev サーバーも `ASPNETCORE_ENVIRONMENT=Development` では動かさない(開発用の証明書になり、ForwardedHeaders も無効になる)。
