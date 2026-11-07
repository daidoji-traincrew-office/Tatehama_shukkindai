# 証明書の自動ローテーションと運用手順

出勤台 IdP の証明書(署名・暗号化)は、出勤台が自動で作り、切り替え、古いものを消す。この文書は、その仕組みと、人がやること(再起動、監視、緊急時の手順)をまとめる。Crew と TTC 側の読み込み方は `Docs/authentication.md` の「署名公開鍵の受け取り方」を参照。

前提(2026-11-07 に決定済み):

- Crew と TTC は出勤台と**同じホスト**で動く。出勤台の `cert/public/`(`.cer` だけ)を読み取り専用でマウントして、検証鍵にする。人が `.cer` を配る工程は無い。
- 証明書の切り替えは**再起動のときだけ**。動いているプロセスの OpenIddict の options は作り直さない。運転会をやっていないときに、人が手動で再起動する。
- 有効な署名証明書が 1 つも無ければ、**起動を止める**(黙って新しい鍵を作って起動することはしない)。ただし初回(用途のディレクトリに証明書が 1 つも無い)は作って起動する。
- インスタンスは 1 つ。複数にする場合の注意は「複数インスタンスにするとき」を参照。

## 用途と寿命

| 用途 | 何に使うか | 寿命 | 次を作る時期 | 次の鍵の NotBefore | 古い鍵を消す時期 | 期限切れで止まるか |
|---|---|---|---|---|---|---|
| `server-signing` | アクセストークンの署名(JWKS と `cert/public/` で公開) | 2 年 | 最も遠い NotAfter まで残り 150 日 | 期限の 60 日前(未来。公開だけ先にする) | NotAfter + 7 日 | **起動時に止まる**(有効な鍵が無いとき) |
| `server-encryption` | refresh token と authorization code の暗号化 | 2 年 | 残り 60 日 | すぐ(今 − 5 分) | NotAfter + 14 日 | 起動時に止まる(OpenIddict が拒否) |
| `client-signing` | Discord ログインの state token の署名 | 2 年 | 残り 60 日 | すぐ | NotAfter + 1 日 | 止まらない |
| `client-encryption` | 同、暗号化 | 2 年 | 残り 60 日 | すぐ | NotAfter + 1 日 | 止まらない |

- 暗号化の古い鍵を NotAfter + 14 日まで残すのは、それで暗号化された refresh token(寿命 7 日)を復号するため。期限切れの証明書でも、復号には使える。先に消すと、その refresh token は `invalid_grant` になり、利用者は再ログインになる。
- Client 用の古い鍵を NotAfter + 1 日で消すのは、state token の寿命が 15 分だから。
- 「最も遠い NotAfter」は、NotBefore が未来の鍵も含めて数える。次の鍵を作った後は、残りが約 2 年になるので、重複して作らない。

## 署名証明書のタイムライン

現在の鍵の NotAfter を E とする。

```
E-150日  次の鍵を自動生成(NotBefore = E-60日、NotAfter = NotBefore + 2 年)。JWKS と cert/public/ に載る
         (ログ: CertificateCreated(7104) Warning。以後、毎日 NextSigningCertificatePending(7103))
         → Crew と TTC は、切り替え(E-60日)までに、cert/public/ の新しい .cer を読み込む(= 再起動か、定期的な再読み込み)
E-60日   次の鍵の NotBefore を過ぎる。OpenIddict は、再起動のときから次の鍵で署名する
         (動いているプロセスは古い鍵のまま。ログ: RestartRequired(7105)、メトリクス restart_required = 1)
         → 運転会のない日に、出勤台を再起動する。ここから E までの 60 日間が、再起動の期限
E        古い鍵が期限切れ(動いているプロセスは、期限切れの鍵でも署名を続ける。Crew と TTC は鍵の期限を見ない)
E+7日    古い鍵の pfx と .cer を削除(ただし、動いているプロセスが使っているものは、再起動まで消さない)
```

- 生成が遅れた場合(出勤台が長く止まっていた、など): NotBefore = max(E-60日, 今 + 14 日)。ただし E-1 日より後にはしない。猶予が 14 日に満たなかったときは、CertificateCreated が Error になる。
- 署名証明書がすべて期限切れ(または NotBefore が未来のものだけ)になっていたら、新しい鍵は作らず、起動を止める。対処は「全部期限切れで起動しないとき」。
- 時刻はコンテナのクロックではなく、アプリの `TimeProvider`(通常はシステム時刻)で決まる。OpenIddict も同じ時計を使う。

