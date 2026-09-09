-- 005_rep_list.sql — "This week" list: hard-coded triggers over the SCOPED views (so a rep only ever sees their book),
-- thresholds in a table so sales can turn the knobs without a deploy. Apply as postgres. Additive, re-runnable.
--
-- Triggers (Proton's five, on our data):
--   list_stale_quotes   open quotes between N and M days old, above a minimum value
--   list_going_quiet    account's gap since last invoice exceeds its OWN usual gap × factor (cadence, not "90 days")
--   list_reorder_due    same cadence math per customer × product group
--   list_new_accounts   first invoice inside the last N days
--   list_category_gaps  lookalike_gap() for the top accounts by 12-month sales (behavior-based peers, see 002/003)

CREATE TABLE IF NOT EXISTS callprep.list_settings (
  key   text PRIMARY KEY,
  value numeric NOT NULL,
  note  text
);
INSERT INTO callprep.list_settings (key, value, note) VALUES
  ('quote_min_age_days',      7,    'ignore quotes younger than this (still fresh)'),
  ('quote_max_age_days',      90,   'ignore quotes older than this (P21 never closes quotes; treat as dead)'),
  ('quote_min_value',         2500, 'minimum quote value worth a follow-up call'),
  ('quiet_min_invoices_12m',  6,    'account needs at least this many invoice days in 12 months to have a cadence'),
  ('quiet_overdue_factor',    1.5,  'silent for more than usual-gap × this = going quiet'),
  ('quiet_min_days',          21,   'never flag an account silent for fewer days than this'),
  ('quiet_max_days',          180,  'silent longer than this is lost, not quiet; leave it to the gap/recapture view'),
  ('reorder_min_orders',      4,    'customer × product group needs this many invoice days in 24 months to have a cadence'),
  ('reorder_min_days',        14,   'never flag a group overdue by fewer days than this (a 3-day cadence is noise at day 6)'),
  ('reorder_overdue_factor',  1.5,  'overdue when gap since last buy exceeds usual gap × this'),
  ('reorder_dead_factor',     4,    'past usual gap × this it is a lapsed group, not a reorder (category gap covers it)'),
  ('reorder_min_value_12m',   2000, 'minimum 12-month sales in that product group to bother the rep'),
  ('new_account_days',        45,   'first invoice within this many days = new account'),
  ('gap_top_accounts',        10,   'how many of the biggest accounts get a lookalike gap check'),
  ('gap_min_pct',             25,   'minimum share of lookalikes buying the group')
ON CONFLICT (key) DO NOTHING;

CREATE OR REPLACE FUNCTION callprep.setting(k text) RETURNS numeric
LANGUAGE sql STABLE AS $$ SELECT value FROM callprep.list_settings WHERE key = k $$;

-- 1. Stale quotes
CREATE OR REPLACE VIEW callprep.list_stale_quotes AS
SELECT q.customer_id, MAX(q.customer_name) AS customer_name, q.order_no,
       MIN(q.order_date) AS quote_date, (CURRENT_DATE - MIN(q.order_date))::int AS age_days,
       ROUND(SUM(q.extended_price)) AS quote_value, COUNT(*) AS lines, MAX(q.taker) AS taker, MAX(q.customer_po) AS customer_po,
       array_agg(DISTINCT q.product_group_desc) FILTER (WHERE q.product_group_desc IS NOT NULL) AS product_groups
FROM callprep.open_quote_line q
WHERE q.order_date BETWEEN CURRENT_DATE - callprep.setting('quote_max_age_days')::int
                       AND CURRENT_DATE - callprep.setting('quote_min_age_days')::int
GROUP BY q.customer_id, q.order_no
HAVING SUM(q.extended_price) >= callprep.setting('quote_min_value');

-- 2. Going quiet (account cadence)
CREATE OR REPLACE VIEW callprep.list_going_quiet AS
WITH d AS (
  SELECT customer_id, MAX(customer_name) AS customer_name, invoice_date
  FROM callprep.sales_line
  WHERE invoice_date >= CURRENT_DATE - INTERVAL '24 months'
  GROUP BY customer_id, invoice_date),
g AS (
  SELECT customer_id, customer_name, invoice_date,
         invoice_date - LAG(invoice_date) OVER (PARTITION BY customer_id ORDER BY invoice_date) AS gap
  FROM d),
s AS (
  SELECT customer_id, MAX(customer_name) AS customer_name,
         COUNT(*) FILTER (WHERE invoice_date >= CURRENT_DATE - INTERVAL '12 months') AS invoice_days_12m,
         percentile_cont(0.5) WITHIN GROUP (ORDER BY gap) AS med_gap,
         MAX(invoice_date) AS last_invoice
  FROM g GROUP BY customer_id)
SELECT s.customer_id, s.customer_name, s.last_invoice,
       (CURRENT_DATE - s.last_invoice)::int AS days_silent,
       ROUND(s.med_gap::numeric) AS usual_gap_days,
       s.invoice_days_12m,
       (SELECT ROUND(COALESCE(SUM(p.sales_12m),0)) FROM callprep.customer_pg_12m p WHERE p.customer_id = s.customer_id) AS sales_12m
FROM s
WHERE s.invoice_days_12m >= callprep.setting('quiet_min_invoices_12m')
  AND (CURRENT_DATE - s.last_invoice) >= GREATEST(callprep.setting('quiet_min_days'), s.med_gap * callprep.setting('quiet_overdue_factor'))
  AND (CURRENT_DATE - s.last_invoice) <= callprep.setting('quiet_max_days');

-- 3. Reorder due (customer × product group cadence)
CREATE OR REPLACE VIEW callprep.list_reorder_due AS
WITH d AS (
  SELECT customer_id, MAX(customer_name) AS customer_name, product_group_id, MAX(product_group_desc) AS product_group_desc,
         invoice_date, SUM(extended_price) AS v
  FROM callprep.sales_line
  WHERE invoice_date >= CURRENT_DATE - INTERVAL '24 months' AND product_group_id IS NOT NULL
  GROUP BY customer_id, product_group_id, invoice_date),
g AS (
  SELECT *, invoice_date - LAG(invoice_date) OVER (PARTITION BY customer_id, product_group_id ORDER BY invoice_date) AS gap
  FROM d),
s AS (
  SELECT customer_id, MAX(customer_name) AS customer_name, product_group_id, MAX(product_group_desc) AS product_group_desc,
         COUNT(*) AS buy_days, percentile_cont(0.5) WITHIN GROUP (ORDER BY gap) AS med_gap, MAX(invoice_date) AS last_buy,
         COALESCE(SUM(v) FILTER (WHERE invoice_date >= CURRENT_DATE - INTERVAL '12 months'), 0) AS sales_12m
  FROM g GROUP BY customer_id, product_group_id)
SELECT s.customer_id, s.customer_name, s.product_group_id, s.product_group_desc, s.last_buy,
       (CURRENT_DATE - s.last_buy)::int AS days_since, ROUND(s.med_gap::numeric) AS usual_gap_days,
       ROUND(s.sales_12m) AS sales_12m, s.buy_days
FROM s
WHERE s.buy_days >= callprep.setting('reorder_min_orders')
  AND s.sales_12m >= callprep.setting('reorder_min_value_12m')
  AND (CURRENT_DATE - s.last_buy) >= GREATEST(callprep.setting('reorder_min_days'), s.med_gap * callprep.setting('reorder_overdue_factor'))
  AND (CURRENT_DATE - s.last_buy) <= s.med_gap * callprep.setting('reorder_dead_factor');

-- 4. New accounts
CREATE OR REPLACE VIEW callprep.list_new_accounts AS
SELECT customer_id, MAX(customer_name) AS customer_name, MIN(invoice_date) AS first_invoice,
       (CURRENT_DATE - MIN(invoice_date))::int AS days_ago, ROUND(SUM(extended_price)) AS sales_to_date,
       COUNT(DISTINCT invoice_no) AS invoices,
       array_agg(DISTINCT product_group_desc) FILTER (WHERE product_group_desc IS NOT NULL) AS product_groups
FROM callprep.sales_line
GROUP BY customer_id
HAVING MIN(invoice_date) >= CURRENT_DATE - callprep.setting('new_account_days')::int;

-- 5. Category gaps for the biggest accounts in scope (one row per account: the highest-value gap)
CREATE OR REPLACE FUNCTION callprep.list_category_gaps(p_top int DEFAULT NULL, p_min_pct numeric DEFAULT NULL)
RETURNS TABLE (customer_id text, customer_name text, sales_12m numeric, product_group_id text, product_group_desc text,
               penetration_pct numeric, avg_sales_per_buyer numeric, my_last_purchase date, status text, other_gaps int)
LANGUAGE sql STABLE AS $$
  WITH top AS (
    SELECT c.customer_id::text AS customer_id, c.customer_name, ROUND(SUM(p.sales_12m)) AS sales_12m
    FROM callprep.customer c JOIN callprep.customer_pg_12m p ON p.customer_id = c.customer_id
    GROUP BY c.customer_id, c.customer_name
    ORDER BY SUM(p.sales_12m) DESC
    LIMIT COALESCE(p_top, callprep.setting('gap_top_accounts')::int)),
  gaps AS (
    SELECT t.customer_id, t.customer_name, t.sales_12m, g.*,
           ROW_NUMBER() OVER (PARTITION BY t.customer_id ORDER BY (g.status LIKE 'bought before%') DESC, g.avg_sales_per_buyer DESC NULLS LAST) AS rn,   -- recapture beats cold pitch
           COUNT(*) OVER (PARTITION BY t.customer_id) AS n
    FROM top t CROSS JOIN LATERAL callprep.lookalike_gap(t.customer_id, 40, COALESCE(p_min_pct, callprep.setting('gap_min_pct'))) g)
  SELECT customer_id, customer_name, sales_12m, product_group_id, product_group_desc,
         penetration_pct, avg_sales_per_buyer, my_last_purchase, status, (n - 1)::int
  FROM gaps WHERE rn = 1
  ORDER BY avg_sales_per_buyer DESC NULLS LAST
$$;

GRANT SELECT ON callprep.list_settings, callprep.list_stale_quotes, callprep.list_going_quiet,
                callprep.list_reorder_due, callprep.list_new_accounts TO callprep_ro;
GRANT EXECUTE ON FUNCTION callprep.setting(text), callprep.list_category_gaps(int, numeric) TO callprep_ro;
-- admins may tune thresholds from the app later; the guard trigger pattern from 003 applies if that is added
