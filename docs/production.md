# Production

Use Docker Compose for production-like deployments.

## 1) Configure secrets

Create a local `.env` and set a strong SA password:

```sh
cp .env.example .env
```

## 2) Start services

Use the production override file (disables auto-migrate):

```sh
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d --build
```

## 3) Run migrations once

Because auto-migrate is disabled in production, run the manual script:

```sh
./scripts/migrate-login-db.sh
```

## 4) Configure the CharServer service token

CharServer authenticates to LoginServer with an HMAC-SHA256 challenge/response
over a shared `ServiceToken` - there is no database row to seed. Set the same
high-entropy value (256 bits/32+ bytes recommended) for both servers, either
in `solutionfiles/secrets/secret.json` under `ServiceAuthentication.CharServer.Token`,
or via the `ATHENA_NET_CHAR_SERVER_SERVICE_TOKEN` environment variable
(suited to deployment secret sources such as Kubernetes Secrets; it takes
priority over the secrets file when both are set). See
`ai/login-server.md` ("Inter-server service authentication") for the full
handshake.
