-- ─────────────────────────────────────────────────────────────────────────────
-- sembrar.sql — datos de la fase de pruebas en producción.
--
-- Lo ejecuta deploy/scripts/pruebas.sh (sembrar) como el superusuario `postgres` por el socket local del contenedor:
-- el superusuario no está sujeto a la RLS, así que no hay que fijar app.tenant_id ni tocar las políticas. Los CHECK,
-- las claves foráneas y los índices únicos SÍ aplican.
--
-- Todo lo que crea pertenece a dos ARL y dos IPS ficticias, y solo a ellas:
--
--   ARL     PRUEBA_A, PRUEBA_B                 (tenant_id)
--   IPS     990000000001, 990000000002         (REPS ficticios)
--   Persona trabajadores CC 99100001 … 99100012 (empleador NIT 9990000011 / 9990000022)
--
-- deploy/pruebas/purgar.sql borra por esas mismas claves. Si añades datos aquí, mantenlos bajo ellas.
-- Los sub de los usuarios son fijos (01999f00-…) y coinciden con deploy/keycloak/crear-usuarios-prueba.sh.
--
-- Variables que pasa pruebas.sh (psql -v):
--   secreto_activo      secreto de 10 caracteres del código ACTIVO del caso A13 (se genera en cada siembra; no está en git)
--
-- Idempotente: ON CONFLICT DO NOTHING en todo. Las fechas son relativas a now(): una segunda corrida no las mueve.
-- Atención: NO escribe en outbox ni en event_store. Los webhooks y los eventos de integración solo nacen de la API.
-- Los datos clínicos cifrados (historia clínica, soportes) tampoco: se crean usando la API (README.md).
-- ─────────────────────────────────────────────────────────────────────────────
\set ON_ERROR_STOP on

BEGIN;

-- Guardarraíl: el mismo script no debe poder sembrar sobre una base sin las migraciones.
DO $$
BEGIN
    IF to_regclass('atel.casos_medicos') IS NULL OR to_regclass('sst.plazos') IS NULL THEN
        RAISE EXCEPTION 'Faltan las migraciones: atel.casos_medicos o sst.plazos no existen';
    END IF;
END
$$;

-- ── Radicados: la API continúa la numeración desde aquí ─────────────────────
INSERT INTO atel.secuencias_radicado (tenant_id, anio, ultimo) VALUES
    ('PRUEBA_A', EXTRACT(YEAR FROM now())::int, 16),
    ('PRUEBA_B', EXTRACT(YEAR FROM now())::int, 3)
ON CONFLICT DO NOTHING;

-- ── Casos ───────────────────────────────────────────────────────────────────
-- Convención de ids: 01999f00-0000-7000-8001-00000000{a|b}0NN. a = PRUEBA_A, b = PRUEBA_B.
-- El radicado lleva el año actual, como el que genera la API: ATEL-{tenant}-{año}-{seq6}.
INSERT INTO atel.casos_medicos (
    id, tenant_id, radicado, tipo_evento, fecha_evento,
    trabajador_tipo, trabajador_numero, empleador_tipo, empleador_numero,
    municipio_divipola, estado, es_grave_o_mortal, radicado_en,
    calificacion_pcl, calificacion_fecha_estructuracion, calificacion_origen,
    duplicado_de, motivo_rechazo, rechazado_en, validado_en)
SELECT c.id::uuid, c.tenant, 'ATEL-' || c.tenant || '-' || EXTRACT(YEAR FROM now())::int || '-' || lpad(c.seq::text, 6, '0'),
       c.tipo, c.fecha_evento, 'CC', c.trabajador, 'NIT', c.empleador, c.divipola, c.estado, c.grave, c.radicado_en,
       c.pcl, c.pcl_estructuracion, c.pcl_origen, c.duplicado_de::uuid, c.motivo, c.rechazado_en, c.validado_en
