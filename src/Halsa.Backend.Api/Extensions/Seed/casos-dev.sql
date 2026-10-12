-- ─────────────────────────────────────────────────────────────────────────────
-- casos-dev.sql  –  Datos de desarrollo para el contexto ATEL (SPEC 02)
--
-- Se ejecuta ÚNICAMENTE en el entorno Development, después de las migraciones,
-- usando la cadena de conexión "Migrations" (halsa_owner) para poder escribir
-- en la tabla sin que RLS bloquee la inserción.
--
-- El script es idempotente: usa ON CONFLICT DO NOTHING para no duplicar filas
-- si se ejecuta varias veces (ej. hot-reload del servidor).
-- ─────────────────────────────────────────────────────────────────────────────

-- Caso 1: Accidente de trabajo para ARL Sura, en atención
INSERT INTO atel.caso_medico (
    id, tenant_id, radicado, tipo_evento, fecha_evento,
    trabajador_tipo, trabajador_numero,
    empleador_tipo, empleador_numero,
    municipio_divipola, estado, es_grave_o_mortal, radicado_en
)
VALUES (
    '00000000-0000-7000-8001-000000000001',
    'arl-sura',
    'ARL-SURA-2026-001',
    1,  -- AccidenteTrabajo
    '2026-09-10 08:30:00+00',
    'CC', '12345678',
    'NIT', '9001234567',
    '11001',
    2,  -- EnAtencion
    false,
    '2026-09-10 09:00:00+00'
)
ON CONFLICT (id) DO NOTHING;

-- Caso 2: Enfermedad laboral para ARL Sura, recién radicado
INSERT INTO atel.caso_medico (
    id, tenant_id, radicado, tipo_evento, fecha_evento,
    trabajador_tipo, trabajador_numero,
    empleador_tipo, empleador_numero,
    municipio_divipola, estado, es_grave_o_mortal, radicado_en
)
VALUES (
    '00000000-0000-7000-8001-000000000002',
    'arl-sura',
    'ARL-SURA-2026-002',
    2,  -- EnfermedadLaboral
    '2026-10-01 00:00:00+00',
    'CC', '98765432',
    'NIT', '8001234001',
    '05001',
    0,  -- Radicado
    false,
    '2026-10-01 10:15:00+00'
)
ON CONFLICT (id) DO NOTHING;

-- Caso 3: Accidente grave para ARL Positiva
INSERT INTO atel.caso_medico (
    id, tenant_id, radicado, tipo_evento, fecha_evento,
    trabajador_tipo, trabajador_numero,
    empleador_tipo, empleador_numero,
    municipio_divipola, estado, es_grave_o_mortal, radicado_en
)
VALUES (
    '00000000-0000-7000-8001-000000000003',
    'arl-positiva',
    'ARL-POSITIVA-2026-001',
    1,  -- AccidenteTrabajo
    '2026-09-25 14:00:00+00',
    'CE', '1234567',
    'NIT', '9001110001',
    '76001',
    1,  -- Validado
    true,
    '2026-09-25 15:00:00+00'
)
ON CONFLICT (id) DO NOTHING;

-- Remisión del Caso 1 a la IPS Clínica Las Américas (código ficticio)
INSERT INTO atel.remision (id, caso_medico_id, ips_id, remitido_por, remitido_en)
VALUES (
    '00000000-0000-7000-8002-000000000001',
    '00000000-0000-7000-8001-000000000001',
    '110011234501',
    'sub-analista-sura',
    '2026-09-11 08:00:00+00'
)
ON CONFLICT DO NOTHING;

-- Asignación del Caso 1 al médico laboral
INSERT INTO atel.asignacion (caso_medico_id, sub, asignado_por, asignado_en)
VALUES (
    '00000000-0000-7000-8001-000000000001',
    'sub-medico-laboral',
    'sub-analista-sura',
    '2026-09-11 09:00:00+00'
)
ON CONFLICT DO NOTHING;
