using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using TaskDeck.Data;
using TaskDeck.Data.CompiledModels;

namespace TaskDeck.Tests.Data;

/// <summary>
/// 実行時は src/TaskDeck.Data/CompiledModels（コンパイル済みモデル。起動を速くする。設計メモの変えた点 #24）が自動で使われる。
/// エンティティや設定を変えたのに作り直していないと古いモデルのまま動くので、今のコードから組んだモデルと突き合わせる。
/// 落ちたら作り直す: dotnet ef dbcontext optimize --project src/TaskDeck.Data --output-dir CompiledModels --namespace TaskDeck.Data.CompiledModels
/// </summary>
public sealed class CompiledModelTests
{
    [Fact]
    public void CompiledModel_MatchesTheModelBuiltFromCode()
    {
        using var db = new TaskDeckDbContext(new DbContextOptionsBuilder<TaskDeckDbContext>().UseSqlite("Data Source=:memory:").Options);
        var compiled = db.Model;
        var built = db.GetService<IDesignTimeModel>().Model;

        Assert.IsType<TaskDeckDbContextModel>(compiled);
        // 注釈（設計時にしか持たないものがある）を除いた形で、エンティティ・プロパティの型と必須・キー・外部キー・索引・ナビゲーションを比べる
        Assert.Equal(built.ToDebugString(MetadataDebugStringOptions.ShortDefault), compiled.ToDebugString(MetadataDebugStringOptions.ShortDefault));
    }
}
