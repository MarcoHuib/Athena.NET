using Athena.Net.CharServer.Db;
using Athena.Net.CharServer.Db.Entities;
using Microsoft.EntityFrameworkCore;

namespace Athena.Net.CharServer.Tests.Db;

public sealed class CharQuestModelTests
{
    [Fact]
    public void QuestMapping_UsesExistingCharacterScopedQuestTable()
    {
        var options = new DbContextOptionsBuilder<CharDbContext>().UseSqlServer("Server=localhost;Database=model-only;User ID=test;Password=test;TrustServerCertificate=true").Options;
        using var db = new CharDbContext(options, new CharDbTableNames());
        var entity = db.Model.FindEntityType(typeof(CharQuest))!;
        Assert.Equal("quest", entity.GetTableName());
        Assert.Equal([nameof(CharQuest.CharId), nameof(CharQuest.QuestId)], entity.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.Equal("state", entity.FindProperty(nameof(CharQuest.State))!.GetColumnName());
    }
}