## ファイルのレイアウトと権限

```
cert/                                            0700   Authentication:CertificateDirectory(Docker では /app/cert。永続化する)
  .rotation.lock                                 0600   排他ロック(複数のプロセスが同時に作らないため。消えていても問題ない)
  server-signing/20261107T000000Z-ACBE7005.pfx   0600   {NotBefore}-{Thumbprint 先頭 8 桁}.pfx。パスワードなし。ディレクトリは 0700
  server-encryption/*.pfx
  client-signing/*.pfx
  client-encryption/*.pfx
  public/server-signing-{Thumbprint}.cer         0644   署名用の公開鍵だけ(DER)。ディレクトリは 0755。Crew と TTC が読む
```

- `public/` には、署名用の pfx が残っている間の `.cer` だけを置く(現在の鍵、次の鍵、期限後 7 日以内の古い鍵)。pfx を消すと `.cer` も消える。pfx は決して `public/` に置かない。
- ファイルは原子的に書く(同じディレクトリに `.{guid}.tmp` を書いて flush してから move)。`*.cer` は、完成したものか、存在しないかのどちらかしか見えない。`*.tmp` は読まれず、1 日以上前のものは自動で消える。
- 起動のたびに、`public/` を署名用の pfx と照合する。`.cer` が無い・内容が違う場合は書き直し、余分な `.cer` は消す。
- 権限が想定より緩いと、状態のログに `CertificatePermissionTooOpen`(7108)の Warning が出る。
- Docker で bind mount するときは、ホスト側の `cert/` の所有者をコンテナのユーザー(`$APP_UID`、既定 1654)にする。例: `mkdir cert && sudo chown 1654:1654 cert && chmod 700 cert`。

## 再起動の運用

動いているプロセスは、起動時に選んだ証明書を使い続ける。切り替えは再起動のときだけで、再起動が必要になると、出勤台がログとメトリクスで知らせる。

いつ再起動するか:

- `RestartRequired`(7105)のログ、またはメトリクス `shukkindai.certificate.restart_required` が 1 になったら、次に運転会がない日に再起動する。
  - 署名: E-60 日から。期限(E)までの約 60 日が猶予。
  - 暗号化・Client: 新しい鍵を作った翌日から(作ってから 60 日以内が目安)。
- 再起動を急ぐ目安は、使用中の鍵の残りが 30 日を切ったとき(7105 が Error になる)。
- 再起動の前に、Crew と TTC が次の `.cer` を読み込み済みであること(切り替えの 90 日前に `public/` に置かれるので、通常は問題ない)。読み込み済みか分からないときは、Crew と TTC を先に再起動する。

再起動の手順と確認:

1. 運転会がないことを確認する(再起動中は認証できない。起動時に Discord と同期するため、30 秒程度かかることがある)。
2. 出勤台を再起動する(例: `docker compose restart idp`)。
3. 起動ログを確認する。
   - `StartupCertificatesLoaded`(7109)が用途ごとに出て、`ActiveThumbprint` が新しいものになっている。
   - `CertificateSetStatus`(7102)の `ActiveThumbprint` が新しい鍵になり、`RestartRequired`(7105)が出なくなる。
4. 新しくログインして、アクセストークンの header の `kid` が新しい鍵の Thumbprint であることを確認する。Crew と TTC でそのトークンが通ること。
5. メトリクスの `shukkindai.certificate.restart_required` が 0 に戻ること。

古いアクセストークン(最長 1 時間)は、古い鍵で署名されている。Crew と TTC は古い `.cer` もまだ持っている(期限後 7 日まで `public/` にある)ので、そのまま通る。

## ログ

`[LoggerMessage]` で EventId を固定している(アラートで数値を使うため、変えない)。カテゴリは `Tatehama.Shukkindai.Server.Services.CertificateService`。状態のログは起動時と 24 時間ごと(`CertificateRotationHostedService`)。

日時のプロパティは、カルチャに依存しないよう ISO 8601("O" 形式、UTC)の文字列で渡す。残り日数は切り捨ての整数(期限切れは負)。

