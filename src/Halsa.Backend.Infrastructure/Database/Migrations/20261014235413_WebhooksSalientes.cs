using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Halsa.Backend.Infrastructure.Database.Migrations
{
    /// <summary>
    /// SPEC 10: las suscripciones a webhooks salientes y sus entregas, en <c>integracion</c>. Las suscripciones tienen RLS
    /// por emisor (<c>tenant:{ARL}</c> / <c>ips:{REPS}</c>, como la idempotencia) y las entregas a través de su
    /// suscripción: <c>halsa_app</c> las gestiona desde la API. <c>halsa_worker</c> las ve todas: el fan-out inserta las
    /// entregas, el dispatcher las envía y suspende suscripciones, y la purga borra las entregadas viejas.
    /// </summary>
    public partial class WebhooksSalientes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "suscripciones_webhook",
                schema: "integracion",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    emisor = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    tenant_id = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ips_id = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: true),
                    url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    eventos = table.Column<string[]>(type: "text[]", nullable: false),
                    estado = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    secreto_ct = table.Column<byte[]>(type: "bytea", nullable: false),
                    secreto_key_id = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    secreto_anterior_ct = table.Column<byte[]>(type: "bytea", nullable: true),
                    secreto_anterior_expira_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    fallos_consecutivos = table.Column<int>(type: "integer", nullable: false),
                    creada_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    creada_por = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_suscripciones_webhook", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "entregas_webhook",
                schema: "integracion",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    suscripcion_id = table.Column<Guid>(type: "uuid", nullable: false),
                    evento_id = table.Column<Guid>(type: "uuid", nullable: false),
                    tipo = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    payload = table.Column<string>(type: "jsonb", nullable: false),
                    estado = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    intentos = table.Column<int>(type: "integer", nullable: false),
                    primer_intento_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    proximo_intento_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ultimo_status = table.Column<int>(type: "integer", nullable: true),
                    ultimo_error = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    entregada_en = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("pk_entregas_webhook", x => x.id);
                    table.ForeignKey(
                        name: "fk_entregas_webhook_suscripciones_webhook",
                        column: x => x.suscripcion_id,
                        principalSchema: "integracion",
                        principalTable: "suscripciones_webhook",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "ix_entregas_webhook_pendientes",
                schema: "integracion",
                table: "entregas_webhook",
                column: "proximo_intento_en",
                filter: "estado = 'PENDIENTE'");

            migrationBuilder.CreateIndex(
                name: "ux_entregas_webhook_suscripcion_evento",
                schema: "integracion",
                table: "entregas_webhook",
                columns: new[] { "suscripcion_id", "evento_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ix_suscripciones_webhook_emisor",
                schema: "integracion",
                table: "suscripciones_webhook",
                column: "emisor");

            // ── Restricciones que EF no expresa ────────────────────────────────
            // A subscription belongs to one ARL or one IPS, and its emisor says which.
            migrationBuilder.Sql("""
                ALTER TABLE integracion.suscripciones_webhook ADD CONSTRAINT ck_suscripciones_webhook_emisor CHECK (
                    (tenant_id IS NOT NULL AND ips_id IS NULL AND emisor = 'tenant:' || tenant_id)
                    OR (ips_id IS NOT NULL AND tenant_id IS NULL AND emisor = 'ips:' || ips_id));

                ALTER TABLE integracion.suscripciones_webhook ADD CONSTRAINT ck_suscripciones_webhook_valores CHECK (
                    estado IN ('ACTIVA', 'PAUSADA', 'SUSPENDIDA', 'ELIMINADA')
                    AND cardinality(eventos) > 0
                    AND fallos_consecutivos >= 0
                    AND url LIKE 'http%'
                    AND (secreto_anterior_ct IS NULL) = (secreto_anterior_expira_en IS NULL));

                ALTER TABLE integracion.entregas_webhook ADD CONSTRAINT ck_entregas_webhook_valores CHECK (
                    estado IN ('PENDIENTE', 'ENTREGADA', 'DEAD_LETTER', 'DESCARTADA')
                    AND intentos >= 0
                    AND (estado <> 'ENTREGADA' OR entregada_en IS NOT NULL));
                """);

            // ── RLS ────────────────────────────────────────────────────────────
            // halsa_app: only the emisor's own subscriptions, and the deliveries of those. halsa_worker: all of them.
            migrationBuilder.Sql("""
                ALTER TABLE integracion.suscripciones_webhook ENABLE ROW LEVEL SECURITY;
                ALTER TABLE integracion.suscripciones_webhook FORCE ROW LEVEL SECURITY;
                ALTER TABLE integracion.entregas_webhook ENABLE ROW LEVEL SECURITY;
                ALTER TABLE integracion.entregas_webhook FORCE ROW LEVEL SECURITY;

                CREATE POLICY p_suscripciones_webhook ON integracion.suscripciones_webhook FOR ALL
                    USING (emisor = 'tenant:' || current_setting('app.tenant_id', true)
                        OR emisor = 'ips:' || current_setting('app.ips_id', true))
                    WITH CHECK (emisor = 'tenant:' || current_setting('app.tenant_id', true)
                        OR emisor = 'ips:' || current_setting('app.ips_id', true));

                CREATE POLICY p_suscripciones_webhook_worker ON integracion.suscripciones_webhook FOR ALL TO halsa_worker
                    USING (true) WITH CHECK (true);

                CREATE POLICY p_entregas_webhook ON integracion.entregas_webhook FOR ALL
                    USING (EXISTS (SELECT 1 FROM integracion.suscripciones_webhook s WHERE s.id = suscripcion_id))
                    WITH CHECK (EXISTS (SELECT 1 FROM integracion.suscripciones_webhook s WHERE s.id = suscripcion_id));

                CREATE POLICY p_entregas_webhook_worker ON integracion.entregas_webhook FOR ALL TO halsa_worker
                    USING (true) WITH CHECK (true);
                """);

            // ── Permisos ───────────────────────────────────────────────────────
            // halsa_app creates and changes subscriptions, and only re-queues or discards deliveries; it never inserts one.
            // halsa_worker never creates a subscription; it only suspends it and counts its failures.
            migrationBuilder.Sql("""
                GRANT SELECT, INSERT ON integracion.suscripciones_webhook TO halsa_app;
                GRANT UPDATE (url, eventos, estado, secreto_ct, secreto_key_id, secreto_anterior_ct,
                              secreto_anterior_expira_en, fallos_consecutivos)
                    ON integracion.suscripciones_webhook TO halsa_app;
                GRANT SELECT ON integracion.entregas_webhook TO halsa_app;
                GRANT UPDATE (estado, intentos, primer_intento_en, proximo_intento_en)
                    ON integracion.entregas_webhook TO halsa_app;

                GRANT SELECT ON integracion.suscripciones_webhook TO halsa_worker;
                GRANT UPDATE (estado, fallos_consecutivos) ON integracion.suscripciones_webhook TO halsa_worker;
                GRANT SELECT, INSERT, UPDATE, DELETE ON integracion.entregas_webhook TO halsa_worker;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The policies, constraints and grants fall with the tables.
            migrationBuilder.DropTable(
                name: "entregas_webhook",
                schema: "integracion");

            migrationBuilder.DropTable(
                name: "suscripciones_webhook",
                schema: "integracion");
        }
    }
}