FROM (VALUES
    -- A01  AT radicado hace minutos: su REPORTE_FURAT vence en 10 min (prueba el barrido del Worker en vivo).
    ('01999f00-0000-7000-8001-00000000a001', 'PRUEBA_A', 1,  'AccidenteTrabajo',  now() - interval '2 days',   '99100001', '9990000011', '11001', 'Radicado',
        false, now() - interval '5 minutes',  NULL::numeric, NULL::date, NULL::text, NULL::text, NULL::text, NULL::timestamptz, NULL::timestamptz),
    -- A02  EL validada, sin remisión ni asignación.
    ('01999f00-0000-7000-8001-00000000a002', 'PRUEBA_A', 2,  'EnfermedadLaboral', now() - interval '20 days',  '99100002', '9990000011', '05001', 'Validado',
        false, now() - interval '19 days',    NULL, NULL, NULL, NULL, NULL, NULL, now() - interval '19 days'),
    -- A03  AT validado, remitido a la IPS 1, con incapacidades y una solicitud de copia de HC recibida.
    ('01999f00-0000-7000-8001-00000000a003', 'PRUEBA_A', 3,  'AccidenteTrabajo',  now() - interval '45 days',  '99100001', '9990000011', '11001', 'Validado',
        false, now() - interval '44 days',    NULL, NULL, NULL, NULL, NULL, NULL, now() - interval '44 days'),
    -- A04  AT grave validado, asignado al calificador (flujo del dictamen en vivo).
    ('01999f00-0000-7000-8001-00000000a004', 'PRUEBA_A', 4,  'AccidenteTrabajo',  now() - interval '10 days',  '99100004', '9990000022', '76001', 'Validado',
        true,  now() - interval '9 days',     NULL, NULL, NULL, NULL, NULL, NULL, now() - interval '9 days'),
    -- A05  rechazado por no afiliado (sus plazos quedan suspendidos).
    ('01999f00-0000-7000-8001-00000000a005', 'PRUEBA_A', 5,  'AccidenteTrabajo',  now() - interval '8 days',   '99100005', '9990000022', '08001', 'Rechazado',
        false, now() - interval '7 days',     NULL, NULL, NULL, NULL, 'NO_AFILIADO', now() - interval '7 days', NULL),
    -- A06  rechazado por duplicado de A03.
    ('01999f00-0000-7000-8001-00000000a006', 'PRUEBA_A', 6,  'AccidenteTrabajo',  now() - interval '45 days',  '99100001', '9990000011', '11001', 'Rechazado',
        false, now() - interval '3 days',     NULL, NULL, NULL, '01999f00-0000-7000-8001-00000000a003', 'DUPLICADO', now() - interval '3 days', NULL),
    -- A07  en calificación PCL con dictamen notificado (controversia abierta).
    ('01999f00-0000-7000-8001-00000000a007', 'PRUEBA_A', 7,  'AccidenteTrabajo',  now() - interval '200 days', '99100002', '9990000011', '11001', 'EnCalificacionPcl',
        false, now() - interval '199 days',   35.50, (current_date - 150), 'LABORAL', NULL, NULL, NULL, now() - interval '199 days'),
    -- A08  en calificación PCL con dictamen en firme (controversia vencida sin objeción).
    ('01999f00-0000-7000-8001-00000000a008', 'PRUEBA_A', 8,  'EnfermedadLaboral', now() - interval '400 days', '99100002', '9990000011', '05001', 'EnCalificacionPcl',
        false, now() - interval '399 days',   62.00, (current_date - 300), 'LABORAL', NULL, NULL, NULL, now() - interval '399 days'),
    -- A09  cadena de 5 incapacidades de 30 días (150 acumulados): la alerta del tope de 180 días ya venció.
    ('01999f00-0000-7000-8001-00000000a009', 'PRUEBA_A', 9,  'AccidenteTrabajo',  now() - interval '170 days', '99100007', '9990000022', '17001', 'Validado',
        false, now() - interval '169 days',   NULL, NULL, NULL, NULL, NULL, NULL, now() - interval '169 days'),
    -- A10  remisión a la IPS 1 REVOCADA: la IPS ya no debe verlo.
    ('01999f00-0000-7000-8001-00000000a010', 'PRUEBA_A', 10, 'AccidenteTrabajo',  now() - interval '30 days',  '99100008', '9990000011', '11001', 'Validado',
        false, now() - interval '29 days',    NULL, NULL, NULL, NULL, NULL, NULL, now() - interval '29 days'),
    -- A11  remitido a la IPS 2: la IPS 1 no debe verlo (aislamiento entre IPS).
    ('01999f00-0000-7000-8001-00000000a011', 'PRUEBA_A', 11, 'EnfermedadLaboral', now() - interval '15 days',  '99100009', '9990000022', '05001', 'Validado',
        false, now() - interval '14 days',    NULL, NULL, NULL, NULL, NULL, NULL, now() - interval '14 days'),
    -- A12  radicado hace semanas y sin validar: plazos de reporte e investigación VENCIDOS.
    ('01999f00-0000-7000-8001-00000000a012', 'PRUEBA_A', 12, 'AccidenteTrabajo',  now() - interval '40 days',  '99100010', '9990000011', '11001', 'Radicado',
        false, now() - interval '30 days',    NULL, NULL, NULL, NULL, NULL, NULL, NULL),
    -- A13  validado; el trabajador 99100003 aún no tiene cuenta vinculada: tiene un código de activación ACTIVO.
    ('01999f00-0000-7000-8001-00000000a013', 'PRUEBA_A', 13, 'AccidenteTrabajo',  now() - interval '12 days',  '99100003', '9990000011', '11001', 'Validado',
        false, now() - interval '11 days',    NULL, NULL, NULL, NULL, NULL, NULL, now() - interval '11 days'),
    -- A14  código de activación BLOQUEADO (5 intentos fallidos).
    ('01999f00-0000-7000-8001-00000000a014', 'PRUEBA_A', 14, 'AccidenteTrabajo',  now() - interval '12 days',  '99100011', '9990000011', '11001', 'Validado',
        false, now() - interval '11 days',    NULL, NULL, NULL, NULL, NULL, NULL, now() - interval '11 days'),
    -- A15  código de activación EXPIRADO.
    ('01999f00-0000-7000-8001-00000000a015', 'PRUEBA_A', 15, 'EnfermedadLaboral', now() - interval '80 days',  '99100012', '9990000022', '05001', 'Validado',
        false, now() - interval '79 days',    NULL, NULL, NULL, NULL, NULL, NULL, now() - interval '79 days'),
    -- A16  accidente mortal validado: el caso de mayor gravedad para los plazos (investigación + remisión).
    ('01999f00-0000-7000-8001-00000000a016', 'PRUEBA_A', 16, 'AccidenteTrabajo',  now() - interval '6 days',   '99100013', '9990000022', '76001', 'Validado',
        true,  now() - interval '5 days',     NULL, NULL, NULL, NULL, NULL, NULL, now() - interval '5 days'),
    -- B01  mismo trabajador que A01/A03 en OTRA ARL: el portal del paciente ve casos de las dos; las ARL no se ven entre sí.
    ('01999f00-0000-7000-8001-00000000b001', 'PRUEBA_B', 1,  'AccidenteTrabajo',  now() - interval '25 days',  '99100001', '9990000033', '11001', 'Validado',
        false, now() - interval '24 days',    NULL, NULL, NULL, NULL, NULL, NULL, now() - interval '24 days'),
    -- B02  radicado, de la otra ARL.
    ('01999f00-0000-7000-8001-00000000b002', 'PRUEBA_B', 2,  'EnfermedadLaboral', now() - interval '3 days',   '99200001', '9990000033', '08001', 'Radicado',
        false, now() - interval '2 days',     NULL, NULL, NULL, NULL, NULL, NULL, NULL),
    -- B03  mismo trabajador que A02, remitido a la IPS 2.
    ('01999f00-0000-7000-8001-00000000b003', 'PRUEBA_B', 3,  'AccidenteTrabajo',  now() - interval '18 days',  '99100002', '9990000033', '11001', 'Validado',
        false, now() - interval '17 days',    NULL, NULL, NULL, NULL, NULL, NULL, now() - interval '17 days')
) AS c(id, tenant, seq, tipo, fecha_evento, trabajador, empleador, divipola, estado, grave, radicado_en,
       pcl, pcl_estructuracion, pcl_origen, duplicado_de, motivo, rechazado_en, validado_en)
