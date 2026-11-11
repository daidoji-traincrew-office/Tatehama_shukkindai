# マイグレーション用のイメージ(atlas + migrations/)。ビルドコンテキストはこのディレクトリ: docker build Server/Database
# dev / 本番のホストに clone を置かずに `atlas migrate apply` するため。CI(serverBuild.yml)がサーバーと同じタグで push する。
# 使い方は Server/deploy/dev/compose.yml の migrate サービス(--dir file:///migrations)。
FROM arigaio/atlas:1.3.4-community
COPY migrations /migrations
