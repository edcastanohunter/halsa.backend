-- ─────────────────────────────────────────────────────────────────────────────
-- casos-dev.sql  –  Datos de desarrollo para el contexto ATEL (SPEC 02)
--
-- Se ejecuta ÚNICAMENTE en Development, después de las migraciones, con la
-- cadena "Migrations" (halsa_owner).
--
-- La RLS está forzada, así que también aplica al dueño: cada bloque fija
-- app.tenant_id antes de insertar las filas de su tenant.
--
-- Idempotente: ON CONFLICT DO NOTHING en todas las inserciones.
-- Los radicados usan el formato de ejemplo del contrato; el definitivo llega en SPEC 05.
--
-- Usuarios (sub de docker/keycloak/halsa-realm.json):
--   calificador.arla  01999000-0000-7000-8000-000000000a02
--   medico.ips        01999000-0000-7000-8000-000000000c01
--   coordinador.ips   01999000-0000-7000-8000-000000000c02
--
-- SPEC 08: una asignación del personal de la IPS guarda su ips_id (la hace coordinador.ips) y la RLS solo la admite
-- con app.ips_id de esa IPS, así que esos bloques fijan también app.ips_id.
-- ─────────────────────────────────────────────────────────────────────────────

-- ── ARL_A: 3 casos, 1 remitido a la IPS 110011234501 ────────────────────────
SELECT set_config('app.tenant_id', 'ARL_A', false);

INSERT INTO atel.casos_medicos (
    id, tenant_id, radicado, tipo_evento, fecha_evento,
    trabajador_tipo, trabajador_numero, empleador_tipo, empleador_numero,
    municipio_divipola, estado, es_grave_o_mortal, radicado_en)
VALUES
    ('01999000-0000-7000-8001-00000000a001', 'ARL_A', 'ATEL-DEV-2026-000001', 'AccidenteTrabajo',
     '2026-09-10 08:30:00+00', 'CC', '12345678', 'NIT', '9001234567', '11001', 'Radicado', false,
     '2026-09-10 09:00:00+00'),
    ('01999000-0000-7000-8001-00000000a002', 'ARL_A', 'ATEL-DEV-2026-000002', 'EnfermedadLaboral',
     '2026-09-15 00:00:00+00', 'CC', '23456789', 'NIT', '9001234567', '05001', 'Radicado', false,
     '2026-09-16 10:00:00+00'),
    ('01999000-0000-7000-8001-00000000a003', 'ARL_A', 'ATEL-DEV-2026-000003', 'AccidenteTrabajo',
     '2026-10-01 14:10:00+00', 'CE', '345678', 'NIT', '9007654321', '76001', 'Radicado', true,
     '2026-10-01 15:00:00+00')
ON CONFLICT DO NOTHING;

INSERT INTO atel.remisiones (id, caso_medico_id, tenant_id, ips_id, remitido_en, remitido_por)
VALUES ('01999000-0000-7000-8002-00000000a001', '01999000-0000-7000-8001-00000000a001', 'ARL_A',
        '110011234501', '2026-09-11 08:00:00+00', '01999000-0000-7000-8000-000000000a01')
ON CONFLICT DO NOTHING;

INSERT INTO atel.asignaciones (caso_medico_id, sub, tenant_id, asignado_en, asignado_por)
VALUES ('01999000-0000-7000-8001-00000000a001', '01999000-0000-7000-8000-000000000a02', 'ARL_A',
        '2026-09-11 08:05:00+00', '01999000-0000-7000-8000-000000000a01')
ON CONFLICT DO NOTHING;

SELECT set_config('app.ips_id', '110011234501', false);

INSERT INTO atel.asignaciones (caso_medico_id, sub, tenant_id, ips_id, asignado_en, asignado_por)
VALUES ('01999000-0000-7000-8001-00000000a001', '01999000-0000-7000-8000-000000000c01', 'ARL_A', '110011234501',
        '2026-09-11 08:05:00+00', '01999000-0000-7000-8000-000000000c02')
ON CONFLICT DO NOTHING;

SELECT set_config('app.ips_id', '', false);

-- ── ARL_B: 2 casos, 1 remitido a la IPS 110011234501 ────────────────────────
SELECT set_config('app.tenant_id', 'ARL_B', false);

INSERT INTO atel.casos_medicos (
    id, tenant_id, radicado, tipo_evento, fecha_evento,
    trabajador_tipo, trabajador_numero, empleador_tipo, empleador_numero,
    municipio_divipola, estado, es_grave_o_mortal, radicado_en)
VALUES
    ('01999000-0000-7000-8001-00000000b001', 'ARL_B', 'ATEL-DEV-2026-000001', 'AccidenteTrabajo',
     '2026-09-20 07:45:00+00', 'CC', '45678901', 'NIT', '8001112223', '11001', 'Radicado', false,
     '2026-09-20 08:30:00+00'),
    ('01999000-0000-7000-8001-00000000b002', 'ARL_B', 'ATEL-DEV-2026-000002', 'EnfermedadLaboral',
     '2026-09-25 00:00:00+00', 'TI', '1012345678', 'NIT', '8001112223', '08001', 'Radicado', false,
     '2026-09-26 11:00:00+00')
ON CONFLICT DO NOTHING;

INSERT INTO atel.remisiones (id, caso_medico_id, tenant_id, ips_id, remitido_en, remitido_por)
VALUES ('01999000-0000-7000-8002-00000000b001', '01999000-0000-7000-8001-00000000b001', 'ARL_B',
        '110011234501', '2026-09-21 09:00:00+00', '01999000-0000-7000-8000-000000000b01')
ON CONFLICT DO NOTHING;

SELECT set_config('app.ips_id', '110011234501', false);

INSERT INTO atel.asignaciones (caso_medico_id, sub, tenant_id, ips_id, asignado_en, asignado_por)
VALUES ('01999000-0000-7000-8001-00000000b001', '01999000-0000-7000-8000-000000000c01', 'ARL_B', '110011234501',
        '2026-09-21 09:05:00+00', '01999000-0000-7000-8000-000000000c02')
ON CONFLICT DO NOTHING;

SELECT set_config('app.ips_id', '', false);

SELECT set_config('app.tenant_id', '', false);