ON CONFLICT DO NOTHING;

-- ── Remisiones a IPS ────────────────────────────────────────────────────────
INSERT INTO atel.remisiones (id, caso_medico_id, tenant_id, ips_id, remitido_en, remitido_por, revocada_en, revocada_por)
VALUES
    ('01999f00-0000-7000-8002-00000000a003', '01999f00-0000-7000-8001-00000000a003', 'PRUEBA_A', '990000000001', now() - interval '43 days', '01999f00-0000-7000-8000-00000000a001', NULL, NULL),
    ('01999f00-0000-7000-8002-00000000a009', '01999f00-0000-7000-8001-00000000a009', 'PRUEBA_A', '990000000001', now() - interval '168 days', '01999f00-0000-7000-8000-00000000a001', NULL, NULL),
    ('01999f00-0000-7000-8002-00000000a010', '01999f00-0000-7000-8001-00000000a010', 'PRUEBA_A', '990000000001', now() - interval '28 days', '01999f00-0000-7000-8000-00000000a001',
        now() - interval '20 days', '01999f00-0000-7000-8000-00000000a001'),
    ('01999f00-0000-7000-8002-00000000a011', '01999f00-0000-7000-8001-00000000a011', 'PRUEBA_A', '990000000002', now() - interval '13 days', '01999f00-0000-7000-8000-00000000a001', NULL, NULL),
    ('01999f00-0000-7000-8002-00000000b001', '01999f00-0000-7000-8001-00000000b001', 'PRUEBA_B', '990000000001', now() - interval '23 days', '01999f00-0000-7000-8000-00000000b001', NULL, NULL),
    ('01999f00-0000-7000-8002-00000000b003', '01999f00-0000-7000-8001-00000000b003', 'PRUEBA_B', '990000000002', now() - interval '16 days', '01999f00-0000-7000-8000-00000000b001', NULL, NULL)
ON CONFLICT DO NOTHING;

