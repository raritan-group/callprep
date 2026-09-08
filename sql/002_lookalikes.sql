-- Behavior-based lookalikes: cosine similarity of product-group sales vectors (trailing 12 months), all active customers.
CREATE OR REPLACE VIEW callprep.customer_vector AS
SELECT customer_id, product_group_id, sales_12m,
       sales_12m / NULLIF(SQRT(SUM(sales_12m*sales_12m) OVER (PARTITION BY customer_id)),0) AS w
FROM callprep.customer_pg_12m WHERE sales_12m > 0;

-- Function: top-N lookalikes for one customer (cosine on normalized vectors)
CREATE OR REPLACE FUNCTION callprep.lookalikes(p_customer text, p_n int DEFAULT 40)
RETURNS TABLE (customer_id text, customer_name text, customer_class text, similarity numeric, sales_12m numeric)
LANGUAGE sql STABLE AS $$
  WITH me AS (SELECT product_group_id, w FROM callprep.customer_vector WHERE customer_id::text = p_customer)
  SELECT v.customer_id::text, c.customer_name, c.customer_class,
         ROUND(SUM(v.w * me.w)::numeric, 3) AS similarity,
         ROUND(SUM(v.sales_12m)::numeric) AS sales_12m
  FROM callprep.customer_vector v JOIN me ON me.product_group_id = v.product_group_id
  JOIN callprep.customer c ON c.customer_id = v.customer_id
  WHERE v.customer_id::text <> p_customer
  GROUP BY v.customer_id, c.customer_name, c.customer_class
  HAVING SUM(v.w * me.w) >= 0.3
  ORDER BY similarity DESC LIMIT p_n
$$;

-- Function: gaps vs lookalikes — product groups bought by >= p_min_pct of the lookalike set that the customer does not buy (12m)
CREATE OR REPLACE FUNCTION callprep.lookalike_gap(p_customer text, p_n int DEFAULT 40, p_min_pct numeric DEFAULT 15)
RETURNS TABLE (product_group_id text, product_group_desc text, lookalike_buyers int, lookalikes int, penetration_pct numeric,
               avg_sales_per_buyer numeric, my_lifetime_sales numeric, my_last_purchase date, status text)
LANGUAGE sql STABLE AS $$
  WITH la AS (SELECT customer_id FROM callprep.lookalikes(p_customer, p_n)),
       n  AS (SELECT COUNT(*) AS cnt FROM la),
       buy AS (SELECT p.product_group_id, MAX(p.product_group_desc) AS product_group_desc,
                      COUNT(DISTINCT p.customer_id) AS buyers, SUM(p.sales_12m) AS sales
               FROM callprep.customer_pg_12m p JOIN la ON la.customer_id = p.customer_id::text
               GROUP BY p.product_group_id)
  SELECT b.product_group_id::text, b.product_group_desc, b.buyers::int, n.cnt::int,
         ROUND(100.0 * b.buyers / NULLIF(n.cnt,0), 1),
         ROUND(b.sales / NULLIF(b.buyers,0)),
         ROUND(COALESCE(l.sales_ltd,0)), l.last_invoice_date,
         CASE WHEN l.customer_id IS NULL THEN 'never bought' ELSE 'bought before, not in last 12 months' END
  FROM buy b CROSS JOIN n
  LEFT JOIN callprep.customer_pg_12m m ON m.customer_id::text = p_customer AND m.product_group_id = b.product_group_id
  LEFT JOIN callprep.customer_pg_ltd l ON l.customer_id::text = p_customer AND l.product_group_id = b.product_group_id
  WHERE m.customer_id IS NULL AND 100.0 * b.buyers / NULLIF(n.cnt,0) >= p_min_pct
  ORDER BY 5 DESC, 6 DESC LIMIT 40
$$;

GRANT SELECT ON callprep.customer_vector TO callprep_ro;
GRANT EXECUTE ON FUNCTION callprep.lookalikes(text,int) TO callprep_ro;
GRANT EXECUTE ON FUNCTION callprep.lookalike_gap(text,int,numeric) TO callprep_ro;
