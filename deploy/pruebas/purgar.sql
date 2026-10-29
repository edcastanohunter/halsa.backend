-- ─────────────────────────────────────────────────────────────────────────────
-- purgar.sql — borra TODO lo que dejó la fase de pruebas: lo sembrado por sembrar.sql y lo que los testers crearon por la API.
--
-- Lo ejecuta deploy/scripts/pruebas.sh (purgar) como `postgres` por el socket local. Qué borra, y solo eso:
--
--   • toda fila con tenant_id PRUEBA_A o PRUEBA_B (atel, integracion, sst, auditoria, clinico), descubiertas por catálogo:
--     una tabla nueva con tenant_id queda cubierta sin tocar este archivo;
--   • lo que no lleva tenant_id pero se deriva de ellas: vínculos y canjes del portal, entregas y suscripciones de webhooks
--     de las IPS de prueba, claves de idempotencia, el outbox y el event_store de esos casos;
--   • la auditoría de acceso (incluida la inmutable) y las revisiones de emergencia de esos tenants, de esas IPS o de esos
--     usuarios de prueba: los accesos de un tester a otra ARL (denegados) también se van.
--   • las llaves de cifrado de esos tenants (clinico.llaves_tenant): aun si quedara un byte cifrado suelto en un backup,
--     la clave que lo abre ya no existe en la base.
--
-- Cómo salta las protecciones sin debilitarlas: la tabla de auditoría es inmutable por trigger y todo está bajo FORCE RLS.
-- `SET LOCAL session_replication_role = replica` (solo superusuario, solo esta transacción) apaga los triggers de usuario;
-- el superusuario no está sujeto a la RLS. Nada de eso persiste: al terminar la transacción los triggers y las políticas
-- están intactos. Las migraciones y el esquema no se tocan.
--
-- Garantías, todas dentro de la misma transacción (si alguna falla, ROLLBACK y no se borra nada):
--   1. los tenants son constantes de este archivo y deben empezar por PRUEBA_;
--   2. los casos y la auditoría de cualquier otro tenant tienen exactamente las mismas filas antes y después;
--   3. al final, ninguna tabla con tenant_id conserva filas de los tenants de prueba.
--
-- Variables de psql: -v dry=1 → cuenta lo que borraría y hace ROLLBACK.
--
-- Fuera de alcance (lo avisa pruebas.sh): los archivos de soportes en el volumen halsa-archivos (los borra el script con la
-- lista de rutas que saca antes), la caché de Redis (caduca en minutos), los logs de Seq y los backups de pgBackRest, que
-- guardan lo anterior a la purga hasta que venza su retención. Los usuarios de Keycloak se borran con
-- deploy/keycloak/crear-usuarios-prueba.sh eliminar.
-- ─────────────────────────────────────────────────────────────────────────────
\set ON_ERROR_STOP on

BEGIN;

SET LOCAL session_replication_role = replica;
SET LOCAL lock_timeout = '15s';

CREATE TEMP TABLE _alcance ON COMMIT DROP AS
SELECT ARRAY['PRUEBA_A', 'PRUEBA_B']                                AS tenants,
       ARRAY['990000000001', '990000000002']                         AS ips,
       ARRAY[
           -- ARL A: analista, médico laboral, calificador, oficial de privacidad, administrador
           '01999f00-0000-7000-8000-00000000a001', '01999f00-0000-7000-8000-00000000a002', '01999f00-0000-7000-8000-00000000a003',
           '01999f00-0000-7000-8000-00000000a004', '01999f00-0000-7000-8000-00000000a005',
           -- ARL B: analista, médico laboral, oficial de privacidad
           '01999f00-0000-7000-8000-00000000b001', '01999f00-0000-7000-8000-00000000b002', '01999f00-0000-7000-8000-00000000b003',
           -- IPS 1 y 2: coordinador y médico de cada una
           '01999f00-0000-7000-8000-00000000c001', '01999f00-0000-7000-8000-00000000c002',
           '01999f00-0000-7000-8000-00000000c003', '01999f00-0000-7000-8000-00000000c004',
           -- pacientes
           '01999f00-0000-7000-8000-00000000d001', '01999f00-0000-7000-8000-00000000d002', '01999f00-0000-7000-8000-00000000d003'
       ]                                                             AS subs;