-- ── Asignaciones ────────────────────────────────────────────────────────────
-- Personal de la ARL (ips_id NULL) y personal de la IPS (ips_id = su REPS, solo sobre casos remitidos a ella).
INSERT INTO atel.asignaciones (caso_medico_id, sub, tenant_id, ips_id, asignado_en, asignado_por)
VALUES
    ('01999f00-0000-7000-8001-00000000a003', '01999f00-0000-7000-8000-00000000a002', 'PRUEBA_A', NULL,           now() - interval '43 days',  '01999f00-0000-7000-8000-00000000a001'),
    ('01999f00-0000-7000-8001-00000000a003', '01999f00-0000-7000-8000-00000000c002', 'PRUEBA_A', '990000000001', now() - interval '42 days',  '01999f00-0000-7000-8000-00000000c001'),
    ('01999f00-0000-7000-8001-00000000a004', '01999f00-0000-7000-8000-00000000a003', 'PRUEBA_A', NULL,           now() - interval '8 days',   '01999f00-0000-7000-8000-00000000a001'),
    ('01999f00-0000-7000-8001-00000000a007', '01999f00-0000-7000-8000-00000000a003', 'PRUEBA_A', NULL,           now() - interval '190 days', '01999f00-0000-7000-8000-00000000a001'),
    ('01999f00-0000-7000-8001-00000000a008', '01999f00-0000-7000-8000-00000000a003', 'PRUEBA_A', NULL,           now() - interval '390 days', '01999f00-0000-7000-8000-00000000a001'),
    ('01999f00-0000-7000-8001-00000000a009', '01999f00-0000-7000-8000-00000000a002', 'PRUEBA_A', NULL,           now() - interval '168 days', '01999f00-0000-7000-8000-00000000a001'),
    ('01999f00-0000-7000-8001-00000000a009', '01999f00-0000-7000-8000-00000000c002', 'PRUEBA_A', '990000000001', now() - interval '167 days', '01999f00-0000-7000-8000-00000000c001'),
    ('01999f00-0000-7000-8001-00000000a011', '01999f00-0000-7000-8000-00000000c004', 'PRUEBA_A', '990000000002', now() - interval '12 days',  '01999f00-0000-7000-8000-00000000c003'),
    ('01999f00-0000-7000-8001-00000000a016', '01999f00-0000-7000-8000-00000000a003', 'PRUEBA_A', NULL,           now() - interval '4 days',   '01999f00-0000-7000-8000-00000000a001'),
    ('01999f00-0000-7000-8001-00000000b001', '01999f00-0000-7000-8000-00000000b002', 'PRUEBA_B', NULL,           now() - interval '23 days',  '01999f00-0000-7000-8000-00000000b001'),
    ('01999f00-0000-7000-8001-00000000b003', '01999f00-0000-7000-8000-00000000c004', 'PRUEBA_B', '990000000002', now() - interval '15 days',  '01999f00-0000-7000-8000-00000000c003')
ON CONFLICT DO NOTHING;

-- ── Incapacidades ───────────────────────────────────────────────────────────
-- Cada cadena empieza en una inicial (cadena_id = su propio id) y sigue con prórrogas contiguas (ck_incapacidades_dias:
-- fecha_fin = fecha_inicio + dias − 1). El detalle clínico y el soporte PDF que referencian (registro_clinico_id,
-- soporte_id) son SINTÉTICOS: no existen en el esquema clinico. Sirven para listar, para los plazos y para el portal;
-- para probar la lectura de HC o la descarga de un soporte, regístralos por la API (README.md).
-- A03: 15 + 15 días. A09: 5 × 30 días (150 acumulados).
INSERT INTO atel.incapacidades (
    id, tenant_id, caso_medico_id, numero_certificado, ips_emisora_reps, ips_emisora_nombre, tipo, prorroga_de, cadena_id,
    fecha_inicio, dias, fecha_fin, dias_acumulados, modalidad, estado,
    profesional_tipo_documento, profesional_numero_documento, profesional_nombre, profesional_registro_rethus,
    diagnostico_categoria_indice, soporte_id, registro_clinico_id, registrada_en, registrada_por, client_id)
SELECT i.id::uuid, 'PRUEBA_A', i.caso::uuid, i.certificado, '990000000001', 'IPS PRUEBA 1',
       i.tipo, i.prorroga::uuid, i.cadena::uuid, current_date - i.inicia_hace, i.dias, current_date - i.inicia_hace + (i.dias - 1), i.acumulados,
       'PRESENCIAL', 'REGISTRADA', 'CC', '80000222', 'Médico IPS Prueba 1', 'RM-PRUEBA-0002',
       sha256(convert_to('PRUEBA-DX-' || i.certificado, 'UTF8')), i.soporte::uuid, i.registro::uuid,
       now() - interval '1 day' * (i.inicia_hace - i.dias), '01999f00-0000-7000-8000-00000000c002', NULL
