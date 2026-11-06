CREATE TABLE "OpenIddictApplications"
(
    id                        text NOT NULL,
    application_type          character varying(50),
    client_id                 character varying(100),
    client_secret             text,
    client_type               character varying(50),
    concurrency_token         character varying(50),
    consent_type              character varying(50),
    display_name              text,
    display_names             text,
    json_web_key_set          text,
    permissions               text,
    post_logout_redirect_uris text,
    properties                text,
    redirect_uris             text,
    requirements              text,
    settings                  text,
    CONSTRAINT "PK_OpenIddictApplications" PRIMARY KEY (id)
);

CREATE UNIQUE INDEX "IX_OpenIddictApplications_client_id" ON "OpenIddictApplications" (client_id);

CREATE TABLE "OpenIddictScopes"
(
    id                text NOT NULL,
    concurrency_token character varying(50),
    description       text,
    descriptions      text,
    display_name      text,
    display_names     text,
    name              character varying(200),
    properties        text,
    resources         text,
    CONSTRAINT "PK_OpenIddictScopes" PRIMARY KEY (id)
);

CREATE UNIQUE INDEX "IX_OpenIddictScopes_name" ON "OpenIddictScopes" (name);

CREATE TABLE "OpenIddictAuthorizations"
(
    id                text NOT NULL,
    application_id    text,
    concurrency_token character varying(50),
    creation_date     timestamp with time zone,
    properties        text,
    scopes            text,
    status            character varying(50),
    subject           character varying(400),
    type              character varying(50),
    CONSTRAINT "PK_OpenIddictAuthorizations" PRIMARY KEY (id),
    CONSTRAINT "FK_OpenIddictAuthorizations_OpenIddictApplications_application~" FOREIGN KEY (application_id) REFERENCES "OpenIddictApplications" (id)
);
CREATE INDEX "IX_OpenIddictAuthorizations_application_id_status_subject_type" ON "OpenIddictAuthorizations" (application_id, status, subject, type);

CREATE TABLE "OpenIddictTokens"
(
    id                text NOT NULL,
    application_id    text,
    authorization_id  text,
    concurrency_token character varying(50),
    creation_date     timestamp with time zone,
    expiration_date   timestamp with time zone,
    payload           text,
    properties        text,
    redemption_date   timestamp with time zone,
    reference_id      character varying(100),
    status            character varying(50),
    subject           character varying(400),
    type              character varying(150),
    CONSTRAINT "PK_OpenIddictTokens" PRIMARY KEY (id),
    CONSTRAINT "FK_OpenIddictTokens_OpenIddictApplications_application_id" FOREIGN KEY (application_id) REFERENCES "OpenIddictApplications" (id),
    CONSTRAINT "FK_OpenIddictTokens_OpenIddictAuthorizations_authorization_id" FOREIGN KEY (authorization_id) REFERENCES "OpenIddictAuthorizations" (id)
);

CREATE INDEX "IX_OpenIddictTokens_authorization_id" ON "OpenIddictTokens" (authorization_id);
CREATE UNIQUE INDEX "IX_OpenIddictTokens_reference_id" ON "OpenIddictTokens" (reference_id);
