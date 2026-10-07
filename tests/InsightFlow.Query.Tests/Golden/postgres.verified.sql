-- case: bar_sum_revenue_by_channel
SELECT
  t0."channel" AS "x",
  SUM(t0."revenue") AS "y"
FROM "ds0" AS t0
GROUP BY t0."channel"
ORDER BY "x" ASC, "y" ASC
LIMIT 5001;
-- sources: ds0=5a1e5000-0000-4000-8000-000000000001
-- params: (none)

-- case: line_monthly_revenue_by_region_join
SELECT
  CAST(date_trunc('month', t0."order_date") AS DATE) AS "x",
  SUM(t0."revenue") AS "y",
  t1."region" AS "color"
FROM "ds0" AS t0
LEFT JOIN "ds1" AS t1 ON t0."store_id" = t1."store_id"
GROUP BY CAST(date_trunc('month', t0."order_date") AS DATE), t1."region"
ORDER BY "x" ASC, "color" ASC, "y" ASC
LIMIT 5001;
-- sources: ds0=5a1e5000-0000-4000-8000-000000000001, ds1=5a1e5000-0000-4000-8000-000000000003
-- params: (none)

-- case: pie_units_by_category_with_label
SELECT
  SUM(t0."quantity") AS "y",
  t1."category" AS "color",
  t1."category" AS "label"
FROM "ds0" AS t0
LEFT JOIN "ds1" AS t1 ON t0."product_id" = t1."product_id"
GROUP BY t1."category"
ORDER BY "color" ASC, "label" ASC, "y" ASC
LIMIT 5001;
-- sources: ds0=5a1e5000-0000-4000-8000-000000000001, ds1=5a1e5000-0000-4000-8000-000000000002
-- params: (none)

-- case: table_raw_rows
SELECT
  t0."order_id" AS "x",
  t0."channel" AS "y",
  t0."revenue" AS "label"
FROM "ds0" AS t0
ORDER BY "x" ASC, "y" ASC, "label" ASC
LIMIT 101;
-- sources: ds0=5a1e5000-0000-4000-8000-000000000001
-- params: (none)

-- case: filters_equals_in_range_null
SELECT
  t1."country" AS "x",
  SUM(t0."revenue") AS "y"
FROM "ds0" AS t0
LEFT JOIN "ds1" AS t1 ON t0."store_id" = t1."store_id"
WHERE t0."channel" = @p0
  AND t1."country" IN (@p1, @p2)
  AND (t0."revenue" >= @p3 AND t0."revenue" < @p4)
  AND t0."is_returned" IS NOT NULL
  AND (t1."store_name" <> @p5 OR t1."store_name" IS NULL)
GROUP BY t1."country"
ORDER BY "x" ASC, "y" ASC
LIMIT 5001;
-- sources: ds0=5a1e5000-0000-4000-8000-000000000001, ds1=5a1e5000-0000-4000-8000-000000000003
-- params: p0 = 'Online' (String), p1 = 'Germany' (String), p2 = 'France' (String), p3 = 10 (Decimal), p4 = 2500.5 (Decimal), p5 = 'Store 001' (String)

-- case: in_filters_with_nulls
SELECT
  t1."region" AS "x",
  COUNT(t0."order_id") AS "y"
FROM "ds0" AS t0
LEFT JOIN "ds1" AS t1 ON t0."store_id" = t1."store_id"
LEFT JOIN "ds2" AS t2 ON t0."product_id" = t2."product_id"
WHERE (t1."region" IN (@p0) OR t1."region" IS NULL)
  AND (t2."category" IS NOT NULL AND t2."category" NOT IN (@p1))
  AND (t0."channel" NOT IN (@p2) OR t0."channel" IS NULL)
GROUP BY t1."region"
ORDER BY "x" ASC, "y" ASC
LIMIT 5001;
-- sources: ds0=5a1e5000-0000-4000-8000-000000000001, ds1=5a1e5000-0000-4000-8000-000000000003, ds2=5a1e5000-0000-4000-8000-000000000002
-- params: p0 = 'North' (String), p1 = 'Toys' (String), p2 = 'Online' (String)

-- case: relative_last_3_months_on_date
SELECT
  CAST(date_trunc('week', t0."order_date") AS DATE) AS "x",
  SUM(t0."revenue") AS "y"
FROM "ds0" AS t0
WHERE (t0."order_date" >= @p0 AND t0."order_date" < @p1)
GROUP BY CAST(date_trunc('week', t0."order_date") AS DATE)
ORDER BY "x" ASC, "y" ASC
LIMIT 5001;
-- sources: ds0=5a1e5000-0000-4000-8000-000000000001
-- params: p0 = 2026-07-01 (DateOnly), p1 = 2026-10-01 (DateOnly)

-- case: top5_products_by_revenue_online
SELECT
  t1."product_name" AS "x",
  SUM(t0."revenue") AS "y"
FROM "ds0" AS t0
LEFT JOIN "ds1" AS t1 ON t0."product_id" = t1."product_id"
WHERE t0."channel" = @p0
  AND t1."product_name" IN (SELECT t1."product_name" FROM "ds0" AS t0 LEFT JOIN "ds1" AS t1 ON t0."product_id" = t1."product_id" WHERE t0."channel" = @p0 GROUP BY t1."product_name" ORDER BY SUM(t0."revenue") DESC, t1."product_name" ASC LIMIT 5)
GROUP BY t1."product_name"
ORDER BY "y" DESC, "x" ASC
LIMIT 5001;
-- sources: ds0=5a1e5000-0000-4000-8000-000000000001, ds1=5a1e5000-0000-4000-8000-000000000002
-- params: p0 = 'Online' (String)

-- case: count_distinct_orders_by_quarter
SELECT
  CAST(date_trunc('quarter', t0."order_date") AS DATE) AS "x",
  COUNT(DISTINCT t0."order_id") AS "y"
FROM "ds0" AS t0
GROUP BY CAST(date_trunc('quarter', t0."order_date") AS DATE)
ORDER BY "x" ASC, "y" ASC
LIMIT 5001;
-- sources: ds0=5a1e5000-0000-4000-8000-000000000001
-- params: (none)

-- case: scatter_raw_points_sized
SELECT
  t0."revenue" AS "x",
  t0."cost" AS "y",
  t0."quantity" AS "size"
FROM "ds0" AS t0
ORDER BY "x" ASC, "y" ASC, "size" ASC
LIMIT 1001;
-- sources: ds0=5a1e5000-0000-4000-8000-000000000001
-- params: (none)

-- case: single_total_kpi
SELECT
  SUM(t0."revenue") AS "y"
FROM "ds0" AS t0
ORDER BY "y" ASC
LIMIT 5001;
-- sources: ds0=5a1e5000-0000-4000-8000-000000000001
-- params: (none)