FROM (VALUES
    ('01999f00-0000-7000-8003-00000000a031', '01999f00-0000-7000-8001-00000000a003', 'PRUEBA-A003-1', 'INICIAL',  NULL,                                   '01999f00-0000-7000-8003-00000000a031',  40, 15,  15, '01999f00-0000-7000-8004-00000000a031', '01999f00-0000-7000-8005-00000000a031'),
    ('01999f00-0000-7000-8003-00000000a032', '01999f00-0000-7000-8001-00000000a003', 'PRUEBA-A003-2', 'PRORROGA', '01999f00-0000-7000-8003-00000000a031', '01999f00-0000-7000-8003-00000000a031',  25, 15,  30, '01999f00-0000-7000-8004-00000000a032', '01999f00-0000-7000-8005-00000000a032'),
    ('01999f00-0000-7000-8003-00000000a091', '01999f00-0000-7000-8001-00000000a009', 'PRUEBA-A009-1', 'INICIAL',  NULL,                                   '01999f00-0000-7000-8003-00000000a091', 160, 30,  30, '01999f00-0000-7000-8004-00000000a091', '01999f00-0000-7000-8005-00000000a091'),
    ('01999f00-0000-7000-8003-00000000a092', '01999f00-0000-7000-8001-00000000a009', 'PRUEBA-A009-2', 'PRORROGA', '01999f00-0000-7000-8003-00000000a091', '01999f00-0000-7000-8003-00000000a091', 130, 30,  60, '01999f00-0000-7000-8004-00000000a092', '01999f00-0000-7000-8005-00000000a092'),
    ('01999f00-0000-7000-8003-00000000a093', '01999f00-0000-7000-8001-00000000a009', 'PRUEBA-A009-3', 'PRORROGA', '01999f00-0000-7000-8003-00000000a092', '01999f00-0000-7000-8003-00000000a091', 100, 30,  90, '01999f00-0000-7000-8004-00000000a093', '01999f00-0000-7000-8005-00000000a093'),
    ('01999f00-0000-7000-8003-00000000a094', '01999f00-0000-7000-8001-00000000a009', 'PRUEBA-A009-4', 'PRORROGA', '01999f00-0000-7000-8003-00000000a093', '01999f00-0000-7000-8003-00000000a091',  70, 30, 120, '01999f00-0000-7000-8004-00000000a094', '01999f00-0000-7000-8005-00000000a094'),
    ('01999f00-0000-7000-8003-00000000a095', '01999f00-0000-7000-8001-00000000a009', 'PRUEBA-A009-5', 'PRORROGA', '01999f00-0000-7000-8003-00000000a094', '01999f00-0000-7000-8003-00000000a091',  40, 30, 150, '01999f00-0000-7000-8004-00000000a095', '01999f00-0000-7000-8005-00000000a095')
) AS i(id, caso, certificado, tipo, prorroga, cadena, inicia_hace, dias, acumulados, soporte, registro)
ON CONFLICT DO NOTHING;

-- ── Dictámenes PCL ──────────────────────────────────────────────────────────
-- Igual que las incapacidades, el registro clínico DICTAMEN_PCL que referencian es sintético: GET del dictamen de A07 y
-- A08 no puede descifrar la motivación. Sirven para el estado, la línea de tiempo del paciente y los plazos de
-- controversia. Para ejercitar el dictamen completo, regístralo por la API sobre A04 o A16 (README.md).
INSERT INTO atel.dictamenes_pcl (
    id, tenant_id, caso_medico_id, registro_clinico_id, porcentaje_pcl, origen, fecha_estructuracion, fecha_dictamen,
    calificador_sub, calificador_nombre, calificador_registro_rethus, estado_notificacion, notificado_en, medio_notificacion)
VALUES
    ('01999f00-0000-7000-8006-00000000a007', 'PRUEBA_A', '01999f00-0000-7000-8001-00000000a007', '01999f00-0000-7000-8005-00000000a007',
     35.50, 'LABORAL', current_date - 150, current_date - 6, '01999f00-0000-7000-8000-00000000a003', 'Calificador Prueba A', 'RM-PRUEBA-0001',
     'NOTIFICADO', now() - interval '2 days', 'ELECTRONICO'),
    ('01999f00-0000-7000-8006-00000000a008', 'PRUEBA_A', '01999f00-0000-7000-8001-00000000a008', '01999f00-0000-7000-8005-00000000a008',
     62.00, 'LABORAL', current_date - 300, current_date - 60, '01999f00-0000-7000-8000-00000000a003', 'Calificador Prueba A', 'RM-PRUEBA-0001',
     'EN_FIRME', now() - interval '40 days', 'CORREO_CERTIFICADO')
ON CONFLICT DO NOTHING;

-- ── Plazos legales ──────────────────────────────────────────────────────────
-- Reglas (sst.reglas_termino, sembradas por las migraciones): …01 REPORTE_FURAT, …02 REPORTE_FUREP, …03 INVESTIGACION_AT,
-- …04 REMISION_INVESTIGACION_GRAVE, …05 CONTROVERSIA_DICTAMEN, …06 TOPE_IT_180, …07 SOLICITUD_COPIA_HC.
-- Cubre los cuatro estados: ABIERTO (con y sin alerta), CUMPLIDO, VENCIDO (extemporáneo) y SUSPENDIDO.
-- Las cifras de vigencia son las del motor, no se recalculan aquí: son datos de prueba para ver el estado, no para validar
-- el cálculo de días hábiles (eso lo hacen las pruebas automáticas).
INSERT INTO sst.plazos (
    id, tenant_id, caso_medico_id, regla_id, regla_codigo, referencia_id, radicado,
    inicia_en, vence_en, alerta_en, estado, alertado_en, cumplido_en, cumplido_por, nota_cumplimiento, extemporaneo, ultima_fecha_fin)
SELECT p.id::uuid, c.tenant_id, c.id, p.regla::uuid, p.codigo, p.referencia::uuid, c.radicado,
       p.inicia, p.vence, p.alerta, p.estado, p.alertado, p.cumplido, p.cumplido_por, p.nota, p.extemporaneo, p.ultima_fecha_fin
