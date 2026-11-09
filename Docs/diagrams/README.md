# 図（PlantUML）

要件定義書と `Docs/usecases/UC-01〜UC-27` をもとに作成した、ユースケース図・シーケンス図・想定UI（Saltワイヤーフレーム）。実装はまだない。

## 閲覧方法

- VS Code：拡張機能「PlantUML」（jebbs.plantuml）を入れ、`.puml` を開いて `Alt+D` でプレビューする
- コマンドライン：`java -jar plantuml.jar -tsvg -charset UTF-8 Docs/diagrams/**/*.puml`
- 共通スタイルは `common/style.iuml`。シーケンス図とユースケース図は `!include ../common/style.iuml` で読み込む（フォントは Yu Gothic UI）

## 一覧

### ユースケース図（`usecase/`）

| ファイル | 範囲 |
|---|---|
| [overview.puml](usecase/overview.puml) | 全体（全27UC） |
| [pkg-auth-member.puml](usecase/pkg-auth-member.puml) | 認証・社員（UC-01〜03） |
| [pkg-role-deadline.puml](usecase/pkg-role-deadline.puml) | ロール記録・見習い期限（UC-04〜09） |
| [pkg-meeting-setup.puml](usecase/pkg-meeting-setup.puml) | 運転会の準備（UC-10〜14） |
| [pkg-roster.puml](usecase/pkg-roster.puml) | 出勤台（UC-15〜21） |
| [pkg-view-ops.puml](usecase/pkg-view-ops.puml) | 閲覧・運用・おもしろ機能（UC-22〜27） |

### シーケンス図（`sequence/`）

UCファイルと同じ名前で27本ある（例：`sequence/UC-17_出勤台の枠への割り当て.puml`）。各UCの `.md` 末尾の「図」節からリンクしている。

### 想定UI（`ui/`）

| ファイル | 画面 | 対応UC |
|---|---|---|
| [00_画面遷移.puml](ui/00_画面遷移.puml) | 画面遷移図 | 全体 |
| [01_ログイン.puml](ui/01_ログイン.puml) | ログイン | UC-01 |
| [02_ホーム.puml](ui/02_ホーム.puml) | ホーム | UC-01 |
| [03_ダイヤ行路編集.puml](ui/03_ダイヤ行路編集.puml) | ダイヤ・行路編集（デフォルト／運転会用 共通） | UC-10, 12 |
| [04_運転会一覧.puml](ui/04_運転会一覧.puml) | 運転会一覧 | UC-11, 24 |
| [05_運転会作成編集.puml](ui/05_運転会作成編集.puml) | 運転会の作成・編集＋公開設定 | UC-11, 14 |
| [06_信号区割りシフト枠.puml](ui/06_信号区割りシフト枠.puml) | 信号区割り・シフト枠 | UC-13 |
| [07_行路希望.puml](ui/07_行路希望.puml) | 行路の希望 | UC-15 |
| [08_抽選.puml](ui/08_抽選.puml) | 人気行路の抽選 | UC-16 |
| [09_出勤台編集.puml](ui/09_出勤台編集.puml) | 出勤台編集（割り当て・直接書き換え・確定） | UC-17, 20, 21 |
| [09b_出勤台ダイアログ.puml](ui/09b_出勤台ダイアログ.puml) | バージョン衝突／確定前チェック | UC-17, 21 |
| [10_途中離脱参加申請.puml](ui/10_途中離脱参加申請.puml) | 途中離脱・途中参加の申請 | UC-18 |
| [11_申請一覧承認.puml](ui/11_申請一覧承認.puml) | 申請一覧・承認／却下 | UC-19 |
| [12_期限一覧.puml](ui/12_期限一覧.puml) | 期限一覧 | UC-22 |
| [13_出勤履歴.puml](ui/13_出勤履歴.puml) | 個人の出勤履歴 | UC-23 |
| [14_確定済み出勤台.puml](ui/14_確定済み出勤台.puml) | 確定済み出勤台 | UC-24 |
| [15_アルコール検査.puml](ui/15_アルコール検査.puml) | アルコール検査 | UC-27 |
| [16_Bot通知文面.puml](ui/16_Bot通知文面.puml) | Discord Botの通知文面（5種＋失敗通知・ダンプ） | UC-06, 08, 09, 18, 19, 21, 25 |

Bot・バッチだけで動くUC（UC-02〜09, 25, 26）には画面がない。通知の文面だけを `16_Bot通知文面.puml` にまとめた。

## 書き方の約束

- シーケンス図は `autonumber` で番号を振り、メッセージやグループにUC本文の代替フロー（A*）・例外フロー（E*）の番号を書いて対応させている
- 権限と閲覧範囲の判定はAPI側で行う。アプリ側の表示の出し分けは補助
- 同時編集はすべて保存時のバージョン照合（楽観ロック）で扱う。不一致なら `409 Conflict` を返し、後から保存した人に読み込み直させる
- 画面の名前・社員番号・日付はすべて架空の例

## 未確定事項の扱い

`要判断事項.md` の **決定（D-1〜D-12）** はそのまま反映している。UC-01（ログイン）とその画面は、出勤台サーバーを OIDC の IdP とする現行の仕様（`Docs/authentication.md`）に合わせている。**推奨** の項目は推奨案で描き、図の中に「Q-xx-x 推奨案」と書いた。**保留** の項目は次のように仮置きしている。

| 保留事項 | 図での仮置き | 該当ファイル |
|---|---|---|
| 出勤台の確定の仕方（誰が・いつ・確定後の修正・確定し忘れ） | 主催者・共同主催者が運転会の終了後に手動で確定する | sequence/UC-21, ui/09, ui/09b, usecase/overview, pkg-roster |
| 人気行路の抽選タイミング | 主催者が手動で実行する（自動なら起点がスケジューラになる） | sequence/UC-16, ui/08, usecase/overview, pkg-roster |
| 希望の受付締切／担当決定済み行路への希望 | 「保留」と書いておくだけ | sequence/UC-15, ui/07 |
| 当選結果の本人DM | 通知文面に入れていない | ui/16 |
| 信号区割りのテンプレート | 「保留」のボタンだけ置く | sequence/UC-13, ui/06 |

決定 D-2（指導員として入った回は参加に数えない）は、要件定義書・UC-07・UC-21 の本文と食い違っている。図は D-2 に合わせた。
