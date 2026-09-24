using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace TaskDeck.Data;

/// <summary>dotnet ef（マイグレーションの生成）専用。アプリの実行時には使わない。</summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<TaskDeckDbContext>
{
    public TaskDeckDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<TaskDeckDbContext>().UseSqlite("Data Source=design-time.db").Options);
}
