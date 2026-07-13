-- 0001_create_schemas.sql
--
-- Creates the base schemas for the PostgreSQL genealogy workspace.
--
-- IMMUTABILITY: Released migrations are immutable. Once this file has been
-- applied to any shared environment it must never be edited. Correct or extend
-- the schema by adding a new, higher-numbered migration instead of changing an
-- existing one. Editing an applied migration breaks the schema_version journal
-- and produces divergent databases.

CREATE SCHEMA IF NOT EXISTS genealogy;
CREATE SCHEMA IF NOT EXISTS research;