FROM (VALUES
    -- A01: vence en 10 min, alerta ya vencida → el Worker envía plazo.proximo-a-vencer y, a los 10 min, plazo.vencido.
    ('01999f00-0000-7000-8004-00000000a011', '01999f00-0000-7000-8001-00000000a001', '01992a00-0000-7000-8000-000000000001', 'REPORTE_FURAT', NULL,
        now() - interval '5 minutes', now() + interval '10 minutes', now() - interval '1 minute', 'ABIERTO', NULL, NULL, NULL, NULL, false, NULL::date),
    ('01999f00-0000-7000-8004-00000000a012', '01999f00-0000-7000-8001-00000000a001', '01992a00-0000-7000-8000-000000000003', 'INVESTIGACION_AT', NULL,
        now() - interval '5 minutes', now() + interval '15 days', now() + interval '12 days', 'ABIERTO', NULL, NULL, NULL, NULL, false, NULL),
    -- A02: reporte cumplido a tiempo.
    ('01999f00-0000-7000-8004-00000000a021', '01999f00-0000-7000-8001-00000000a002', '01992a00-0000-7000-8000-000000000002', 'REPORTE_FUREP', NULL,
        now() - interval '19 days', now() - interval '17 days', now() - interval '18 days', 'CUMPLIDO', NULL, now() - interval '19 days', 'sistema', NULL, false, NULL),
    -- A03: tope de 180 días (30 acumulados, alerta lejana) + reporte cumplido.
    ('01999f00-0000-7000-8004-00000000a031', '01999f00-0000-7000-8001-00000000a003', '01992a00-0000-7000-8000-000000000006', 'TOPE_IT_180', '01999f00-0000-7000-8003-00000000a032',
        now() - interval '40 days', now() + interval '150 days', now() + interval '120 days', 'ABIERTO', NULL, NULL, NULL, NULL, false, current_date - 11),
    ('01999f00-0000-7000-8004-00000000a032', '01999f00-0000-7000-8001-00000000a003', '01992a00-0000-7000-8000-000000000001', 'REPORTE_FURAT', NULL,
        now() - interval '44 days', now() - interval '42 days', now() - interval '43 days', 'CUMPLIDO', NULL, now() - interval '44 days', 'sistema', NULL, false, NULL),
    ('01999f00-0000-7000-8004-00000000a033', '01999f00-0000-7000-8001-00000000a003', '01992a00-0000-7000-8000-000000000003', 'INVESTIGACION_AT', NULL,
        now() - interval '44 days', now() - interval '29 days', now() - interval '32 days', 'VENCIDO', now() - interval '32 days', NULL, NULL, NULL, false, NULL),
    -- A04: AT grave sin investigar (ambos plazos corriendo).
    ('01999f00-0000-7000-8004-00000000a041', '01999f00-0000-7000-8001-00000000a004', '01992a00-0000-7000-8000-000000000003', 'INVESTIGACION_AT', NULL,
        now() - interval '9 days', now() + interval '6 days', now() + interval '3 days', 'ABIERTO', NULL, NULL, NULL, NULL, false, NULL),
    ('01999f00-0000-7000-8004-00000000a042', '01999f00-0000-7000-8001-00000000a004', '01992a00-0000-7000-8000-000000000004', 'REMISION_INVESTIGACION_GRAVE', NULL,
        now() - interval '9 days', now() + interval '6 days', now() + interval '3 days', 'ABIERTO', NULL, NULL, NULL, NULL, false, NULL),
    -- A05: rechazado → plazos suspendidos.
    ('01999f00-0000-7000-8004-00000000a051', '01999f00-0000-7000-8001-00000000a005', '01992a00-0000-7000-8000-000000000001', 'REPORTE_FURAT', NULL,
        now() - interval '7 days', now() - interval '5 days', now() - interval '6 days', 'SUSPENDIDO', NULL, NULL, NULL, NULL, false, NULL),
    ('01999f00-0000-7000-8004-00000000a052', '01999f00-0000-7000-8001-00000000a005', '01992a00-0000-7000-8000-000000000003', 'INVESTIGACION_AT', NULL,
        now() - interval '7 days', now() + interval '8 days', now() + interval '5 days', 'SUSPENDIDO', NULL, NULL, NULL, NULL, false, NULL),
    -- A07: controversia del dictamen corriendo.
    ('01999f00-0000-7000-8004-00000000a071', '01999f00-0000-7000-8001-00000000a007', '01992a00-0000-7000-8000-000000000005', 'CONTROVERSIA_DICTAMEN', '01999f00-0000-7000-8006-00000000a007',
        now() - interval '2 days', now() + interval '12 days', now() + interval '9 days', 'ABIERTO', NULL, NULL, NULL, NULL, false, NULL),
    -- A08: controversia cumplida (el dictamen quedó en firme al vencer).
    ('01999f00-0000-7000-8004-00000000a081', '01999f00-0000-7000-8001-00000000a008', '01992a00-0000-7000-8000-000000000005', 'CONTROVERSIA_DICTAMEN', '01999f00-0000-7000-8006-00000000a008',
        now() - interval '40 days', now() - interval '26 days', now() - interval '29 days', 'CUMPLIDO', now() - interval '29 days', now() - interval '26 days', 'sistema', NULL, false, NULL),
    -- A09: tope de 180 días con 150 acumulados: la alerta venció hace 11 días sin enviarse y el tope llega en 30.
    ('01999f00-0000-7000-8004-00000000a091', '01999f00-0000-7000-8001-00000000a009', '01992a00-0000-7000-8000-000000000006', 'TOPE_IT_180', '01999f00-0000-7000-8003-00000000a095',
        now() - interval '160 days', now() + interval '30 days', now() - interval '11 days', 'ABIERTO', NULL, NULL, NULL, NULL, false, current_date - 11),
    -- A12: reporte e investigación VENCIDOS (el reporte, además, extemporáneo).
    ('01999f00-0000-7000-8004-00000000a121', '01999f00-0000-7000-8001-00000000a012', '01992a00-0000-7000-8000-000000000001', 'REPORTE_FURAT', NULL,
        now() - interval '30 days', now() - interval '28 days', now() - interval '29 days', 'VENCIDO', now() - interval '29 days', NULL, NULL, NULL, true, NULL),
    ('01999f00-0000-7000-8004-00000000a122', '01999f00-0000-7000-8001-00000000a012', '01992a00-0000-7000-8000-000000000003', 'INVESTIGACION_AT', NULL,
        now() - interval '30 days', now() - interval '15 days', now() - interval '18 days', 'VENCIDO', now() - interval '18 days', NULL, NULL, NULL, false, NULL),
    -- A16: mortal.
    ('01999f00-0000-7000-8004-00000000a161', '01999f00-0000-7000-8001-00000000a016', '01992a00-0000-7000-8000-000000000003', 'INVESTIGACION_AT', NULL,
        now() - interval '5 days', now() + interval '10 days', now() + interval '7 days', 'ABIERTO', NULL, NULL, NULL, NULL, false, NULL),
    ('01999f00-0000-7000-8004-00000000a162', '01999f00-0000-7000-8001-00000000a016', '01992a00-0000-7000-8000-000000000004', 'REMISION_INVESTIGACION_GRAVE', NULL,
        now() - interval '5 days', now() + interval '10 days', now() + interval '7 days', 'ABIERTO', NULL, NULL, NULL, NULL, false, NULL),
    -- B02: reporte corriendo en la otra ARL.
    ('01999f00-0000-7000-8004-00000000b021', '01999f00-0000-7000-8001-00000000b002', '01992a00-0000-7000-8000-000000000002', 'REPORTE_FUREP', NULL,
        now() - interval '2 days', now() + interval '1 day', now() - interval '1 hour', 'ABIERTO', NULL, NULL, NULL, NULL, false, NULL),
    -- Solicitudes de copia de HC (ver más abajo): una abierta (A03), una en trámite (A07) y una cumplida (A08).
    ('01999f00-0000-7000-8004-00000000a034', '01999f00-0000-7000-8001-00000000a003', '01992a00-0000-7000-8000-000000000007', 'SOLICITUD_COPIA_HC', '01999f00-0000-7000-8007-00000000a003',
        now() - interval '1 day', now() + interval '13 days', now() + interval '9 days', 'ABIERTO', NULL, NULL, NULL, NULL, false, NULL),
    ('01999f00-0000-7000-8004-00000000a072', '01999f00-0000-7000-8001-00000000a007', '01992a00-0000-7000-8000-000000000007', 'SOLICITUD_COPIA_HC', '01999f00-0000-7000-8007-00000000a007',
        now() - interval '9 days', now() + interval '5 days', now() + interval '1 day', 'ABIERTO', NULL, NULL, NULL, NULL, false, NULL),
    ('01999f00-0000-7000-8004-00000000a082', '01999f00-0000-7000-8001-00000000a008', '01992a00-0000-7000-8000-000000000007', 'SOLICITUD_COPIA_HC', '01999f00-0000-7000-8007-00000000a008',
        now() - interval '20 days', now() - interval '6 days', now() - interval '10 days', 'CUMPLIDO', now() - interval '10 days', now() - interval '8 days', '01999f00-0000-7000-8000-00000000a004',
        'Entregada por correo certificado (dato de prueba)', false, NULL)
) AS p(id, caso, regla, codigo, referencia, inicia, vence, alerta, estado, alertado, cumplido, cumplido_por, nota, extemporaneo, ultima_fecha_fin)
JOIN atel.casos_medicos c ON c.id = p.caso::uuid
ON CONFLICT DO NOTHING;

