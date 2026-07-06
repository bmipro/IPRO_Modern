using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IPRO.DataAccess;

public class IPRODbContextFactory : IDesignTimeDbContextFactory<IPRODbContext>
{
    public IPRODbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<IPRODbContext>()
            .UseMySql(
                "server=localhost;port=3306;database=ipro_design_time;user=root;password=password;",
                new MySqlServerVersion(new Version(8, 0, 36)))
            .Options;

        return new IPRODbContext(options);
    }
}
