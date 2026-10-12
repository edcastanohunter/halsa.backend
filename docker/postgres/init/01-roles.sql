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
-- Rol reservado para el esquema clínico (SPEC 03).  Sin permisos en esta spec.
DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'halsa_clinico') THEN
        CREATE ROLE halsa_clinico LOGIN PASSWORD 'halsa_clinico_dev';
    END IF;
END$$;

-- ── Propiedad de la base de datos ─────────────────────────────────────────────
-- halsa_owner necesita CREATE en la base para que EF Core pueda crear esquemas
-- en las migraciones.
GRANT CREATE ON DATABASE "halsa.backend" TO halsa_owner;
