-- ─────────────────────────────────────────────────────────────────────────────
-- 01-roles.sql  —  Roles de base de datos para Halsa Backend
--
-- Este script se ejecuta UNA VEZ, sobre un volumen vacío, por el entrypoint de
-- la imagen oficial de PostgreSQL.  Los permisos sobre tablas y esquemas se
-- aplican en las migraciones de EF Core (halsa_owner los crea, así que no
-- puede GRANT sobre sí mismo aquí).
-- ─────────────────────────────────────────────────────────────────────────────

-- ── halsa_owner ──────────────────────────────────────────────────────────────
-- Dueño de todos los esquemas y objetos.  Las migraciones y el seed corren con
-- él.  No tiene BYPASSRLS para que el FORCE RLS de las migraciones lo cubra.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'halsa_owner') THEN
        CREATE ROLE halsa_owner LOGIN PASSWORD 'halsa_owner_dev' CREATEDB;
    END IF;
END$$;

-- ── halsa_app ─────────────────────────────────────────────────────────────────
-- Rol de la API en tiempo de ejecución.  Solo tiene los permisos mínimos sobre
-- las tablas que necesita.  Los GRANT concretos los hace halsa_owner en las
-- migraciones.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'halsa_app') THEN
        CREATE ROLE halsa_app LOGIN PASSWORD 'halsa_app_dev';
    END IF;
END$$;

-- ── halsa_clinico ─────────────────────────────────────────────────────────────
-- Rol del esquema clínico (SPEC 03).  Lo usa solo ClinicalDbContext.  Sus permisos
-- (USAGE sobre clinico, SELECT/INSERT, EXECUTE de la función de remisión) los da
-- la migración ClinicoInicial; nunca recibe USAGE sobre atel.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'halsa_clinico') THEN
        CREATE ROLE halsa_clinico LOGIN PASSWORD 'halsa_clinico_dev';
    END IF;
END$$;

-- ── halsa_ingesta ─────────────────────────────────────────────────────────────
-- Rol de la ingesta de FURAT/FUREP (SPEC 05). Lo usa solo IngestaDbContext. Escribe
-- en atel, integracion y clinico en una sola transacción, pero NO lee la historia
-- clínica: sobre clinico.historia_clinica_detalle solo tiene INSERT. Sus permisos
-- los da la migración IntegracionEsquema.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'halsa_ingesta') THEN
        CREATE ROLE halsa_ingesta LOGIN PASSWORD 'halsa_ingesta_dev';
    END IF;
END$$;

-- ── halsa_worker ──────────────────────────────────────────────────────────────
-- Rol de los barridos del Worker entre tenants (SPEC 09). Sus políticas son TO
-- halsa_worker y solo en las tablas que se barren: sst.plazos, la idempotencia
-- vencida, clinico.soportes (id, ruta), el outbox y quartz. Nunca recibe USAGE
-- sobre atel. Sus permisos los dan las migraciones SstTerminos, SoportesWorker y
-- Quartz. Los handlers de eventos del Worker siguen usando halsa_app.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'halsa_worker') THEN
        CREATE ROLE halsa_worker LOGIN PASSWORD 'halsa_worker_dev';
    END IF;
END$$;

-- ── Propiedad de la base de datos ─────────────────────────────────────────────
-- halsa_owner necesita CREATE en la base para que EF Core pueda crear esquemas
-- en las migraciones.
GRANT CREATE ON DATABASE "halsa.backend" TO halsa_owner;
