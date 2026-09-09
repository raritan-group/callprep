-- 006_customer_address.sql — customer address on the semantic layer.
-- reports_push.ps1 (VMSQL2 Agent job "ReportsApp - Sync to Hetzner") carries P21 address.* for each customer since 2026-09-09:
-- phys_address1, phys_city, phys_state, phys_zip, mail_city, mail_zip, phone on public.customers.
-- Re-runnable. CREATE OR REPLACE VIEW can only append columns, so both views keep their existing column order.

CREATE OR REPLACE VIEW callprep.customer_all AS
SELECT c.customer_id, c.customer_name, c.customer_class, c.salesrep_id,
       NULLIF(TRIM(COALESCE(r.first_name,'') || ' ' || COALESCE(r.last_name,'')), '') AS salesrep_name,
       NULLIF(TRIM(c.phys_address1), '') AS address,
       NULLIF(TRIM(c.phys_city), '')     AS city,
       NULLIF(TRIM(c.phys_state), '')    AS state,
       NULLIF(LEFT(TRIM(c.phys_zip), 5), '') AS zip,
       NULLIF(TRIM(c.phone), '')         AS phone
FROM customers c
LEFT JOIN salesreps r ON r.id::text = c.salesrep_id::text;

CREATE OR REPLACE VIEW callprep.customer AS
SELECT a.customer_id, a.customer_name, a.customer_class, a.salesrep_id, a.salesrep_name,
       a.address, a.city, a.state, a.zip, a.phone
FROM callprep.customer_all a
WHERE a.customer_id IN (SELECT customer_id FROM callprep.scope_customer);

-- 003 revoked customer_all from callprep_ro and granted customer; CREATE OR REPLACE keeps existing grants.
