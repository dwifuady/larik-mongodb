using Larik.Data.MongoDB;
using Xunit;

namespace Larik.Data.Tests;

/// <summary>
/// The mongosh passthrough shapes. These pin the arguments a <c>find</c> carries into the driver:
/// a projection must reach the command, and the row cap must be applied by the server.
/// </summary>
public sealed class MongoShellRunnerTests
{
    [Fact]
    public void Find_carries_its_projection()
    {
        var find = Assert.IsType<ShellFind>(
            MongoShellRunner.Parse("db.users.find({ age: 30 }, { name: 1, _id: 0 })"));

        Assert.Equal(30, find.Filter["age"].AsInt32);
        Assert.NotNull(find.Projection);
        Assert.Equal(1, find.Projection!["name"].AsInt32);
        Assert.Equal(0, find.Projection["_id"].AsInt32);
    }

    [Fact]
    public void Find_options_push_the_row_cap_to_the_server()
    {
        var find = Assert.IsType<ShellFind>(MongoShellRunner.Parse("db.users.find({})"));
        var options = MongoShellRunner.BuildFindOptions(find, 100);

        Assert.Equal(101, options.Limit);
        Assert.Null(options.Projection);
    }

    [Fact]
    public void Find_carries_cursor_modifiers()
    {
        var find = Assert.IsType<ShellFind>(
            MongoShellRunner.Parse("db.users.find({}).sort({ age: -1 }).skip(2).limit(5)"));

        Assert.NotNull(find.Sort);
        Assert.Equal(2, find.Skip);
        Assert.Equal(5, find.Limit);
    }

    [Fact]
    public void Find_options_apply_cursor_modifiers()
    {
        var find = Assert.IsType<ShellFind>(
            MongoShellRunner.Parse("db.users.find({}).sort({ age: -1 }).skip(2).limit(5)"));
        var options = MongoShellRunner.BuildFindOptions(find, 100);

        Assert.Equal(5, options.Limit);
        Assert.Equal(2, options.Skip);
        Assert.NotNull(options.Sort);
    }

    [Fact]
    public void A_user_limit_above_the_row_cap_is_clipped()
    {
        var find = Assert.IsType<ShellFind>(
            MongoShellRunner.Parse("db.users.find({}).limit(500)"));
        var options = MongoShellRunner.BuildFindOptions(find, 100);

        Assert.Equal(101, options.Limit);
    }

    [Fact]
    public void Cursor_modifiers_are_refused_outside_find()
    {
        Assert.Throws<TranslationException>(() => MongoShellRunner.Parse("db.users.count({}).limit(5)"));
    }

    [Theory]
    [InlineData("db.users.find({}).limit(-1)")]
    [InlineData("db.users.find({}).limit(1.5)")]
    public void Find_limit_must_be_a_non_negative_integer(string statement)
    {
        Assert.Throws<TranslationException>(() => MongoShellRunner.Parse(statement));
    }

    [Fact]
    public void Find_options_apply_a_projection()
    {
        var find = Assert.IsType<ShellFind>(
            MongoShellRunner.Parse("db.users.find({}, { name: 1 })"));
        var options = MongoShellRunner.BuildFindOptions(find, 100);

        Assert.NotNull(options.Projection);
    }

    [Fact]
    public void FindOne_options_limit_to_one_and_apply_a_projection()
    {
        var find = Assert.IsType<ShellFindOne>(
            MongoShellRunner.Parse("db.users.findOne({}, { name: 1 })"));
        var options = MongoShellRunner.BuildFindOneOptions(find);

        Assert.Equal(1, options.Limit);
        Assert.NotNull(options.Projection);
    }

    [Fact]
    public void Parses_the_collection_read_methods()
    {
        Assert.IsType<ShellEstimatedCount>(MongoShellRunner.Parse("db.users.estimatedDocumentCount()"));
        Assert.IsType<ShellGetIndexes>(MongoShellRunner.Parse("db.users.getIndexes()"));
        Assert.IsType<ShellCollectionStats>(MongoShellRunner.Parse("db.users.stats()"));
    }

    [Fact]
    public void Parses_the_database_read_methods()
    {
        Assert.IsType<ShellGetCollectionNames>(MongoShellRunner.Parse("db.getCollectionNames()"));
        Assert.IsType<ShellDatabaseStats>(MongoShellRunner.Parse("db.stats()"));
    }

    [Fact]
    public void Distinct_parses_the_field_and_filter()
    {
        var distinct = Assert.IsType<ShellDistinct>(
            MongoShellRunner.Parse("db.users.distinct('city', { status: 'open' })"));

        Assert.Equal("city", distinct.Field);
        Assert.Equal("open", distinct.Filter["status"].AsString);
    }

    [Theory]
    [InlineData("db.users.distinct(city)")]
    [InlineData("db.users.distinct('city', {}, {})")]
    public void Distinct_arguments_are_validated(string statement)
    {
        Assert.Throws<TranslationException>(() => MongoShellRunner.Parse(statement));
    }

    [Theory]
    [InlineData("db.getIndexes()")]
    [InlineData("db.users.getCollectionNames()")]
    [InlineData("db.find({})")]
    public void Read_methods_are_only_valid_at_their_own_level(string statement)
    {
        Assert.Throws<TranslationException>(() => MongoShellRunner.Parse(statement));
    }

    [Theory]
    [InlineData("db.users.find({}, { name: 1 }, { age: 1 })")]
    [InlineData("db.users.findOne({}, { name: 1 }, { age: 1 })")]
    public void Extra_find_arguments_are_refused(string statement)
    {
        Assert.Throws<TranslationException>(() => MongoShellRunner.Parse(statement));
    }
}