| EventId / Name | レベル | メッセージ(プロパティ) |
|---|---|---|
| 7101 `CertificateStatus` | Information(証明書ごと) | `CertificatePurpose`、`CertificateThumbprint`、`CertificateState`(`Active`=使用中 / `Pending`=NotBefore が未来 / `Standby`=有効だが未使用 / `Expired`)、`NotBefore`、`NotAfter`、`RemainingDays` |
| 7102 `CertificateSetStatus` | 下の表 | 用途ごと。`ActiveThumbprint`(このプロセスが使っている鍵)、`UsableUntil`、`RemainingDays`(使用中の鍵の残り)、`NewestNotAfter`、`NewestRemainingDays`(ディスク上で最も遠い NotAfter までの残り)、`SuccessorExists`(使用中の鍵より NotAfter が遠い、期限内の鍵がある) |
| 7103 `NextSigningCertificatePending` | Information。切り替えまで 14 日以内なら Warning | 次の署名鍵がある間、毎日。`CertificateThumbprint`、`ActivatesAt`、`DaysUntilActivation`、`CerFileName` |
| 7104 `CertificateCreated` | 署名の次の鍵は Warning(猶予が 14 日に満たなければ Error)。それ以外と初回は Information | `CertificatePurpose`、`CertificateThumbprint`、`NotBefore`、`NotAfter` |
| 7105 `RestartRequired` | Warning。使用中の鍵の残りが 30 日以内なら Error | 使うべき鍵と、プロセスが使っている鍵が違う。`CertificatePurpose`、`CertificateThumbprint`(使うべき鍵)、`PreviousThumbprint`(使用中)、`PreviousNotAfter`、`RemainingDays`。**毎日出る(再起動するまで)** |
| 7106 `CertificateDeleted` | Information | `CertificatePurpose`、`CertificateThumbprint`、`NotAfter` |
| 7107 `CertificateRotationFailed` | Error(例外つき) | `CertificatePurpose`(`server-signing` など、`public`、`all`) |
| 7108 `CertificatePermissionTooOpen` | Warning | `Path`(権限が想定より緩い) |
| 7109 `StartupCertificatesLoaded` | Information | 起動時、用途ごと。`CertificateCount`、`ActiveThumbprint` |
| 7110 `NoValidSigningCertificate` | Critical | 有効な署名証明書が無く、起動を止めた(この直後に例外) |
| 7111 `PublicCertificatesChanged` | Information | `public/` に書いた(`+`)・消した(`-`)`.cer` |

7102 のレベル(何を人に知らせるかで決めている。再起動すれば済む話は、上げすぎない):

| 使用中の鍵の残り | 次の鍵(使用中より NotAfter が遠い鍵)がある | 次の鍵が無い |
|---|---|---|
| 90 日超 | Information | Information |
| 30 日超〜90 日 | Information | **Warning**(自動ローテーションが追いついていない) |
| 0 日超〜30 日 | Warning | **Critical**(人が対応しないと、再起動したときに起動できなくなる) |
| 0 日以下(期限切れ) | Error(再起動すれば直る) | **Critical** |

「再起動が必要」という事実そのものは 7102 ではなく 7105 で知らせる(使用中の鍵の残りが 30 日を超えていれば Warning、30 日以内なら Error)。

## メトリクス(OpenTelemetry)

`OTEL_EXPORTER_OTLP_ENDPOINT` が設定されているときだけ、OpenTelemetry が有効になる(Crew と同じ)。有効なときの内容:

- ASP.NET Core と HttpClient の metrics と tracing
- ログの OTLP 出力(`IncludeFormattedMessage`、`IncludeScopes`)
- 自前の Meter `Tatehama.Shukkindai.Server.Certificates`(下の表)

その他の設定は OpenTelemetry の標準の環境変数で行う(`OTEL_EXPORTER_OTLP_PROTOCOL`、`OTEL_EXPORTER_OTLP_HEADERS`、`OTEL_SERVICE_NAME` など)。`OTEL_SERVICE_NAME=tatehama-shukkindai` を付けること(未設定だと `unknown_service` になる)。

自前のメトリクスは、すべて ObservableGauge。読み取られた時点の時刻で計算するので、1 日 1 回の更新を待たずに値が連続して変わる。tag は用途だけ(`certificate.purpose` = `server-signing` / `server-encryption` / `client-signing` / `client-encryption`。カーディナリティは 4)。

