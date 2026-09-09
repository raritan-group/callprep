// The run_select guard is the only thing between the model's free-form SQL and the database (the role grants are the
// second line). These are pure, no database.

namespace CallPrep.Tests.Unit;

public class SqlGuardTests
{
    [Theory]
    [InlineData("SELECT customer_id, customer_name FROM callprep.customer LIMIT 5")]
    [InlineData("select * from callprep.sales_line where customer_id::text = '10046'")]
    [InlineData("WITH t AS (SELECT customer_id FROM callprep.customer) SELECT * FROM t")]
    [InlineData("WITH a AS (SELECT 1 x), b AS (SELECT 2 y) SELECT * FROM a JOIN b ON true")]
    [InlineData("SELECT c.customer_name, SUM(s.extended_price) FROM callprep.customer c JOIN callprep.sales_line s ON s.customer_id = c.customer_id GROUP BY 1")]
    [InlineData("  \n SELECT 1")]
    [InlineData("SELECT * FROM callprep.class_pg_penetration WHERE customer_class = 'CONTRACTOR' ORDER BY penetration_pct DESC")]
    [InlineData("SELECT * FROM callprep.customer ORDER BY customer_id LIMIT 10 OFFSET 20")]
    [InlineData("SELECT customer_name, product_group_desc AS setting FROM callprep.customer_pg_12m")]
    public void Allows_read_only_selects_on_callprep(string sql)
    {
        Assert.Null(SqlGuard.Check(sql));
    }

    [Theory]
    [InlineData("", "empty")]
    [InlineData("   ", "empty")]
    [InlineData("SELECT 1; SELECT 2", "semicolon")]
    [InlineData("DELETE FROM callprep.audit_log", "SELECT or WITH")]
    [InlineData("INSERT INTO callprep.audit_log VALUES (1)", "SELECT or WITH")]
    [InlineData("EXPLAIN SELECT 1", "SELECT or WITH")]
    [InlineData("SELECT * FROM callprep.customer FOR UPDATE", "forbidden")]
    [InlineData("SELECT pg_sleep(30)", "forbidden")]
    [InlineData("SELECT * FROM callprep.customer -- hidden", "comments")]
    [InlineData("SELECT /* x */ 1", "comments")]
    [InlineData("SELECT * FROM public.customers", "not allowed")]
    [InlineData("SELECT * FROM customers", "not allowed")]
    [InlineData("SELECT * FROM callprep.customer c JOIN reports.sales s ON true", "not allowed")]
    [InlineData("SELECT * FROM pg_catalog.pg_tables", "not allowed")]
    [InlineData("SELECT * FROM information_schema.tables", "not allowed")]
    [InlineData("WITH t AS (SELECT 1) SELECT * FROM t JOIN other_table ON true", "not allowed")]
    [InlineData("SELECT set_config('callprep.login', 'x', true)", "forbidden")]
    [InlineData("SELECT * FROM callprep.customer WHERE 1=1 UNION SELECT * FROM users", "not allowed")]
    [InlineData("SELECT lo_import('/etc/passwd')", "forbidden")]
    [InlineData("SELECT pg_read_file('/etc/passwd')", "forbidden")]
    [InlineData("SELECT * FROM dblink('x','y')", "forbidden")]
    [InlineData("SELECT pg_catalog.set_config('callprep.login', 'it@raritangroup.com', true)", "forbidden")]
    [InlineData("WITH x AS (SELECT set_config('callprep.login','it@raritangroup.com',true) s) SELECT * FROM callprep.customer, x", "forbidden")]
    [InlineData("SELECT current_setting('callprep.login')", "forbidden")]
    [InlineData("SELECT 1 FROM callprep.customer WHERE pg_terminate_backend(1)", "forbidden")]
    public void Rejects_writes_other_schemas_and_tricks(string sql, string reasonFragment)
    {
        var err = SqlGuard.Check(sql);
        Assert.NotNull(err);
        Assert.Contains(reasonFragment, err, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Cte_names_are_case_insensitive()
    {
        Assert.Null(SqlGuard.Check("WITH Recent AS (SELECT * FROM callprep.sales_line) SELECT * FROM recent"));
    }

    [Fact]
    public void Keyword_check_is_whole_word_so_column_names_pass()
    {
        // 'updated_at' contains 'update'; 'created' contains 'create'. Word-boundary match must not trip on them.
        Assert.Null(SqlGuard.Check("SELECT updated_at, created_by FROM callprep.customer"));
    }

    [Fact]
    public void Trailing_semicolon_is_stripped_by_the_tool_before_checking()
    {
        // Tools.Invoke does .TrimEnd(';') first; the guard itself refuses semicolons. Both facts hold.
        Assert.NotNull(SqlGuard.Check("SELECT 1;"));
        Assert.Null(SqlGuard.Check("SELECT 1;".TrimEnd(';')));
    }
}
