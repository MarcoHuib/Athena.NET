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

## 4) Optional seed

```sh
./scripts/seed-login-server-account.sh
```
