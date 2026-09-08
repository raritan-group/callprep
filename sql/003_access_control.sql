-- Call Prep 003: per-user access control enforced INSIDE the database.
-- The API authenticates the user (Windows/domain account today, Entra later) and, on every connection it uses,
-- runs  SELECT set_config('callprep.login', '<sAMAccountName>', true)  inside a transaction. Every scoped view
-- below reads that setting; a rep sees only customers assigned to their P21 salesrep_id, manager/admin see all,
-- and an unknown or disabled login sees NOTHING. This holds for the model's free-form run_select tool too,
-- because callprep_ro has no SELECT on anything unscoped.
--
-- Roles:  rep      = own accounts only (salesrep_id required)
--         manager  = all accounts
--         admin    = all accounts + can edit user_access through the app
--
-- Additive + idempotent. Run as postgres:  sudo -u postgres psql -d reportsdb -v ON_ERROR_STOP=1 -f 003_access_control.sql

BEGIN;

-- ───────────────────────── 1. who may use the app ─────────────────────────
CREATE TABLE IF NOT EXISTS callprep.user_access (
  login        text PRIMARY KEY,                       -- domain sAMAccountName, lower-case, no DOMAIN\ prefix
  display_name text,
  role         text NOT NULL CHECK (role IN ('rep','manager','admin')),
  salesrep_id  text,                                   -- P21 salesrep id (customers.salesrep_id); required for role = rep
  enabled      boolean NOT NULL DEFAULT true,
  notes        text,
  updated_at   timestamptz NOT NULL DEFAULT now(),
  updated_by   text,
  CONSTRAINT user_access_rep_needs_id CHECK (role <> 'rep' OR salesrep_id IS NOT NULL)
);

-- ───────────────────────── 2. scope functions ─────────────────────────
CREATE OR REPLACE FUNCTION callprep.current_login() RETURNS text
LANGUAGE sql STABLE AS $$ SELECT lower(NULLIF(current_setting('callprep.login', true), '')) $$;

CREATE OR REPLACE FUNCTION callprep.sees_all() RETURNS boolean
LANGUAGE sql STABLE AS $$
  SELECT EXISTS (SELECT 1 FROM callprep.user_access u
                 WHERE u.login = callprep.current_login() AND u.enabled AND u.role IN ('manager','admin'))
$$;

CREATE OR REPLACE FUNCTION callprep.my_rep() RETURNS text
LANGUAGE sql STABLE AS $$
  SELECT u.salesrep_id FROM callprep.user_access u
  WHERE u.login = callprep.current_login() AND u.enabled AND u.role = 'rep'
$$;

CREATE OR REPLACE FUNCTION callprep.is_admin() RETURNS boolean
LANGUAGE sql STABLE AS $$
  SELECT EXISTS (SELECT 1 FROM callprep.user_access u
                 WHERE u.login = callprep.current_login() AND u.enabled AND u.role = 'admin')
$$;

-- Customers the current login may see (all of them for manager/admin; none for unknown/disabled).
CREATE OR REPLACE VIEW callprep.scope_customer AS
SELECT c.customer_id
FROM customers c
WHERE callprep.sees_all() OR c.salesrep_id::text = callprep.my_rep();

-- ───────────────────────── 3. unscoped internals (NOT readable by callprep_ro) ─────────────────────────
-- Used only by the SECURITY DEFINER lookalike functions, which need the whole population as the peer group.
CREATE OR REPLACE VIEW callprep.customer_all AS
SELECT c.customer_id, c.customer_name, c.customer_class, c.salesrep_id,
       NULLIF(TRIM(COALESCE(r.first_name,'') || ' ' || COALESCE(r.last_name,'')), '') AS salesrep_name
FROM customers c
LEFT JOIN salesreps r ON r.id::text = c.salesrep_id::text;

CREATE OR REPLACE VIEW callprep.customer_pg_12m_all AS
SELECT customer_id, product_group_id, MAX(product_group_desc) AS product_group_desc,
       SUM(extended_price) AS sales_12m, COUNT(*) AS lines_12m, MAX(invoice_date) AS last_invoice_date
FROM invoice_sales
WHERE invoice_date >= (CURRENT_DATE - INTERVAL '12 months')
  AND COALESCE(other_charge_item,'N') <> 'Y'
GROUP BY customer_id, product_group_id;

CREATE OR REPLACE VIEW callprep.customer_pg_ltd_all AS
SELECT customer_id, product_group_id, MAX(product_group_desc) AS product_group_desc,
       SUM(extended_price) AS sales_ltd, COUNT(*) AS lines_ltd,
       MIN(invoice_date) AS first_invoice_date, MAX(invoice_date) AS last_invoice_date