-- Una sola fuente de verdad: si alguien edita la lista y se equivoca, no se borra nada.
DO $$
DECLARE a _alcance%ROWTYPE;
BEGIN
    SELECT * INTO a FROM _alcance;
    IF EXISTS (SELECT 1 FROM unnest(a.tenants) t WHERE t !~ '^PRUEBA_[A-Z0-9_]{1,13}$') THEN
        RAISE EXCEPTION 'Todos los tenants de la purga deben empezar por PRUEBA_: %', a.tenants;
    END IF;
    IF EXISTS (SELECT 1 FROM unnest(a.ips) i WHERE i !~ '^99[0-9]{10}$') THEN
        RAISE EXCEPTION 'Las IPS de prueba deben ser REPS ficticios 99XXXXXXXXXX: %', a.ips;
    END IF;
    IF EXISTS (SELECT 1 FROM unnest(a.subs) s WHERE s !~ '^01999f00-0000-7000-8000-') THEN
        RAISE EXCEPTION 'Los usuarios de prueba deben ser 01999f00-0000-7000-8000-…: %', a.subs;
    END IF;
END
$$;

CREATE TEMP TABLE _casos ON COMMIT DROP AS
SELECT c.id FROM atel.casos_medicos c, _alcance a WHERE c.tenant_id = ANY (a.tenants);

-- Foto de lo que NO es de prueba. Debe quedar idéntica.
CREATE TEMP TABLE _ajeno ON COMMIT DROP AS
SELECT 'atel.casos_medicos' AS tabla, count(*) AS filas FROM atel.casos_medicos c, _alcance a WHERE c.tenant_id <> ALL (a.tenants)
UNION ALL
SELECT 'auditoria.accesos', count(*) FROM auditoria.accesos x, _alcance a
 WHERE coalesce(x.tenant_id, '')       <> ALL (a.tenants)
   AND coalesce(x.actor_tenant_id, '') <> ALL (a.tenants)
   AND coalesce(x.actor_ips_id, '')    <> ALL (a.ips)
   AND x.actor_id                      <> ALL (a.subs)
   AND (x.caso_medico_id IS NULL OR x.caso_medico_id NOT IN (SELECT id FROM _casos));

CREATE TEMP TABLE _purga (orden serial, tabla text, filas bigint) ON COMMIT DROP;

-- ── 1. Lo que depende de los tenants pero no lleva tenant_id ────────────────
DO $$
DECLARE
    a _alcance%ROWTYPE;
    n bigint;
