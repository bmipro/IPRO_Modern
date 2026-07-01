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
                "Server=localhost;Database=ipro_design_time;User=root;Password=password;",
                new MySqlServerVersion(new Version(8, 0, 0)))
            .Options;

        return new IPRODbContext(options);
    }
}
