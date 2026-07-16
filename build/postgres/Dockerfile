FROM pgvector/pgvector:pg18

ARG POSTGRES_MAJOR=18

RUN apt-get update \
    && apt-get install -y --no-install-recommends \
    "postgresql-${POSTGRES_MAJOR}-partman" \
    "postgresql-${POSTGRES_MAJOR}-cron" \
    && rm -rf /var/lib/apt/lists/*

RUN printf "\n%s\n%s\n%s\n" \
    "shared_preload_libraries = 'pg_partman_bgw,pg_cron'" \
    "cron.database_name = 'zeeq'" \
    "max_connections = 300" \
    >> /usr/share/postgresql/postgresql.conf.sample
