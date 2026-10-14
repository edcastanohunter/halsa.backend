using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Halsa.Backend.Infrastructure.Database.Migrations
{
    /// <summary>
    /// SPEC 09: el JobStore de Quartz.NET en el esquema <c>quartz</c>, para el scheduler en clúster del Worker. Es el
    /// script oficial <c>tables_postgres.sql</c> de Quartz.NET 3.x (tablas <c>qrtz_*</c>, con <c>TablePrefix</c>
    /// <c>quartz.qrtz_</c>). Solo <c>halsa_worker</c> lo usa, sin RLS: no guarda datos de ningún tenant.
    /// </summary>
    public partial class Quartz : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(name: "quartz");

            // ── tables_postgres.sql (Quartz.NET 3.x) ───────────────────────────
            migrationBuilder.Sql("""
                CREATE TABLE quartz.qrtz_job_details
                (
                    sched_name        TEXT    NOT NULL,
                    job_name          TEXT    NOT NULL,
                    job_group         TEXT    NOT NULL,
                    description       TEXT    NULL,
                    job_class_name    TEXT    NOT NULL,
                    is_durable        BOOL    NOT NULL,
                    is_nonconcurrent  BOOL    NOT NULL,
                    is_update_data    BOOL    NOT NULL,
                    requests_recovery BOOL    NOT NULL,
                    job_data          BYTEA   NULL,
                    PRIMARY KEY (sched_name, job_name, job_group)
                );

                CREATE TABLE quartz.qrtz_triggers
                (
                    sched_name     TEXT     NOT NULL,
                    trigger_name   TEXT     NOT NULL,
                    trigger_group  TEXT     NOT NULL,
                    job_name       TEXT     NOT NULL,
                    job_group      TEXT     NOT NULL,
                    description    TEXT     NULL,
                    next_fire_time BIGINT   NULL,
                    prev_fire_time BIGINT   NULL,
                    priority       INTEGER  NULL,
                    trigger_state  TEXT     NOT NULL,
                    trigger_type   TEXT     NOT NULL,
                    start_time     BIGINT   NOT NULL,
                    end_time       BIGINT   NULL,
                    calendar_name  TEXT     NULL,
                    misfire_instr  SMALLINT NULL,
                    job_data       BYTEA    NULL,
                    PRIMARY KEY (sched_name, trigger_name, trigger_group),
                    FOREIGN KEY (sched_name, job_name, job_group)
                        REFERENCES quartz.qrtz_job_details (sched_name, job_name, job_group)
                );

                CREATE TABLE quartz.qrtz_simple_triggers
                (
                    sched_name      TEXT   NOT NULL,
                    trigger_name    TEXT   NOT NULL,
                    trigger_group   TEXT   NOT NULL,
                    repeat_count    BIGINT NOT NULL,
                    repeat_interval BIGINT NOT NULL,
                    times_triggered BIGINT NOT NULL,
                    PRIMARY KEY (sched_name, trigger_name, trigger_group),
                    FOREIGN KEY (sched_name, trigger_name, trigger_group)
                        REFERENCES quartz.qrtz_triggers (sched_name, trigger_name, trigger_group) ON DELETE CASCADE
                );

                CREATE TABLE quartz.qrtz_simprop_triggers
                (
                    sched_name    TEXT    NOT NULL,
                    trigger_name  TEXT    NOT NULL,
                    trigger_group TEXT    NOT NULL,
                    str_prop_1    TEXT    NULL,
                    str_prop_2    TEXT    NULL,
                    str_prop_3    TEXT    NULL,
                    int_prop_1    INTEGER NULL,
                    int_prop_2    INTEGER NULL,
                    long_prop_1   BIGINT  NULL,
                    long_prop_2   BIGINT  NULL,
                    dec_prop_1    NUMERIC NULL,
                    dec_prop_2    NUMERIC NULL,
                    bool_prop_1   BOOL    NULL,
                    bool_prop_2   BOOL    NULL,
                    time_zone_id  TEXT    NULL,
                    PRIMARY KEY (sched_name, trigger_name, trigger_group),
                    FOREIGN KEY (sched_name, trigger_name, trigger_group)
                        REFERENCES quartz.qrtz_triggers (sched_name, trigger_name, trigger_group) ON DELETE CASCADE
                );

                CREATE TABLE quartz.qrtz_cron_triggers
                (
                    sched_name      TEXT NOT NULL,
                    trigger_name    TEXT NOT NULL,
                    trigger_group   TEXT NOT NULL,
                    cron_expression TEXT NOT NULL,
                    time_zone_id    TEXT,
                    PRIMARY KEY (sched_name, trigger_name, trigger_group),
                    FOREIGN KEY (sched_name, trigger_name, trigger_group)
                        REFERENCES quartz.qrtz_triggers (sched_name, trigger_name, trigger_group) ON DELETE CASCADE
                );

                CREATE TABLE quartz.qrtz_blob_triggers
                (
                    sched_name    TEXT  NOT NULL,
                    trigger_name  TEXT  NOT NULL,
                    trigger_group TEXT  NOT NULL,
                    blob_data     BYTEA NULL,
                    PRIMARY KEY (sched_name, trigger_name, trigger_group),
                    FOREIGN KEY (sched_name, trigger_name, trigger_group)
                        REFERENCES quartz.qrtz_triggers (sched_name, trigger_name, trigger_group) ON DELETE CASCADE
                );

                CREATE TABLE quartz.qrtz_calendars
                (
                    sched_name    TEXT  NOT NULL,
                    calendar_name TEXT  NOT NULL,
                    calendar      BYTEA NOT NULL,
                    PRIMARY KEY (sched_name, calendar_name)
                );

                CREATE TABLE quartz.qrtz_paused_trigger_grps
                (
                    sched_name    TEXT NOT NULL,
                    trigger_group TEXT NOT NULL,
                    PRIMARY KEY (sched_name, trigger_group)
                );

                CREATE TABLE quartz.qrtz_fired_triggers
                (
                    sched_name        TEXT    NOT NULL,
                    entry_id          TEXT    NOT NULL,
                    trigger_name      TEXT    NOT NULL,
                    trigger_group     TEXT    NOT NULL,
                    instance_name     TEXT    NOT NULL,
                    fired_time        BIGINT  NOT NULL,
                    sched_time        BIGINT  NOT NULL,
                    priority          INTEGER NOT NULL,
                    state             TEXT    NOT NULL,
                    job_name          TEXT    NULL,
                    job_group         TEXT    NULL,
                    is_nonconcurrent  BOOL    NOT NULL,
                    requests_recovery BOOL    NULL,
                    PRIMARY KEY (sched_name, entry_id)
                );

                CREATE TABLE quartz.qrtz_scheduler_state
                (
                    sched_name        TEXT   NOT NULL,
                    instance_name     TEXT   NOT NULL,
                    last_checkin_time BIGINT NOT NULL,
                    checkin_interval  BIGINT NOT NULL,
                    PRIMARY KEY (sched_name, instance_name)
                );

                CREATE TABLE quartz.qrtz_locks
                (
                    sched_name TEXT NOT NULL,
                    lock_name  TEXT NOT NULL,
                    PRIMARY KEY (sched_name, lock_name)
                );

                CREATE INDEX idx_qrtz_j_req_recovery ON quartz.qrtz_job_details (requests_recovery);
                CREATE INDEX idx_qrtz_t_next_fire_time ON quartz.qrtz_triggers (next_fire_time);
                CREATE INDEX idx_qrtz_t_state ON quartz.qrtz_triggers (trigger_state);
                CREATE INDEX idx_qrtz_t_nft_st ON quartz.qrtz_triggers (next_fire_time, trigger_state);
                CREATE INDEX idx_qrtz_ft_trig_name ON quartz.qrtz_fired_triggers (trigger_name);
                CREATE INDEX idx_qrtz_ft_trig_group ON quartz.qrtz_fired_triggers (trigger_group);
                CREATE INDEX idx_qrtz_ft_trig_nm_gp ON quartz.qrtz_fired_triggers (sched_name, trigger_name, trigger_group);
                CREATE INDEX idx_qrtz_ft_trig_inst_name ON quartz.qrtz_fired_triggers (instance_name);
                CREATE INDEX idx_qrtz_ft_job_name ON quartz.qrtz_fired_triggers (job_name);
                CREATE INDEX idx_qrtz_ft_job_group ON quartz.qrtz_fired_triggers (job_group);
                CREATE INDEX idx_qrtz_ft_job_req_recovery ON quartz.qrtz_fired_triggers (requests_recovery);
                """);

            // ── Permisos ───────────────────────────────────────────────────────
            migrationBuilder.Sql("""
                REVOKE ALL ON SCHEMA quartz FROM PUBLIC;
                GRANT USAGE ON SCHEMA quartz TO halsa_worker;
                GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA quartz TO halsa_worker;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP SCHEMA quartz CASCADE;");
        }
    }
}
