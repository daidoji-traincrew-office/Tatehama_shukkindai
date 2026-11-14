-- Create "OpenIddictApplications" table
CREATE TABLE "OpenIddictApplications" (
  "id" text NOT NULL,
  "application_type" character varying(50) NULL,
  "client_id" character varying(100) NULL,
  "client_secret" text NULL,
  "client_type" character varying(50) NULL,
  "concurrency_token" character varying(50) NULL,
  "consent_type" character varying(50) NULL,
  "display_name" text NULL,
  "display_names" text NULL,
  "json_web_key_set" text NULL,
  "permissions" text NULL,
  "post_logout_redirect_uris" text NULL,
  "properties" text NULL,
  "redirect_uris" text NULL,
  "requirements" text NULL,
  "settings" text NULL,
  CONSTRAINT "PK_OpenIddictApplications" PRIMARY KEY ("id")
);
-- Create index "IX_OpenIddictApplications_client_id" to table: "OpenIddictApplications"
CREATE UNIQUE INDEX "IX_OpenIddictApplications_client_id" ON "OpenIddictApplications" ("client_id");
-- Create "OpenIddictScopes" table
CREATE TABLE "OpenIddictScopes" (
  "id" text NOT NULL,
  "concurrency_token" character varying(50) NULL,
  "description" text NULL,
  "descriptions" text NULL,
  "display_name" text NULL,
  "display_names" text NULL,
  "name" character varying(200) NULL,
  "properties" text NULL,
  "resources" text NULL,
  CONSTRAINT "PK_OpenIddictScopes" PRIMARY KEY ("id")
);
-- Create index "IX_OpenIddictScopes_name" to table: "OpenIddictScopes"
CREATE UNIQUE INDEX "IX_OpenIddictScopes_name" ON "OpenIddictScopes" ("name");
-- Create "OpenIddictAuthorizations" table
CREATE TABLE "OpenIddictAuthorizations" (
  "id" text NOT NULL,
  "application_id" text NULL,
  "concurrency_token" character varying(50) NULL,
  "creation_date" timestamptz NULL,
  "properties" text NULL,
  "scopes" text NULL,
  "status" character varying(50) NULL,
  "subject" character varying(400) NULL,
  "type" character varying(50) NULL,
  CONSTRAINT "PK_OpenIddictAuthorizations" PRIMARY KEY ("id"),
  CONSTRAINT "FK_OpenIddictAuthorizations_OpenIddictApplications_application~" FOREIGN KEY ("application_id") REFERENCES "OpenIddictApplications" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION
);
-- Create index "IX_OpenIddictAuthorizations_application_id_status_subject_type" to table: "OpenIddictAuthorizations"
CREATE INDEX "IX_OpenIddictAuthorizations_application_id_status_subject_type" ON "OpenIddictAuthorizations" ("application_id", "status", "subject", "type");
-- Create "OpenIddictTokens" table
CREATE TABLE "OpenIddictTokens" (
  "id" text NOT NULL,
  "application_id" text NULL,
  "authorization_id" text NULL,
  "concurrency_token" character varying(50) NULL,
  "creation_date" timestamptz NULL,
  "expiration_date" timestamptz NULL,
  "payload" text NULL,
  "properties" text NULL,
  "redemption_date" timestamptz NULL,
  "reference_id" character varying(100) NULL,
  "status" character varying(50) NULL,
  "subject" character varying(400) NULL,
  "type" character varying(150) NULL,
  CONSTRAINT "PK_OpenIddictTokens" PRIMARY KEY ("id"),
  CONSTRAINT "FK_OpenIddictTokens_OpenIddictApplications_application_id" FOREIGN KEY ("application_id") REFERENCES "OpenIddictApplications" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION,
  CONSTRAINT "FK_OpenIddictTokens_OpenIddictAuthorizations_authorization_id" FOREIGN KEY ("authorization_id") REFERENCES "OpenIddictAuthorizations" ("id") ON UPDATE NO ACTION ON DELETE NO ACTION
);
-- Create index "IX_OpenIddictTokens_application_id_status_subject_type" to table: "OpenIddictTokens"
CREATE INDEX "IX_OpenIddictTokens_application_id_status_subject_type" ON "OpenIddictTokens" ("application_id", "status", "subject", "type");
-- Create index "IX_OpenIddictTokens_authorization_id" to table: "OpenIddictTokens"
CREATE INDEX "IX_OpenIddictTokens_authorization_id" ON "OpenIddictTokens" ("authorization_id");
-- Create index "IX_OpenIddictTokens_reference_id" to table: "OpenIddictTokens"
CREATE UNIQUE INDEX "IX_OpenIddictTokens_reference_id" ON "OpenIddictTokens" ("reference_id");
