using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Athena.Net.CharServer.Db;

public sealed class CharDbContextFactory : IDesignTimeDbContextFactory<CharDbContext>
{
    public CharDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<CharDbContext>()
            .UseSqlServer("Server=localhost;Database=athena_char_design;Trusted_Connection=True;TrustServerCertificate=True")
            .Options;
        return new CharDbContext(options, new CharDbTableNames());
    }
}