FROM invoice_sales
WHERE COALESCE(other_charge_item,'N') <> 'Y'
GROUP BY customer_id, product_group_id;

CREATE OR REPLACE VIEW callprep.customer_vector_all AS
SELECT customer_id, product_group_id, sales_12m,
       sales_12m / NULLIF(SQRT(SUM(sales_12m*sales_12m) OVER (PARTITION BY customer_id)),0) AS w
FROM callprep.customer_pg_12m_all WHERE sales_12m > 0;

REVOKE ALL ON callprep.customer_all, callprep.customer_pg_12m_all, callprep.customer_pg_ltd_all, callprep.customer_vector_all FROM callprep_ro;

-- ───────────────────────── 4. scoped views (what callprep_ro reads) ─────────────────────────
CREATE OR REPLACE VIEW callprep.customer AS
SELECT a.customer_id, a.customer_name, a.customer_class, a.salesrep_id, a.salesrep_name
FROM callprep.customer_all a
WHERE a.customer_id IN (SELECT customer_id FROM callprep.scope_customer);

CREATE OR REPLACE VIEW callprep.sales_line AS
SELECT invoice_no, line_no, order_no, invoice_date, ship_date,
       customer_id, customer_name, salesrep_id, taker,
       item_id, item_desc, qty_shipped, unit_price, extended_price,
       product_group_id, product_group_desc, supplier_id, supplier_name,
       location_id, county, ship_city, ship_state, ship_zip, other_charge_item
FROM invoice_sales s
WHERE s.customer_id IN (SELECT customer_id FROM callprep.scope_customer);

CREATE OR REPLACE VIEW callprep.open_quote_line AS
SELECT order_no, line_no, order_date, customer_id, customer_name, salesrep_id, taker, customer_po,
       item_id, qty_ordered, extended_price, product_group_id, product_group_desc,
       supplier_id, supplier_name, location_id, county, ship_city, ship_state, ship_zip, date_last_modified
FROM open_quotes q
WHERE q.customer_id IN (SELECT customer_id FROM callprep.scope_customer);

CREATE OR REPLACE VIEW callprep.open_order_line AS
SELECT order_no, line_no, order_date, customer_id, customer_name, salesrep_id, taker, customer_po,
       item_id, qty_ordered, qty_invoiced, extended_price, product_group_id, product_group_desc,
       supplier_id, supplier_name, location_id, county, ship_city, ship_state, ship_zip, date_last_modified
FROM open_orders o
WHERE o.customer_id IN (SELECT customer_id FROM callprep.scope_customer);

CREATE OR REPLACE VIEW callprep.cancelled_quote_line AS
SELECT order_no, line_no, order_date, customer_id, customer_name, salesrep_id, taker, customer_po,
       item_id, qty_ordered, extended_price, cancel_reason, cancel_reason_desc,
       product_group_id, product_group_desc, supplier_id, supplier_name,
       location_id, county, ship_city, ship_state, ship_zip, date_last_modified
FROM cancelled_quotes x
WHERE x.customer_id IN (SELECT customer_id FROM callprep.scope_customer);

CREATE OR REPLACE VIEW callprep.customer_pg_12m AS
SELECT customer_id, product_group_id, product_group_desc, sales_12m, lines_12m, last_invoice_date
FROM callprep.customer_pg_12m_all p
WHERE p.customer_id IN (SELECT customer_id FROM callprep.scope_customer);

CREATE OR REPLACE VIEW callprep.customer_pg_ltd AS
SELECT customer_id, product_group_id, product_group_desc, sales_ltd, lines_ltd, first_invoice_date, last_invoice_date
FROM callprep.customer_pg_ltd_all p
WHERE p.customer_id IN (SELECT customer_id FROM callprep.scope_customer);

CREATE OR REPLACE VIEW callprep.customer_vector AS
SELECT customer_id, product_group_id, sales_12m, w
FROM callprep.customer_vector_all v
WHERE v.customer_id IN (SELECT customer_id FROM callprep.scope_customer);

