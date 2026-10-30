using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using MySqlConnector;
using Xunit;

namespace IPRO.IntegrationTests;

// 619: three tests of 614's gate failed with no assertion involved: the local database took more than 30 seconds
// to answer, behind another test's drop of its database. The harness waits longer now.
public class TestDatabaseCleanup619Tests
{
    [Fact]
    public async Task A_test_waits_out_a_slow_database_rather_than_failing()
    {
        await using var testDb = await TestDatabase.CreateAsync(applyLedgerGuard: false);
        await using var db = testDb.CreateContext();
        Assert.Equal(300u, new MySqlConnectionStringBuilder(db.Database.GetConnectionString()).DefaultCommandTimeout);
    }

    [Fact]
    public void The_leftovers_a_stopped_run_leaves_are_dropped_by_their_exact_name_and_age_only()
    {
        var harness = Lock583.Read(@"tests\IPRO.IntegrationTests\TestDatabase.cs");
        Assert.Contains("table_schema REGEXP '^ipro_test_[0-9a-f]{20}$'", harness);
        Assert.Contains("HAVING MAX(create_time) < NOW() - INTERVAL 1 DAY", harness);
        Assert.Contains("await Sweep.Value;", harness);
    }

    [Fact]
    public void Edge_is_asked_to_close_itself_before_its_connection_goes()
    {
        var harness = Lock583.Read(@"tests\IPRO.IntegrationTests\Accessibility\HeadlessEdge.cs");
        Assert.True(harness.IndexOf("SendAsync(\"Browser.close\"", System.StringComparison.Ordinal)
            < harness.IndexOf("_stop.Cancel();", System.StringComparison.Ordinal));
    }
}
