using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IPRO.DataAccess;

public class IPRODbContextFactory : IDesignTimeDbContextFactory<IPRODbContext>
{
    public IPRODbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__DefaultConnection")
            ?? "Server=localhost;Database=ipro_design_time;User=root;Password=password;";

        var optionsBuilder = new DbContextOptionsBuilder<IPRODbContext>();
        optionsBuilder.UseMySql(connectionString, new MySqlServerVersion(new Version(8, 0, 36)));

        return new IPRODbContext(optionsBuilder.Options);
    }
}