-- class_pg_penetration stays as defined in 001: market-segment aggregates, no customer names. Readable by any enabled login.
-- Gate it too so an unknown login gets nothing at all.
CREATE OR REPLACE VIEW callprep.class_pg_penetration AS
WITH active AS (
  SELECT c.customer_class, s.customer_id
  FROM invoice_sales s JOIN customers c ON c.customer_id = s.customer_id
  WHERE s.invoice_date >= (CURRENT_DATE - INTERVAL '12 months') AND c.customer_class IS NOT NULL
  GROUP BY 1,2
), class_size AS (
  SELECT customer_class, COUNT(*) AS active_customers FROM active GROUP BY 1
), buyers AS (
  SELECT c.customer_class, s.product_group_id, MAX(s.product_group_desc) AS product_group_desc,
         COUNT(DISTINCT s.customer_id) AS buyers_12m,
         SUM(s.extended_price) AS class_sales_12m
  FROM invoice_sales s JOIN customers c ON c.customer_id = s.customer_id
  WHERE s.invoice_date >= (CURRENT_DATE - INTERVAL '12 months') AND c.customer_class IS NOT NULL
    AND COALESCE(s.other_charge_item,'N') <> 'Y'
  GROUP BY 1,2
)
SELECT b.customer_class, b.product_group_id, b.product_group_desc, b.buyers_12m, z.active_customers,
       ROUND(100.0 * b.buyers_12m / NULLIF(z.active_customers,0), 1) AS penetration_pct,
       b.class_sales_12m,
       ROUND(b.class_sales_12m / NULLIF(b.buyers_12m,0), 2) AS avg_sales_per_buyer
FROM buyers b JOIN class_size z ON z.customer_class = b.customer_class
WHERE callprep.sees_all() OR callprep.my_rep() IS NOT NULL;

-- ───────────────────────── 5. lookalikes over the whole population (SECURITY DEFINER) ─────────────────────────
-- The peer group must be every active customer, not just the caller's book, or the comparison is meaningless.
-- Policy: a rep gets the peers' names, class and similarity; peers' sales figures are hidden unless the caller sees all.
-- Both functions return nothing for an unknown/disabled login, and nothing for a customer outside the caller's scope.
CREATE OR REPLACE FUNCTION callprep.lookalikes(p_customer text, p_n int DEFAULT 40)
RETURNS TABLE (customer_id text, customer_name text, customer_class text, similarity numeric, sales_12m numeric)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = callprep, public, pg_temp AS $$
  WITH me AS (
    SELECT v.product_group_id, v.w
    FROM callprep.customer_vector_all v
    WHERE v.customer_id::text = p_customer
      AND v.customer_id IN (SELECT customer_id FROM callprep.scope_customer)   -- caller must be allowed to see the subject
  )
  SELECT v.customer_id::text, c.customer_name, c.customer_class,
         ROUND(SUM(v.w * me.w)::numeric, 3) AS similarity,
         CASE WHEN callprep.sees_all() THEN ROUND(SUM(v.sales_12m)::numeric) END AS sales_12m
  FROM callprep.customer_vector_all v JOIN me ON me.product_group_id = v.product_group_id
  JOIN callprep.customer_all c ON c.customer_id = v.customer_id
  WHERE v.customer_id::text <> p_customer
  GROUP BY v.customer_id, c.customer_name, c.customer_class
  HAVING SUM(v.w * me.w) >= 0.3
  ORDER BY similarity DESC LIMIT p_n
$$;

CREATE OR REPLACE FUNCTION callprep.lookalike_gap(p_customer text, p_n int DEFAULT 40, p_min_pct numeric DEFAULT 15)
RETURNS TABLE (product_group_id text, product_group_desc text, lookalike_buyers int, lookalikes int, penetration_pct numeric,
               avg_sales_per_buyer numeric, my_lifetime_sales numeric, my_last_purchase date, status text)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = callprep, public, pg_temp AS $$
  WITH la AS (SELECT customer_id FROM callprep.lookalikes(p_customer, p_n)),
       n  AS (SELECT COUNT(*) AS cnt FROM la),
       buy AS (SELECT p.product_group_id, MAX(p.product_group_desc) AS product_group_desc,
                      COUNT(DISTINCT p.customer_id) AS buyers, SUM(p.sales_12m) AS sales
               FROM callprep.customer_pg_12m_all p JOIN la ON la.customer_id = p.customer_id::text
               GROUP BY p.product_group_id)
  SELECT b.product_group_id::text, b.product_group_desc, b.buyers::int, n.cnt::int,
         ROUND(100.0 * b.buyers / NULLIF(n.cnt,0), 1),
         ROUND(b.sales / NULLIF(b.buyers,0)),
         ROUND(COALESCE(l.sales_ltd,0)), l.last_invoice_date,
         CASE WHEN l.customer_id IS NULL THEN 'never bought' ELSE 'bought before, not in last 12 months' END
  FROM buy b CROSS JOIN n
  LEFT JOIN callprep.customer_pg_12m_all m ON m.customer_id::text = p_customer AND m.product_group_id = b.product_group_id
  LEFT JOIN callprep.customer_pg_ltd_all l ON l.customer_id::text = p_customer AND l.product_group_id = b.product_group_id
  WHERE m.customer_id IS NULL AND 100.0 * b.buyers / NULLIF(n.cnt,0) >= p_min_pct
  ORDER BY 5 DESC, 6 DESC LIMIT 40
