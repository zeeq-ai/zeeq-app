-- A kept installation always uses these stable LOGIN identities from its first migration.
DO $$
BEGIN
  IF current_database() <> current_setting('cron.database_name') THEN
    RAISE EXCEPTION 'cron.database_name does not match the installation database';
  END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'zeeq_owner') THEN
    CREATE ROLE zeeq_owner LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;
  END IF;
  IF NOT EXISTS (SELECT FROM pg_roles WHERE rolname = 'zeeq_runtime') THEN
    CREATE ROLE zeeq_runtime LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE;
  END IF;
  IF EXISTS (SELECT FROM pg_roles WHERE rolname IN ('zeeq_owner','zeeq_runtime') AND (rolsuper OR rolcreatedb OR rolcreaterole OR rolbypassrls)) THEN
    RAISE EXCEPTION 'Existing deployment roles have unexpected administrative privileges';
  END IF;
END $$;
-- NOTE: Bootstrap receives owner membership; zeeq_owner does not inherit the administrator's privileges.
GRANT zeeq_owner TO CURRENT_USER WITH INHERIT TRUE, SET TRUE;
CREATE SCHEMA IF NOT EXISTS zeeq AUTHORIZATION zeeq_owner;
CREATE SCHEMA IF NOT EXISTS messaging AUTHORIZATION zeeq_owner;
CREATE SCHEMA IF NOT EXISTS cache AUTHORIZATION zeeq_owner;
CREATE SCHEMA IF NOT EXISTS partman;
CREATE EXTENSION IF NOT EXISTS pg_partman SCHEMA partman;
CREATE EXTENSION IF NOT EXISTS pg_cron;
CREATE EXTENSION IF NOT EXISTS vector SCHEMA public;
CREATE EXTENSION IF NOT EXISTS pg_trgm SCHEMA public;
CREATE EXTENSION IF NOT EXISTS btree_gin SCHEMA public;
CREATE EXTENSION IF NOT EXISTS fuzzystrmatch SCHEMA public;
CREATE EXTENSION IF NOT EXISTS unaccent SCHEMA public;
DO $$
BEGIN
  IF EXISTS (SELECT FROM pg_namespace WHERE nspname IN ('zeeq','messaging','cache') AND nspowner <> 'zeeq_owner'::regrole) THEN
    RAISE EXCEPTION 'Existing application schemas are not owned by zeeq_owner';
  END IF;
  IF EXISTS (SELECT FROM pg_extension e JOIN pg_namespace n ON n.oid=e.extnamespace WHERE
    (e.extname='pg_partman' AND n.nspname <> 'partman') OR
    (e.extname IN ('vector','pg_trgm','btree_gin','fuzzystrmatch','unaccent') AND n.nspname <> 'public')) THEN
    RAISE EXCEPTION 'Extension schema does not match the installation contract';
  END IF;
END $$;
-- Existing owned schemas can have explicitly revoked privileges; restore the migration contract.
GRANT USAGE, CREATE ON SCHEMA zeeq, messaging, cache TO zeeq_owner;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
REVOKE ALL ON SCHEMA cron, partman FROM PUBLIC;
REVOKE EXECUTE ON ALL FUNCTIONS IN SCHEMA cron, partman FROM PUBLIC;
REVOKE EXECUTE ON ALL PROCEDURES IN SCHEMA partman FROM PUBLIC;
GRANT USAGE ON SCHEMA public, cron TO zeeq_owner;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA cron TO zeeq_owner;
GRANT SELECT, DELETE ON cron.job_run_details TO zeeq_owner;
-- Non-superuser maintenance needs pg_partman schema, configuration, and routine access.
-- Keep these grants aligned with the installed pg_partman version after extension upgrades.
GRANT ALL ON SCHEMA partman TO zeeq_owner;
GRANT ALL ON ALL TABLES IN SCHEMA partman TO zeeq_owner;
GRANT ALL ON ALL SEQUENCES IN SCHEMA partman TO zeeq_owner;
GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA partman TO zeeq_owner;
GRANT EXECUTE ON ALL PROCEDURES IN SCHEMA partman TO zeeq_owner;
DO $$ BEGIN
  EXECUTE format('GRANT CONNECT, TEMPORARY ON DATABASE %I TO zeeq_owner', current_database());
  EXECUTE format('GRANT CONNECT ON DATABASE %I TO zeeq_runtime', current_database());
END $$;
GRANT USAGE ON SCHEMA zeeq, messaging, cache, public TO zeeq_runtime;
ALTER ROLE zeeq_owner SET search_path = zeeq, public;
ALTER ROLE zeeq_runtime SET search_path = zeeq, public;
ALTER DEFAULT PRIVILEGES FOR ROLE zeeq_owner IN SCHEMA zeeq, messaging, cache
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO zeeq_runtime;
ALTER DEFAULT PRIVILEGES FOR ROLE zeeq_owner IN SCHEMA zeeq, messaging, cache
  GRANT USAGE, SELECT ON SEQUENCES TO zeeq_runtime;
GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA zeeq, messaging, cache TO zeeq_runtime;
GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA zeeq, messaging, cache TO zeeq_runtime;