-- ── Códigos de activación y vínculos del portal del paciente ────────────────
-- secreto_hash = SHA-256(sal ‖ secreto en UTF-8), igual que atel.canjear_codigo_activacion. Los códigos USADO/BLOQUEADO/
-- EXPIRADO llevan un secreto que nadie conoce (se desecha), porque ya no se pueden canjear. El único canjeable es el
-- ACTIVO de A13 y su secreto llega por la variable de psql :secreto_activo.
--   formato visible del ACTIVO:  PRB013-{secreto}
INSERT INTO atel.codigos_activacion (
    id, tenant_id, caso_medico_id, trabajador_tipo, trabajador_numero, secreto_hash, secreto_sal,
    emitido_en, emitido_por, expira_en, usado_en, usado_por, intentos_fallidos, estado)
SELECT k.id, 'PRUEBA_A', k.caso::uuid, 'CC', k.trabajador,
       sha256(k.sal || convert_to(k.secreto, 'UTF8')), k.sal,
       k.emitido, '01999f00-0000-7000-8000-00000000a001', k.expira, k.usado, k.usado_por, k.intentos, k.estado
FROM (VALUES
    ('PRB001', '01999f00-0000-7000-8001-00000000a003', '99100001', decode('aa000000000000000000000000000001', 'hex'), 'secreto-descartado-01',
        now() - interval '40 days', now() - interval '10 days', now() - interval '39 days', '01999f00-0000-7000-8000-00000000d001', 0, 'USADO'),
    ('PRB002', '01999f00-0000-7000-8001-00000000a002', '99100002', decode('aa000000000000000000000000000002', 'hex'), 'secreto-descartado-02',
        now() - interval '19 days', now() + interval '11 days', now() - interval '18 days', '01999f00-0000-7000-8000-00000000d002', 1, 'USADO'),
    ('PRB013', '01999f00-0000-7000-8001-00000000a013', '99100003', decode('aa000000000000000000000000000013', 'hex'), :'secreto_activo',
        now() - interval '1 day',  now() + interval '29 days', NULL, NULL, 0, 'ACTIVO'),
    ('PRB014', '01999f00-0000-7000-8001-00000000a014', '99100011', decode('aa000000000000000000000000000014', 'hex'), 'secreto-descartado-14',
        now() - interval '3 days', now() + interval '27 days', NULL, NULL, 5, 'BLOQUEADO'),
    ('PRB015', '01999f00-0000-7000-8001-00000000a015', '99100012', decode('aa000000000000000000000000000015', 'hex'), 'secreto-descartado-15',
        now() - interval '60 days', now() - interval '30 days', NULL, NULL, 0, 'EXPIRADO')
) AS k(id, caso, trabajador, sal, secreto, emitido, expira, usado, usado_por, intentos, estado)
ON CONFLICT DO NOTHING;