| メトリクス名 | 型 | 単位 | tag | 意味 |
|---|---|---|---|---|
| `shukkindai.certificate.remaining_time` | ObservableGauge(double) | `s` | `certificate.purpose` | このプロセスが使っている証明書の NotAfter までの残り。期限切れは負 |
| `shukkindai.certificate.newest_remaining_time` | ObservableGauge(double) | `s` | `certificate.purpose` | ディスク上で最も遠い NotAfter までの残り。自動ローテーションが動いていれば、署名は 140 日、他は 50 日を下回らない |
| `shukkindai.certificate.restart_required` | ObservableGauge(int) | `{bool}` | `certificate.purpose` | 1: 今の時刻で使うべき証明書が、プロセスの使用中のものと違う(再起動すれば切り替わる) |
| `shukkindai.certificate.next_signing.exists` | ObservableGauge(int) | `{bool}` | なし | 1: NotBefore が未来の署名鍵(次の鍵)がある |
| `shukkindai.certificate.next_signing.activation_time` | ObservableGauge(double) | `s` | なし | 次の署名鍵が有効になるまで。次の鍵がある間だけ出る |
| `shukkindai.certificate.rotation.last_success_timestamp` | ObservableGauge(double) | `s` | なし | 全用途のローテーションが成功した最後の時刻(Unix 時刻の秒) |

- Prometheus 互換に変換されると、名前の `.` は `_` に、単位 `s` は `_seconds` の接尾辞になる想定(例: `shukkindai_certificate_remaining_time_seconds{certificate_purpose="server-signing"}`)。実際の名前は送り先の設定(変換方式)で変わりうるので、Grafana の Explore で確かめること(この実装では確かめていない)。
- Development 環境では、証明書の管理自体が無効(開発用の証明書を使う)なので、これらのメトリクスは出ない。

## Grafana のアラート設計

送り先が Prometheus 互換かどうかは**未確定**。以下は PromQL を想定したクエリで、Loki や別のバックエンドなら、同じ条件に読み替える。評価間隔は 1 分〜1 時間でよい(数日単位の話)。ラベル `service_name` などは収集の設定次第なので、必要に応じて絞り込みを足す。

| # | 名前 | 条件(PromQL) | 継続 | 重大度 | 人がやること |
|---|---|---|---|---|---|
| A1 | 署名: 自動ローテーションが追いついていない | `shukkindai_certificate_newest_remaining_time_seconds{certificate_purpose="server-signing"} < 140 * 86400` | 1h | Warning | 7107 のログで失敗の原因を調べる。「自動化が止まっていたとき」 |
| A2 | 署名: 期限が近い | `shukkindai_certificate_newest_remaining_time_seconds{certificate_purpose="server-signing"} < 30 * 86400` | 5m | Critical | 同上。さらに、猶予が短いので急ぐ |
| A3 | 暗号化・Client: 自動ローテーションが追いついていない | `shukkindai_certificate_newest_remaining_time_seconds{certificate_purpose!="server-signing"} < 50 * 86400` | 1h | Warning | 同上 |
| A4 | 暗号化・Client: 期限が近い | `shukkindai_certificate_newest_remaining_time_seconds{certificate_purpose!="server-signing"} < 14 * 86400` | 5m | Critical | 同上 |
| B1 | 再起動が必要(未実施) | `shukkindai_certificate_restart_required == 1` | 24h | Warning | 次の運転会がない日に再起動する |
| B2 | 再起動が必要で、使用中の鍵の期限が近い | `shukkindai_certificate_restart_required == 1 and on (certificate_purpose) shukkindai_certificate_remaining_time_seconds < 30 * 86400` | 5m | Critical | 早めに再起動する(運転会があっても、短時間で済ませる) |
| B3 | 使用中の鍵が期限切れ | `shukkindai_certificate_remaining_time_seconds < 0` | 5m | Critical | 再起動(次の鍵があれば直る。無ければ手順書) |
| C1 | ローテーションの成功が古い | `time() - shukkindai_certificate_rotation_last_success_timestamp_seconds > 3 * 86400` | 1h | Warning | 7107 のログを見る。ディスク・権限・ロック |
| C2 | ローテーションの成功がとても古い | `time() - shukkindai_certificate_rotation_last_success_timestamp_seconds > 7 * 86400` | 5m | Critical | 同上 |
| C3 | メトリクスが来ない(出勤台が止まっている、OTLP が届いていない) | `absent(shukkindai_certificate_rotation_last_success_timestamp_seconds)` | 30m | Critical | 出勤台のプロセスと OTLP の経路を確認する |
| D1 | 次の署名鍵の切り替えが近い | `shukkindai_certificate_next_signing_activation_time_seconds < 14 * 86400` | 1h | Info(または Warning) | Crew と TTC が次の `.cer` を読み込み済みか確認する。その後、再起動の計画 |

