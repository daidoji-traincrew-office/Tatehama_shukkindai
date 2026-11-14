env "local" {
  src = "file://schema.sql"

  url = "postgres://postgres:postgres@localhost:5434/postgres?search_path=public&sslmode=disable"

  dev = "docker://postgres/16/dev?search_path=public"
}
