-- Postgres only auto-creates the database named by POSTGRES_DB (orders).
-- inventory-service and payments-service each need their own database on
-- the same instance (see docs/saga/milestone-12-payments-saga.md), and
-- until now the only way to get them was two undiscoverable manual scripts
-- (scripts/init-inventory-db.sh, scripts/init-payments-db.sh) that nothing
-- ever actually invoked - so a fresh clone or CI runner comes up with both
-- services unable to connect ("database ... does not exist"), exactly the
-- bring-up-reproducibility problem the farmate-observability network and
-- nginx TLS cert fixes already closed for their own layers.
--
-- Postgres runs every docker-entrypoint-initdb.d/*.sql once, automatically,
-- the first time this container starts against an empty data volume - so
-- this happens for free on any fresh bring-up and is a pure no-op on an
-- already-initialized volume (like the lab server's, where both databases
-- already exist from a prior manual run of those scripts).
CREATE DATABASE inventory OWNER orders;
CREATE DATABASE payments OWNER orders;