補足:

- A 系は「ローテーション自体が止まった」ことの検知。正常なら、署名の newest は常に 140 日超(150 日を切った日に次の鍵ができて、約 2 年に戻る)、他は 50 日超。
- B 系は「人が再起動する」ことの検知。B1 は再起動するまで鳴り続ける。再起動すると 0 に戻る。
- C 系は、アラート自体が働いていることの保険。A 系は自動化が止まったときに値が下がるので鳴るが、プロセス全体が止まるとメトリクスが出なくなる(C3)。
- Grafana のアラートの「No Data」は、C3 以外では Normal 扱いにする(A・B は値が来ているときだけ判定する)。
- 用途ごとに通知先を分ける必要はない(`certificate_purpose` をラベルとして通知に含めれば足りる)。
- `/healthz` には含めない。証明書の期限や再起動の要否は、今リクエストを受けられるかとは別の話で、503 にするとロードバランサーから外れて自分で障害を起こす。

### ログでの代替(Loki)

メトリクスを使わない場合や、メトリクスの補助として。ログの形式は 2 通りある(どちらを送るかは未確定)。

1. **JSON console**: `Logging__Console__FormatterName=json` で標準出力を 1 行の JSON にして、Alloy / Promtail で Loki に送る。EventId は数値(`"EventId":7102`)で、Name は出ない。プロパティは `State` の中に入る(`State.CertificatePurpose`、`State.RemainingDays` など)。
2. **OTLP でログを送る**: Loki の OTLP 取り込みでは、ログの属性(`CertificatePurpose`、`RemainingDays` など)が structured metadata になる。

クエリの例(LogQL。ラベル名は収集の設定次第で、実際の取り込みでは確かめていない):

```logql
# 用途ごとの、使用中の鍵の直近の残り日数(JSON console の場合)。< 90 で Warning、< 30 で Critical
min by (purpose) (
  last_over_time(
    {service="tatehama-shukkindai"}
      | json event_id="EventId", purpose="State.CertificatePurpose", remaining="State.RemainingDays"
      | event_id = "7102"
      | unwrap remaining [26h]
  )
)
```

```logql
# 状態のログが 26 時間出ていない(出勤台が止まっている、HostedService が死んでいる)
absent_over_time({service="tatehama-shukkindai"} | json event_id="EventId" | event_id = "7102" [26h])
```

```logql
# 再起動が必要(7105)、ローテーションの失敗(7107)、起動の停止(7110)が 1 件でもあれば通知
sum(count_over_time({service="tatehama-shukkindai"} | json event_id="EventId" | event_id =~ "7105|7107|7110" [1h]))
```

- ログでは、ログレベルでも絞り込める(7102 は上の表のレベルで出るので、`LogLevel = "Critical"` の 7102 は「次の鍵が無く、期限が近い」)。
- 評価間隔は 1 時間、範囲は 26 時間(1 日 1 回のログを取りこぼさない)。

## 手順書(Runbook)

用語: 「証明書のディレクトリ」は `cert/`(Docker では `/app/cert` にマウントしたもの)。以下のコマンドは、`Server/tools/` のスクリプト(.NET 10 の file-based app)を使う。`dotnet run Server/tools/inspect-cert.cs -- <ファイル>...` で kid と期限を、`dotnet run Server/tools/gen-cert.cs -- <purpose> <certDir> [notBefore] [years]` で、アプリと同じレイアウトの pfx を作れる。OpenSSL でも確認できる: `openssl x509 -in x.cer -inform der -noout -dates -fingerprint -sha1`(fingerprint の `:` を除いたものが kid)。

### 0. 事前確認(どの手順でも最初に)