-- El vínculo es por documento y vale para los casos de cualquier ARL: paciente.1 ve A01, A03, A06 y B01; paciente.2 ve
-- A02, A07, A08 y B03. paciente.3 no tiene vínculo todavía: canjea PRB013 para probar el flujo completo.
INSERT INTO atel.vinculos_paciente (paciente_sub, trabajador_tipo, trabajador_numero, codigo_id, vinculado_en)
VALUES
    ('01999f00-0000-7000-8000-00000000d001', 'CC', '99100001', 'PRB001', now() - interval '39 days'),
    ('01999f00-0000-7000-8000-00000000d002', 'CC', '99100002', 'PRB002', now() - interval '18 days')
ON CONFLICT DO NOTHING;

-- ── Solicitudes de copia de la historia clínica ─────────────────────────────
INSERT INTO atel.solicitudes_copia_hc (
    id, tenant_id, caso_medico_id, paciente_sub, medio_entrega, direccion_o_correo, observaciones, solicitada_en,
    estado, atendida_en, atendida_por, nota_atencion)
VALUES
    ('01999f00-0000-7000-8007-00000000a003', 'PRUEBA_A', '01999f00-0000-7000-8001-00000000a003', '01999f00-0000-7000-8000-00000000d001',
     'ELECTRONICO', 'paciente1@pruebas.halsa.invalid', 'Solicitud de prueba (RECIBIDA)', now() - interval '1 day', 'RECIBIDA', NULL, NULL, NULL),
    ('01999f00-0000-7000-8007-00000000a007', 'PRUEBA_A', '01999f00-0000-7000-8001-00000000a007', '01999f00-0000-7000-8000-00000000d002',
     'CORREO_CERTIFICADO', 'Calle 1 # 2-3, Bogotá (dato de prueba)', 'Solicitud de prueba (EN_TRAMITE)', now() - interval '9 days', 'EN_TRAMITE', NULL, NULL, NULL),
    ('01999f00-0000-7000-8007-00000000a008', 'PRUEBA_A', '01999f00-0000-7000-8001-00000000a008', '01999f00-0000-7000-8000-00000000d002',
     'PRESENCIAL', NULL, 'Solicitud de prueba (ENTREGADA)', now() - interval '20 days', 'ENTREGADA', now() - interval '8 days', '01999f00-0000-7000-8000-00000000a004',
     'Entregada en ventanilla (dato de prueba)')
ON CONFLICT DO NOTHING;

COMMIT;

-- ── Resumen ─────────────────────────────────────────────────────────────────
SELECT c.tenant_id, c.estado, count(*) AS casos
FROM atel.casos_medicos c
WHERE c.tenant_id IN ('PRUEBA_A', 'PRUEBA_B')
GROUP BY 1, 2 ORDER BY 1, 2;