BEGIN
    SELECT * INTO a FROM _alcance;

    -- Portal del paciente: el vínculo cuelga del código; los canjes, del sub del paciente (de prueba o que canjeó uno).
    DELETE FROM atel.vinculos_paciente
     WHERE codigo_id IN (SELECT id FROM atel.codigos_activacion WHERE tenant_id = ANY (a.tenants))
        OR paciente_sub = ANY (a.subs);
    GET DIAGNOSTICS n = ROW_COUNT;
    INSERT INTO _purga (tabla, filas) VALUES ('atel.vinculos_paciente', n);

    DELETE FROM atel.intentos_canje
     WHERE paciente_sub = ANY (a.subs)
        OR paciente_sub IN (SELECT usado_por FROM atel.codigos_activacion WHERE tenant_id = ANY (a.tenants) AND usado_por IS NOT NULL);
    GET DIAGNOSTICS n = ROW_COUNT;
    INSERT INTO _purga (tabla, filas) VALUES ('atel.intentos_canje', n);

    -- Webhooks salientes: los de una ARL de prueba (tenant_id) y los de una IPS de prueba (ips_id, sin tenant).
    DELETE FROM integracion.entregas_webhook
     WHERE suscripcion_id IN (SELECT id FROM integracion.suscripciones_webhook
                               WHERE tenant_id = ANY (a.tenants) OR ips_id = ANY (a.ips));
    GET DIAGNOSTICS n = ROW_COUNT;
    INSERT INTO _purga (tabla, filas) VALUES ('integracion.entregas_webhook', n);

    DELETE FROM integracion.suscripciones_webhook WHERE tenant_id = ANY (a.tenants) OR ips_id = ANY (a.ips);
    GET DIAGNOSTICS n = ROW_COUNT;
    INSERT INTO _purga (tabla, filas) VALUES ('integracion.suscripciones_webhook', n);

    -- Claves de idempotencia: por tenant, o por el emisor (tenant:X / ips:X; una IPS no tiene tenant).
    DELETE FROM integracion.solicitudes_idempotentes
     WHERE tenant_id = ANY (a.tenants)
        OR emisor = ANY (SELECT 'tenant:' || t FROM unnest(a.tenants) t)
        OR emisor = ANY (SELECT 'ips:' || i FROM unnest(a.ips) i);
    GET DIAGNOSTICS n = ROW_COUNT;
    INSERT INTO _purga (tabla, filas) VALUES ('integracion.solicitudes_idempotentes', n);

    -- Auditoría. Es inmutable y está sellada (SPEC 11 aún no); por eso la purga necesita el superusuario. Se van los accesos
    -- sobre recursos de prueba y los que hicieron actores de prueba, aunque el recurso fuera de otro tenant (los denegados).
    DELETE FROM auditoria.revisiones_emergencia
     WHERE tenant_id = ANY (a.tenants) OR actor_id = ANY (a.subs) OR caso_medico_id IN (SELECT id FROM _casos);
    GET DIAGNOSTICS n = ROW_COUNT;
    INSERT INTO _purga (tabla, filas) VALUES ('auditoria.revisiones_emergencia', n);

    DELETE FROM auditoria.accesos
     WHERE tenant_id       = ANY (a.tenants)
        OR actor_tenant_id = ANY (a.tenants)
        OR actor_ips_id    = ANY (a.ips)
        OR actor_id        = ANY (a.subs)
        OR caso_medico_id IN (SELECT id FROM _casos);
    GET DIAGNOSTICS n = ROW_COUNT;
    INSERT INTO _purga (tabla, filas) VALUES ('auditoria.accesos', n);

    -- Outbox y event_store no llevan tenant: se reconocen por el stream (el caso) o por el contenido.
    DELETE FROM outbox.outbox_messages m
     WHERE EXISTS (SELECT 1 FROM unnest(a.tenants) t WHERE strpos(m.content::text, t) > 0)
        OR EXISTS (SELECT 1 FROM _casos c WHERE strpos(m.content::text, c.id::text) > 0);
    GET DIAGNOSTICS n = ROW_COUNT;
    INSERT INTO _purga (tabla, filas) VALUES ('outbox.outbox_messages', n);

    DELETE FROM event_store.events e
     WHERE e.stream_id IN (SELECT id::text FROM _casos)
        OR EXISTS (SELECT 1 FROM unnest(a.tenants) t WHERE strpos(e.payload::text, t) > 0);
    GET DIAGNOSTICS n = ROW_COUNT;
    INSERT INTO _purga (tabla, filas) VALUES ('event_store.events', n);
END
$$;

-- ── 2. Todo lo demás con tenant_id, por catálogo ────────────────────────────
-- Las particiones se alcanzan por su tabla madre. Las tablas ya tratadas arriba no se repiten.
DO $$
DECLARE
    a _alcance%ROWTYPE;
    t record;
    n bigint;