1. 今の証明書の一覧と期限を確かめる: `dotnet run Server/tools/inspect-cert.cs -- cert/server-signing/*.pfx cert/public/*.cer`。
2. JWKS(`{issuer}.well-known/jwks`)の `kid` と一致することを確かめる。
3. 出勤台のログで、直近の `CertificateSetStatus`(7102)と `RestartRequired`(7105)を見る。
4. 作業前に `cert/` を丸ごとバックアップする(権限を保つ: `cp -a`)。バックアップは作業後に消す(秘密鍵のコピーを増やさない)。

### 1. 通常の再起動(切り替え)

「再起動の運用」を参照。

### 2. 緊急ローテーション(鍵の漏洩)

漏洩した鍵は**すぐ**撤去する。猶予期間は置かない。配り終えるまで Crew と TTC が 401 になることは受け入れる。

署名鍵が漏洩した場合:

1. 出勤台を停止する。
2. `cert/server-signing/` の pfx を全部消す(漏洩した鍵だけでなく、次の鍵も新しく作り直すほうが安全。次の鍵が漏洩していないと言い切れるなら、それは残してよい)。
3. 出勤台を起動する。ディレクトリが空なので、**すぐ有効な新しい鍵**が作られ、`cert/public/` の `.cer` が入れ替わる(古い `.cer` は消える)。ログに `CertificateCreated`(7104、初回)と `PublicCertificatesChanged`(7111)が出る。
4. Crew と TTC に新しい `.cer` を読み込ませる(再起動)。古い `.cer` を信頼しなくなるので、漏洩した鍵で作られた偽のトークンは通らなくなる。
5. 利用者は、出勤台クライアントで取り直す(refresh で新しいアクセストークンが出る)。

暗号化鍵が漏洩した場合: `cert/server-encryption/` の pfx を全部消して再起動する。その鍵で暗号化された refresh token は使えなくなり、**全員が再ログイン**になる。refresh token の一括失効は MVP の範囲外(DB の `OpenIddictTokens` を直接消す、など別途検討)。

### 3. ロールバック(切り替えたら Crew/TTC で 401 が出た)

原因の例: Crew/TTC が次の `.cer` を読み込んでいない。

1. 出勤台を停止し、新しい鍵の pfx を `cert/server-signing/` から別の場所へ移す(消さない)。古い鍵が期限内であることを確かめる(`inspect-cert.cs`)。
2. 出勤台を起動する。古い鍵で署名するように戻る。このとき自動ローテーションが、次の鍵を新しく作る(猶予は最低 14 日。期限が近ければ短くなり、Error のログが出る)。
3. Crew と TTC が `cert/public/` の `.cer` を全部読み込んでいることを確認する(「署名公開鍵の受け取り方」)。
4. 移した pfx は、不要になったら安全に消す。
5. 新しい鍵で発行済みのトークン(最長 1 時間)は、Crew と TTC では通らない。利用者には取り直してもらう。

古い鍵がもう期限切れの場合は戻せない(起動を拒否される)。この状況にしないために、切り替えは期限の 60 日前に行う設計になっている。

### 4. 自動化が止まっていたときの復旧

症状: 7107 `CertificateRotationFailed` が出ている、メトリクス `rotation.last_success_timestamp` が古い、署名の `newest_remaining_time` が 140 日を切った(アラート A1〜A4、C1〜C2)。

1. 7107 の例外を見て、原因を取り除く。よくある原因:
   - `cert/` の権限・所有者(コンテナのユーザーが書き込めない)、ディスクの空き。
   - 壊れた pfx(`InvalidDataException`。例外メッセージにパスが出る)→ 「5. 壊れた pfx」。
   - `.rotation.lock` が取れない(`IOException`)。他のプロセスが持っている。ロックはプロセスが終わると自動で解放される。ファイルが残っていても問題ない。
2. 原因を直したら、出勤台を再起動する(起動時に 1 回、ローテーションが走る)か、次の 24 時間ごとの実行を待つ。
3. 署名の次の鍵が、猶予が短いまま作られた場合(CertificateCreated が Error)は、Crew と TTC に `public/` を早めに読み込ませる。
4. 期限が切れる前に直らない場合は、「全部期限切れで起動しないとき」の手順を使う。

### 5. 壊れた pfx

出勤台が `Certificate file ... could not be read as a PKCS#12 file` で起動に失敗する、または 7107 が出続ける。