$$;

ALTER FUNCTION callprep.lookalikes(text,int) OWNER TO postgres;
ALTER FUNCTION callprep.lookalike_gap(text,int,numeric) OWNER TO postgres;
REVOKE ALL ON FUNCTION callprep.lookalikes(text,int), callprep.lookalike_gap(text,int,numeric) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION callprep.lookalikes(text,int), callprep.lookalike_gap(text,int,numeric) TO callprep_ro;

-- ───────────────────────── 6. user_access editing: only an enabled admin (via the app) or postgres itself ─────────────────────────
CREATE OR REPLACE FUNCTION callprep.user_access_guard() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
  IF current_user = 'callprep_ro' AND NOT callprep.is_admin() THEN
    RAISE EXCEPTION 'callprep: only an admin may change user_access (login=%)', COALESCE(callprep.current_login(), '<none>')
      USING ERRCODE = 'insufficient_privilege';
  END IF;
  NEW.updated_at := now();
  NEW.updated_by := COALESCE(callprep.current_login(), current_user);
  NEW.login := lower(NEW.login);
  RETURN NEW;
END $$;
DROP TRIGGER IF EXISTS user_access_guard ON callprep.user_access;
CREATE TRIGGER user_access_guard BEFORE INSERT OR UPDATE ON callprep.user_access
FOR EACH ROW EXECUTE FUNCTION callprep.user_access_guard();

-- Reps list for the admin screen (ids + names, nothing else)
CREATE OR REPLACE VIEW callprep.salesrep AS
SELECT r.id::text AS salesrep_id,
       NULLIF(TRIM(COALESCE(r.first_name,'') || ' ' || COALESCE(r.last_name,'')), '') AS salesrep_name,
       (SELECT COUNT(*) FROM customers c WHERE c.salesrep_id::text = r.id::text) AS customers
FROM salesreps r
WHERE callprep.is_admin();

-- ───────────────────────── 7. grants ─────────────────────────
GRANT SELECT ON callprep.user_access, callprep.scope_customer, callprep.salesrep TO callprep_ro;
GRANT INSERT, UPDATE ON callprep.user_access TO callprep_ro;      -- trigger above enforces admin; no DELETE (disable instead)
GRANT SELECT ON callprep.customer, callprep.sales_line, callprep.open_quote_line, callprep.open_order_line,
                callprep.cancelled_quote_line, callprep.customer_pg_12m, callprep.customer_pg_ltd,
                callprep.customer_vector, callprep.class_pg_penetration TO callprep_ro;
-- default privileges from 001 would hand SELECT on new views to callprep_ro; the internals were revoked explicitly above.

-- ───────────────────────── 8. seed (2026-09-04) ─────────────────────────
-- Domain accounts matched to P21 salesreps by name where unambiguous. Douglas Miller (1025), John Convery (20992) and
-- HOUSE ACCOUNT (1032) have no matching domain account and are left unmapped. Everything here is editable from the app.
INSERT INTO callprep.user_access (login, display_name, role, salesrep_id, enabled, notes) VALUES
  ('callprep-service', 'Call Prep service',  'manager', NULL,    true, 'internal: whisper vocabulary at startup; never a web identity'),
  ('it',               'Information Technology', 'admin', NULL,  true, 'IT workstation account'),
  ('pfernandes',       'Paul Fernandes',     'admin',   NULL,    true, NULL),
  ('jcook',            'Joel Cook',          'manager', NULL,    true, 'sees all accounts (POC tester)'),
  ('brichardson',      'Bill Richardson',    'manager', NULL,    true, NULL),
  ('jrichardson',      'Jim Richardson',     'manager', NULL,    true, NULL),
  ('trichardson',      'Tom Richardson',     'manager', NULL,    true, NULL),
  ('ddickman',         'Doug Dickman',       'rep',     '25505', true, 'P21 rep DOUG DICKMAN'),
  ('ftenerovich',      'Frank Tenerovich',   'rep',     '27129', true, 'P21 rep Frank Tenerovich'),
  ('kperry',           'Kelly Perry',        'rep',     '16930', true, 'P21 rep KELLY PERRY'),
  ('tennis',           'Tim Ennis',          'rep',     '1023',  true, 'P21 rep TIM ENNIS'),
  ('prichardson',      'Patrick Richardson', 'rep',     '1007',  true, 'P21 rep Patrick Richardson')
ON CONFLICT (login) DO NOTHING;

COMMIT;
