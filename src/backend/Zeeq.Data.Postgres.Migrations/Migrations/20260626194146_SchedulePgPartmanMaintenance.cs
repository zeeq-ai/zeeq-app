using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Zeeq.Data.Postgres.Migrations.Migrations
{
    /// <inheritdoc />
    public partial class SchedulePgPartmanMaintenance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                CREATE EXTENSION IF NOT EXISTS pg_cron;
                """
            );

            migrationBuilder.Sql(
                """
                DO $$
                DECLARE
                    maintenance_command text;
                BEGIN
                    SELECT format('CALL %I.run_maintenance_proc()', schema.nspname)
                    INTO maintenance_command
                    FROM pg_proc proc
                    JOIN pg_namespace schema ON schema.oid = proc.pronamespace
                    WHERE proc.proname = 'run_maintenance_proc'
                      AND proc.prokind = 'p'
                    ORDER BY (schema.nspname = 'partman') DESC, schema.nspname
                    LIMIT 1;

                    IF maintenance_command IS NULL THEN
                        RAISE EXCEPTION 'pg_partman run_maintenance_proc() was not found.';
                    END IF;

                    IF NOT EXISTS (
                        SELECT 1
                        FROM cron.job
                        WHERE jobname = 'zeeq_pg_partman_maintenance'
                    ) THEN
                        PERFORM cron.schedule(
                            'zeeq_pg_partman_maintenance',
                            '0 * * * *',
                            maintenance_command
                        );
                    END IF;
                END $$;
                """
            );
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    PERFORM cron.unschedule(jobid)
                    FROM cron.job
                    WHERE jobname = 'zeeq_pg_partman_maintenance';
                END $$;
                """
            );
        }
    }
}
