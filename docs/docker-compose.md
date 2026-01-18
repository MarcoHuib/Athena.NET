# Docker Compose

## Production-style stack
From repo root:
```
export SA_PASSWORD="<your password>"
docker compose -f docker-compose.yml -f docker-compose.prod.yml up -d --build
```

Note: `docker-compose.prod.yml` disables auto-migrate. Run migrations manually if needed.

## Local config/secrets mounts
```
docker compose -f docker-compose.yml -f docker-compose.override.yml up -d --build
```