1. 例外メッセージのパスのファイルを、別の場所へ移す(`.pfx` 以外の拡張子にしてもよい。調査後に消す)。
2. 出勤台を再起動する。残りの有効な鍵で起動し、足りない用途は自動で作られる。
3. 署名の pfx が 1 つも残らなかった場合は、「全部期限切れで起動しないとき」と同じ扱い(新しい鍵が作られる。Crew/TTC は読み込み直しが必要)。

### 6. 全部期限切れで起動しないとき

症状: 起動ログに `NoValidSigningCertificate`(7110、Critical)と、「有効なサーバー署名証明書がありません」の例外。署名の pfx がすべて期限切れ(または NotBefore が未来のものだけ)。原因は、出勤台が長期間止まっていた、自動化が止まっていた、など。

これは黙って直さない設計になっている(新しい鍵に勝手に切り替わると、Crew と TTC が全部 401 になるため)。人が判断して次を行う。

1. 「0. 事前確認」で、期限を確かめ、`cert/` をバックアップする。
2. 古い鍵を使い続けたい理由が無ければ(通常は無い)、`cert/server-signing/` の pfx をすべて別の場所へ移す。
   - NotBefore が未来の鍵だけが残っている場合は、その NotBefore を待つか、同じく移す。
3. 出勤台を起動する。ディレクトリが空なので、**すぐ有効な新しい鍵**が作られる。`cert/public/` の `.cer` も入れ替わる。
4. Crew と TTC を再起動して、新しい `.cer` を読み込ませる。それまで Crew と TTC は 401 になる。
5. 利用者は、出勤台クライアントで再ログイン(または refresh)する。
6. 移した古い pfx は、不要になったら安全に消す。

暗号化(`server-encryption`)の pfx が全部期限切れの場合は、自動で新しい鍵が作られて起動する(署名と違い、外部に配る必要が無いため)。古い鍵で暗号化された refresh token は、保持期間内(NotAfter + 14 日)なら復号できる。

### 7. 次の署名鍵を手動で作る(特殊な場合)

自動生成を待たずに、NotBefore を指定した鍵を作りたいとき。

1. `dotnet run Server/tools/gen-cert.cs -- server-signing <certDir> 2027-08-01T00:00:00Z` で pfx を作る(NotBefore を過去にすれば、すぐ有効)。
2. 出勤台を停止/再起動する。起動時に、`public/` の `.cer` が書かれる。
3. JWKS に新しい kid が**増えていること**、新しくログインして得たトークンの kid が、NotBefore 前は古いままであることを確かめる。

### 複数インスタンスにするとき(現状は想定しない)

- `cert/` を共有ボリュームにすること。インスタンスごとに別の鍵を作ると、Crew と TTC がどれを信頼すべきか分からなくなる。
- 生成は `cert/.rotation.lock` の排他ロックの中で行い、ロックを取った後に一覧を読み直す。同じホストのプロセス間では効くが、NFS などのネットワークファイルシステムでは効かないことがある。
- 切り替えは再起動のときだけなので、**すべてのインスタンスを再起動**すること。暗号化鍵は、新しい鍵を使い始めたインスタンスが暗号化したトークンを、まだ新しい鍵を読み込んでいないインスタンスが復号できない時間ができる。ローリング再起動のときは、この点を考慮する。

## 実装メモ(コードの場所)

- `Services/CertificateRotationPolicy.cs`: 作る・消すの判断(純粋関数。時刻と証明書の一覧から決める)
- `Services/CertificateService.cs`: 起動時の読み込み(`GetStartupCertificates`)と定期実行(`Maintain`)、状態のスナップショット
- `Repositories/Certificate/`: ファイルの I/O(原子的な書き込み、権限、ロック)
- `Authentication/OpenIddictCertificateConfiguration.cs`: 起動時に決めた証明書を OpenIddict の Server と Client に登録する
- `HostedService/CertificateRotationHostedService.cs`: Kestrel が listen する前に証明書をそろえ(OpenIddict の options もここで作る)、24 時間ごとに `Maintain` を呼ぶ
- `Services/CertificateMetrics.cs`、`Services/CertificateLog.cs`: メトリクスとログ
- OpenIddict の挙動(複数の証明書、NotBefore が未来の鍵、options の作り直しが 1 回だけであること)は、調査報告をもとにしている。OpenIddict を上げたときは、IT(`CertificateRotationTest`)で挙動が変わっていないか確かめること。