BEGIN
    SELECT * INTO a FROM _alcance;

    FOR t IN
        SELECT ns.nspname AS esquema, cl.relname AS tabla
        FROM pg_class cl
        JOIN pg_namespace ns ON ns.oid = cl.relnamespace
        JOIN pg_attribute at ON at.attrelid = cl.oid AND at.attname = 'tenant_id' AND NOT at.attisdropped
        WHERE ns.nspname IN ('atel', 'integracion', 'sst', 'auditoria', 'clinico')
          AND cl.relkind IN ('r', 'p')
          AND NOT cl.relispartition
          AND (ns.nspname || '.' || cl.relname) NOT IN
              ('auditoria.accesos', 'auditoria.revisiones_emergencia', 'integracion.suscripciones_webhook',
               'integracion.solicitudes_idempotentes')
        ORDER BY ns.nspname, cl.relname
    LOOP
        EXECUTE format('DELETE FROM %I.%I WHERE tenant_id = ANY ($1)', t.esquema, t.tabla) USING a.tenants;
        GET DIAGNOSTICS n = ROW_COUNT;
        INSERT INTO _purga (tabla, filas) VALUES (t.esquema || '.' || t.tabla, n);
    END LOOP;
END
$$;

-- ── 3. Comprobaciones: si alguna falla, ROLLBACK y no se borra nada ─────────
DO $$
DECLARE
    a _alcance%ROWTYPE;
    t record;
    n bigint;
    antes bigint;
BEGIN
    SELECT * INTO a FROM _alcance;

    -- 3.1 No quedó nada de los tenants de prueba en ninguna tabla con tenant_id.
    FOR t IN
        SELECT ns.nspname AS esquema, cl.relname AS tabla
        FROM pg_class cl
        JOIN pg_namespace ns ON ns.oid = cl.relnamespace
        JOIN pg_attribute at ON at.attrelid = cl.oid AND at.attname = 'tenant_id' AND NOT at.attisdropped
        WHERE ns.nspname IN ('atel', 'integracion', 'sst', 'auditoria', 'clinico')
          AND cl.relkind IN ('r', 'p') AND NOT cl.relispartition
    LOOP
        EXECUTE format('SELECT count(*) FROM %I.%I WHERE tenant_id = ANY ($1)', t.esquema, t.tabla) INTO n USING a.tenants;
        IF n > 0 THEN
            RAISE EXCEPTION 'Quedaron % filas de prueba en %.%', n, t.esquema, t.tabla;
        END IF;
    END LOOP;

    -- 3.2 Lo ajeno no cambió.
    SELECT filas INTO antes FROM _ajeno WHERE tabla = 'atel.casos_medicos';
    SELECT count(*) INTO n FROM atel.casos_medicos c WHERE c.tenant_id <> ALL (a.tenants);
    IF n <> antes THEN
        RAISE EXCEPTION 'atel.casos_medicos de otros tenants cambió: % → %', antes, n;
    END IF;

    SELECT filas INTO antes FROM _ajeno WHERE tabla = 'auditoria.accesos';
    SELECT count(*) INTO n FROM auditoria.accesos x
     WHERE coalesce(x.tenant_id, '')       <> ALL (a.tenants)
       AND coalesce(x.actor_tenant_id, '') <> ALL (a.tenants)
       AND coalesce(x.actor_ips_id, '')    <> ALL (a.ips)
       AND x.actor_id                      <> ALL (a.subs)
       AND (x.caso_medico_id IS NULL OR x.caso_medico_id NOT IN (SELECT id FROM _casos));
    IF n <> antes THEN
        RAISE EXCEPTION 'auditoria.accesos de otros tenants cambió: % → %', antes, n;
    END IF;
END
$$;

SELECT tabla, filas FROM _purga WHERE filas > 0 ORDER BY orden;
SELECT coalesce(sum(filas), 0) AS filas_borradas FROM _purga;

\if :{?dry}
    \echo '── dry=1: ROLLBACK, no se borró nada ──'
    ROLLBACK;
\else
    COMMIT;
    \echo '── Purga confirmada ──'
\endif
