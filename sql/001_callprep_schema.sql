-- Call Prep: read-only semantic layer over reportsdb (Hetzner) for the sales call-prep assistant.
-- Additive only. Views are owned by postgres; role callprep_ro can SELECT them and INSERT audit rows, nothing else.
-- Deliberately EXCLUDED: cogs_amount (cost/margin), credit/AR, other reps' commission data.

CREATE SCHEMA IF NOT EXISTS callprep;

-- Customers with rep name and market class
CREATE OR REPLACE VIEW callprep.customer AS
SELECT c.customer_id,
       c.customer_name,
       c.customer_class,
       c.salesrep_id,
       NULLIF(TRIM(COALESCE(r.first_name,'') || ' ' || COALESCE(r.last_name,'')), '') AS salesrep_name
FROM customers c
LEFT JOIN salesreps r ON r.id::text = c.salesrep_id::text;

-- Invoiced sales lines (no cost columns)
CREATE OR REPLACE VIEW callprep.sales_line AS
SELECT invoice_no, line_no, order_no, invoice_date, ship_date,
       customer_id, customer_name, salesrep_id, taker,
       item_id, item_desc, qty_shipped, unit_price, extended_price,
       product_group_id, product_group_desc, supplier_id, supplier_name,
       location_id, county, ship_city, ship_state, ship_zip, other_charge_item
FROM invoice_sales;

CREATE OR REPLACE VIEW callprep.open_quote_line AS
SELECT order_no, line_no, order_date, customer_id, customer_name, salesrep_id, taker, customer_po,
       item_id, qty_ordered, extended_price, product_group_id, product_group_desc,
       supplier_id, supplier_name, location_id, county, ship_city, ship_state, ship_zip, date_last_modified
FROM open_quotes;

CREATE OR REPLACE VIEW callprep.open_order_line AS
SELECT order_no, line_no, order_date, customer_id, customer_name, salesrep_id, taker, customer_po,
       item_id, qty_ordered, qty_invoiced, extended_price, product_group_id, product_group_desc,
       supplier_id, supplier_name, location_id, county, ship_city, ship_state, ship_zip, date_last_modified
FROM open_orders;

CREATE OR REPLACE VIEW callprep.cancelled_quote_line AS
SELECT order_no, line_no, order_date, customer_id, customer_name, salesrep_id, taker, customer_po,
       item_id, qty_ordered, extended_price, cancel_reason, cancel_reason_desc,
       product_group_id, product_group_desc, supplier_id, supplier_name,
       location_id, county, ship_city, ship_state, ship_zip, date_last_modified
FROM cancelled_quotes;

-- Customer x product group, trailing 12 months
CREATE OR REPLACE VIEW callprep.customer_pg_12m AS
SELECT customer_id, product_group_id, MAX(product_group_desc) AS product_group_desc,
       SUM(extended_price) AS sales_12m, COUNT(*) AS lines_12m, MAX(invoice_date) AS last_invoice_date
FROM invoice_sales
WHERE invoice_date >= (CURRENT_DATE - INTERVAL '12 months')
  AND COALESCE(other_charge_item,'N') <> 'Y'
GROUP BY customer_id, product_group_id;

-- Customer x product group, lifetime (history starts 2022-03-30)
CREATE OR REPLACE VIEW callprep.customer_pg_ltd AS
SELECT customer_id, product_group_id, MAX(product_group_desc) AS product_group_desc,
       SUM(extended_price) AS sales_ltd, COUNT(*) AS lines_ltd,
       MIN(invoice_date) AS first_invoice_date, MAX(invoice_date) AS last_invoice_date
FROM invoice_sales
WHERE COALESCE(other_charge_item,'N') <> 'Y'
GROUP BY customer_id, product_group_id;

-- Market class x product group penetration, trailing 12 months
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
FROM buyers b JOIN class_size z ON z.customer_class = b.customer_class;

-- Audit log: every question, tool call, SQL statement, answer
CREATE TABLE IF NOT EXISTS callprep.audit_log (
  id          bigserial PRIMARY KEY,
  ts          timestamptz NOT NULL DEFAULT now(),
  user_name   text,
  session_id  text,
  kind        text NOT NULL,          -- question | tool | sql | answer | error
  payload     jsonb,
  ms          integer,
  row_count   integer
);
CREATE INDEX IF NOT EXISTS audit_log_ts_idx ON callprep.audit_log (ts);

-- Role: login, read-only, 30s cap, no access outside the callprep schema
DO $$ BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'callprep_ro') THEN
    CREATE ROLE callprep_ro LOGIN;
  END IF;
END $$;
ALTER ROLE callprep_ro SET statement_timeout = '30s';
ALTER ROLE callprep_ro SET search_path = callprep;
REVOKE ALL ON SCHEMA public FROM callprep_ro;
GRANT USAGE ON SCHEMA callprep TO callprep_ro;
GRANT SELECT ON ALL TABLES IN SCHEMA callprep TO callprep_ro;
GRANT INSERT ON callprep.audit_log TO callprep_ro;
GRANT USAGE, SELECT ON SEQUENCE callprep.audit_log_id_seq TO callprep_ro;
ALTER DEFAULT PRIVILEGES IN SCHEMA callprep GRANT SELECT ON TABLES TO callprep_ro;
