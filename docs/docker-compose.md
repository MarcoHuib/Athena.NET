# Docker Compose

Create a copy of `docs/.env.example` as `docs/.env` and set `SA_PASSWORD`.

## Production-style stack
From repo root:
```
docker compose --env-file docs/.env -f docker-compose.yml -f docker-compose.prod.yml up -d --build
```

Note: `docker-compose.prod.yml` disables auto-migrate. Run migrations manually if needed.

## Local config/secrets mounts
```
docker compose --env-file docs/.env -f docker-compose.yml -f docker-compose.override.yml up -d --build
```
