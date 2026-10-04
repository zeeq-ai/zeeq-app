-- Microsoft.Extensions.Caching.Postgres 1.2.2, SqlQueries.cs at 5267eaacfe2ea68e1552fec458f0eb557b605282.
-- Bootstrap precreates cache; CREATE SCHEMA IF NOT EXISTS still requires database CREATE.
CREATE UNLOGGED TABLE IF NOT EXISTS cache.hybrid_cache (
    id VARCHAR(449) COLLATE "C" PRIMARY KEY,
    value BYTEA NOT NULL,
    expiresattime TIMESTAMPTZ NOT NULL,
    slidingexpirationinseconds BIGINT NULL,
    absoluteexpiration TIMESTAMPTZ NULL
);
CREATE INDEX IF NOT EXISTS ix_expiresattime ON cache.hybrid_cache (expiresattime) WITH (deduplicate_items=True);
SELECT cron.schedule('zeeq_prune_cron_history', '15 3 * * *', $$DELETE FROM cron.job_run_details WHERE end_time < now() - interval '14 days'$$);
